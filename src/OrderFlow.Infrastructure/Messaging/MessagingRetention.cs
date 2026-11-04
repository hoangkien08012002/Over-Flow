using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OrderFlow.Infrastructure.Persistence;

namespace OrderFlow.Infrastructure.Messaging;

public sealed record RetentionResult(int OutboxRows, int InboxRows, int FailedMessages);

public sealed class MessagingRetention(OrderFlowDbContext db, TimeProvider clock, IOptions<RetentionOptions> retentionOptions)
{
    private readonly RetentionOptions options = retentionOptions.Value;

    // Unpublished, parked, and unreplayed rows never match: each still needs a publish or a person.
    public async Task<RetentionResult> PurgeAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var outboxCutoff = now.AddDays(-options.OutboxDays);
        var inboxCutoff = now.AddDays(-options.InboxDays);
        var failedCutoff = now.AddDays(-options.FailedMessageDays);
        var batch = options.BatchSize;
        var outbox = await DeleteInBatchesAsync(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE TOP ({batch}) FROM dbo.OutboxMessages WHERE ProcessedAtUtc < {outboxCutoff}", cancellationToken));
        var inbox = await DeleteInBatchesAsync(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE TOP ({batch}) FROM dbo.InboxMessages WHERE ProcessedAtUtc < {inboxCutoff}", cancellationToken));
        var failed = await DeleteInBatchesAsync(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE TOP ({batch}) FROM dbo.FailedMessages WHERE Status = {FailedMessageStatus.Replayed} AND ReplayedAtUtc < {failedCutoff}", cancellationToken));
        return new RetentionResult(outbox, inbox, failed);
    }

    // Small transactions keep locks short, so publishing and consuming continue while old rows are removed.
    private async Task<int> DeleteInBatchesAsync(Func<Task<int>> deleteBatch)
    {
        var total = 0;
        int deleted;
        do
        {
            deleted = await deleteBatch();
            total += deleted;
        } while (deleted == options.BatchSize);
        return total;
    }
}
