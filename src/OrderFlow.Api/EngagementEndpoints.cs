using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using OrderFlow.Domain.Engagement;
using OrderFlow.Domain.Ordering;
using OrderFlow.Infrastructure.Persistence;
using static OrderFlow.Api.EndpointSupport;

namespace OrderFlow.Api;

public static class EngagementEndpoints
{
    public static void MapEngagementEndpoints(this WebApplication app)
    {
        var wishlist = app.MapGroup("/api/v1/wishlist").RequireAuthorization("Customer");
        wishlist.MapGet("", ListWishlist);
        wishlist.MapPut("/{productId:guid}", AddToWishlist);
        wishlist.MapDelete("/{productId:guid}", RemoveFromWishlist);

        app.MapGet("/api/v1/products/{productId:guid}/reviews", ListProductReviews);
        app.MapPost("/api/v1/products/{productId:guid}/reviews", CreateReview).RequireAuthorization("Customer");
        var moderation = app.MapGroup("/api/v1/admin/reviews").RequireAuthorization("Admin");
        moderation.MapGet("", ListReviews);
        moderation.MapPost("/{id:guid}/moderate", Moderate).Audited("review.moderate", "Review");
    }

    private static async Task<IResult> ListWishlist(OrderFlowDbContext db, HttpContext context, CancellationToken ct)
    {
        var userId = UserId(context);
        var items = await (from item in db.WishlistItems.AsNoTracking()
                           join product in db.Products.AsNoTracking() on item.ProductId equals product.Id
                           join category in db.Categories.AsNoTracking() on product.CategoryId equals category.Id
                           join stock in db.Inventories.AsNoTracking() on product.Id equals stock.ProductId into stocks
                           from stock in stocks.DefaultIfEmpty()
                           where item.UserId == userId
                           orderby item.CreatedAtUtc descending
                           select new { item.ProductId, product.Name, product.Price, product.CurrencyCode, item.CreatedAtUtc,
                               isAvailable = product.IsActive && category.IsActive && stock != null && stock.OnHand > stock.Reserved }).ToListAsync(ct);
        return Results.Ok(new { items });
    }

    // PUT is naturally idempotent here: adding what is already listed changes nothing.
    private static async Task<IResult> AddToWishlist(Guid productId, OrderFlowDbContext db, TimeProvider clock, HttpContext context, CancellationToken ct)
    {
        var userId = UserId(context);
        var valid = await (from p in db.Products.AsNoTracking() join c in db.Categories.AsNoTracking() on p.CategoryId equals c.Id
                           where p.Id == productId && p.IsActive && c.IsActive select p.Id).AnyAsync(ct);
        if (!valid) return Error(context, 404, "RESOURCE_NOT_FOUND");
        if (await db.WishlistItems.AsNoTracking().AnyAsync(x => x.UserId == userId && x.ProductId == productId, ct)) return Results.NoContent();
        db.WishlistItems.Add(new WishlistItem(userId, productId, clock.GetUtcNow()));
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 }) { /* A concurrent add reached the same state. */ }
        return Results.NoContent();
    }

    private static async Task<IResult> RemoveFromWishlist(Guid productId, OrderFlowDbContext db, HttpContext context, CancellationToken ct)
    {
        var userId = UserId(context);
        await db.WishlistItems.Where(x => x.UserId == userId && x.ProductId == productId).ExecuteDeleteAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> ListProductReviews(Guid productId, OrderFlowDbContext db, HttpContext context, int? page, int? pageSize, CancellationToken ct)
    {
        if (!TryPage(page, pageSize, out var currentPage, out var currentPageSize)) return Error(context, 400, "VALIDATION_FAILED");
        var published = db.Reviews.AsNoTracking().Where(x => x.ProductId == productId && x.Status == ReviewStatus.Published);
        var count = await published.CountAsync(ct);
        var average = count == 0 ? (double?)null : Math.Round(await published.AverageAsync(x => (double)x.Rating, ct), 2);
        // Only a first name leaves the system: this list is public and the reviewer is a customer.
        var items = await (from review in published
                           join user in db.Users.AsNoTracking() on review.UserId equals user.Id
                           orderby review.CreatedAtUtc descending
                           select new { review.Id, review.Rating, review.Comment, review.CreatedAtUtc, authorName = user.FirstName })
            .Skip((currentPage - 1) * currentPageSize).Take(currentPageSize).ToListAsync(ct);
        return Results.Ok(new { summary = new { count, averageRating = average }, items, page = currentPage, pageSize = currentPageSize, totalCount = count, totalPages = (count + currentPageSize - 1) / currentPageSize });
    }

    private static async Task<IResult> CreateReview(Guid productId, ReviewRequest request, OrderFlowDbContext db, TimeProvider clock, HttpContext context, CancellationToken ct)
    {
        if (request.OrderItemId == Guid.Empty || request.Rating is < 1 or > 5 || request.Comment?.Trim().Length > Review.MaxCommentLength) return Error(context, 400, "VALIDATION_FAILED");
        var userId = UserId(context);
        var purchase = await (from item in db.OrderItems.AsNoTracking() join order in db.Orders.AsNoTracking() on item.OrderId equals order.Id
                              where item.Id == request.OrderItemId && item.ProductId == productId && order.UserId == userId
                              select new { item.OrderId, order.Status }).SingleOrDefaultAsync(ct);
        // Someone else's purchase answers like one that does not exist, so order item IDs cannot be probed.
        if (purchase is null) return Error(context, 404, "RESOURCE_NOT_FOUND");
        if (purchase.Status != OrderStatus.Delivered) return Error(context, 409, "REVIEW_NOT_ELIGIBLE");
        var review = Review.Create(Guid.NewGuid(), productId, purchase.OrderId, request.OrderItemId, userId, request.Rating, request.Comment, clock.GetUtcNow());
        db.Reviews.Add(review);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 }) { return Error(context, 409, "REVIEW_ALREADY_EXISTS"); }
        return Results.Created($"/api/v1/products/{productId}/reviews", ToResponse(review));
    }

    private static async Task<IResult> ListReviews(OrderFlowDbContext db, HttpContext context, string? status, int? page, int? pageSize, CancellationToken ct)
    {
        if (!TryPage(page, pageSize, out var currentPage, out var currentPageSize) || status is not null && !Enum.TryParse<ReviewStatus>(status, true, out _))
            return Error(context, 400, "VALIDATION_FAILED");
        var query = db.Reviews.AsNoTracking();
        if (status is not null) { var parsed = Enum.Parse<ReviewStatus>(status, true); query = query.Where(x => x.Status == parsed); }
        var count = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(x => x.CreatedAtUtc).Skip((currentPage - 1) * currentPageSize).Take(currentPageSize).ToListAsync(ct);
        return Results.Ok(Paged(rows.Select(ToResponse), currentPage, currentPageSize, count));
    }

    private static async Task<IResult> Moderate(Guid id, ModerateReviewRequest request, OrderFlowDbContext db, TimeProvider clock, HttpContext context, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Trim().Length > 500 || !TryVersion(request.Version, out var version)) return Error(context, 400, "VALIDATION_FAILED");
        var review = await db.Reviews.FindAsync([id], ct);
        if (review is null) return Error(context, 404, "RESOURCE_NOT_FOUND");
        if (version is not null) db.Entry(review).Property(x => x.RowVersion).OriginalValue = version;
        review.Moderate(request.Status, request.Reason, UserId(context), clock.GetUtcNow());
        context.Reason(request.Reason.Trim());
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { return Error(context, 409, "CONCURRENCY_CONFLICT"); }
        return Results.Ok(ToResponse(review));
    }

    private static object ToResponse(Review x) => new
    {
        x.Id, x.ProductId, x.OrderId, x.OrderItemId, x.UserId, x.Rating, x.Comment, x.Status, x.ModerationReason, x.ModeratedByUserId, x.ModeratedAtUtc, x.CreatedAtUtc,
        version = Convert.ToBase64String(x.RowVersion)
    };
}

public sealed record ReviewRequest(Guid OrderItemId, int Rating, string? Comment);
public sealed record ModerateReviewRequest(ReviewStatus Status, string? Reason, string? Version = null);
