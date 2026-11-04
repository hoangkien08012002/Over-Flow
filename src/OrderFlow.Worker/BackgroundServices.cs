using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrderFlow.Application.Inventory;
using OrderFlow.Infrastructure.Messaging;

namespace OrderFlow.Worker;

public sealed class ExpiryOptions
{
    public int SweepSeconds { get; set; } = 30;
    public int BatchSize { get; set; } = 100;
}

public sealed class OutboxPublisherService(IServiceScopeFactory scopes, IOptions<OutboxOptions> options, ILogger<OutboxPublisherService> logger) : BackgroundService
{
    private static readonly TimeSpan BacklogInterval = TimeSpan.FromSeconds(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        var nextBacklogRead = DateTimeOffset.MinValue;
        var pollInterval = TimeSpan.FromMilliseconds(options.Value.PollIntervalMilliseconds);
        while (!stoppingToken.IsCancellationRequested)
        {
            var fullBatch = false;
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var publisher = scope.ServiceProvider.GetRequiredService<OutboxPublisher>();
                var result = await publisher.PublishBatchAsync(stoppingToken);
                fullBatch = result.Published == options.Value.BatchSize;
                if (DateTimeOffset.UtcNow >= nextBacklogRead)
                {
                    var backlog = await publisher.ReadBacklogAsync(stoppingToken);
                    nextBacklogRead = DateTimeOffset.UtcNow.Add(BacklogInterval);
                    if (backlog.Count > 0) logger.LogInformation("Outbox backlog is {Count} messages, the oldest {AgeSeconds:F0} seconds old", backlog.Count, backlog.OldestAge.TotalSeconds);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { logger.LogError("Outbox polling failed: {ErrorType}", ex.GetType().Name); }
            if (!fullBatch) await DelayAsync(pollInterval, stoppingToken);
        }
    }

    private static async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try { await Task.Delay(delay, cancellationToken); }
        catch (OperationCanceledException) { }
    }
}

public sealed class EventConsumerService(ConsumerDefinition definition, EventConsumerRunner runner, ILogger<EventConsumerService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Consume blocks its thread, so each consumer gets its own pool thread rather than holding host startup.
                await Task.Run(() => runner.RunAsync(definition, stoppingToken), stoppingToken);
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                logger.LogError("Consumer {Consumer} stopped unexpectedly and will restart: {ErrorType}", definition.Name, ex.GetType().Name);
                try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
                catch (OperationCanceledException) { return; }
            }
        }
    }
}

public sealed class ReservationExpiryService(IServiceScopeFactory scopes, IOptions<ExpiryOptions> options, ILogger<ReservationExpiryService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.Value.SweepSeconds));
        try
        {
            do
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var expired = await scope.ServiceProvider.GetRequiredService<IReservationExpiryJob>().ExpireDueAsync(options.Value.BatchSize, stoppingToken);
                    if (expired > 0) logger.LogInformation("Expired {Count} inventory reservations", expired);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch (Exception ex) { logger.LogError("Reservation expiry sweep failed: {ErrorType}", ex.GetType().Name); }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) { }
    }
}

public sealed class MessagingRetentionService(IServiceScopeFactory scopes, IOptions<RetentionOptions> options, ILogger<MessagingRetentionService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.Value.SweepSeconds));
        try
        {
            do
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var result = await scope.ServiceProvider.GetRequiredService<MessagingRetention>().PurgeAsync(stoppingToken);
                    if (result.OutboxRows + result.InboxRows + result.FailedMessages > 0)
                        logger.LogInformation("Retention removed {OutboxRows} outbox rows, {InboxRows} Inbox rows, and {FailedMessages} failure records", result.OutboxRows, result.InboxRows, result.FailedMessages);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch (Exception ex) { logger.LogError("Retention sweep failed: {ErrorType}", ex.GetType().Name); }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) { }
    }
}

public sealed class FailedMessageReplayService(IServiceScopeFactory scopes, IOptions<ConsumerOptions> options, ILogger<FailedMessageReplayService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.Value.ReplayPollSeconds));
        try
        {
            do
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    await scope.ServiceProvider.GetRequiredService<FailedMessageReplayer>().ReplayRequestedAsync(50, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch (Exception ex) { logger.LogError("Failed-message replay poll failed: {ErrorType}", ex.GetType().Name); }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) { }
    }
}
