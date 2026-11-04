using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OrderFlow.Application.Messaging;
using OrderFlow.Domain.Ordering;
using OrderFlow.Infrastructure.Messaging;
using OrderFlow.Infrastructure.Persistence;
using Xunit;

namespace OrderFlow.IntegrationTests;

public sealed class OutboxPublisherTests
{
    private static Dictionary<string, string?> Settings(int batch = 50, int lease = 60) => new()
    {
        ["Outbox:BatchSize"] = batch.ToString(), ["Outbox:LeaseSeconds"] = lease.ToString()
    };

    private static async Task<List<Guid>> SeedOutboxAsync(MessagingHarness harness, int count)
    {
        await using var db = harness.NewDb();
        var ids = new List<Guid>();
        for (var i = 0; i < count; i++)
        {
            var row = OutboxMessage.ForEvent(EventTypes.InventoryLow, Guid.NewGuid(), "outbox-test", null, new InventoryLowPayload(Guid.NewGuid(), 1, 2), DateTimeOffset.UtcNow.AddMilliseconds(i));
            db.OutboxMessages.Add(row);
            ids.Add(row.Id);
        }
        await db.SaveChangesAsync(MessagingHarness.Ct);
        return ids;
    }

    private static async Task<OutboxBatchResult> PublishOnceAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<OutboxPublisher>().PublishBatchAsync(MessagingHarness.Ct);
    }

    [Fact]
    public async Task AC_03_failed_publish_keeps_the_event_with_retry_metadata_and_recovers()
    {
        await using var harness = await MessagingHarness.CreateAsync();
        var product = await harness!.SeedProductAsync(10m, 5);
        var orderId = await harness.CheckoutAsync(product, 1, "COD");
        var producer = new RecordingProducer { Failure = (_, attempt) => attempt == 1 ? new EventPublishException("Broker unavailable", false) : null };
        using var worker = harness.BuildWorker(x => x.Replace(ServiceDescriptor.Singleton<IEventProducer>(producer)));

        Assert.Equal(new OutboxBatchResult(1, 0), await PublishOnceAsync(worker.Services));
        await using (var db = harness.NewDb())
        {
            // The order committed although the broker refused the event.
            Assert.Equal(OrderStatus.PendingInventory, (await db.Orders.AsNoTracking().SingleAsync(x => x.Id == orderId, MessagingHarness.Ct)).Status);
            var row = await db.OutboxMessages.AsNoTracking().SingleAsync(x => x.AggregateId == orderId, MessagingHarness.Ct);
            Assert.Null(row.ProcessedAtUtc);
            Assert.Equal(1, row.AttemptCount);
            Assert.Equal("Broker unavailable", row.LastError);
            Assert.NotNull(row.NextAttemptAtUtc);
            Assert.Null(row.LockId);
        }

        await harness.WaitForAsync(db => db.OutboxMessages.AsNoTracking().Where(x => x.AggregateId == orderId).Select(x => x.NextAttemptAtUtc).SingleAsync(MessagingHarness.Ct),
            x => x <= DateTimeOffset.UtcNow, "retry backoff", 10);
        Assert.Equal(new OutboxBatchResult(1, 1), await PublishOnceAsync(worker.Services));
        await using (var db = harness.NewDb())
        {
            var row = await db.OutboxMessages.AsNoTracking().SingleAsync(x => x.AggregateId == orderId, MessagingHarness.Ct);
            Assert.NotNull(row.ProcessedAtUtc);
            Assert.Equal(2, row.AttemptCount);
            Assert.Null(row.LastError);
        }
        var message = producer.Messages[^1];
        Assert.Equal(orderId.ToString(), message.Key);
        Assert.EndsWith("order.created", message.Topic);
        Assert.Equal("messaging-test", message.Headers["correlationId"]);
    }

    [Fact]
    public async Task Crash_after_broker_ack_republishes_the_same_event_rather_than_losing_it()
    {
        await using var harness = await MessagingHarness.CreateAsync();
        var ids = await SeedOutboxAsync(harness!, 1);
        var producer = new RecordingProducer();
        using var worker = harness!.BuildWorker(x => x.Replace(ServiceDescriptor.Singleton<IEventProducer>(producer)));

        // A lease that has lapsed with no ProcessedAtUtc is exactly what a Worker dying after the broker ack leaves behind.
        await using (var db = harness.NewDb())
            await db.OutboxMessages.Where(x => x.Id == ids[0]).ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.LockId, Guid.NewGuid()).SetProperty(x => x.AttemptCount, 1)
                .SetProperty(x => x.LockedUntilUtc, DateTimeOffset.UtcNow.AddSeconds(-1)), MessagingHarness.Ct);

        Assert.Equal(new OutboxBatchResult(1, 1), await PublishOnceAsync(worker.Services));
        Assert.Equal(ids[0].ToString(), producer.Messages.Single().Headers["eventId"]);
        await using var verify = harness.NewDb();
        Assert.NotNull((await verify.OutboxMessages.AsNoTracking().SingleAsync(MessagingHarness.Ct)).ProcessedAtUtc);
    }

    [Fact]
    public async Task An_unexpired_lease_is_not_claimed_by_another_worker()
    {
        await using var harness = await MessagingHarness.CreateAsync();
        var ids = await SeedOutboxAsync(harness!, 1);
        var producer = new RecordingProducer();
        using var worker = harness!.BuildWorker(x => x.Replace(ServiceDescriptor.Singleton<IEventProducer>(producer)));
        await using (var db = harness.NewDb())
            await db.OutboxMessages.Where(x => x.Id == ids[0]).ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.LockId, Guid.NewGuid()).SetProperty(x => x.LockedUntilUtc, DateTimeOffset.UtcNow.AddMinutes(1)), MessagingHarness.Ct);

        Assert.Equal(new OutboxBatchResult(0, 0), await PublishOnceAsync(worker.Services));
        Assert.Empty(producer.Messages);
    }

    [Fact]
    public async Task Concurrent_workers_publish_each_row_once()
    {
        await using var harness = await MessagingHarness.CreateAsync();
        var ids = await SeedOutboxAsync(harness!, 120);
        var producer = new RecordingProducer();
        using var worker = harness!.BuildWorker(x => x.Replace(ServiceDescriptor.Singleton<IEventProducer>(producer)), Settings(batch: 7));

        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            while ((await PublishOnceAsync(worker.Services)).Claimed > 0) { }
        }, MessagingHarness.Ct)));
        // A worker can stop when it sees only rows others hold, so finish whatever remains.
        while ((await PublishOnceAsync(worker.Services)).Claimed > 0) { }

        Assert.Equal(ids.Select(x => x.ToString()).Order(), producer.Messages.Select(x => x.Headers["eventId"]).Order());
    }

    [Fact]
    public async Task A_message_that_can_never_publish_is_parked_and_leaves_the_backlog()
    {
        await using var harness = await MessagingHarness.CreateAsync();
        var producer = new RecordingProducer();
        using var worker = harness!.BuildWorker(x => x.Replace(ServiceDescriptor.Singleton<IEventProducer>(producer)));
        var id = Guid.NewGuid();
        await using (var db = harness.NewDb())
        {
            db.OutboxMessages.Add(new OutboxMessage { Id = id, EventType = "NotARealEvent", EventVersion = 1, AggregateId = Guid.NewGuid(),
                CorrelationId = "outbox-test", Payload = "{}", OccurredAtUtc = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync(MessagingHarness.Ct);
        }

        Assert.Equal(new OutboxBatchResult(1, 0), await PublishOnceAsync(worker.Services));
        Assert.Equal(new OutboxBatchResult(0, 0), await PublishOnceAsync(worker.Services));
        Assert.Empty(producer.Messages);
        await using var verify = harness.NewDb();
        var row = await verify.OutboxMessages.AsNoTracking().SingleAsync(x => x.Id == id, MessagingHarness.Ct);
        Assert.NotNull(row.ParkedAtUtc);
        Assert.Null(row.ProcessedAtUtc);
        await using var scope = worker.Services.CreateAsyncScope();
        Assert.Equal(0, (await scope.ServiceProvider.GetRequiredService<OutboxPublisher>().ReadBacklogAsync(MessagingHarness.Ct)).Count);
    }

    [Fact]
    public async Task Backlog_reports_count_and_oldest_age()
    {
        await using var harness = await MessagingHarness.CreateAsync();
        await SeedOutboxAsync(harness!, 3);
        using var worker = harness!.BuildWorker();
        await using var scope = worker.Services.CreateAsyncScope();
        var backlog = await scope.ServiceProvider.GetRequiredService<OutboxPublisher>().ReadBacklogAsync(MessagingHarness.Ct);
        Assert.Equal(3, backlog.Count);
        Assert.True(backlog.OldestAge >= TimeSpan.Zero);
    }
}
