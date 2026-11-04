namespace OrderFlow.Domain.Payments;

public enum PaymentStatus { Pending, Processing, Succeeded, Failed, RefundPending, Refunded, RefundFailed }
public enum SettlementStatus { None, Settled, PendingCollection, Collected, Voided }
public enum RefundStatus { Pending, Succeeded, Failed }

public sealed class Payment
{
    private Payment() { }
    public Guid Id { get; private set; }
    public Guid OrderId { get; private set; }
    public string Method { get; private set; } = null!;
    public PaymentStatus Status { get; private set; }
    public SettlementStatus SettlementStatus { get; private set; }
    public decimal Amount { get; private set; }
    public string CurrencyCode { get; private set; } = null!;
    public string? ProviderReference { get; private set; }
    public string? ResultCode { get; private set; }
    public int AttemptCount { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset UpdatedAtUtc { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public static Payment Create(Guid id, Guid orderId, string method, decimal amount, string currencyCode, DateTimeOffset now)
    {
        if (id == Guid.Empty || orderId == Guid.Empty || string.IsNullOrWhiteSpace(method) || amount < 0 || now.Offset != TimeSpan.Zero)
            throw new ArgumentException("Invalid payment.");
        return new Payment { Id = id, OrderId = orderId, Method = method, Status = PaymentStatus.Pending, Amount = amount,
            CurrencyCode = currencyCode, CreatedAtUtc = now, UpdatedAtUtc = now };
    }

    public void Begin(DateTimeOffset now)
    {
        Require(PaymentStatus.Pending);
        Status = PaymentStatus.Processing;
        AttemptCount++;
        UpdatedAtUtc = now;
    }

    public void Succeed(string providerReference, string resultCode, bool defersSettlement, DateTimeOffset now)
    {
        Require(PaymentStatus.Processing);
        Status = PaymentStatus.Succeeded;
        SettlementStatus = defersSettlement ? SettlementStatus.PendingCollection : SettlementStatus.Settled;
        ProviderReference = providerReference;
        ResultCode = resultCode;
        UpdatedAtUtc = now;
    }

    public void Fail(string providerReference, string resultCode, DateTimeOffset now)
    {
        Require(PaymentStatus.Processing);
        Status = PaymentStatus.Failed;
        ProviderReference = providerReference;
        ResultCode = resultCode;
        UpdatedAtUtc = now;
    }

    // Nothing was collected for an accepted COD order, so cancelling it needs no refund.
    public void VoidCollection(DateTimeOffset now)
    {
        Require(PaymentStatus.Succeeded);
        if (SettlementStatus != SettlementStatus.PendingCollection) throw new InvalidOperationException("INVALID_STATE_TRANSITION");
        SettlementStatus = SettlementStatus.Voided;
        UpdatedAtUtc = now;
    }

    // Delivery can be reported twice, and a return can reach Payment before the delivery event does, so a settled payment stays as it is.
    public void MarkCollected(DateTimeOffset now)
    {
        if (SettlementStatus != SettlementStatus.PendingCollection) return;
        SettlementStatus = SettlementStatus.Collected;
        UpdatedAtUtc = now;
    }

    // An Admin sends a refund stuck in RefundFailed back for another attempt with the same idempotency key.
    public void RetryRefund(DateTimeOffset now) { Require(PaymentStatus.RefundFailed); Status = PaymentStatus.RefundPending; UpdatedAtUtc = now; }
    public void BeginRefund(DateTimeOffset now) { Require(PaymentStatus.Succeeded); Status = PaymentStatus.RefundPending; UpdatedAtUtc = now; }
    public void CompleteRefund(DateTimeOffset now) { Require(PaymentStatus.RefundPending); Status = PaymentStatus.Refunded; UpdatedAtUtc = now; }
    public void FailRefund(DateTimeOffset now) { Require(PaymentStatus.RefundPending); Status = PaymentStatus.RefundFailed; UpdatedAtUtc = now; }

    private void Require(PaymentStatus expected)
    {
        if (Status != expected) throw new InvalidOperationException("INVALID_STATE_TRANSITION");
    }
}

public sealed class Refund
{
    private Refund() { }
    public Guid Id { get; private set; }
    public Guid PaymentId { get; private set; }
    public Guid OrderId { get; private set; }
    public RefundStatus Status { get; private set; }
    public decimal Amount { get; private set; }
    public string CurrencyCode { get; private set; } = null!;
    public string Reason { get; private set; } = null!;
    public string? ProviderReference { get; private set; }
    public string IdempotencyKey { get; private set; } = null!;
    public int AttemptCount { get; private set; }
    public DateTimeOffset? NextAttemptAtUtc { get; private set; }
    public string CorrelationId { get; private set; } = "";
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public void SetCorrelation(string correlationId) => CorrelationId = correlationId;

    public void RecordAttempt(DateTimeOffset now)
    {
        if (Status != RefundStatus.Pending) throw new InvalidOperationException("INVALID_STATE_TRANSITION");
        AttemptCount++;
        NextAttemptAtUtc = null;
        UpdatedAtUtc = now;
    }

    public void ScheduleRetry(string providerReference, DateTimeOffset nextAttemptAtUtc, DateTimeOffset now)
    {
        if (Status != RefundStatus.Pending) throw new InvalidOperationException("INVALID_STATE_TRANSITION");
        ProviderReference = providerReference;
        NextAttemptAtUtc = nextAttemptAtUtc;
        UpdatedAtUtc = now;
    }

    public void Reopen(DateTimeOffset now)
    {
        if (Status != RefundStatus.Failed) throw new InvalidOperationException("INVALID_STATE_TRANSITION");
        Status = RefundStatus.Pending;
        AttemptCount = 0;
        NextAttemptAtUtc = now;
        UpdatedAtUtc = now;
    }

    public static Refund Create(Guid id, Payment payment, string idempotencyKey, string reason, DateTimeOffset now)
    {
        if (id == Guid.Empty || string.IsNullOrWhiteSpace(idempotencyKey) || string.IsNullOrWhiteSpace(reason) || now.Offset != TimeSpan.Zero)
            throw new ArgumentException("Invalid refund.");
        return new Refund { Id = id, PaymentId = payment.Id, OrderId = payment.OrderId, Status = RefundStatus.Pending, Amount = payment.Amount,
            CurrencyCode = payment.CurrencyCode, Reason = reason, IdempotencyKey = idempotencyKey, CreatedAtUtc = now, UpdatedAtUtc = now };
    }

    public void Complete(string providerReference, DateTimeOffset now) => Settle(RefundStatus.Succeeded, providerReference, now);
    public void Fail(string providerReference, DateTimeOffset now) => Settle(RefundStatus.Failed, providerReference, now);

    private void Settle(RefundStatus status, string providerReference, DateTimeOffset now)
    {
        if (Status != RefundStatus.Pending) throw new InvalidOperationException("INVALID_STATE_TRANSITION");
        Status = status;
        ProviderReference = providerReference;
        UpdatedAtUtc = now;
    }
}
