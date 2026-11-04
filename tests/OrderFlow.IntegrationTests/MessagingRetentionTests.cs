using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OrderFlow.Application.Messaging;
using OrderFlow.Infrastructure.Messaging;
using OrderFlow.Infrastructure.Persistence;
using Xunit;

namespace OrderFlow.IntegrationTests;

public sealed class MessagingRetentionTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private static OutboxMessage Outbox(DateTimeOffset? processed, DateTimeOffset? parked = null)
    {
        var row = OutboxMessage.ForEvent(EventTypes.InventoryLow, Guid.NewGuid(), "retention-test", null, new InventoryLowPayload(Guid.NewGuid(), 1, 2), Now.AddDays(-400));
        row.ProcessedAtUtc = processed;
        row.ParkedAtUtc = parked;
        return row;
    }

    private static InboxMessage Inbox(DateTimeOffset processed) => new()
    {
        ConsumerName = "retention-test", EventId = Guid.NewGuid(), EventType = EventTypes.InventoryLow, ReceivedAtUtc = processed, ProcessedAtUtc = processed
    };

    private static FailedMessage Failed(string status, DateTimeOffset? replayed, long offset) => new()
    {
        Id = Guid.NewGuid(), ConsumerName = "retention-test", SourceTopic = "topic", Partition = 0, Offset = offset, RawMessage = "{}", AttemptCount = 1,
        ErrorSummary = "Test", FailedAtUtc = Now.AddDays(-400), Status = status, ReplayedAtUtc = replayed
    };

    private static async Task<RetentionResult> PurgeAsync(IHost worker)
    {
        await using var scope = worker.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<MessagingRetention>().PurgeAsync(MessagingHarness.Ct);
    }

    [Fact]
    public async Task D_24_removes_only_rows_past_their_window_and_keeps_anything_still_needing_work()
    {
        await using var harness = await MessagingHarness.CreateAsync();
        var oldOutbox = Outbox(Now.AddDays(-8));
        var recentOutbox = Outbox(Now.AddDays(-6));
        var unpublished = Outbox(null);
        var parked = Outbox(null, Now.AddDays(-400));
        var oldInbox = Inbox(Now.AddDays(-31));
        var recentInbox = Inbox(Now.AddDays(-29));
        var oldReplayed = Failed(FailedMessageStatus.Replayed, Now.AddDays(-91), 1);
        var recentReplayed = Failed(FailedMessageStatus.Replayed, Now.AddDays(-89), 2);
        var neverReplayed = Failed(FailedMessageStatus.Failed, null, 3);
        var requested = Failed(FailedMessageStatus.ReplayRequested, Now.AddDays(-200), 4);
        await using (var db = harness!.NewDb())
        {
            db.OutboxMessages.AddRange(oldOutbox, recentOutbox, unpublished, parked);
            db.InboxMessages.AddRange(oldInbox, recentInbox);
            db.FailedMessages.AddRange(oldReplayed, recentReplayed, neverReplayed, requested);
            await db.SaveChangesAsync(MessagingHarness.Ct);
        }
        using var worker = harness.BuildWorker();

        Assert.Equal(new RetentionResult(1, 1, 1), await PurgeAsync(worker));
        Assert.Equal(new RetentionResult(0, 0, 0), await PurgeAsync(worker));

        await using var verify = harness.NewDb();
        Assert.Equal(new[] { recentOutbox.Id, unpublished.Id, parked.Id }.Order(), (await verify.OutboxMessages.AsNoTracking().Select(x => x.Id).ToListAsync(MessagingHarness.Ct)).Order());
        Assert.Equal(recentInbox.EventId, (await verify.InboxMessages.AsNoTracking().SingleAsync(MessagingHarness.Ct)).EventId);
        Assert.Equal(new[] { recentReplayed.Id, neverReplayed.Id, requested.Id }.Order(), (await verify.FailedMessages.AsNoTracking().Select(x => x.Id).ToListAsync(MessagingHarness.Ct)).Order());
    }

    [Fact]
    public async Task D_24_keeps_deleting_batches_until_the_backlog_is_gone()
    {
        await using var harness = await MessagingHarness.CreateAsync();
        await using (var db = harness!.NewDb())
        {
            for (var i = 0; i < 7; i++) db.OutboxMessages.Add(Outbox(Now.AddDays(-30)));
            await db.SaveChangesAsync(MessagingHarness.Ct);
        }
        using var worker = harness.BuildWorker(settings: new() { ["Retention:BatchSize"] = "3" });

        Assert.Equal(7, (await PurgeAsync(worker)).OutboxRows);
    }

    [Fact]
    public async Task D_24_worker_refuses_inbox_retention_that_kafka_could_outlast()
    {
        await using var harness = await MessagingHarness.CreateAsync();
        using var worker = harness!.BuildWorker(settings: new() { ["Retention:InboxDays"] = "7", ["Kafka:TopicRetentionDays"] = "7" });
        await Assert.ThrowsAsync<Microsoft.Extensions.Options.OptionsValidationException>(() => worker.StartAsync(MessagingHarness.Ct));
    }
}
