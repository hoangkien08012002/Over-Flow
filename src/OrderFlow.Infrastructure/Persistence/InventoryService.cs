using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OrderFlow.Application.Inventory;
using OrderFlow.Application.Messaging;
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
        await ApplyQuantitiesAsync(inventory.Id, reservation.Quantity, target, now, cancellationToken);
        if (target == ReservationStatus.Expired)
            db.OutboxMessages.Add(OutboxMessage.ForEvent(EventTypes.InventoryReleased, orderId, orderId.ToString("N"), null,
                new InventoryReleasedPayload(orderId, ReleaseReasons.ReservationExpired, 1), now));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Snapshot(reservation);
    }

    public async Task<int> ReleaseForCancellationAsync(Guid orderId, CancellationToken cancellationToken)
    {
        if (orderId == Guid.Empty || db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Cancellation release requires an active SQL transaction.");
        var productIds = await db.InventoryReservations.AsNoTracking().Where(x => x.OrderId == orderId)
            .OrderBy(x => x.ProductId).Select(x => x.ProductId).ToListAsync(cancellationToken);
        var released = 0;
        foreach (var productId in productIds)
        {
            // Match the inventory-first lock order used by reservation and terminal transitions.
            var inventory = await LockedInventoryAsync(productId, cancellationToken)
                ?? throw new DbUpdateConcurrencyException("Inventory disappeared during cancellation.");
            var reservation = await db.InventoryReservations.SingleAsync(x => x.OrderId == orderId && x.ProductId == productId, cancellationToken);
            if (reservation.Status == ReservationStatus.Released) continue;
            if (reservation.Status == ReservationStatus.Expired) continue;
            if (reservation.Status != ReservationStatus.Active)
                throw new InvalidOperationException("Reservation cannot be released after commitment.");
            var now = clock.GetUtcNow();
            reservation.TransitionTo(ReservationStatus.Released, now);
            var changed = await db.Inventories.Where(x => x.Id == inventory.Id && x.Reserved >= reservation.Quantity)
                .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.Reserved, x => x.Reserved - reservation.Quantity)
                    .SetProperty(x => x.UpdatedAtUtc, now), cancellationToken);
            if (changed != 1) throw new DbUpdateConcurrencyException("Inventory changed during cancellation release.");
            released++;
        }
        return released;
    }

    public async Task<OrderReservationResult> ReserveOrderAsync(Guid orderId, IReadOnlyList<ReservationLine> lines, string correlationId, Guid? causationId, CancellationToken cancellationToken)
    {
        if (orderId == Guid.Empty || lines.Count == 0 || lines.Any(x => x.ProductId == Guid.Empty || x.Quantity <= 0) || db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Order reservation requires valid lines and an active SQL transaction.");
        var existing = await db.InventoryReservations.AsNoTracking().Where(x => x.OrderId == orderId).ToListAsync(cancellationToken);
        if (existing.Count > 0) return new OrderReservationResult(existing.Select(Snapshot).ToArray(), []);
        // Locks are taken in product order so two multi-line orders cannot deadlock each other.
        var locked = new List<(ReservationLine Line, OrderFlow.Domain.Inventory.Inventory Inventory)>();
        var rejected = new List<RejectedLine>();
        foreach (var line in lines.OrderBy(x => x.ProductId))
        {
            var inventory = await LockedInventoryAsync(line.ProductId, cancellationToken);
            if (inventory is null || !await db.Products.AsNoTracking().AnyAsync(x => x.Id == line.ProductId && x.IsActive, cancellationToken))
                rejected.Add(new RejectedLine(line.ProductId, line.Quantity, 0, "PRODUCT_UNAVAILABLE"));
            else if (inventory.Available < line.Quantity)
                rejected.Add(new RejectedLine(line.ProductId, line.Quantity, inventory.Available, "INSUFFICIENT_STOCK"));
            else locked.Add((line, inventory));
        }
        if (rejected.Count > 0) return new OrderReservationResult([], rejected);
        var now = clock.GetUtcNow();
        var expiresAt = now.AddMinutes(options.Value.ReservationMinutes);
        var reservations = new List<ReservationSnapshot>();
        foreach (var (line, inventory) in locked)
        {
            var changed = await db.Inventories.Where(x => x.Id == inventory.Id && x.OnHand - x.Reserved >= line.Quantity)
                .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.Reserved, x => x.Reserved + line.Quantity)
                    .SetProperty(x => x.UpdatedAtUtc, now), cancellationToken);
            if (changed != 1) throw new DbUpdateConcurrencyException("Inventory changed during reservation.");
            var reservation = InventoryReservation.Create(Guid.NewGuid(), orderId, line.ProductId, line.Quantity, expiresAt, now);
            db.InventoryReservations.Add(reservation);
            reservations.Add(Snapshot(reservation));
            AddLowStockEvent(inventory, inventory.Available, inventory.Available - line.Quantity, correlationId, now, causationId);
        }
        return new OrderReservationResult(reservations, []);
    }

    public async Task<OrderCommitResult> CommitOrderAsync(Guid orderId, CancellationToken cancellationToken)
    {
        if (orderId == Guid.Empty || db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Order commit requires an active SQL transaction.");
        var productIds = await db.InventoryReservations.AsNoTracking().Where(x => x.OrderId == orderId)
            .OrderBy(x => x.ProductId).Select(x => x.ProductId).ToListAsync(cancellationToken);
        if (productIds.Count == 0) return new OrderCommitResult(OrderCommitOutcome.NoReservations, [], 0);
        var inventories = new Dictionary<Guid, Guid>();
        foreach (var productId in productIds)
        {
            var inventory = await LockedInventoryAsync(productId, cancellationToken)
                ?? throw new DbUpdateConcurrencyException("Inventory disappeared during commit.");
            inventories[productId] = inventory.Id;
        }
        var reservations = await db.InventoryReservations.Where(x => x.OrderId == orderId).ToListAsync(cancellationToken);
        var ids = reservations.Select(x => x.Id).ToArray();
        if (reservations.All(x => x.Status == ReservationStatus.Committed)) return new OrderCommitResult(OrderCommitOutcome.AlreadyCommitted, ids, 0);
        var now = clock.GetUtcNow();
        if (reservations.All(x => x.Status == ReservationStatus.Active && now < x.ExpiresAtUtc))
        {
            foreach (var reservation in reservations)
            {
                reservation.TransitionTo(ReservationStatus.Committed, now);
                await ApplyQuantitiesAsync(inventories[reservation.ProductId], reservation.Quantity, ReservationStatus.Committed, now, cancellationToken);
            }
            return new OrderCommitResult(OrderCommitOutcome.Committed, ids, 0);
        }
        // A line was released or has lapsed, so stock cannot be committed for the whole order: give up what is still held.
        var newlyExpired = 0;
        foreach (var reservation in reservations.Where(x => x.Status == ReservationStatus.Active))
        {
            var target = now >= reservation.ExpiresAtUtc ? ReservationStatus.Expired : ReservationStatus.Released;
            reservation.TransitionTo(target, now);
            await ApplyQuantitiesAsync(inventories[reservation.ProductId], reservation.Quantity, target, now, cancellationToken);
            if (target == ReservationStatus.Expired) newlyExpired++;
        }
        return new OrderCommitResult(OrderCommitOutcome.NotCommittable, ids, newlyExpired);
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

    private async Task ApplyQuantitiesAsync(Guid inventoryId, int quantity, ReservationStatus target, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var changed = target == ReservationStatus.Committed
            ? await db.Inventories.Where(x => x.Id == inventoryId && x.Reserved >= quantity && x.OnHand >= quantity)
                .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.OnHand, x => x.OnHand - quantity)
                    .SetProperty(x => x.Reserved, x => x.Reserved - quantity).SetProperty(x => x.UpdatedAtUtc, now), cancellationToken)
            : await db.Inventories.Where(x => x.Id == inventoryId && x.Reserved >= quantity)
                .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.Reserved, x => x.Reserved - quantity)
                    .SetProperty(x => x.UpdatedAtUtc, now), cancellationToken);
        if (changed != 1) throw new DbUpdateConcurrencyException("Inventory changed during transition.");
    }

    private void AddLowStockEvent(OrderFlow.Domain.Inventory.Inventory inventory, int before, int after, string correlationId, DateTimeOffset now, Guid? causationId = null)
    {
        if (before <= inventory.LowStockThreshold || after > inventory.LowStockThreshold) return;
        db.OutboxMessages.Add(OutboxMessage.ForEvent(EventTypes.InventoryLow, inventory.ProductId, correlationId, causationId,
            new InventoryLowPayload(inventory.ProductId, after, inventory.LowStockThreshold), now));
    }

    private static InventorySnapshot Snapshot(OrderFlow.Domain.Inventory.Inventory x) =>
        new(x.ProductId, x.OnHand, x.Reserved, x.Available, x.LowStockThreshold, Convert.ToBase64String(x.RowVersion));
    private static ReservationSnapshot Snapshot(InventoryReservation x) =>
        new(x.Id, x.OrderId, x.ProductId, x.Quantity, x.Status, x.ExpiresAtUtc);
}
