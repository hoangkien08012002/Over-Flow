using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OrderFlow.Application.Messaging;
using OrderFlow.Infrastructure.Messaging;
using OrderFlow.Infrastructure.Persistence;

namespace OrderFlow.Api;

public static class AdminEndpoints
{
    public static void MapAdminEndpoints(this WebApplication app)
    {
        var failed = app.MapGroup("/api/v1/admin/failed-messages").RequireAuthorization("Admin");
        failed.MapGet("", ListFailedMessages);
        failed.MapPost("/{id:guid}/reprocess", Reprocess);
    }

    private static async Task<IResult> ListFailedMessages(OrderFlowDbContext db, HttpContext context, string? status, int? page, int? pageSize, CancellationToken ct)
    {
        var currentPage = page ?? 1;
        var currentPageSize = pageSize ?? 20;
        if (currentPage < 1 || currentPageSize is < 1 or > 100 ||
            status is not (null or FailedMessageStatus.Failed or FailedMessageStatus.ReplayRequested or FailedMessageStatus.Replayed))
            return Error(context, 400, "VALIDATION_FAILED", "Invalid failed-message query");
        var query = db.FailedMessages.AsNoTracking();
        if (status is not null) query = query.Where(x => x.Status == status);
        var count = await query.CountAsync(ct);
        // The raw message stays out of the response; the summary and position are enough to triage.
        var items = await query.OrderByDescending(x => x.FailedAtUtc).Skip((currentPage - 1) * currentPageSize).Take(currentPageSize)
            .Select(x => new { x.Id, x.ConsumerName, x.SourceTopic, x.Partition, x.Offset, x.EventId, x.EventType, x.AttemptCount, x.ErrorSummary,
                x.FailedAtUtc, x.DeadLetteredAtUtc, x.Status, x.ReplayRequestedAtUtc, x.ReplayedAtUtc }).ToListAsync(ct);
        return Results.Ok(new { items, page = currentPage, pageSize = currentPageSize, totalCount = count, totalPages = (count + currentPageSize - 1) / currentPageSize });
    }

    private static async Task<IResult> Reprocess(Guid id, OrderFlowDbContext db, TimeProvider clock, IOptions<RetentionOptions> retention, HttpContext context, CancellationToken ct)
    {
        var message = await db.FailedMessages.AsNoTracking().Where(x => x.Id == id).Select(x => new { x.EventId, x.RawMessage }).SingleOrDefaultAsync(ct);
        if (message is null) return Error(context, 404, "RESOURCE_NOT_FOUND", "Failed message not found");
        // Without an envelope there is no EventId for the Inbox to protect, so the message cannot be replayed safely.
        if (message.EventId is null) return Error(context, 409, "INVALID_STATE_TRANSITION", "Failed message has no readable event");
        var now = clock.GetUtcNow();
        // Measured from the event, not the failure: a repeat failure is recent even when other consumers' Inbox rows are already old.
        if (EventSerializer.Parse(message.RawMessage).OccurredAtUtc.AddDays(retention.Value.InboxDays) <= now)
            return Error(context, 409, "INVALID_STATE_TRANSITION", "Failed message is older than Inbox retention");
        await db.FailedMessages.Where(x => x.Id == id && x.Status != FailedMessageStatus.ReplayRequested).ExecuteUpdateAsync(setters => setters
            .SetProperty(x => x.Status, FailedMessageStatus.ReplayRequested).SetProperty(x => x.ReplayRequestedAtUtc, now), ct);
        return Results.Accepted($"/api/v1/admin/failed-messages/{id}", new { id, status = FailedMessageStatus.ReplayRequested });
    }

    private static IResult Error(HttpContext context, int status, string code, string title) => Results.Json(new
    {
        type = $"https://orderflow/errors/{code.ToLowerInvariant()}", title, status, code,
        correlationId = context.Items[CorrelationIdMiddleware.ItemKey]?.ToString() ?? ""
    }, statusCode: status, contentType: "application/problem+json");
}
