using System.Diagnostics.Metrics;
using System.Text;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrderFlow.Application.Messaging;

namespace OrderFlow.Infrastructure.Messaging;

public sealed class KafkaOptions
{
    public string BootstrapServers { get; set; } = "127.0.0.1:9092";
    public string TopicPrefix { get; set; } = "";
    public string ConsumerGroupPrefix { get; set; } = "orderflow-";
    public int TopicPartitions { get; set; } = 3;
    public short ReplicationFactor { get; set; } = 1;
    public int PublishTimeoutMilliseconds { get; set; } = 10000;
    public int TopicRetentionDays { get; set; } = 7;
    // Compose binds Kafka to 127.0.0.1 but advertises "localhost", so trying IPv6 first costs seconds on each connection.
    public bool Ipv4Only { get; set; } = true;
    public BrokerAddressFamily AddressFamily => Ipv4Only ? BrokerAddressFamily.V4 : BrokerAddressFamily.Any;
    public string Topic(string name) => TopicPrefix + name;
}

public sealed class OutboxOptions
{
    public int BatchSize { get; set; } = 50;
    public int PollIntervalMilliseconds { get; set; } = 500;
    public int LeaseSeconds { get; set; } = 60;
    public int BaseRetryMilliseconds { get; set; } = 1000;
    public int MaxRetrySeconds { get; set; } = 60;
}

public sealed class ConsumerOptions
{
    public int MaxAttempts { get; set; } = 5;
    public int BaseRetryMilliseconds { get; set; } = 200;
    public int MaxRetryMilliseconds { get; set; } = 5000;
    public int ReplayPollSeconds { get; set; } = 5;
}

public sealed class RetentionOptions
{
    public int OutboxDays { get; set; } = 7;
    // A message Kafka can still redeliver, or an operator can still replay, must find its Inbox row, so this outlasts both.
    public int InboxDays { get; set; } = 30;
    public int FailedMessageDays { get; set; } = 90;
    public int BatchSize { get; set; } = 1000;
    public int SweepSeconds { get; set; } = 3600;
}

public sealed record OutgoingMessage(string Topic, string? Key, string Value, IReadOnlyDictionary<string, string> Headers);

public sealed class EventPublishException(string safeError, bool permanent) : Exception(safeError)
{
    public bool Permanent { get; } = permanent;
}

public interface IEventProducer
{
    Task PublishAsync(OutgoingMessage message, CancellationToken cancellationToken);
}

public sealed class KafkaEventProducer : IEventProducer, IDisposable
{
    private readonly KafkaOptions options;
    private readonly ILogger<KafkaEventProducer> logger;
    private readonly object gate = new();
    private IProducer<string?, string> producer;

    public KafkaEventProducer(IOptions<KafkaOptions> options, ILogger<KafkaEventProducer> logger)
    {
        this.options = options.Value;
        this.logger = logger;
        producer = Build();
    }

    public async Task PublishAsync(OutgoingMessage message, CancellationToken cancellationToken)
    {
        var headers = new Headers();
        foreach (var (name, value) in message.Headers) headers.Add(name, Encoding.UTF8.GetBytes(value));
        var current = Volatile.Read(ref producer);
        try { await current.ProduceAsync(message.Topic, new Message<string?, string> { Key = message.Key, Value = message.Value, Headers = headers }, cancellationToken); }
        catch (ProduceException<string?, string> ex)
        {
            // An idempotent producer cannot recover from a fatal error, so it is replaced rather than left failing until restart.
            if (ex.Error.IsFatal) Replace(current);
            throw new EventPublishException(ex.Error.Reason, ex.Error.Code is ErrorCode.MsgSizeTooLarge or ErrorCode.InvalidMsg or ErrorCode.InvalidMsgSize);
        }
    }

    public void Dispose() => producer.Dispose();

    private void Replace(IProducer<string?, string> failed)
    {
        lock (gate)
        {
            if (!ReferenceEquals(producer, failed)) return;
            producer = Build();
        }
        failed.Dispose();
    }

    // Acks.All with idempotence keeps broker retries from duplicating or reordering; the timeout bounds each attempt so the lease is not outlived.
    private IProducer<string?, string> Build() => new ProducerBuilder<string?, string>(new ProducerConfig
    {
        BootstrapServers = options.BootstrapServers,
        BrokerAddressFamily = options.AddressFamily,
        Acks = Acks.All,
        EnableIdempotence = true,
        MessageTimeoutMs = options.PublishTimeoutMilliseconds,
        RequestTimeoutMs = Math.Min(options.PublishTimeoutMilliseconds, 5000)
    }).SetLogHandler((_, log) => logger.LogDebug("Kafka producer: {Message}", log.Message))
      .SetErrorHandler((_, error) => logger.LogWarning("Kafka producer error: {Reason}", error.Reason))
      .Build();
}

public sealed class KafkaTopicProvisioner(IOptions<KafkaOptions> options, ILogger<KafkaTopicProvisioner> logger)
{
    // Waits for the broker, so a Worker started during a Kafka outage begins consuming once it recovers.
    public async Task EnsureCreatedAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = settings.BootstrapServers, BrokerAddressFamily = settings.AddressFamily })
            .SetLogHandler((_, log) => logger.LogDebug("Kafka admin: {Message}", log.Message))
            .SetErrorHandler((_, error) => logger.LogDebug("Kafka admin error: {Reason}", error.Reason)).Build();
        var specifications = EventTopics.All.SelectMany(x => new[] { x, EventTopics.DeadLetter(x) })
            .Select(x => new TopicSpecification { Name = settings.Topic(x), NumPartitions = settings.TopicPartitions, ReplicationFactor = settings.ReplicationFactor,
                Configs = new Dictionary<string, string> { ["retention.ms"] = TimeSpan.FromDays(settings.TopicRetentionDays).TotalMilliseconds.ToString("F0") } }).ToList();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await admin.CreateTopicsAsync(specifications, new CreateTopicsOptions { RequestTimeout = TimeSpan.FromSeconds(5), OperationTimeout = TimeSpan.FromSeconds(5) });
                return;
            }
            catch (CreateTopicsException ex) when (ex.Results.All(x => x.Error.Code is ErrorCode.NoError or ErrorCode.TopicAlreadyExists)) { return; }
            catch (KafkaException ex)
            {
                logger.LogWarning("Kafka topics could not be provisioned yet: {Reason}", ex.Error.Reason);
                await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
            }
        }
    }
}

public sealed class MessagingMetrics : IDisposable
{
    private readonly Meter meter = new("OrderFlow.Messaging");
    private long backlog;
    private long oldestAgeSeconds;
    public Counter<long> Published { get; }
    public Counter<long> PublishFailures { get; }
    public Counter<long> Parked { get; }
    public Counter<long> Processed { get; }
    public Counter<long> Duplicates { get; }
    public Counter<long> Retries { get; }
    public Counter<long> DeadLettered { get; }

    public MessagingMetrics()
    {
        Published = meter.CreateCounter<long>("orderflow.outbox.published");
        PublishFailures = meter.CreateCounter<long>("orderflow.outbox.publish_failures");
        Parked = meter.CreateCounter<long>("orderflow.outbox.parked");
        Processed = meter.CreateCounter<long>("orderflow.consumer.processed");
        Duplicates = meter.CreateCounter<long>("orderflow.consumer.duplicates");
        Retries = meter.CreateCounter<long>("orderflow.consumer.retries");
        DeadLettered = meter.CreateCounter<long>("orderflow.consumer.dead_lettered");
        meter.CreateObservableGauge("orderflow.outbox.backlog", () => Volatile.Read(ref backlog));
        meter.CreateObservableGauge("orderflow.outbox.oldest_age_seconds", () => Volatile.Read(ref oldestAgeSeconds));
    }

    public void SetBacklog(long count, long oldestAge)
    {
        Volatile.Write(ref backlog, count);
        Volatile.Write(ref oldestAgeSeconds, oldestAge);
    }

    public void Dispose() => meter.Dispose();
}
