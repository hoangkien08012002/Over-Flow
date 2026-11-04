using System.Text;
using Confluent.Kafka;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrderFlow.Application.Messaging;
using OrderFlow.Infrastructure.Persistence;

namespace OrderFlow.Infrastructure.Messaging;

public interface IEventHandler
{
    Task HandleAsync(EventEnvelope envelope, CancellationToken cancellationToken);
}

public sealed record ConsumerDefinition(string Name, Type HandlerType, IReadOnlyList<string> EventTypes)
{
    public IEnumerable<string> Topics => EventTypes.Select(x => EventTopics.For(x)!);
}

public static class EventConsumers
{
    public static readonly IReadOnlyList<ConsumerDefinition> All =
    [
        new("inventory", typeof(InventoryEventHandler), [EventTypes.OrderCreated, EventTypes.OrderCancelled, EventTypes.PaymentSucceeded, EventTypes.PaymentFailed]),
        new("ordering", typeof(OrderingEventHandler), [EventTypes.InventoryReserved, EventTypes.InventoryRejected, EventTypes.InventoryCommitted, EventTypes.InventoryReleased, EventTypes.PaymentSucceeded, EventTypes.PaymentFailed, EventTypes.ShipmentStatusChanged, EventTypes.RefundCompleted]),
        new("payment", typeof(PaymentEventHandler), [EventTypes.PaymentRequested, EventTypes.OrderCancelled, EventTypes.ShipmentStatusChanged, EventTypes.RefundRequested]),
        new("returns", typeof(ReturnEventHandler), [EventTypes.RefundCompleted]),
        new("notifications", typeof(NotificationEventHandler), [EventTypes.InventoryRejected, EventTypes.PaymentSucceeded, EventTypes.PaymentFailed, EventTypes.OrderCancelled, EventTypes.RefundCompleted, EventTypes.InventoryLow, EventTypes.ShipmentStatusChanged])
    ];
}

public sealed class InboxProcessor(OrderFlowDbContext db, TimeProvider clock)
{
    // Returns false when this consumer has already committed the event. The Inbox row, side effect, and next outbox rows commit together.
    public async Task<bool> ProcessAsync(string consumerName, IEventHandler handler, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (await db.InboxMessages.AsNoTracking().AnyAsync(x => x.ConsumerName == consumerName && x.EventId == envelope.EventId, cancellationToken)) return false;
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, cancellationToken);
        var inbox = new InboxMessage { ConsumerName = consumerName, EventId = envelope.EventId, EventType = envelope.EventType, ReceivedAtUtc = clock.GetUtcNow() };
        db.InboxMessages.Add(inbox);
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 }) { return false; }
        await handler.HandleAsync(envelope, cancellationToken);
        inbox.ProcessedAtUtc = clock.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }
}

public sealed class EventConsumerRunner(IServiceScopeFactory scopes, IEventProducer producer, KafkaTopicProvisioner provisioner, TimeProvider clock,
    IOptions<KafkaOptions> kafkaOptions, IOptions<ConsumerOptions> consumerOptions, MessagingMetrics metrics, ILogger<EventConsumerRunner> logger)
{
    private readonly KafkaOptions kafka = kafkaOptions.Value;
    private readonly ConsumerOptions options = consumerOptions.Value;

    public async Task RunAsync(ConsumerDefinition definition, CancellationToken cancellationToken)
    {
        await provisioner.EnsureCreatedAsync(cancellationToken);
        using var consumer = new ConsumerBuilder<string?, byte[]>(new ConsumerConfig
        {
            BootstrapServers = kafka.BootstrapServers,
            BrokerAddressFamily = kafka.AddressFamily,
            GroupId = kafka.ConsumerGroupPrefix + definition.Name,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            // Offsets advance only after the SQL commit, so a crash redelivers and the Inbox absorbs the repeat.
            EnableAutoCommit = false
        }).SetLogHandler((_, log) => logger.LogDebug("Kafka consumer {Consumer}: {Message}", definition.Name, log.Message))
          .SetErrorHandler((_, error) => logger.LogWarning("Kafka consumer {Consumer} error: {Reason}", definition.Name, error.Reason)).Build();
        consumer.Subscribe(definition.Topics.Select(kafka.Topic));
        logger.LogInformation("Consumer {Consumer} subscribed to {Topics}", definition.Name, string.Join(", ", definition.Topics));
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                ConsumeResult<string?, byte[]> message;
                try { message = consumer.Consume(cancellationToken); }
                catch (ConsumeException ex) when (!ex.Error.IsFatal)
                {
                    logger.LogWarning("Consumer {Consumer} could not read: {Reason}", definition.Name, ex.Error.Reason);
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                    continue;
                }
                if (await SettleAsync(definition, message, cancellationToken)) Acknowledge(consumer, definition, message);
                else
                {
                    // SQL could not record the outcome. Without an offset commit the message is read again once SQL recovers.
                    consumer.Seek(message.TopicPartitionOffset);
                    await Task.Delay(options.MaxRetryMilliseconds, cancellationToken);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally { consumer.Close(); }
    }

    private void Acknowledge(IConsumer<string?, byte[]> consumer, ConsumerDefinition definition, ConsumeResult<string?, byte[]> message)
    {
        try { consumer.Commit(message); }
        catch (KafkaException ex) { logger.LogWarning("Consumer {Consumer} could not commit an offset, so the message may repeat: {Reason}", definition.Name, ex.Error.Reason); }
    }

    private async Task<bool> SettleAsync(ConsumerDefinition definition, ConsumeResult<string?, byte[]> message, CancellationToken cancellationToken)
    {
        var raw = Encoding.UTF8.GetString(message.Message.Value ?? []);
        EventEnvelope envelope;
        try
        {
            envelope = EventSerializer.Parse(raw);
            if (!definition.EventTypes.Contains(envelope.EventType)) throw new EventContractException($"Consumer does not handle {envelope.EventType}.");
        }
        catch (EventContractException ex) { return await RecordFailureAsync(definition, message, raw, null, 1, ex.Message, cancellationToken); }

        using var logScope = logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = envelope.CorrelationId, ["EventId"] = envelope.EventId, ["Consumer"] = definition.Name });
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var handler = (IEventHandler)scope.ServiceProvider.GetRequiredService(definition.HandlerType);
                var applied = await scope.ServiceProvider.GetRequiredService<InboxProcessor>().ProcessAsync(definition.Name, handler, envelope, cancellationToken);
                (applied ? metrics.Processed : metrics.Duplicates).Add(1);
                logger.LogInformation("Consumer {Consumer} {Outcome} {EventType} {EventId} for {AggregateId}", definition.Name, applied ? "processed" : "skipped duplicate", envelope.EventType, envelope.EventId, envelope.AggregateId);
                return true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (EventContractException ex) { return await RecordFailureAsync(definition, message, raw, envelope, attempt, ex.Message, cancellationToken); }
            catch (Exception ex)
            {
                var summary = SafeSummary(ex);
                if (attempt >= options.MaxAttempts) return await RecordFailureAsync(definition, message, raw, envelope, attempt, summary, cancellationToken);
                metrics.Retries.Add(1);
                var delay = RetryDelay(attempt);
                logger.LogWarning("Consumer {Consumer} attempt {Attempt} failed for {EventType} {EventId}, retrying in {DelayMs} ms: {Error}", definition.Name, attempt, envelope.EventType, envelope.EventId, (int)delay.TotalMilliseconds, summary);
                await Task.Delay(delay, cancellationToken);
            }
        }
    }

    private async Task<bool> RecordFailureAsync(ConsumerDefinition definition, ConsumeResult<string?, byte[]> message, string raw, EventEnvelope? envelope, int attempts, string summary, CancellationToken cancellationToken)
    {
        var failed = new FailedMessage
        {
            Id = Guid.NewGuid(), ConsumerName = definition.Name, SourceTopic = message.Topic, Partition = message.Partition.Value, Offset = message.Offset.Value,
            EventId = envelope?.EventId, EventType = envelope?.EventType, MessageKey = message.Message.Key, RawMessage = raw, AttemptCount = attempts,
            ErrorSummary = summary.Length <= 1000 ? summary : summary[..1000], FailedAtUtc = clock.GetUtcNow()
        };
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<OrderFlowDbContext>();
            db.FailedMessages.Add(failed);
            try { await db.SaveChangesAsync(cancellationToken); }
            catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 })
            {
                logger.LogInformation("Failure for {Topic}/{Partition}/{Offset} was already recorded", message.Topic, message.Partition.Value, message.Offset.Value);
                return true;
            }
            metrics.DeadLettered.Add(1);
            logger.LogError("Consumer {Consumer} gave up on {Topic}/{Partition}/{Offset} ({EventType} {EventId}) after {Attempts} attempts: {Error}",
                definition.Name, message.Topic, message.Partition.Value, message.Offset.Value, envelope?.EventType, envelope?.EventId, attempts, failed.ErrorSummary);
            // SQL holds the durable record and the replay source; the DLQ topic is the operational feed, so a broker failure here does not block the partition.
            try
            {
                await producer.PublishAsync(new OutgoingMessage(EventTopics.DeadLetter(message.Topic), message.Message.Key, raw, new Dictionary<string, string>
                {
                    ["sourceTopic"] = message.Topic, ["sourcePartition"] = message.Partition.Value.ToString(), ["sourceOffset"] = message.Offset.Value.ToString(),
                    ["consumer"] = definition.Name, ["attemptCount"] = attempts.ToString(), ["error"] = failed.ErrorSummary
                }), cancellationToken);
                var now = clock.GetUtcNow();
                await db.FailedMessages.Where(x => x.Id == failed.Id).ExecuteUpdateAsync(setters => setters.SetProperty(x => x.DeadLetteredAtUtc, now), cancellationToken);
            }
            catch (EventPublishException ex) { logger.LogWarning("DLQ publish failed for failure record {FailedMessageId}: {Error}", failed.Id, ex.Message); }
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogError("Consumer {Consumer} could not record a failed message: {Error}", definition.Name, SafeSummary(ex));
            return false;
        }
    }

    private TimeSpan RetryDelay(int attempt)
    {
        var exponential = options.BaseRetryMilliseconds * Math.Pow(2, attempt - 1);
        return TimeSpan.FromMilliseconds(Math.Min(exponential, options.MaxRetryMilliseconds) * (0.5 + Random.Shared.NextDouble() / 2));
    }

    // Exception messages can quote values, so stored diagnostics keep only the type and, for SQL, its error number.
    private static string SafeSummary(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
            if (current is SqlException sql) return $"{exception.GetType().Name}: SQL error {sql.Number}";
        return exception.GetType().Name;
    }
}

public sealed class FailedMessageReplayer(OrderFlowDbContext db, IEventProducer producer, TimeProvider clock, ILogger<FailedMessageReplayer> logger)
{
    // The original bytes return to the source topic, so EventId is kept and every consumer's Inbox still applies.
    public async Task<int> ReplayRequestedAsync(int batchSize, CancellationToken cancellationToken)
    {
        var requested = await db.FailedMessages.AsNoTracking().Where(x => x.Status == FailedMessageStatus.ReplayRequested)
            .OrderBy(x => x.ReplayRequestedAtUtc).Take(batchSize).ToListAsync(cancellationToken);
        var replayed = 0;
        foreach (var message in requested)
        {
            try { await producer.PublishAsync(new OutgoingMessage(message.SourceTopic, message.MessageKey, message.RawMessage, new Dictionary<string, string> { ["replayOf"] = message.Id.ToString() }), cancellationToken); }
            catch (EventPublishException ex)
            {
                logger.LogWarning("Replay of failed message {FailedMessageId} will be retried: {Error}", message.Id, ex.Message);
                continue;
            }
            var now = clock.GetUtcNow();
            await db.FailedMessages.Where(x => x.Id == message.Id && x.ReplayRequestedAtUtc == message.ReplayRequestedAtUtc).ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Status, FailedMessageStatus.Replayed).SetProperty(x => x.ReplayedAtUtc, now), cancellationToken);
            logger.LogInformation("Replayed failed message {FailedMessageId} ({EventType} {EventId}) to {Topic}", message.Id, message.EventType, message.EventId, message.SourceTopic);
            replayed++;
        }
        return replayed;
    }
}
