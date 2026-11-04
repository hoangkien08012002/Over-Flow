using System.Data;
using Microsoft.EntityFrameworkCore;
using OrderFlow.Application.Messaging;
using OrderFlow.Application.Payments;
using OrderFlow.Domain.Ordering;
using OrderFlow.Domain.Payments;

namespace OrderFlow.Infrastructure.Persistence;

public sealed class PaymentService(OrderFlowDbContext db, PaymentStrategyFactory strategies, TimeProvider clock)
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

    private async Task<Refund?> RefundCoreAsync(Guid orderId, string idempotencyKey, string reason, string correlationId, Guid? causationId, CancellationToken ct)
    {
        var payment = await db.Payments.FromSqlInterpolated($"SELECT * FROM dbo.Payments WITH (UPDLOCK, ROWLOCK) WHERE OrderId = {orderId}").SingleOrDefaultAsync(ct);
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
        db.Refunds.Add(refund);
        var result = await strategy.RefundAsync(refund.Id, new PaymentAttempt(payment.Id, orderId, payment.Amount, payment.CurrencyCode), ct);
        if (!result.Accepted)
        {
            refund.Fail(result.ProviderReference, now);
            payment.FailRefund(now);
            return refund;
        }
        refund.Complete(result.ProviderReference, now);
        payment.CompleteRefund(now);
        db.OutboxMessages.Add(OutboxMessage.ForEvent(EventTypes.RefundCompleted, orderId, correlationId, causationId,
            new RefundCompletedPayload(orderId, payment.Id, refund.Id, refund.Amount, refund.CurrencyCode, result.ProviderReference), now));
        return refund;
    }
}
