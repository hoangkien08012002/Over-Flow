using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrderFlow.Application.Messaging;
using OrderFlow.Infrastructure.Persistence;

namespace OrderFlow.Infrastructure.Messaging;

public sealed record OutboxBatchResult(int Claimed, int Published);
public sealed record OutboxBacklog(int Count, TimeSpan OldestAge);

public sealed class OutboxPublisher(OrderFlowDbContext db, IEventProducer producer, TimeProvider clock, IOptions<OutboxOptions> outboxOptions,
    IOptions<KafkaOptions> kafkaOptions, MessagingMetrics metrics, ILogger<OutboxPublisher> logger)
{
    private const int MaxErrorLength = 1000;
    private readonly OutboxOptions options = outboxOptions.Value;

    public async Task<OutboxBatchResult> PublishBatchAsync(CancellationToken cancellationToken)
    {
        var lockId = Guid.NewGuid();
        var rows = await ClaimAsync(lockId, cancellationToken);
        if (rows.Count == 0) return new OutboxBatchResult(0, 0);
        // Sends run together so one batch fits inside its lease even when every send times out during an outage.
        var outcomes = await Task.WhenAll(rows.Select(x => SendAsync(x, cancellationToken)));
        var published = 0;
        foreach (var (row, failure) in outcomes)
        {
            if (failure is null) { await MarkPublishedAsync(row, lockId, cancellationToken); published++; }
            else if (failure.Permanent) await ParkAsync(row, lockId, failure.Message, cancellationToken);
            else await ScheduleRetryAsync(row, lockId, failure.Message, cancellationToken);
        }
        return new OutboxBatchResult(rows.Count, published);
    }

    public async Task<OutboxBacklog> ReadBacklogAsync(CancellationToken cancellationToken)
    {
        var pending = db.OutboxMessages.AsNoTracking().Where(x => x.ProcessedAtUtc == null && x.ParkedAtUtc == null);
        var count = await pending.CountAsync(cancellationToken);
        var oldest = count == 0 ? (DateTimeOffset?)null : await pending.OrderBy(x => x.OccurredAtUtc).Select(x => (DateTimeOffset?)x.OccurredAtUtc).FirstOrDefaultAsync(cancellationToken);
        var age = oldest is null ? TimeSpan.Zero : clock.GetUtcNow() - oldest.Value;
        metrics.SetBacklog(count, (long)age.TotalSeconds);
        return new OutboxBacklog(count, age);
    }

    // READPAST lets each Worker skip rows another is claiming, and the lease returns rows held by a Worker that died.
    private async Task<List<OutboxMessage>> ClaimAsync(Guid lockId, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var lockedUntil = now.AddSeconds(options.LeaseSeconds);
        // READPAST is refused under Serializable, which a pooled connection can still carry, so the isolation level is set explicitly.
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var rows = await db.OutboxMessages.FromSqlInterpolated($"""
            WITH claimable AS (
                SELECT TOP ({options.BatchSize}) * FROM dbo.OutboxMessages WITH (UPDLOCK, READPAST, ROWLOCK)
                WHERE ProcessedAtUtc IS NULL AND ParkedAtUtc IS NULL
                  AND (NextAttemptAtUtc IS NULL OR NextAttemptAtUtc <= {now})
                  AND (LockedUntilUtc IS NULL OR LockedUntilUtc <= {now})
                ORDER BY OccurredAtUtc)
            UPDATE claimable SET LockId = {lockId}, LockedUntilUtc = {lockedUntil}, AttemptCount = AttemptCount + 1
            OUTPUT inserted.*
            """).AsNoTracking().ToListAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return rows.OrderBy(x => x.OccurredAtUtc).ToList();
    }

    private async Task<(OutboxMessage Row, EventPublishException? Failure)> SendAsync(OutboxMessage row, CancellationToken cancellationToken)
    {
        try
        {
            var topic = EventTopics.For(row.EventType) ?? throw new EventPublishException("No topic is mapped for this event type.", true);
            var envelope = ParseEnvelope(row);
            var headers = new Dictionary<string, string>
            {
                ["eventId"] = envelope.EventId.ToString(), ["eventType"] = envelope.EventType, ["eventVersion"] = envelope.EventVersion.ToString(),
                ["correlationId"] = envelope.CorrelationId
            };
            if (envelope.CausationId is Guid causationId) headers["causationId"] = causationId.ToString();
            await producer.PublishAsync(new OutgoingMessage(kafkaOptions.Value.Topic(topic), row.AggregateId.ToString(), row.Payload, headers), cancellationToken);
            return (row, null);
        }
        catch (EventPublishException ex) { return (row, ex); }
    }

    private static EventEnvelope ParseEnvelope(OutboxMessage row)
    {
        try
        {
            var envelope = EventSerializer.Parse(row.Payload);
            return envelope.EventId == row.Id ? envelope : throw new EventPublishException("Envelope EventId does not match the outbox row.", true);
        }
        catch (EventContractException ex) { throw new EventPublishException(ex.Message, true); }
    }

    private async Task MarkPublishedAsync(OutboxMessage row, Guid lockId, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        await db.OutboxMessages.Where(x => x.Id == row.Id && x.LockId == lockId).ExecuteUpdateAsync(setters => setters
            .SetProperty(x => x.ProcessedAtUtc, now).SetProperty(x => x.LockId, (Guid?)null).SetProperty(x => x.LockedUntilUtc, (DateTimeOffset?)null)
            .SetProperty(x => x.NextAttemptAtUtc, (DateTimeOffset?)null).SetProperty(x => x.LastError, (string?)null), cancellationToken);
        metrics.Published.Add(1);
        logger.LogInformation("Published {EventType} {EventId} for {AggregateId} on attempt {Attempt} (correlation {CorrelationId})",
            row.EventType, row.Id, row.AggregateId, row.AttemptCount, row.CorrelationId);
    }

    // A broker outage must not lose committed events, so retries continue at the capped interval; only a message that can never publish is parked.
    private async Task ScheduleRetryAsync(OutboxMessage row, Guid lockId, string error, CancellationToken cancellationToken)
    {
        var nextAttempt = clock.GetUtcNow().Add(NextDelay(row.AttemptCount));
        var safeError = Truncate(error);
        await db.OutboxMessages.Where(x => x.Id == row.Id && x.LockId == lockId).ExecuteUpdateAsync(setters => setters
            .SetProperty(x => x.NextAttemptAtUtc, nextAttempt).SetProperty(x => x.LastError, safeError)
            .SetProperty(x => x.LockId, (Guid?)null).SetProperty(x => x.LockedUntilUtc, (DateTimeOffset?)null), cancellationToken);
        metrics.PublishFailures.Add(1);
        logger.LogWarning("Publishing {EventType} {EventId} failed on attempt {Attempt}; next attempt at {NextAttemptAtUtc}: {Error}",
            row.EventType, row.Id, row.AttemptCount, nextAttempt, safeError);
    }

    private async Task ParkAsync(OutboxMessage row, Guid lockId, string error, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var safeError = Truncate(error);
        await db.OutboxMessages.Where(x => x.Id == row.Id && x.LockId == lockId).ExecuteUpdateAsync(setters => setters
            .SetProperty(x => x.ParkedAtUtc, now).SetProperty(x => x.LastError, safeError)
            .SetProperty(x => x.LockId, (Guid?)null).SetProperty(x => x.LockedUntilUtc, (DateTimeOffset?)null), cancellationToken);
        metrics.Parked.Add(1);
        logger.LogError("Parked {EventType} {EventId}, which cannot be published: {Error}", row.EventType, row.Id, safeError);
    }

    private TimeSpan NextDelay(int attempt)
    {
        var exponential = options.BaseRetryMilliseconds * Math.Pow(2, Math.Min(attempt - 1, 16));
        var capped = Math.Min(exponential, options.MaxRetrySeconds * 1000d);
        return TimeSpan.FromMilliseconds(capped * (0.5 + Random.Shared.NextDouble() / 2));
    }

    private static string Truncate(string value) => value.Length <= MaxErrorLength ? value : value[..MaxErrorLength];
}
