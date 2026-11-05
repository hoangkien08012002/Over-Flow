using System.Diagnostics.Metrics;

namespace OrderFlow.Infrastructure.Persistence;

// Business outcomes that the cache and messaging meters do not see. Exported with the others when telemetry is enabled.
public static class BusinessMetrics
{
    public const string MeterName = "OrderFlow.Business";
    private static readonly Meter Meter = new(MeterName);
    public static readonly Counter<long> OrdersCreated = Meter.CreateCounter<long>("orderflow.orders.created");
    public static readonly Counter<long> OrdersCancelled = Meter.CreateCounter<long>("orderflow.orders.cancelled");
    public static readonly Counter<long> PaymentOutcomes = Meter.CreateCounter<long>("orderflow.payments.outcomes");
    public static readonly Counter<long> InventoryRejections = Meter.CreateCounter<long>("orderflow.inventory.rejections");
    public static readonly Counter<long> InventoryConflicts = Meter.CreateCounter<long>("orderflow.inventory.conflicts");
    public static readonly Counter<long> RefundAttemptsFailed = Meter.CreateCounter<long>("orderflow.refunds.attempts_failed");
    public static readonly Counter<long> RefundsQueuedForAdmin = Meter.CreateCounter<long>("orderflow.refunds.queued_for_admin");
}
