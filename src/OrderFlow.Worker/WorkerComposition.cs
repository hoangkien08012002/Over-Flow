using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using OrderFlow.Application.Inventory;
using OrderFlow.Application.Notifications;
using OrderFlow.Application.Payments;
using OrderFlow.Infrastructure.Messaging;
using OrderFlow.Infrastructure.Persistence;

namespace OrderFlow.Worker;

public static class WorkerComposition
{
    public static IServiceCollection AddOrderFlowWorker(this IServiceCollection services, IConfiguration configuration)
    {
        var connection = configuration.GetConnectionString("OrderFlow") ?? Environment.GetEnvironmentVariable("ORDERFLOW_SQL_CONNECTION")
            ?? throw new InvalidOperationException("ConnectionStrings:OrderFlow or ORDERFLOW_SQL_CONNECTION is required.");
        services.AddDbContext<OrderFlowDbContext>(options => options.UseSqlServer(connection));
        services.AddSingleton(TimeProvider.System);

        services.AddOptions<KafkaOptions>().Bind(configuration.GetSection("Kafka"))
            .Validate(x => !string.IsNullOrWhiteSpace(x.BootstrapServers), "Kafka:BootstrapServers is required.")
            .Validate(x => x.TopicPartitions >= 1 && x.ReplicationFactor >= 1, "Kafka topic settings must be positive.")
            .Validate(x => x.PublishTimeoutMilliseconds is >= 1000 and <= 60000, "Kafka publish timeout must be 1 to 60 seconds.")
            .ValidateOnStart();
        services.AddOptions<RetentionOptions>().Bind(configuration.GetSection("Retention"))
            .Validate(x => x.OutboxDays >= 1 && x.FailedMessageDays >= x.InboxDays, "Retention days are out of range; failure records must outlast Inbox rows.")
            .Validate(x => x.BatchSize is >= 1 and <= 10000 && x.SweepSeconds >= 1, "Retention batch or sweep settings are out of range.")
            .Validate<IOptions<KafkaOptions>>((x, kafka) => kafka.Value.TopicRetentionDays >= 1 && x.InboxDays > kafka.Value.TopicRetentionDays,
                "Retention:InboxDays must exceed Kafka:TopicRetentionDays, or a redelivered message could repeat its side effect.")
            .ValidateOnStart();
        services.AddOptions<OutboxOptions>().Bind(configuration.GetSection("Outbox"))
            .Validate(x => x.BatchSize is >= 1 and <= 1000 && x.PollIntervalMilliseconds >= 10, "Outbox batch or poll settings are out of range.")
            .Validate(x => x.BaseRetryMilliseconds >= 1 && x.MaxRetrySeconds >= 1, "Outbox retry settings must be positive.")
            .ValidateOnStart();
        services.AddOptions<ConsumerOptions>().Bind(configuration.GetSection("Consumers"))
            .Validate(x => x.MaxAttempts is >= 1 and <= 20, "Consumer attempts must be 1 to 20.")
            .Validate(x => x.BaseRetryMilliseconds >= 1 && x.MaxRetryMilliseconds >= x.BaseRetryMilliseconds, "Consumer retry settings are out of range.")
            .ValidateOnStart();
        services.AddOptions<InventoryOptions>().Bind(configuration.GetSection("Inventory"))
            .Validate(x => x.ReservationMinutes is >= 1 and <= 1440, "Inventory reservation lifetime must be 1 to 1440 minutes.")
            .ValidateOnStart();
        services.AddOptions<ExpiryOptions>().Bind(configuration.GetSection("ReservationExpiry"))
            .Validate(x => x.SweepSeconds >= 1 && x.BatchSize is >= 1 and <= 1000, "Reservation expiry settings are out of range.")
            .ValidateOnStart();

        services.AddSingleton(configuration.GetSection("Payments").Get<PaymentSimulationOptions>() ?? new PaymentSimulationOptions());
        services.AddSingleton<IPaymentStrategy, CodPaymentStrategy>();
        services.AddSingleton<IPaymentStrategy, BankTransferSimulationStrategy>();
        services.AddSingleton<IPaymentStrategy, CardSimulationStrategy>();
        services.AddSingleton<PaymentStrategyFactory>();
        services.AddSingleton<INotificationProvider, LogNotificationProvider>();

        services.AddSingleton<MessagingMetrics>();
        services.AddSingleton<IEventProducer, KafkaEventProducer>();
        services.AddSingleton<KafkaTopicProvisioner>();
        services.AddSingleton<EventConsumerRunner>();
        services.AddScoped<InventoryService>();
        services.AddScoped<IInventoryService>(sp => sp.GetRequiredService<InventoryService>());
        services.AddScoped<IReservationExpiryJob>(sp => sp.GetRequiredService<InventoryService>());
        services.AddScoped<PaymentService>();
        services.AddScoped<OutboxPublisher>();
        services.AddScoped<InboxProcessor>();
        services.AddScoped<FailedMessageReplayer>();
        services.AddScoped<MessagingRetention>();
        services.AddScoped<InventoryEventHandler>();
        services.AddScoped<OrderingEventHandler>();
        services.AddScoped<PaymentEventHandler>();
        services.AddScoped<NotificationEventHandler>();

        services.AddHostedService<OutboxPublisherService>();
        foreach (var definition in EventConsumers.All)
            services.AddSingleton<IHostedService>(sp => ActivatorUtilities.CreateInstance<EventConsumerService>(sp, definition));
        services.AddHostedService<ReservationExpiryService>();
        services.AddHostedService<FailedMessageReplayService>();
        services.AddHostedService<MessagingRetentionService>();
        return services;
    }
}
