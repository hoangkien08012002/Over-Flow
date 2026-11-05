using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OrderFlow.Domain.Ordering;
using OrderFlow.Infrastructure.Persistence;

namespace OrderFlow.Api;

public static class OrderingEndpoints
{
    public static void MapOrderingEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/shipping-methods", async (OrderFlowDbContext db, CancellationToken ct) =>
            Results.Ok(await db.ShippingMethods.AsNoTracking().Where(x => x.IsActive)
                .Select(x => new { x.Id, x.Code, x.Name, x.Fee, x.CurrencyCode }).ToListAsync(ct)));
        var cart = app.MapGroup("/api/v1/cart").RequireAuthorization("Customer");
        cart.MapGet("", GetCart);
        cart.MapPost("/items", AddItem);
        cart.MapPut("/items/{id:guid}", UpdateItem);
        cart.MapDelete("/items/{id:guid}", DeleteItem);
        var orders = app.MapGroup("/api/v1/orders").RequireAuthorization("Customer");
        orders.MapPost("", Checkout).RequireRateLimiting("checkout");
        orders.MapGet("", ListOrders);
        orders.MapGet("/{id:guid}", GetOrder);
        orders.MapPost("/{id:guid}/cancel", CancelOrder);
    }

    private static Guid UserId(HttpContext context) => Guid.Parse(context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? context.User.FindFirstValue("sub")!);
    private static string Correlation(HttpContext context) => context.Items[CorrelationIdMiddleware.ItemKey]?.ToString() ?? "";
    private static IResult Error(HttpContext context, int status, string code) => Results.Json(new
    { type = $"https://orderflow/errors/{code.ToLowerInvariant()}", title = code.Replace('_', ' '), status, code, correlationId = Correlation(context) },
        statusCode: status, contentType: "application/problem+json");
    private static IResult Result(HttpContext context, CheckoutResult result)
    {
        if (result.Code is not null)
        {
            if (result.Code == "REQUEST_IN_PROGRESS") context.Response.Headers.RetryAfter = "1";
            return Error(context, result.StatusCode, result.Code);
        }
        return Results.Content(result.Body, "application/json", statusCode: result.StatusCode);
    }

    private static async Task<IResult> GetCart(OrderFlowDbContext db, HttpContext context, CancellationToken ct)
    {
        var userId = UserId(context);
        var cart = await db.Carts.AsNoTracking().Include(x => x.Items).SingleOrDefaultAsync(x => x.UserId == userId, ct);
        if (cart is null) return Results.Ok(new { id = (Guid?)null, items = Array.Empty<object>(), subtotal = 0m });
        var productIds = cart.Items.Select(x => x.ProductId).ToList();
        var products = await (from p in db.Products.AsNoTracking() join category in db.Categories.AsNoTracking() on p.CategoryId equals category.Id
                              where productIds.Contains(p.Id) select new { Product = p, CategoryActive = category.IsActive }).ToDictionaryAsync(x => x.Product.Id, ct);
        var stock = await db.Inventories.AsNoTracking().Where(x => productIds.Contains(x.ProductId)).ToDictionaryAsync(x => x.ProductId, ct);
        var items = cart.Items.Select(x => new { x.Id, x.ProductId, x.Quantity,
            productName = products.TryGetValue(x.ProductId, out var p) ? p.Product.Name : null,
            currentUnitPrice = products.TryGetValue(x.ProductId, out p) ? p.Product.Price : 0m,
            available = products.TryGetValue(x.ProductId, out p) && p.Product.IsActive && p.CategoryActive && stock.TryGetValue(x.ProductId, out var i) ? i.Available : 0,
            unavailable = !products.TryGetValue(x.ProductId, out p) || !p.Product.IsActive || !p.CategoryActive || !stock.TryGetValue(x.ProductId, out i) || i.Available < x.Quantity,
            lineSubtotal = products.TryGetValue(x.ProductId, out p) ? p.Product.Price * x.Quantity : 0m }).ToList();
        return Results.Ok(new { cart.Id, items, subtotal = items.Sum(x => x.lineSubtotal) });
    }

    private static async Task<IResult> AddItem(CartItemRequest request, OrderFlowDbContext db, TimeProvider clock, HttpContext context, CancellationToken ct)
    {
        if (request.ProductId == Guid.Empty || request.Quantity is < 1 or > 100) return Error(context, 400, "VALIDATION_FAILED");
        var valid = await (from p in db.Products.AsNoTracking() join c in db.Categories.AsNoTracking() on p.CategoryId equals c.Id
                           where p.Id == request.ProductId && p.IsActive && c.IsActive select p.Id).AnyAsync(ct);
        if (!valid) return Error(context, 400, "VALIDATION_FAILED");
        var userId = UserId(context);
        var cart = await db.Carts.Include(x => x.Items).SingleOrDefaultAsync(x => x.UserId == userId, ct);
        if (cart is null) { cart = new Cart(userId, clock.GetUtcNow()); db.Carts.Add(cart); }
        var item = cart.Items.SingleOrDefault(x => x.ProductId == request.ProductId);
        if (item is null) cart.Items.Add(new CartItem(cart.Id, request.ProductId, request.Quantity, clock.GetUtcNow()));
        else if (item.Quantity + request.Quantity > 100) return Error(context, 400, "VALIDATION_FAILED");
        else item.SetQuantity(item.Quantity + request.Quantity, clock.GetUtcNow());
        cart.Touch(clock.GetUtcNow());
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { return Error(context, 409, "CONCURRENCY_CONFLICT"); }
        return await GetCart(db, context, ct);
    }

    private static async Task<IResult> UpdateItem(Guid id, CartQuantityRequest request, OrderFlowDbContext db, TimeProvider clock, HttpContext context, CancellationToken ct)
    {
        if (request.Quantity is < 1 or > 100) return Error(context, 400, "VALIDATION_FAILED");
        var item = await (from i in db.CartItems join c in db.Carts on i.CartId equals c.Id
                          where i.Id == id && c.UserId == UserId(context) select i).SingleOrDefaultAsync(ct);
        if (item is null) return Error(context, 404, "RESOURCE_NOT_FOUND");
        item.SetQuantity(request.Quantity, clock.GetUtcNow());
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { return Error(context, 404, "RESOURCE_NOT_FOUND"); }
        return await GetCart(db, context, ct);
    }

    private static async Task<IResult> DeleteItem(Guid id, OrderFlowDbContext db, HttpContext context, CancellationToken ct)
    {
        var item = await (from i in db.CartItems join c in db.Carts on i.CartId equals c.Id
                          where i.Id == id && c.UserId == UserId(context) select i).SingleOrDefaultAsync(ct);
        if (item is null) return Error(context, 404, "RESOURCE_NOT_FOUND");
        db.CartItems.Remove(item);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { return Error(context, 404, "RESOURCE_NOT_FOUND"); }
        return Results.NoContent();
    }

    private static async Task<IResult> Checkout(CheckoutRequest request, CheckoutService service, HttpContext context, CancellationToken ct)
    {
        if (!context.Request.Headers.TryGetValue("Idempotency-Key", out var key) || string.IsNullOrWhiteSpace(key))
            return Error(context, 400, "IDEMPOTENCY_KEY_REQUIRED");
        var result = await service.CheckoutAsync(UserId(context), key.ToString(), request, Correlation(context), ct);
        if (result.StatusCode == 202 && result.Body is not null)
        {
            using var doc = JsonDocument.Parse(result.Body);
            context.Response.Headers.Location = doc.RootElement.GetProperty("statusUrl").GetString();
        }
        return Result(context, result);
    }

    private static async Task<IResult> ListOrders(OrderFlowDbContext db, HttpContext context, string? status, DateTimeOffset? fromUtc,
        DateTimeOffset? toUtc, int? page, int? pageSize, CancellationToken ct)
    {
        var currentPage = page ?? 1;
        var currentPageSize = pageSize ?? 20;
        if (currentPage < 1 || currentPageSize is < 1 or > 100 || fromUtc > toUtc ||
            status is not null && (!Enum.TryParse<OrderStatus>(status, true, out var state) || !Enum.IsDefined(state)))
            return Error(context, 400, "VALIDATION_FAILED");
        var query = db.Orders.AsNoTracking().Where(x => x.UserId == UserId(context));
        if (status is not null) { var parsed = Enum.Parse<OrderStatus>(status, true); query = query.Where(x => x.Status == parsed); }
        if (fromUtc is not null) query = query.Where(x => x.CreatedAtUtc >= fromUtc);
        if (toUtc is not null) query = query.Where(x => x.CreatedAtUtc <= toUtc);
        var count = await query.CountAsync(ct);
        var items = await query.OrderByDescending(x => x.CreatedAtUtc).Skip((currentPage - 1) * currentPageSize).Take(currentPageSize).ToListAsync(ct);
        return Results.Ok(new { items = items.Select(CheckoutService.ToResponse), page = currentPage, pageSize = currentPageSize, totalCount = count,
            totalPages = (count + currentPageSize - 1) / currentPageSize });
    }

    private static async Task<IResult> GetOrder(Guid id, OrderFlowDbContext db, HttpContext context, CancellationToken ct)
    {
        var order = await db.Orders.AsNoTracking().Include(x => x.Items).Include(x => x.History)
            .SingleOrDefaultAsync(x => x.Id == id && x.UserId == UserId(context), ct);
        return order is null ? Error(context, 404, "ORDER_NOT_FOUND") : Results.Ok(ToDetail(order));
    }

    internal static object ToDetail(Order order) => new
    { order = CheckoutService.ToResponse(order),
        items = order.Items.OrderBy(x => x.ProductId).Select(x => new OrderItemResponse(x.Id, x.ProductId, x.SkuSnapshot, x.ProductNameSnapshot, x.UnitPrice, x.Quantity, x.LineSubtotal, x.DiscountAmount)),
        history = order.History.OrderBy(x => x.ChangedAtUtc).Select(x => new OrderHistoryResponse(x.FromStatus, x.ToStatus, x.ChangedAtUtc, x.ActorUserId, x.EventId, x.Reason, x.CorrelationId)),
        deliveryAddress = CheckoutService.ReadAddress(order), order.PaymentMethod, order.ShippingMethodName };

    private static async Task<IResult> CancelOrder(Guid id, CancelOrderRequest request, CheckoutService service, HttpContext context, CancellationToken ct) =>
        Result(context, await service.CancelAsync(UserId(context), id, request.Reason, Correlation(context), ct));
}

public sealed record CartItemRequest(Guid ProductId, int Quantity);
public sealed record CartQuantityRequest(int Quantity);
public sealed record CancelOrderRequest(string? Reason);
public sealed record OrderItemResponse(Guid Id, Guid ProductId, string SkuSnapshot, string ProductNameSnapshot, decimal UnitPrice, int Quantity, decimal LineSubtotal, decimal DiscountAmount);
public sealed record OrderHistoryResponse(OrderStatus? FromStatus, OrderStatus ToStatus, DateTimeOffset ChangedAtUtc, Guid? ActorUserId, Guid? EventId, string? Reason, string CorrelationId);
