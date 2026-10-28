namespace OrderFlow.Infrastructure.Persistence;

public sealed class OutboxMessage
{
    public Guid Id { get; set; }
    public string EventType { get; set; } = null!;
    public int EventVersion { get; set; }
    public Guid AggregateId { get; set; }
    public string CorrelationId { get; set; } = null!;
    public Guid? CausationId { get; set; }
    public string Payload { get; set; } = null!;
    public DateTimeOffset OccurredAtUtc { get; set; }
    public DateTimeOffset? ProcessedAtUtc { get; set; }
    public int AttemptCount { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset? NextAttemptAtUtc { get; set; }
    public DateTimeOffset? LockedUntilUtc { get; set; }
    public Guid? LockId { get; set; }
}
