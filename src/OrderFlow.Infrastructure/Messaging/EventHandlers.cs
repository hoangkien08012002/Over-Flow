using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OrderFlow.Application.Inventory;
using OrderFlow.Application.Messaging;
using OrderFlow.Application.Notifications;
using OrderFlow.Domain.Notifications;
using OrderFlow.Domain.Ordering;
using OrderFlow.Infrastructure.Persistence;

namespace OrderFlow.Infrastructure.Messaging;

public sealed class InventoryEventHandler(OrderFlowDbContext db, IInventoryService inventory, TimeProvider clock, ILogger<InventoryEventHandler> logger) : IEventHandler
{
    public Task HandleAsync(EventEnvelope envelope, CancellationToken cancellationToken) => envelope.EventType switch
    {
        EventTypes.OrderCreated => ReserveAsync(envelope, cancellationToken),
        EventTypes.PaymentSucceeded => CommitAsync(envelope, cancellationToken),
        EventTypes.PaymentFailed => ReleaseAsync(envelope, EventSerializer.ReadPayload<PaymentResultPayload>(envelope).OrderId, ReleaseReasons.PaymentFailed, cancellationToken),
        EventTypes.OrderCancelled => ReleaseAsync(envelope, EventSerializer.ReadPayload<OrderCancelledPayload>(envelope).OrderId, ReleaseReasons.OrderCancelled, cancellationToken),
        _ => throw new EventContractException($"Inventory does not handle {envelope.EventType}.")
    };

    private async Task ReserveAsync(EventEnvelope envelope, CancellationToken cancellationToken)
    {
        var request = EventSerializer.ReadPayload<OrderCreatedPayload>(envelope);
        // CancelAsync locks the order before releasing, so a cancellation either stops this reservation or releases what it committed.
        var order = await db.Orders.FromSqlInterpolated($"SELECT * FROM dbo.Orders WITH (UPDLOCK, ROWLOCK) WHERE Id = {request.OrderId}")
            .AsNoTracking().SingleOrDefaultAsync(cancellationToken) ?? throw new EventContractException("OrderCreated refers to an unknown order.");
        if (order.Status != OrderStatus.PendingInventory)
        {
            logger.LogInformation("Skipping reservation for order {OrderId} in status {Status}", order.Id, order.Status);
            return;
        }
        if (request.Items is null || request.Items.Count == 0) throw new EventContractException("OrderCreated has no items.");
        var result = await inventory.ReserveOrderAsync(order.Id, request.Items.Select(x => new ReservationLine(x.ProductId, x.Quantity)).ToList(), envelope.CorrelationId, envelope.EventId, cancellationToken);
        var now = clock.GetUtcNow();
        if (result.Rejected.Count > 0)
            db.OutboxMessages.Add(OutboxMessage.ForEvent(EventTypes.InventoryRejected, order.Id, envelope.CorrelationId, envelope.EventId,
                new InventoryRejectedPayload(order.Id, result.Rejected.Select(x => new RejectedItem(x.ProductId, x.RequestedQuantity, x.AvailableQuantity, x.ReasonCode)).ToList()), now));
        else
            db.OutboxMessages.Add(OutboxMessage.ForEvent(EventTypes.InventoryReserved, order.Id, envelope.CorrelationId, envelope.EventId,
                new InventoryReservedPayload(order.Id, result.Reservations.Select(x => x.Id).ToList(), result.Reservations.Min(x => x.ExpiresAtUtc),
                    result.Reservations.Select(x => new ReservedItem(x.ProductId, x.Quantity)).ToList()), now));
    }

    private async Task CommitAsync(EventEnvelope envelope, CancellationToken cancellationToken)
    {
        var orderId = EventSerializer.ReadPayload<PaymentResultPayload>(envelope).OrderId;
        var result = await inventory.CommitOrderAsync(orderId, cancellationToken);
        var now = clock.GetUtcNow();
        if (result.Outcome == OrderCommitOutcome.Committed)
            db.OutboxMessages.Add(OutboxMessage.ForEvent(EventTypes.InventoryCommitted, orderId, envelope.CorrelationId, envelope.EventId,
                new InventoryCommittedPayload(orderId, result.ReservationIds), now));
        else if (result.NewlyExpired > 0)
            db.OutboxMessages.Add(OutboxMessage.ForEvent(EventTypes.InventoryReleased, orderId, envelope.CorrelationId, envelope.EventId,
                new InventoryReleasedPayload(orderId, ReleaseReasons.ReservationExpired, result.NewlyExpired), now));
    }

    private async Task ReleaseAsync(EventEnvelope envelope, Guid orderId, string reason, CancellationToken cancellationToken)
    {
        var released = await inventory.ReleaseForCancellationAsync(orderId, cancellationToken);
        if (released > 0)
            db.OutboxMessages.Add(OutboxMessage.ForEvent(EventTypes.InventoryReleased, orderId, envelope.CorrelationId, envelope.EventId,
                new InventoryReleasedPayload(orderId, reason, released), clock.GetUtcNow()));
    }
}

public sealed class OrderingEventHandler(OrderFlowDbContext db, TimeProvider clock, ILogger<OrderingEventHandler> logger) : IEventHandler
{
    public Task HandleAsync(EventEnvelope envelope, CancellationToken cancellationToken) => envelope.EventType switch
    {
        EventTypes.InventoryReserved => OnReservedAsync(envelope, cancellationToken),
        EventTypes.InventoryRejected => OnRejectedAsync(envelope, cancellationToken),
        EventTypes.PaymentSucceeded => OnPaymentSucceededAsync(envelope, cancellationToken),
        EventTypes.PaymentFailed => OnPaymentFailedAsync(envelope, cancellationToken),
        EventTypes.InventoryCommitted => OnCommittedAsync(envelope, cancellationToken),
        EventTypes.InventoryReleased => OnReleasedAsync(envelope, cancellationToken),
        _ => throw new EventContractException($"Ordering does not handle {envelope.EventType}.")
    };

    private async Task OnReservedAsync(EventEnvelope envelope, CancellationToken cancellationToken)
    {
        var reserved = EventSerializer.ReadPayload<InventoryReservedPayload>(envelope);
        var order = await LockOrderAsync(reserved.OrderId, cancellationToken);
        if (order.Status != OrderStatus.PendingInventory) { LogIgnored(envelope, order); return; }
        var now = clock.GetUtcNow();
        order.Transition(OrderStatus.AwaitingPayment, now, envelope.CorrelationId, eventId: envelope.EventId);
        db.OutboxMessages.Add(OutboxMessage.ForEvent(EventTypes.PaymentRequested, order.Id, envelope.CorrelationId, envelope.EventId,
            new PaymentRequestedPayload(order.Id, order.GrandTotal, order.CurrencyCode, order.PaymentMethod, reserved.ReservationIds), now));
    }

    private async Task OnRejectedAsync(EventEnvelope envelope, CancellationToken cancellationToken)
    {
        var order = await LockOrderAsync(EventSerializer.ReadPayload<InventoryRejectedPayload>(envelope).OrderId, cancellationToken);
        if (order.Status != OrderStatus.PendingInventory) { LogIgnored(envelope, order); return; }
        var now = clock.GetUtcNow();
        order.Transition(OrderStatus.Rejected, now, envelope.CorrelationId, eventId: envelope.EventId, reason: "INVENTORY_REJECTED");
        await CheckoutService.ReleaseCouponAsync(db, order, now, cancellationToken);
    }

    private async Task OnPaymentSucceededAsync(EventEnvelope envelope, CancellationToken cancellationToken)
    {
        var order = await LockOrderAsync(EventSerializer.ReadPayload<PaymentResultPayload>(envelope).OrderId, cancellationToken);
        if (order.Status != OrderStatus.AwaitingPayment) { LogIgnored(envelope, order); return; }
        order.Transition(OrderStatus.Paid, clock.GetUtcNow(), envelope.CorrelationId, eventId: envelope.EventId);
    }

    private async Task OnPaymentFailedAsync(EventEnvelope envelope, CancellationToken cancellationToken)
    {
        var payment = EventSerializer.ReadPayload<PaymentResultPayload>(envelope);
        var order = await LockOrderAsync(payment.OrderId, cancellationToken);
        if (order.Status != OrderStatus.AwaitingPayment) { LogIgnored(envelope, order); return; }
        var now = clock.GetUtcNow();
        order.Transition(OrderStatus.PaymentFailed, now, envelope.CorrelationId, eventId: envelope.EventId, reason: payment.ResultCode);
        await CheckoutService.ReleaseCouponAsync(db, order, now, cancellationToken);
    }

    private async Task OnCommittedAsync(EventEnvelope envelope, CancellationToken cancellationToken)
    {
        var order = await LockOrderAsync(EventSerializer.ReadPayload<InventoryCommittedPayload>(envelope).OrderId, cancellationToken);
        var now = clock.GetUtcNow();
        // The two events travel on different topics. A commit only follows a successful payment, so it may safely advance through Paid.
        if (order.Status == OrderStatus.AwaitingPayment) order.Transition(OrderStatus.Paid, now, envelope.CorrelationId, eventId: envelope.EventId, reason: "RESERVATION_COMMITTED_FIRST");
        if (order.Status != OrderStatus.Paid) { LogIgnored(envelope, order); return; }
        order.Transition(OrderStatus.Processing, now, envelope.CorrelationId, eventId: envelope.EventId);
    }

    private async Task OnReleasedAsync(EventEnvelope envelope, CancellationToken cancellationToken)
    {
        var released = EventSerializer.ReadPayload<InventoryReleasedPayload>(envelope);
        // Payment failure and cancellation have already moved the order; only a lapsed reservation still needs an order decision.
        if (released.ReasonCode != ReleaseReasons.ReservationExpired) return;
        var order = await LockOrderAsync(released.OrderId, cancellationToken);
        if (order.Status is not (OrderStatus.PendingInventory or OrderStatus.AwaitingPayment or OrderStatus.Paid)) { LogIgnored(envelope, order); return; }
        var now = clock.GetUtcNow();
        order.Transition(OrderStatus.Cancelled, now, envelope.CorrelationId, eventId: envelope.EventId, reason: ReleaseReasons.ReservationExpired);
        await CheckoutService.ReleaseCouponAsync(db, order, now, cancellationToken);
        db.OutboxMessages.Add(OutboxMessage.ForEvent(EventTypes.OrderCancelled, order.Id, envelope.CorrelationId, envelope.EventId,
            new OrderCancelledPayload(order.Id, order.UserId, ReleaseReasons.ReservationExpired), now));
    }

    private async Task<Order> LockOrderAsync(Guid orderId, CancellationToken cancellationToken) =>
        await db.Orders.FromSqlInterpolated($"SELECT * FROM dbo.Orders WITH (UPDLOCK, ROWLOCK) WHERE Id = {orderId}").SingleOrDefaultAsync(cancellationToken)
            ?? throw new EventContractException("Event refers to an unknown order.");

    private void LogIgnored(EventEnvelope envelope, Order order) =>
        logger.LogInformation("Ignoring {EventType} {EventId} for order {OrderId} in status {Status}", envelope.EventType, envelope.EventId, order.Id, order.Status);
}

public sealed class PaymentEventHandler(PaymentService payments) : IEventHandler
{
    public Task HandleAsync(EventEnvelope envelope, CancellationToken cancellationToken) => envelope.EventType switch
    {
        EventTypes.PaymentRequested => payments.ProcessRequestAsync(envelope, EventSerializer.ReadPayload<PaymentRequestedPayload>(envelope), cancellationToken),
        EventTypes.OrderCancelled => RefundAsync(envelope, cancellationToken),
        _ => throw new EventContractException($"Payment does not handle {envelope.EventType}.")
    };

    private async Task RefundAsync(EventEnvelope envelope, CancellationToken cancellationToken)
    {
        var cancelled = EventSerializer.ReadPayload<OrderCancelledPayload>(envelope);
        await payments.RefundForOrderAsync(cancelled.OrderId, $"order-cancelled:{cancelled.OrderId:N}", cancelled.ReasonCode, envelope.CorrelationId, envelope.EventId, cancellationToken);
    }
}

public sealed class NotificationEventHandler(OrderFlowDbContext db, INotificationProvider provider, TimeProvider clock) : IEventHandler
{
    public async Task HandleAsync(EventEnvelope envelope, CancellationToken cancellationToken)
    {
        var (orderId, describe) = Describe(envelope);
        Guid? userId = null;
        var reference = envelope.AggregateId.ToString("N");
        if (orderId is not null)
        {
            var order = await db.Orders.AsNoTracking().Where(x => x.Id == orderId).Select(x => new { x.UserId, x.OrderNumber }).SingleOrDefaultAsync(cancellationToken)
                ?? throw new EventContractException("Event refers to an unknown order.");
            userId = order.UserId;
            reference = order.OrderNumber;
        }
        var notification = Notification.Create(Guid.NewGuid(), userId, orderId, envelope.EventType, provider.Channel, describe(reference), envelope.EventId, clock.GetUtcNow());
        // A provider call cannot join the transaction, so a commit failure after it can repeat the send. The log provider makes that harmless.
        await provider.SendAsync(new NotificationMessage(notification.Id, userId, orderId, notification.Type, notification.Text), cancellationToken);
        db.Notifications.Add(notification);
    }

    private static (Guid? OrderId, Func<string, string> Describe) Describe(EventEnvelope envelope) => envelope.EventType switch
    {
        EventTypes.InventoryRejected => (EventSerializer.ReadPayload<InventoryRejectedPayload>(envelope).OrderId, x => $"Order {x} was rejected because stock is unavailable."),
        EventTypes.PaymentSucceeded => (EventSerializer.ReadPayload<PaymentResultPayload>(envelope).OrderId, x => $"Payment for order {x} succeeded."),
        EventTypes.PaymentFailed => (EventSerializer.ReadPayload<PaymentResultPayload>(envelope).OrderId, x => $"Payment for order {x} failed."),
        EventTypes.OrderCancelled => (EventSerializer.ReadPayload<OrderCancelledPayload>(envelope).OrderId, x => $"Order {x} was cancelled."),
        EventTypes.RefundCompleted => (EventSerializer.ReadPayload<RefundCompletedPayload>(envelope).OrderId, x => $"The refund for order {x} is complete."),
        EventTypes.InventoryLow => ((Guid?)null, x => $"Product {x} is low on stock."),
        _ => throw new EventContractException($"Notifications does not handle {envelope.EventType}.")
    };
}

public sealed class LogNotificationProvider(ILogger<LogNotificationProvider> logger) : INotificationProvider
{
    public string Channel => "Log";
    public Task SendAsync(NotificationMessage message, CancellationToken cancellationToken)
    {
        logger.LogInformation("Notification {NotificationId} ({Type}) for user {UserId}: {Text}", message.NotificationId, message.Type, message.UserId, message.Text);
        return Task.CompletedTask;
    }
}
