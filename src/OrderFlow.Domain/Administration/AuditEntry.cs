namespace OrderFlow.Domain.Administration;

public sealed class AuditEntry
{
    private AuditEntry() { }
    public Guid Id { get; private set; }
    public Guid? ActorUserId { get; private set; }
    public string Action { get; private set; } = null!;
    public string TargetType { get; private set; } = null!;
    public string? TargetId { get; private set; }
    public int ResponseStatus { get; private set; }
    public string? Reason { get; private set; }
    public string CorrelationId { get; private set; } = null!;
    public DateTimeOffset OccurredAtUtc { get; private set; }

    public static AuditEntry Create(Guid? actorUserId, string action, string targetType, string? targetId, int responseStatus, string? reason, string correlationId, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(action) || string.IsNullOrWhiteSpace(targetType) || string.IsNullOrWhiteSpace(correlationId) || now.Offset != TimeSpan.Zero)
            throw new ArgumentException("Invalid audit entry.");
        return new AuditEntry { Id = Guid.NewGuid(), ActorUserId = actorUserId, Action = action, TargetType = targetType, TargetId = targetId, ResponseStatus = responseStatus,
            Reason = reason?.Length > 500 ? reason[..500] : reason, CorrelationId = correlationId, OccurredAtUtc = now };
    }
}
