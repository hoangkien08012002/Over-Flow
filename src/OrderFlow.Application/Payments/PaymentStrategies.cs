namespace OrderFlow.Application.Payments;

public sealed record PaymentAttempt(Guid PaymentId, Guid OrderId, decimal Amount, string CurrencyCode);
public sealed record ProviderResult(bool Accepted, string ProviderReference, string ResultCode);

public interface IPaymentStrategy
{
    string Method { get; }
    // COD is accepted now and collected on delivery, so nothing has been settled yet.
    bool DefersSettlement { get; }
    Task<ProviderResult> ChargeAsync(PaymentAttempt attempt, CancellationToken cancellationToken);
    Task<ProviderResult> RefundAsync(Guid refundId, PaymentAttempt attempt, CancellationToken cancellationToken);
}

public sealed class PaymentSimulationOptions
{
    // An amount ending in these cents is declined, which makes a failed payment reproducible without a client-supplied switch.
    public int DeclineCents { get; set; } = 13;
    // A refund of an amount ending in these cents fails, which exercises the retry and Admin queue path. Off by default.
    public int? RefundFailCents { get; set; }
}

public sealed class CodPaymentStrategy : IPaymentStrategy
{
    public string Method => "COD";
    public bool DefersSettlement => true;
    public Task<ProviderResult> ChargeAsync(PaymentAttempt attempt, CancellationToken cancellationToken) =>
        Task.FromResult(new ProviderResult(true, $"COD-{attempt.PaymentId:N}", "COD_ACCEPTED"));
    public Task<ProviderResult> RefundAsync(Guid refundId, PaymentAttempt attempt, CancellationToken cancellationToken) =>
        Task.FromResult(new ProviderResult(true, $"COD-REFUND-{refundId:N}", "REFUNDED"));
}

public abstract class SimulatedPaymentStrategy(PaymentSimulationOptions options, string prefix, string declineCode) : IPaymentStrategy
{
    public abstract string Method { get; }
    public bool DefersSettlement => false;

    // The reference derives from a stable ID, so re-running the simulation after a rollback yields the same provider identity.
    public Task<ProviderResult> ChargeAsync(PaymentAttempt attempt, CancellationToken cancellationToken) =>
        Task.FromResult(decimal.Truncate(attempt.Amount % 1 * 100) == options.DeclineCents
            ? new ProviderResult(false, $"{prefix}-{attempt.PaymentId:N}", declineCode)
            : new ProviderResult(true, $"{prefix}-{attempt.PaymentId:N}", "APPROVED"));

    public Task<ProviderResult> RefundAsync(Guid refundId, PaymentAttempt attempt, CancellationToken cancellationToken) =>
        Task.FromResult(decimal.Truncate(attempt.Amount % 1 * 100) == options.RefundFailCents
            ? new ProviderResult(false, $"{prefix}-REFUND-{refundId:N}", "REFUND_REJECTED")
            : new ProviderResult(true, $"{prefix}-REFUND-{refundId:N}", "REFUNDED"));
}

public sealed class BankTransferSimulationStrategy(PaymentSimulationOptions options) : SimulatedPaymentStrategy(options, "BANK", "BANK_TRANSFER_REJECTED")
{
    public override string Method => "BankTransferSimulation";
}

public sealed class CardSimulationStrategy(PaymentSimulationOptions options) : SimulatedPaymentStrategy(options, "CARD", "CARD_DECLINED")
{
    public override string Method => "CardSimulation";
}

public sealed class PaymentStrategyFactory(IEnumerable<IPaymentStrategy> strategies)
{
    private readonly Dictionary<string, IPaymentStrategy> byMethod = strategies.ToDictionary(x => x.Method, StringComparer.Ordinal);
    public IPaymentStrategy? Find(string method) => byMethod.GetValueOrDefault(method);
}
