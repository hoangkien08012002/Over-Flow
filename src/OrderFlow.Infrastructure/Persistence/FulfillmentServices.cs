using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OrderFlow.Application.Inventory;
using OrderFlow.Application.Messaging;
using OrderFlow.Domain.Ordering;
using OrderFlow.Domain.Returns;
using OrderFlow.Domain.Shipping;

namespace OrderFlow.Infrastructure.Persistence;

public sealed record ServiceResult<T>(int StatusCode, string? Code, T? Value) where T : class
{
    public static ServiceResult<T> Ok(T value, int status = 200) => new(status, null, value);
    public static ServiceResult<T> Fail(int status, string code) => new(status, code, null);
}

public sealed class ShippingService(OrderFlowDbContext db, TimeProvider clock)
{
    // The order lock serializes shipment changes with staff cancellation, so a cancelled order cannot be dispatched.
    public async Task<ServiceResult<Shipment>> CreateAsync(Guid orderId, Guid actorId, string correlationId, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var order = await db.Orders.FromSqlInterpolated($"SELECT * FROM dbo.Orders WITH (UPDLOCK, ROWLOCK) WHERE Id = {orderId}").AsNoTracking().SingleOrDefaultAsync(ct);
        if (order is null) return ServiceResult<Shipment>.Fail(404, "ORDER_NOT_FOUND");
        var existing = await db.Shipments.AsNoTracking().Include(x => x.History).SingleOrDefaultAsync(x => x.OrderId == orderId, ct);
        if (existing is not null) return ServiceResult<Shipment>.Ok(existing);
        if (order.Status != OrderStatus.Processing) return ServiceResult<Shipment>.Fail(409, "INVALID_STATE_TRANSITION");
        var shipment = Shipment.Create(Guid.NewGuid(), orderId, actorId, clock.GetUtcNow(), correlationId);
        db.Shipments.Add(shipment);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return ServiceResult<Shipment>.Ok(shipment, 201);
    }

    public async Task<ServiceResult<Shipment>> ChangeStatusAsync(Guid shipmentId, ShipmentStatus target, string? reason, Guid actorId, string correlationId, CancellationToken ct)
    {
        // Cancelling a shipment is a consequence of cancelling its order, so it is not available as a direct command.
        if (target is ShipmentStatus.Pending or ShipmentStatus.Cancelled || reason?.Trim().Length > 500) return ServiceResult<Shipment>.Fail(400, "VALIDATION_FAILED");
        var orderId = await db.Shipments.AsNoTracking().Where(x => x.Id == shipmentId).Select(x => (Guid?)x.OrderId).SingleOrDefaultAsync(ct);
        if (orderId is null) return ServiceResult<Shipment>.Fail(404, "RESOURCE_NOT_FOUND");
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await db.Orders.FromSqlInterpolated($"SELECT * FROM dbo.Orders WITH (UPDLOCK, ROWLOCK) WHERE Id = {orderId.Value}").AsNoTracking().SingleAsync(ct);
        var shipment = await db.Shipments.Include(x => x.History).SingleAsync(x => x.Id == shipmentId, ct);
        var now = clock.GetUtcNow();
        bool changed;
        try { changed = shipment.TransitionTo(target, actorId, reason?.Trim(), now, correlationId); }
        catch (InvalidOperationException) { return ServiceResult<Shipment>.Fail(409, "INVALID_STATE_TRANSITION"); }
        if (!changed) return ServiceResult<Shipment>.Ok(shipment);
        if (target is ShipmentStatus.Shipped or ShipmentStatus.Delivered or ShipmentStatus.DeliveryFailed)
            db.OutboxMessages.Add(OutboxMessage.ForEvent(EventTypes.ShipmentStatusChanged, orderId.Value, correlationId, null,
                new ShipmentStatusChangedPayload(orderId.Value, shipment.Id, target.ToString(), shipment.TrackingCode), now));
        try { await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct); }
        catch (DbUpdateConcurrencyException) { return ServiceResult<Shipment>.Fail(409, "CONCURRENCY_CONFLICT"); }
        return ServiceResult<Shipment>.Ok(shipment);
    }
}

public sealed class ReturnOptions
{
    public int WindowDays { get; set; } = 7;
}

public sealed record ReceivedLine(Guid OrderItemId, bool Restock);

public sealed class ReturnService(OrderFlowDbContext db, IInventoryService inventory, TimeProvider clock, IOptions<ReturnOptions> options)
{
    public async Task<ServiceResult<ReturnRequest>> RequestAsync(Guid userId, Guid orderId, string? reason, string correlationId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length > 500) return ServiceResult<ReturnRequest>.Fail(400, "VALIDATION_FAILED");
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var order = await db.Orders.FromSqlInterpolated($"SELECT * FROM dbo.Orders WITH (UPDLOCK, ROWLOCK) WHERE Id = {orderId} AND UserId = {userId}")
            .AsNoTracking().Include(x => x.Items).SingleOrDefaultAsync(ct);
        if (order is null) return ServiceResult<ReturnRequest>.Fail(404, "ORDER_NOT_FOUND");
        if (order.Status != OrderStatus.Delivered) return ServiceResult<ReturnRequest>.Fail(409, "RETURN_NOT_ELIGIBLE");
        var deliveredAt = await db.OrderStatusHistories.AsNoTracking().Where(x => x.OrderId == orderId && x.ToStatus == OrderStatus.Delivered).Select(x => (DateTimeOffset?)x.ChangedAtUtc).MaxAsync(ct);
        var now = clock.GetUtcNow();
        if (deliveredAt is null || now > deliveredAt.Value.AddDays(options.Value.WindowDays)) return ServiceResult<ReturnRequest>.Fail(409, "RETURN_WINDOW_CLOSED");
        var request = ReturnRequest.Create(Guid.NewGuid(), orderId, userId, reason, order.Items.Select(x => (x.Id, x.ProductId, x.Quantity)), now, correlationId);
        db.ReturnRequests.Add(request);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 }) { return ServiceResult<ReturnRequest>.Fail(409, "RETURN_ALREADY_EXISTS"); }
        await transaction.CommitAsync(ct);
        return ServiceResult<ReturnRequest>.Ok(request, 201);
    }

    public Task<ServiceResult<ReturnRequest>> ApproveAsync(Guid id, Guid actorId, string correlationId, CancellationToken ct) =>
        ChangeAsync(id, ReturnStatus.Approved, actorId, null, correlationId, ct);

    public Task<ServiceResult<ReturnRequest>> RejectAsync(Guid id, Guid actorId, string? reason, string correlationId, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(reason) || reason.Trim().Length > 500
            ? Task.FromResult(ServiceResult<ReturnRequest>.Fail(400, "VALIDATION_FAILED"))
            : ChangeAsync(id, ReturnStatus.Rejected, actorId, reason.Trim(), correlationId, ct);

    public Task<ServiceResult<ReturnRequest>> CloseAsync(Guid id, Guid actorId, string correlationId, CancellationToken ct) =>
        ChangeAsync(id, ReturnStatus.Closed, actorId, null, correlationId, ct);

    // Receipt, restock, and the refund request commit together, so stock cannot come back without the refund being requested, or twice.
    public async Task<ServiceResult<ReturnRequest>> ReceiveAsync(Guid id, IReadOnlyList<ReceivedLine>? lines, Guid actorId, string correlationId, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var request = await LockAsync(id, ct);
        if (request is null) return ServiceResult<ReturnRequest>.Fail(404, "RESOURCE_NOT_FOUND");
        if (request.Status is ReturnStatus.Received or ReturnStatus.Refunded or ReturnStatus.Closed) return ServiceResult<ReturnRequest>.Ok(request);
        if (lines is null || lines.Select(x => x.OrderItemId).Distinct().Count() != lines.Count || request.Items.Count != lines.Count ||
            request.Items.Any(item => lines.All(line => line.OrderItemId != item.OrderItemId)))
            return ServiceResult<ReturnRequest>.Fail(400, "VALIDATION_FAILED");
        var now = clock.GetUtcNow();
        try { request.TransitionTo(ReturnStatus.Received, actorId, null, null, now, correlationId); }
        catch (InvalidOperationException) { return ServiceResult<ReturnRequest>.Fail(409, "INVALID_STATE_TRANSITION"); }
        // Product order matches every other inventory lock, so two receipts cannot deadlock.
        foreach (var item in request.Items.OrderBy(x => x.ProductId))
        {
            var restock = lines.Single(x => x.OrderItemId == item.OrderItemId).Restock;
            item.RecordReceipt(restock);
            if (restock) await inventory.RestockReturnAsync(item.ProductId, item.Quantity, $"Return {id:N}", actorId, correlationId, ct);
        }
        db.OutboxMessages.Add(OutboxMessage.ForEvent(EventTypes.RefundRequested, request.OrderId, correlationId, null,
            new RefundRequestedPayload(request.OrderId, request.Id, "RETURN_RECEIVED"), now));
        try { await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct); }
        catch (DbUpdateConcurrencyException) { return ServiceResult<ReturnRequest>.Fail(409, "CONCURRENCY_CONFLICT"); }
        return ServiceResult<ReturnRequest>.Ok(request);
    }

    private async Task<ServiceResult<ReturnRequest>> ChangeAsync(Guid id, ReturnStatus target, Guid actorId, string? reason, string correlationId, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var request = await LockAsync(id, ct);
        if (request is null) return ServiceResult<ReturnRequest>.Fail(404, "RESOURCE_NOT_FOUND");
        bool changed;
        try { changed = request.TransitionTo(target, actorId, null, reason, clock.GetUtcNow(), correlationId); }
        catch (InvalidOperationException) { return ServiceResult<ReturnRequest>.Fail(409, "INVALID_STATE_TRANSITION"); }
        if (!changed) return ServiceResult<ReturnRequest>.Ok(request);
        try { await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct); }
        catch (DbUpdateConcurrencyException) { return ServiceResult<ReturnRequest>.Fail(409, "CONCURRENCY_CONFLICT"); }
        return ServiceResult<ReturnRequest>.Ok(request);
    }

    private Task<ReturnRequest?> LockAsync(Guid id, CancellationToken ct) =>
        db.ReturnRequests.FromSqlInterpolated($"SELECT * FROM dbo.ReturnRequests WITH (UPDLOCK, ROWLOCK) WHERE Id = {id}")
            .Include(x => x.Items).Include(x => x.History).SingleOrDefaultAsync(ct);
}
