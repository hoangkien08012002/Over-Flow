using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OrderFlow.Application.Inventory;
using OrderFlow.Domain.Inventory;

namespace OrderFlow.Infrastructure.Persistence;

public sealed class InventoryOptions
{
    public int ReservationMinutes { get; set; } = 15;
}

public sealed class InventoryService(OrderFlowDbContext db, TimeProvider clock, IOptions<InventoryOptions> options) : IInventoryService, IReservationExpiryJob
{

    public async Task<InventorySnapshot?> GetAsync(Guid productId, CancellationToken cancellationToken)
    {
        var row = await db.Inventories.AsNoTracking().SingleOrDefaultAsync(x => x.ProductId == productId, cancellationToken);
        return row is null ? null : Snapshot(row);
    }

    public async Task<IReadOnlyList<ReservationSnapshot>> GetReservationsAsync(Guid productId, CancellationToken cancellationToken) =>
        (await db.InventoryReservations.AsNoTracking().Where(x => x.ProductId == productId)
            .OrderByDescending(x => x.CreatedAtUtc).Take(100).ToListAsync(cancellationToken)).Select(Snapshot).ToArray();

    public async Task<InventorySnapshot?> AdjustAsync(Guid productId, int quantityDelta, string reasonCode, string? note,
        Guid actorUserId, string correlationId, CancellationToken cancellationToken)
    {
        if (productId == Guid.Empty || quantityDelta == 0) throw new ArgumentException("Invalid adjustment.");
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var inventory = await LockedInventoryAsync(productId, cancellationToken);
        if (inventory is null) return null;
        var before = inventory.Available;
        var previousOnHand = inventory.OnHand;
        var previousReserved = inventory.Reserved;
        var now = clock.GetUtcNow();
        var adjustment = inventory.Adjust(quantityDelta, reasonCode, note, actorUserId, correlationId, now);
        var changed = await db.Inventories.Where(x => x.Id == inventory.Id && x.OnHand == previousOnHand && x.Reserved == previousReserved)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.OnHand, inventory.OnHand)
                .SetProperty(x => x.UpdatedAtUtc, now), cancellationToken);
        if (changed != 1) throw new DbUpdateConcurrencyException("Inventory changed during adjustment.");
        db.InventoryAdjustments.Add(adjustment);
        AddLowStockEvent(inventory, before, inventory.Available, correlationId, now);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (await GetAsync(productId, cancellationToken))!;
    }

    public async Task<InventorySnapshot?> SetLowStockThresholdAsync(Guid productId, int threshold, CancellationToken cancellationToken)
    {
        if (productId == Guid.Empty || threshold < 0) throw new ArgumentException("Invalid low-stock threshold.");
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var inventory = await LockedInventoryAsync(productId, cancellationToken);
        if (inventory is null) return null;
        var now = clock.GetUtcNow();
        inventory.SetLowStockThreshold(threshold, now);
        await db.Inventories.Where(x => x.Id == inventory.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.LowStockThreshold, threshold)
                .SetProperty(x => x.UpdatedAtUtc, now), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (await GetAsync(productId, cancellationToken))!;
    }

    public async Task<ReserveResult> ReserveAsync(Guid orderId, Guid productId, int quantity, CancellationToken cancellationToken)
    {
        if (orderId == Guid.Empty || productId == Guid.Empty || quantity <= 0) throw new ArgumentException("Invalid reservation request.");
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var inventory = await LockedInventoryAsync(productId, cancellationToken);
        if (inventory is null || !await db.Products.AsNoTracking().AnyAsync(x => x.Id == productId && x.IsActive, cancellationToken))
            return new ReserveResult(ReserveOutcome.ProductUnavailable, null);
        var existing = await db.InventoryReservations.AsNoTracking().SingleOrDefaultAsync(x => x.OrderId == orderId && x.ProductId == productId, cancellationToken);
        if (existing is not null)
        {
            if (existing.Quantity != quantity || existing.Status != ReservationStatus.Active)
                throw new InvalidOperationException("Reservation already exists with different terms or terminal status.");
            return new ReserveResult(ReserveOutcome.AlreadyReserved, Snapshot(existing));
        }
        if (inventory.Available < quantity) return new ReserveResult(ReserveOutcome.InsufficientStock, null);
        var now = clock.GetUtcNow();
        var changed = await db.Inventories.Where(x => x.Id == inventory.Id && x.OnHand - x.Reserved >= quantity)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.Reserved, x => x.Reserved + quantity)
                .SetProperty(x => x.UpdatedAtUtc, now), cancellationToken);
        if (changed != 1) throw new DbUpdateConcurrencyException("Inventory changed during reservation.");
        var reservation = InventoryReservation.Create(Guid.NewGuid(), orderId, productId, quantity, now.AddMinutes(options.Value.ReservationMinutes), now);
        db.InventoryReservations.Add(reservation);
        AddLowStockEvent(inventory, inventory.Available, inventory.Available - quantity, orderId.ToString("N"), now);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new ReserveResult(ReserveOutcome.Reserved, Snapshot(reservation));
    }

    public async Task<ReservationSnapshot?> TransitionAsync(Guid orderId, Guid productId, ReservationStatus target, CancellationToken cancellationToken)
    {
        if (orderId == Guid.Empty || productId == Guid.Empty || target == ReservationStatus.Active) throw new ArgumentException("Invalid transition request.");
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var inventory = await LockedInventoryAsync(productId, cancellationToken);
        if (inventory is null) return null;
        var reservation = await db.InventoryReservations.SingleOrDefaultAsync(x => x.OrderId == orderId && x.ProductId == productId, cancellationToken);
        if (reservation is null) return null;
        var now = clock.GetUtcNow();
        if (!reservation.TransitionTo(target, now)) return Snapshot(reservation);
        var changed = target == ReservationStatus.Committed
            ? await db.Inventories.Where(x => x.Id == inventory.Id && x.Reserved >= reservation.Quantity && x.OnHand >= reservation.Quantity)
                .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.OnHand, x => x.OnHand - reservation.Quantity)
                    .SetProperty(x => x.Reserved, x => x.Reserved - reservation.Quantity).SetProperty(x => x.UpdatedAtUtc, now), cancellationToken)
            : await db.Inventories.Where(x => x.Id == inventory.Id && x.Reserved >= reservation.Quantity)
                .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.Reserved, x => x.Reserved - reservation.Quantity)
                    .SetProperty(x => x.UpdatedAtUtc, now), cancellationToken);
        if (changed != 1) throw new DbUpdateConcurrencyException("Inventory changed during transition.");
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Snapshot(reservation);
    }

    public async Task<int> ExpireDueAsync(int batchSize, CancellationToken cancellationToken)
    {
        if (batchSize is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(batchSize));
        var now = clock.GetUtcNow();
        var due = await db.InventoryReservations.AsNoTracking()
            .Where(x => x.Status == ReservationStatus.Active && x.ExpiresAtUtc <= now)
            .OrderBy(x => x.ExpiresAtUtc).Take(batchSize)
            .Select(x => new { x.OrderId, x.ProductId }).ToListAsync(cancellationToken);
        var count = 0;
        foreach (var item in due)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var result = await TransitionAsync(item.OrderId, item.ProductId, ReservationStatus.Expired, cancellationToken);
                if (result?.Status == ReservationStatus.Expired) count++;
            }
            catch (InvalidOperationException) { /* A concurrent terminal transition won. */ }
        }
        return count;
    }

    private Task<OrderFlow.Domain.Inventory.Inventory?> LockedInventoryAsync(Guid productId, CancellationToken cancellationToken) =>
        db.Inventories.FromSqlInterpolated($"SELECT * FROM dbo.Inventories WITH (UPDLOCK, ROWLOCK) WHERE ProductId = {productId}")
            .AsNoTracking().SingleOrDefaultAsync(cancellationToken);

    private void AddLowStockEvent(OrderFlow.Domain.Inventory.Inventory inventory, int before, int after, string correlationId, DateTimeOffset now)
    {
        if (before <= inventory.LowStockThreshold || after > inventory.LowStockThreshold) return;
        db.OutboxMessages.Add(new OutboxMessage
        {
            Id = Guid.NewGuid(), EventType = "InventoryLow", EventVersion = 1, AggregateId = inventory.ProductId,
            CorrelationId = correlationId, Payload = JsonSerializer.Serialize(new { inventory.ProductId, Available = after, inventory.LowStockThreshold }),
            OccurredAtUtc = now
        });
    }

    private static InventorySnapshot Snapshot(OrderFlow.Domain.Inventory.Inventory x) =>
        new(x.ProductId, x.OnHand, x.Reserved, x.Available, x.LowStockThreshold, Convert.ToBase64String(x.RowVersion));
    private static ReservationSnapshot Snapshot(InventoryReservation x) =>
        new(x.Id, x.OrderId, x.ProductId, x.Quantity, x.Status, x.ExpiresAtUtc);
}
