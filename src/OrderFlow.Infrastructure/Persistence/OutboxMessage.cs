using OrderFlow.Application.Messaging;

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
    public DateTimeOffset? ParkedAtUtc { get; set; }

    public static OutboxMessage ForEvent<T>(string eventType, Guid aggregateId, string correlationId, Guid? causationId, T payload, DateTimeOffset now)
    {
        var envelope = EventSerializer.Create(eventType, aggregateId, correlationId, causationId, payload, now);
        return new OutboxMessage { Id = envelope.EventId, EventType = eventType, EventVersion = envelope.EventVersion, AggregateId = aggregateId,
            CorrelationId = correlationId, CausationId = causationId, OccurredAtUtc = now, Payload = EventSerializer.Serialize(envelope) };
    }
}
