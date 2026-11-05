using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OrderFlow.Application.Messaging;
using OrderFlow.Domain.Ordering;
using OrderFlow.Domain.Payments;
using OrderFlow.Infrastructure.Messaging;
using OrderFlow.Infrastructure.Persistence;
using static OrderFlow.Api.EndpointSupport;

namespace OrderFlow.Api;

public static class AdminEndpoints
{
    public static void MapAdminEndpoints(this WebApplication app)
    {
        var failed = app.MapGroup("/api/v1/admin/failed-messages").RequireAuthorization("Admin");
        failed.MapGet("", ListFailedMessages);
        failed.MapPost("/{id:guid}/reprocess", Reprocess).Audited("failed-message.reprocess", "FailedMessage");

        var refunds = app.MapGroup("/api/v1/admin/failed-refunds").RequireAuthorization("Admin");
        refunds.MapGet("", ListFailedRefunds);
        refunds.MapPost("/{id:guid}/retry", RetryRefund).Audited("refund.retry", "Refund");

        var orders = app.MapGroup("/api/v1/admin/orders");
        orders.MapGet("", ListOrders).RequireAuthorization("OrderStaff");
        orders.MapGet("/{id:guid}", GetOrder).RequireAuthorization("OrderStaff");
        orders.MapPost("/{id:guid}/cancel", CancelOrder).RequireAuthorization("OrderStaff").Audited("order.staff-cancel", "Order");

        var coupons = app.MapGroup("/api/v1/admin/coupons").RequireAuthorization("Admin");
        coupons.MapGet("", ListCoupons);
        coupons.MapGet("/{id:guid}", GetCoupon);
        coupons.MapPost("", CreateCoupon).Audited("coupon.create", "Coupon");
        coupons.MapPut("/{id:guid}", UpdateCoupon).Audited("coupon.update", "Coupon");
        coupons.MapPut("/{id:guid}/activation", SetCouponEnabled).Audited("coupon.activation", "Coupon");

        var methods = app.MapGroup("/api/v1/admin/shipping-methods").RequireAuthorization("Admin");
        methods.MapGet("", ListShippingMethods);
        methods.MapPost("", CreateShippingMethod).Audited("shipping-method.create", "ShippingMethod");
        methods.MapPut("/{id:guid}", UpdateShippingMethod).Audited("shipping-method.update", "ShippingMethod");

        app.MapGet("/api/v1/admin/audit-logs", ListAuditLogs).RequireAuthorization("Admin");
    }

    private static async Task<IResult> ListFailedMessages(OrderFlowDbContext db, HttpContext context, string? status, int? page, int? pageSize, CancellationToken ct)
    {
        if (!TryPage(page, pageSize, out var currentPage, out var currentPageSize) ||
            status is not (null or FailedMessageStatus.Failed or FailedMessageStatus.ReplayRequested or FailedMessageStatus.Replayed))
            return Error(context, 400, "VALIDATION_FAILED", "Invalid failed-message query");
        var query = db.FailedMessages.AsNoTracking();
        if (status is not null) query = query.Where(x => x.Status == status);
        var count = await query.CountAsync(ct);
        // The raw message stays out of the response; the summary and position are enough to triage.
        var items = await query.OrderByDescending(x => x.FailedAtUtc).Skip((currentPage - 1) * currentPageSize).Take(currentPageSize)
            .Select(x => new { x.Id, x.ConsumerName, x.SourceTopic, x.Partition, x.Offset, x.EventId, x.EventType, x.AttemptCount, x.ErrorSummary,
                x.FailedAtUtc, x.DeadLetteredAtUtc, x.Status, x.ReplayRequestedAtUtc, x.ReplayedAtUtc }).ToListAsync(ct);
        return Results.Ok(Paged(items, currentPage, currentPageSize, count));
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

    // D-23: refunds that used their retry budget wait here until an Admin acts.
    private static async Task<IResult> ListFailedRefunds(OrderFlowDbContext db, HttpContext context, int? page, int? pageSize, CancellationToken ct)
    {
        if (!TryPage(page, pageSize, out var currentPage, out var currentPageSize)) return Error(context, 400, "VALIDATION_FAILED");
        var query = db.Refunds.AsNoTracking().Where(x => x.Status == RefundStatus.Failed);
        var count = await query.CountAsync(ct);
        var items = await query.OrderBy(x => x.UpdatedAtUtc).Skip((currentPage - 1) * currentPageSize).Take(currentPageSize)
            .Select(x => new { x.Id, x.OrderId, x.PaymentId, x.Status, x.Amount, x.CurrencyCode, x.Reason, x.AttemptCount, x.CreatedAtUtc, x.UpdatedAtUtc }).ToListAsync(ct);
        return Results.Ok(Paged(items, currentPage, currentPageSize, count));
    }

    // The API only reopens the refund. The Worker owns the provider call and retries it with the same idempotency key.
    private static async Task<IResult> RetryRefund(Guid id, OrderFlowDbContext db, TimeProvider clock, HttpContext context, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct);
        var orderId = await db.Refunds.AsNoTracking().Where(x => x.Id == id).Select(x => (Guid?)x.OrderId).SingleOrDefaultAsync(ct);
        if (orderId is null) return Error(context, 404, "RESOURCE_NOT_FOUND");
        var payment = await db.Payments.FromSqlInterpolated($"SELECT * FROM dbo.Payments WITH (UPDLOCK, ROWLOCK) WHERE OrderId = {orderId.Value}").SingleAsync(ct);
        var refund = await db.Refunds.SingleAsync(x => x.Id == id, ct);
        if (refund.Status != RefundStatus.Failed) return Error(context, 409, "INVALID_STATE_TRANSITION");
        var now = clock.GetUtcNow();
        refund.Reopen(now);
        payment.RetryRefund(now);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Results.Accepted($"/api/v1/admin/failed-refunds/{id}", new { id, status = refund.Status });
    }

    private static async Task<IResult> ListOrders(OrderFlowDbContext db, HttpContext context, string? status, Guid? userId, int? page, int? pageSize, CancellationToken ct)
    {
        if (!TryPage(page, pageSize, out var currentPage, out var currentPageSize) || status is not null && !Enum.TryParse<OrderStatus>(status, true, out _))
            return Error(context, 400, "VALIDATION_FAILED");
        var query = db.Orders.AsNoTracking();
        if (status is not null) { var parsed = Enum.Parse<OrderStatus>(status, true); query = query.Where(x => x.Status == parsed); }
        if (userId is not null) query = query.Where(x => x.UserId == userId);
        var count = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(x => x.CreatedAtUtc).Skip((currentPage - 1) * currentPageSize).Take(currentPageSize).ToListAsync(ct);
        return Results.Ok(Paged(rows.Select(CheckoutService.ToResponse), currentPage, currentPageSize, count));
    }

    private static async Task<IResult> GetOrder(Guid id, OrderFlowDbContext db, HttpContext context, CancellationToken ct)
    {
        var order = await db.Orders.AsNoTracking().Include(x => x.Items).Include(x => x.History).AsSplitQuery().SingleOrDefaultAsync(x => x.Id == id, ct);
        return order is null ? Error(context, 404, "ORDER_NOT_FOUND") : Results.Ok(OrderingEndpoints.ToDetail(order));
    }

    private static async Task<IResult> CancelOrder(Guid id, CancelOrderRequest request, CheckoutService service, HttpContext context, CancellationToken ct)
    {
        context.Reason(request.Reason);
        var result = await service.CancelByStaffAsync(UserId(context), id, request.Reason, Correlation(context), ct);
        return result.Code is not null ? Error(context, result.StatusCode, result.Code) : Results.Content(result.Body, "application/json", statusCode: result.StatusCode);
    }

    private static async Task<IResult> ListCoupons(OrderFlowDbContext db, HttpContext context, int? page, int? pageSize, CancellationToken ct)
    {
        if (!TryPage(page, pageSize, out var currentPage, out var currentPageSize)) return Error(context, 400, "VALIDATION_FAILED");
        var count = await db.Coupons.AsNoTracking().CountAsync(ct);
        var rows = await db.Coupons.AsNoTracking().OrderByDescending(x => x.CreatedAtUtc).Skip((currentPage - 1) * currentPageSize).Take(currentPageSize).ToListAsync(ct);
        return Results.Ok(Paged(rows.Select(ToResponse), currentPage, currentPageSize, count));
    }

    private static async Task<IResult> GetCoupon(Guid id, OrderFlowDbContext db, HttpContext context, CancellationToken ct)
    {
        var coupon = await db.Coupons.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (coupon is null) return Error(context, 404, "RESOURCE_NOT_FOUND");
        var usages = await db.CouponUsages.AsNoTracking().Where(x => x.CouponId == id).GroupBy(x => x.ReleasedAtUtc == null)
            .Select(x => new { Active = x.Key, Count = x.Count(), Discount = x.Sum(u => u.DiscountAmount) }).ToListAsync(ct);
        return Results.Ok(new
        {
            coupon = ToResponse(coupon),
            activeUsages = usages.SingleOrDefault(x => x.Active)?.Count ?? 0,
            returnedUsages = usages.SingleOrDefault(x => !x.Active)?.Count ?? 0,
            discountGiven = usages.SingleOrDefault(x => x.Active)?.Discount ?? 0m
        });
    }

    private static async Task<IResult> CreateCoupon(CouponRequest request, OrderFlowDbContext db, TimeProvider clock, HttpContext context, CancellationToken ct)
    {
        Coupon coupon;
        var now = clock.GetUtcNow();
        try { coupon = Coupon.Create(Guid.NewGuid(), request.Code ?? "", request.DiscountType, request.DiscountValue, request.MinimumSubtotal, request.ValidFromUtc.ToUniversalTime(), request.ValidUntilUtc.ToUniversalTime(), request.TotalUsageLimit, request.PerUserUsageLimit, now); }
        catch (ArgumentException) { return Error(context, 400, "VALIDATION_FAILED"); }
        if (!request.IsEnabled) coupon.SetEnabled(false, now);
        db.Coupons.Add(coupon);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 }) { return Error(context, 409, "COUPON_CODE_ALREADY_EXISTS"); }
        context.Target(coupon.Id);
        return Results.Created($"/api/v1/admin/coupons/{coupon.Id}", ToResponse(coupon));
    }

    private static async Task<IResult> UpdateCoupon(Guid id, CouponRequest request, OrderFlowDbContext db, TimeProvider clock, HttpContext context, CancellationToken ct)
    {
        if (!TryVersion(request.Version, out var version)) return Error(context, 400, "VALIDATION_FAILED");
        var coupon = await db.Coupons.FindAsync([id], ct);
        if (coupon is null) return Error(context, 404, "RESOURCE_NOT_FOUND");
        if (version is not null) db.Entry(coupon).Property(x => x.RowVersion).OriginalValue = version;
        try { coupon.Update(request.DiscountType, request.DiscountValue, request.MinimumSubtotal, request.ValidFromUtc.ToUniversalTime(), request.ValidUntilUtc.ToUniversalTime(), request.TotalUsageLimit, request.PerUserUsageLimit, request.IsEnabled, clock.GetUtcNow()); }
        catch (ArgumentException) { return Error(context, 400, "VALIDATION_FAILED"); }
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { return Error(context, 409, "CONCURRENCY_CONFLICT"); }
        return Results.Ok(ToResponse(coupon));
    }

    private static async Task<IResult> SetCouponEnabled(Guid id, CouponActivationRequest request, OrderFlowDbContext db, TimeProvider clock, HttpContext context, CancellationToken ct)
    {
        if (!TryVersion(request.Version, out var version)) return Error(context, 400, "VALIDATION_FAILED");
        var coupon = await db.Coupons.FindAsync([id], ct);
        if (coupon is null) return Error(context, 404, "RESOURCE_NOT_FOUND");
        if (version is not null) db.Entry(coupon).Property(x => x.RowVersion).OriginalValue = version;
        coupon.SetEnabled(request.IsEnabled, clock.GetUtcNow());
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { return Error(context, 409, "CONCURRENCY_CONFLICT"); }
        return Results.Ok(ToResponse(coupon));
    }

    private static async Task<IResult> ListShippingMethods(OrderFlowDbContext db, CancellationToken ct) =>
        Results.Ok((await db.ShippingMethods.AsNoTracking().OrderBy(x => x.Code).ToListAsync(ct)).Select(ToResponse));

    private static async Task<IResult> CreateShippingMethod(ShippingMethodRequest request, OrderFlowDbContext db, TimeProvider clock, HttpContext context, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Code) || request.Code.Trim().Length > 40 || !ValidMethod(request)) return Error(context, 400, "VALIDATION_FAILED");
        var now = clock.GetUtcNow();
        var method = new ShippingMethod(Guid.NewGuid(), request.Code.Trim().ToUpperInvariant(), request.Name!.Trim(), request.Fee, request.CurrencyCode!, now);
        method.Update(request.Name, request.Fee, request.CurrencyCode!, request.IsActive, now);
        db.ShippingMethods.Add(method);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 }) { return Error(context, 409, "SHIPPING_CODE_ALREADY_EXISTS"); }
        context.Target(method.Id);
        return Results.Created($"/api/v1/admin/shipping-methods/{method.Id}", ToResponse(method));
    }

    private static async Task<IResult> UpdateShippingMethod(Guid id, ShippingMethodRequest request, OrderFlowDbContext db, TimeProvider clock, HttpContext context, CancellationToken ct)
    {
        if (!ValidMethod(request) || !TryVersion(request.Version, out var version)) return Error(context, 400, "VALIDATION_FAILED");
        var method = await db.ShippingMethods.FindAsync([id], ct);
        if (method is null) return Error(context, 404, "RESOURCE_NOT_FOUND");
        if (version is not null) db.Entry(method).Property(x => x.RowVersion).OriginalValue = version;
        method.Update(request.Name!, request.Fee, request.CurrencyCode!, request.IsActive, clock.GetUtcNow());
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { return Error(context, 409, "CONCURRENCY_CONFLICT"); }
        return Results.Ok(ToResponse(method));
    }

    private static async Task<IResult> ListAuditLogs(OrderFlowDbContext db, HttpContext context, string? targetType, string? targetId, Guid? actorUserId, string? correlationId, int? page, int? pageSize, CancellationToken ct)
    {
        if (!TryPage(page, pageSize, out var currentPage, out var currentPageSize)) return Error(context, 400, "VALIDATION_FAILED");
        var query = db.AuditEntries.AsNoTracking();
        if (targetType is not null) query = query.Where(x => x.TargetType == targetType);
        if (targetId is not null) query = query.Where(x => x.TargetId == targetId);
        if (actorUserId is not null) query = query.Where(x => x.ActorUserId == actorUserId);
        if (correlationId is not null) query = query.Where(x => x.CorrelationId == correlationId);
        var count = await query.CountAsync(ct);
        var items = await query.OrderByDescending(x => x.OccurredAtUtc).Skip((currentPage - 1) * currentPageSize).Take(currentPageSize)
            .Select(x => new { x.Id, x.ActorUserId, x.Action, x.TargetType, x.TargetId, x.ResponseStatus, x.Reason, x.CorrelationId, x.OccurredAtUtc }).ToListAsync(ct);
        return Results.Ok(Paged(items, currentPage, currentPageSize, count));
    }

    private static bool ValidMethod(ShippingMethodRequest x) => !string.IsNullOrWhiteSpace(x.Name) && x.Name.Trim().Length <= 150 && x.Fee >= 0 && decimal.Round(x.Fee, 2) == x.Fee &&
        x.CurrencyCode is { Length: 3 } && x.CurrencyCode.All(c => c is >= 'A' and <= 'Z');

    private static object ToResponse(Coupon x) => new
    {
        x.Id, x.Code, x.DiscountType, x.DiscountValue, x.MinimumSubtotal, x.ValidFromUtc, x.ValidUntilUtc, x.TotalUsageLimit, x.PerUserUsageLimit, x.UsageCount, x.IsEnabled,
        x.CreatedAtUtc, x.UpdatedAtUtc, version = Convert.ToBase64String(x.RowVersion)
    };

    private static object ToResponse(ShippingMethod x) => new { x.Id, x.Code, x.Name, x.Fee, x.CurrencyCode, x.IsActive, x.CreatedAtUtc, x.UpdatedAtUtc, version = Convert.ToBase64String(x.RowVersion) };
}

public sealed record CouponRequest(string? Code, CouponDiscountType DiscountType, decimal DiscountValue, decimal MinimumSubtotal, DateTimeOffset ValidFromUtc, DateTimeOffset ValidUntilUtc,
    int? TotalUsageLimit, int? PerUserUsageLimit, bool IsEnabled = true, string? Version = null);
public sealed record CouponActivationRequest(bool IsEnabled, string? Version = null);
public sealed record ShippingMethodRequest(string? Code, string? Name, decimal Fee, string? CurrencyCode, bool IsActive = true, string? Version = null);
