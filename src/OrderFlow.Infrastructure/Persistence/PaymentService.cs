using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrderFlow.Application.Messaging;
using OrderFlow.Application.Payments;
using OrderFlow.Domain.Ordering;
using OrderFlow.Domain.Payments;

namespace OrderFlow.Infrastructure.Persistence;

public sealed class RefundOptions
{
    // Attempts made before a failed refund goes to the Admin queue (D-23).
    public int MaxAttempts { get; set; } = 3;
    public int RetryDelaySeconds { get; set; } = 30;
    public int PollSeconds { get; set; } = 5;
    public int BatchSize { get; set; } = 50;
}

public sealed class PaymentService(OrderFlowDbContext db, PaymentStrategyFactory strategies, TimeProvider clock, IOptions<RefundOptions> refundOptions, ILogger<PaymentService> logger)
{
    // Runs inside the consumer transaction. The simulated providers are local and deterministic, so the call rolls back with the Inbox record.
    public async Task ProcessRequestAsync(EventEnvelope source, PaymentRequestedPayload request, CancellationToken ct)
    {
        // CancelAsync takes the same order lock, so a cancellation is either seen here or arrives later as OrderCancelled and refunds.
        var order = await db.Orders.FromSqlInterpolated($"SELECT * FROM dbo.Orders WITH (UPDLOCK, ROWLOCK) WHERE Id = {request.OrderId}")
            .AsNoTracking().SingleOrDefaultAsync(ct) ?? throw new EventContractException("PaymentRequested refers to an unknown order.");
        if (order.Status != OrderStatus.AwaitingPayment || await db.Payments.AnyAsync(x => x.OrderId == order.Id, ct)) return;
        var strategy = strategies.Find(order.PaymentMethod) ?? throw new EventContractException("PaymentRequested has an unsupported payment method.");
        var now = clock.GetUtcNow();
        var payment = Payment.Create(Guid.NewGuid(), order.Id, order.PaymentMethod, order.GrandTotal, order.CurrencyCode, now);
        payment.Begin(now);
        var result = await strategy.ChargeAsync(new PaymentAttempt(payment.Id, order.Id, payment.Amount, payment.CurrencyCode), ct);
        if (result.Accepted) payment.Succeed(result.ProviderReference, result.ResultCode, strategy.DefersSettlement, now);
        else payment.Fail(result.ProviderReference, result.ResultCode, now);
        db.Payments.Add(payment);
        db.OutboxMessages.Add(OutboxMessage.ForEvent(result.Accepted ? EventTypes.PaymentSucceeded : EventTypes.PaymentFailed, order.Id, source.CorrelationId, source.EventId,
            new PaymentResultPayload(order.Id, payment.Id, payment.Amount, payment.CurrencyCode, payment.Method, result.ProviderReference, result.ResultCode), now));
        BusinessMetrics.PaymentOutcomes.Add(1, new KeyValuePair<string, object?>("outcome", result.Accepted ? "succeeded" : "failed"), new KeyValuePair<string, object?>("method", payment.Method));
    }

    // Safe to repeat: one payment has one refund, so a repeated command or event returns the existing refund.
    public async Task<Refund?> RefundForOrderAsync(Guid orderId, string idempotencyKey, string reason, string correlationId, Guid? causationId, CancellationToken ct)
    {
        await using var ownTransaction = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct) : null;
        var refund = await RefundCoreAsync(orderId, idempotencyKey, reason, correlationId, causationId, ct);
        if (ownTransaction is not null)
        {
            await db.SaveChangesAsync(ct);
            await ownTransaction.CommitAsync(ct);
        }
        return refund;
    }

    // Runs inside the consumer transaction. Delivery is what collects COD, and a return exists only after delivery, so collection is recorded first
    // even when the delivery event has not reached this consumer yet. Otherwise the refund would void a collection that really happened.
    public async Task<Refund?> RefundForReturnAsync(RefundRequestedPayload request, string correlationId, Guid causationId, CancellationToken ct)
    {
        var payment = await LockPaymentAsync(request.OrderId, ct);
        payment?.MarkCollected(clock.GetUtcNow());
        return await RefundForOrderAsync(request.OrderId, $"return:{request.ReturnRequestId:N}", request.ReasonCode, correlationId, causationId, ct);
    }

    public async Task MarkCollectedAsync(Guid orderId, CancellationToken ct)
    {
        var payment = await LockPaymentAsync(orderId, ct);
        payment?.MarkCollected(clock.GetUtcNow());
    }

    // One refund per transaction, so a failure leaves the rest of the batch unaffected. The payment row lock keeps two Workers from attempting the same refund.
    public async Task<int> RetryDueRefundsAsync(CancellationToken ct)
    {
        var options = refundOptions.Value;
        var now = clock.GetUtcNow();
        var due = await db.Refunds.AsNoTracking().Where(x => x.Status == RefundStatus.Pending && x.NextAttemptAtUtc != null && x.NextAttemptAtUtc <= now)
            .OrderBy(x => x.NextAttemptAtUtc).Take(options.BatchSize).Select(x => new { x.Id, x.OrderId }).ToListAsync(ct);
        var attempted = 0;
        foreach (var item in due)
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
            var payment = await LockPaymentAsync(item.OrderId, ct);
            var refund = await db.Refunds.SingleOrDefaultAsync(x => x.Id == item.Id, ct);
            if (payment is null || refund is null || refund.Status != RefundStatus.Pending || refund.NextAttemptAtUtc is null || refund.NextAttemptAtUtc > clock.GetUtcNow()) continue;
            var strategy = strategies.Find(payment.Method) ?? throw new InvalidOperationException("Payment has an unsupported method.");
            // A refund created before this column existed has no correlation ID, and an event needs one, so it falls back as expiry events do.
            await AttemptAsync(payment, refund, strategy, string.IsNullOrEmpty(refund.CorrelationId) ? item.OrderId.ToString("N") : refund.CorrelationId, null, ct);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            attempted++;
        }
        return attempted;
    }

    private Task<Payment?> LockPaymentAsync(Guid orderId, CancellationToken ct) =>
        db.Payments.FromSqlInterpolated($"SELECT * FROM dbo.Payments WITH (UPDLOCK, ROWLOCK) WHERE OrderId = {orderId}").SingleOrDefaultAsync(ct);

    private async Task<Refund?> RefundCoreAsync(Guid orderId, string idempotencyKey, string reason, string correlationId, Guid? causationId, CancellationToken ct)
    {
        var payment = await LockPaymentAsync(orderId, ct);
        if (payment is null) return null;
        var existing = await db.Refunds.SingleOrDefaultAsync(x => x.PaymentId == payment.Id, ct);
        if (existing is not null || payment.Status != PaymentStatus.Succeeded) return existing;
        var now = clock.GetUtcNow();
        if (payment.SettlementStatus == SettlementStatus.PendingCollection)
        {
            payment.VoidCollection(now);
            return null;
        }
        if (payment.SettlementStatus == SettlementStatus.Voided) return null;
        var strategy = strategies.Find(payment.Method) ?? throw new InvalidOperationException("Payment has an unsupported method.");
        payment.BeginRefund(now);
        var refund = Refund.Create(Guid.NewGuid(), payment, idempotencyKey, reason, now);
        refund.SetCorrelation(correlationId);
        db.Refunds.Add(refund);
        await AttemptAsync(payment, refund, strategy, correlationId, causationId, ct);
        return refund;
    }

    // Every attempt reuses the refund's identity, so a provider that already applied it can answer without paying twice.
    private async Task AttemptAsync(Payment payment, Refund refund, IPaymentStrategy strategy, string correlationId, Guid? causationId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        refund.RecordAttempt(now);
        var result = await strategy.RefundAsync(refund.Id, new PaymentAttempt(payment.Id, payment.OrderId, payment.Amount, payment.CurrencyCode), ct);
        if (result.Accepted)
        {
            refund.Complete(result.ProviderReference, now);
            payment.CompleteRefund(now);
            db.OutboxMessages.Add(OutboxMessage.ForEvent(EventTypes.RefundCompleted, payment.OrderId, correlationId, causationId,
                new RefundCompletedPayload(payment.OrderId, payment.Id, refund.Id, refund.Amount, refund.CurrencyCode, result.ProviderReference), now));
            return;
        }
        BusinessMetrics.RefundAttemptsFailed.Add(1);
        var options = refundOptions.Value;
        if (refund.AttemptCount < options.MaxAttempts)
        {
            refund.ScheduleRetry(result.ProviderReference, now.AddSeconds(options.RetryDelaySeconds), now);
            logger.LogWarning("Refund {RefundId} for order {OrderId} failed attempt {Attempt} of {MaxAttempts} and will retry", refund.Id, payment.OrderId, refund.AttemptCount, options.MaxAttempts);
            return;
        }
        refund.Fail(result.ProviderReference, now);
        payment.FailRefund(now);
        BusinessMetrics.RefundsQueuedForAdmin.Add(1);
        // This error log and the metric above are the alert; the Admin queue lists the refund until someone acts.
        logger.LogError("Refund {RefundId} for order {OrderId} failed after {Attempts} attempts and needs an Admin", refund.Id, payment.OrderId, refund.AttemptCount);
    }
}
