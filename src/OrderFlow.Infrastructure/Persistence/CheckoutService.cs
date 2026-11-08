using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OrderFlow.Application.Inventory;
using OrderFlow.Application.Messaging;
using OrderFlow.Domain.Ordering;
using OrderFlow.Domain.Shipping;

namespace OrderFlow.Infrastructure.Persistence;

public sealed record DeliveryAddress(string RecipientName, string Phone, string Line1, string Ward, string District, string Province, string CountryCode);
public sealed record CheckoutRequest(Guid CartId, Guid ShippingMethodId, string PaymentMethod, string? CouponCode, DeliveryAddress DeliveryAddress);
public sealed record OrderResponse(Guid Id, string OrderNumber, string Status, decimal Subtotal, decimal DiscountTotal,
    decimal ShippingFee, decimal GrandTotal, string CurrencyCode, string StatusUrl);
public sealed record CheckoutResult(int StatusCode, string? Code, string? Body, IReadOnlyDictionary<string, string[]>? Errors = null)
{
    // The code stays VALIDATION_FAILED for every case. The field tells a client what to show next to its form.
    public static CheckoutResult Invalid(string field, string message) => new(400, "VALIDATION_FAILED", null, new Dictionary<string, string[]> { [field] = [message] });
}

public sealed class OrderingOptions
{
    public int IdempotencyRetentionHours { get; set; } = 24;
}

public sealed class CheckoutService(OrderFlowDbContext db, TimeProvider clock, IOptions<OrderingOptions> options, IInventoryService inventoryService)
{
    private const string Endpoint = "POST /api/v1/orders";
    internal const int MaxAddressSnapshotLength = 2000;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    // The snapshot is only stored and read back, never embedded in HTML, so non-ASCII text can stay unescaped to fit the column.
    private static readonly JsonSerializerOptions AddressSnapshotOptions = new(JsonSerializerDefaults.Web) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public async Task<CheckoutResult> CheckoutAsync(Guid userId, string key, CheckoutRequest request, string correlationId, CancellationToken ct)
    {
        if (key.Length is < 8 or > 100 || request.CartId == Guid.Empty || request.ShippingMethodId == Guid.Empty ||
            request.PaymentMethod is not ("COD" or "BankTransferSimulation" or "CardSimulation") || !ValidAddress(request.DeliveryAddress))
            return new(400, "VALIDATION_FAILED", null);
        var addressSnapshot = JsonSerializer.Serialize(request.DeliveryAddress, AddressSnapshotOptions);
        if (addressSnapshot.Length > MaxAddressSnapshotLength) return new(400, "VALIDATION_FAILED", null);
        var canonical = request with { CouponCode = request.CouponCode?.Trim().ToUpperInvariant() };
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(canonical, JsonOptions))));
        var requestTime = clock.GetUtcNow();
        var existing = await db.IdempotencyRecords.AsNoTracking().SingleOrDefaultAsync(x => x.UserId == userId && x.Endpoint == Endpoint && x.Key == key, ct);
        if (existing is not null && existing.ExpiresAtUtc > requestTime) return Replay(existing, hash);
        try
        {
            // Each invariant has its own guard: the unique index decides the key, and UPDLOCK serializes one cart and one coupon.
            // Serializable added nothing to these, and its range locks on CartItems covered other customers' rows, so unrelated checkouts deadlocked.
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
            if (existing is not null && existing.ExpiresAtUtc <= requestTime)
                await db.IdempotencyRecords.Where(x => x.Id == existing.Id && x.ExpiresAtUtc <= requestTime).ExecuteDeleteAsync(ct);
            var record = new IdempotencyRecord(userId, key, hash, requestTime,
                TimeSpan.FromHours(options.Value.IdempotencyRetentionHours));
            db.IdempotencyRecords.Add(record);
            await db.SaveChangesAsync(ct);
            // Serializes checkouts of one cart: the later one waits, then sees the emptied cart instead of deadlocking on the item delete.
            var cart = await db.Carts.FromSqlInterpolated($"SELECT * FROM dbo.Carts WITH (UPDLOCK, ROWLOCK) WHERE Id = {request.CartId} AND UserId = {userId}")
                .Include(x => x.Items).SingleOrDefaultAsync(ct);
            if (cart is null || cart.Items.Count == 0) return CheckoutResult.Invalid("cartId", "The cart is empty.");
            var method = await db.ShippingMethods.AsNoTracking().SingleOrDefaultAsync(x => x.Id == request.ShippingMethodId && x.IsActive, ct);
            if (method is null) return CheckoutResult.Invalid("shippingMethodId", "This shipping method is not available.");
            var productIds = cart.Items.Select(x => x.ProductId).ToList();
            var products = await (from p in db.Products.AsNoTracking()
                                  join category in db.Categories.AsNoTracking() on p.CategoryId equals category.Id
                                  where productIds.Contains(p.Id) && p.IsActive && category.IsActive
                                  select p).ToDictionaryAsync(x => x.Id, ct);
            if (products.Count != productIds.Count || products.Values.Any(x => x.CurrencyCode != method.CurrencyCode))
                return CheckoutResult.Invalid("cartId", "An item in the cart is no longer for sale.");
            var now = clock.GetUtcNow();
            var orderId = Guid.NewGuid();
            var items = cart.Items.OrderBy(x => x.ProductId).Select(x => new OrderItem(orderId, x.ProductId, products[x.ProductId].Sku,
                products[x.ProductId].Name, products[x.ProductId].Price, x.Quantity)).ToList();
            var subtotal = items.Sum(x => x.LineSubtotal);
            Coupon? coupon = null;
            decimal discount = 0;
            if (!string.IsNullOrWhiteSpace(canonical.CouponCode))
            {
                coupon = await db.Coupons.FromSqlInterpolated($"SELECT * FROM dbo.Coupons WITH (UPDLOCK, ROWLOCK) WHERE Code = {canonical.CouponCode}")
                    .SingleOrDefaultAsync(ct);
                if (coupon is null || !coupon.IsEnabled || now < coupon.ValidFromUtc || now >= coupon.ValidUntilUtc || subtotal < coupon.MinimumSubtotal ||
                    coupon.TotalUsageLimit is int limit && coupon.UsageCount >= limit ||
                    coupon.PerUserUsageLimit is int userLimit && await db.CouponUsages.CountAsync(x => x.CouponId == coupon.Id && x.UserId == userId && x.ReleasedAtUtc == null, ct) >= userLimit)
                    return CheckoutResult.Invalid("couponCode", "This coupon cannot be applied to this order.");
                discount = coupon.CalculateDiscount(subtotal);
                coupon.Use();
                // Apply the discount to one line so line and order totals agree.
                var first = items[0];
                items[0] = new OrderItem(orderId, first.ProductId, first.SkuSnapshot, first.ProductNameSnapshot,
                    first.UnitPrice, first.Quantity, Math.Min(discount, first.LineSubtotal));
                var allocated = items[0].DiscountAmount;
                for (var i = 1; i < items.Count && allocated < discount; i++)
                {
                    var line = items[i]; var amount = Math.Min(discount - allocated, line.LineSubtotal);
                    items[i] = new OrderItem(orderId, line.ProductId, line.SkuSnapshot, line.ProductNameSnapshot, line.UnitPrice, line.Quantity, amount);
                    allocated += amount;
                }
                db.CouponUsages.Add(new CouponUsage(coupon.Id, orderId, userId, discount, now));
            }
            var number = $"OF-{now:yyyyMMdd}-{orderId:N}"[..32];
            var order = Order.Create(orderId, userId, number, request.PaymentMethod, method.Id, method.Name,
                addressSnapshot, method.CurrencyCode, coupon?.Id, method.Fee, items, now, correlationId);
            db.Orders.Add(order);
            var body = JsonSerializer.Serialize(ToResponse(order), JsonOptions);
            record.Complete(orderId, body);
            db.CartItems.RemoveRange(cart.Items);
            cart.Touch(now);
            db.OutboxMessages.Add(OutboxMessage.ForEvent(EventTypes.OrderCreated, orderId, correlationId, null,
                new OrderCreatedPayload(orderId, userId, number, order.CurrencyCode, order.GrandTotal, order.PaymentMethod,
                    order.Items.Select(x => new OrderCreatedItem(x.ProductId, x.Quantity, x.UnitPrice)).ToList()), now));
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            BusinessMetrics.OrdersCreated.Add(1, new KeyValuePair<string, object?>("method", order.PaymentMethod));
            return new(202, null, body);
        }
        catch (Exception ex) when (FindSqlException(ex) is { Number: 2601 or 2627 or 1205 })
        {
            db.ChangeTracker.Clear();
            existing = await db.IdempotencyRecords.AsNoTracking().SingleOrDefaultAsync(x => x.UserId == userId && x.Endpoint == Endpoint && x.Key == key, ct);
            return existing is null
                ? new(409, FindSqlException(ex)?.Number == 1205 ? "CONCURRENCY_CONFLICT" : "REQUEST_IN_PROGRESS", null)
                : Replay(existing, hash);
        }
        catch (DbUpdateConcurrencyException) { return new(409, "CONCURRENCY_CONFLICT", null); }
    }

    public async Task<CheckoutResult> CancelAsync(Guid userId, Guid orderId, string? reason, string correlationId, CancellationToken ct)
    {
        if (reason?.Length > 500) return new(400, "VALIDATION_FAILED", null);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var order = await db.Orders.FromSqlInterpolated($"SELECT * FROM dbo.Orders WITH (UPDLOCK, ROWLOCK) WHERE Id = {orderId} AND UserId = {userId}")
            .SingleOrDefaultAsync(ct);
        if (order is null) return new(404, "ORDER_NOT_FOUND", null);
        if (order.Status == OrderStatus.Cancelled) return new(200, null, JsonSerializer.Serialize(ToResponse(order), JsonOptions));
        try { order.Cancel(userId, reason?.Trim() ?? "", clock.GetUtcNow(), correlationId); }
        catch (InvalidOperationException) { return new(409, "INVALID_STATE_TRANSITION", null); }
        try { await inventoryService.ReleaseForCancellationAsync(orderId, ct); }
        catch (InvalidOperationException) { return new(409, "INVALID_STATE_TRANSITION", null); }
        catch (DbUpdateConcurrencyException) { return new(409, "CONCURRENCY_CONFLICT", null); }
        await ReleaseCouponAsync(db, order, clock.GetUtcNow(), ct);
        db.OutboxMessages.Add(OutboxMessage.ForEvent(EventTypes.OrderCancelled, order.Id, correlationId, null,
            new OrderCancelledPayload(order.Id, userId, CancellationReasons.CustomerRequest), clock.GetUtcNow()));
        try { await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct); }
        catch (DbUpdateConcurrencyException) { return new(409, "CONCURRENCY_CONFLICT", null); }
        BusinessMetrics.OrdersCancelled.Add(1, new KeyValuePair<string, object?>("reason", CancellationReasons.CustomerRequest));
        return new(200, null, JsonSerializer.Serialize(ToResponse(order), JsonOptions));
    }

    // Stock, shipment, and coupon are compensated in this transaction; the refund or void follows from OrderCancelled through the Payment consumer.
    public async Task<CheckoutResult> CancelByStaffAsync(Guid actorId, Guid orderId, string? reason, string correlationId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length > 500) return new(400, "VALIDATION_FAILED", null);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        var order = await db.Orders.FromSqlInterpolated($"SELECT * FROM dbo.Orders WITH (UPDLOCK, ROWLOCK) WHERE Id = {orderId}").SingleOrDefaultAsync(ct);
        if (order is null) return new(404, "ORDER_NOT_FOUND", null);
        if (order.Status == OrderStatus.Cancelled) return new(200, null, JsonSerializer.Serialize(ToResponse(order), JsonOptions));
        // ShippingService holds this order lock while it moves a shipment, so this read cannot race a dispatch.
        var shipment = await db.Shipments.Include(x => x.History).SingleOrDefaultAsync(x => x.OrderId == orderId, ct);
        if (shipment?.Status is ShipmentStatus.Shipped or ShipmentStatus.Delivered or ShipmentStatus.DeliveryFailed) return new(409, "INVALID_STATE_TRANSITION", null);
        var now = clock.GetUtcNow();
        try { order.CancelByStaff(actorId, reason, now, correlationId); }
        catch (InvalidOperationException) { return new(409, "INVALID_STATE_TRANSITION", null); }
        shipment?.TransitionTo(ShipmentStatus.Cancelled, actorId, reason.Trim(), now, correlationId);
        try { await inventoryService.CompensateStaffCancellationAsync(orderId, actorId, correlationId, ct); }
        catch (DbUpdateConcurrencyException) { return new(409, "CONCURRENCY_CONFLICT", null); }
        await ReleaseCouponAsync(db, order, now, ct);
        db.OutboxMessages.Add(OutboxMessage.ForEvent(EventTypes.OrderCancelled, order.Id, correlationId, null,
            new OrderCancelledPayload(order.Id, order.UserId, CancellationReasons.StaffRequest), now));
        try { await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct); }
        catch (DbUpdateConcurrencyException) { return new(409, "CONCURRENCY_CONFLICT", null); }
        BusinessMetrics.OrdersCancelled.Add(1, new KeyValuePair<string, object?>("reason", CancellationReasons.StaffRequest));
        return new(200, null, JsonSerializer.Serialize(ToResponse(order), JsonOptions));
    }

    // Called in the transaction that moves an order to a failed terminal status. Coupon before usage matches checkout's lock order.
    public static async Task ReleaseCouponAsync(OrderFlowDbContext db, Order order, DateTimeOffset now, CancellationToken ct)
    {
        if (order.CouponId is not Guid couponId) return;
        var coupon = await db.Coupons.FromSqlInterpolated($"SELECT * FROM dbo.Coupons WITH (UPDLOCK, ROWLOCK) WHERE Id = {couponId}").SingleAsync(ct);
        var usage = await db.CouponUsages.SingleOrDefaultAsync(x => x.OrderId == order.Id && x.CouponId == couponId && x.ReleasedAtUtc == null, ct);
        if (usage is null) return;
        usage.Release(now);
        coupon.ReturnUse(now);
    }

    public static OrderResponse ToResponse(Order order) => new(order.Id, order.OrderNumber, order.Status.ToString(), order.Subtotal,
        order.DiscountTotal, order.ShippingFee, order.GrandTotal, order.CurrencyCode, $"/api/v1/orders/{order.Id}");
    public static DeliveryAddress? ReadAddress(Order order) => JsonSerializer.Deserialize<DeliveryAddress>(order.AddressSnapshotJson, AddressSnapshotOptions);
    private static CheckoutResult Replay(IdempotencyRecord record, string hash) => record.RequestHash != hash
        ? new(409, "IDEMPOTENCY_KEY_REUSED", null)
        : record.Status == "Completed" ? new(record.ResponseStatusCode ?? 202, null, record.ResponseBody)
        : new(409, "REQUEST_IN_PROGRESS", null);
    private static SqlException? FindSqlException(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
            if (current is SqlException sql) return sql;
        return null;
    }
    private static bool ValidAddress(DeliveryAddress? address) => address is not null &&
        !string.IsNullOrWhiteSpace(address.RecipientName) && address.RecipientName.Length <= 150 &&
        !string.IsNullOrWhiteSpace(address.Phone) && address.Phone.Length <= 30 &&
        !string.IsNullOrWhiteSpace(address.Line1) && address.Line1.Length <= 300 &&
        !string.IsNullOrWhiteSpace(address.Ward) && address.Ward.Length <= 100 &&
        !string.IsNullOrWhiteSpace(address.District) && address.District.Length <= 100 &&
        !string.IsNullOrWhiteSpace(address.Province) && address.Province.Length <= 100 &&
        address.CountryCode?.Length == 2;
}
