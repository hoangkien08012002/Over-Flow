namespace OrderFlow.Domain.Ordering;

public sealed class Cart
{
    private Cart() { }
    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset UpdatedAtUtc { get; private set; }
    public List<CartItem> Items { get; private set; } = [];
    public Cart(Guid userId, DateTimeOffset now) { Id = Guid.NewGuid(); UserId = userId; CreatedAtUtc = now; UpdatedAtUtc = now; }
    public void Touch(DateTimeOffset now) => UpdatedAtUtc = now;
}

public sealed class CartItem
{
    private CartItem() { }
    public Guid Id { get; private set; }
    public Guid CartId { get; private set; }
    public Guid ProductId { get; private set; }
    public int Quantity { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset UpdatedAtUtc { get; private set; }
    public CartItem(Guid cartId, Guid productId, int quantity, DateTimeOffset now)
    { Id = Guid.NewGuid(); CartId = cartId; ProductId = productId; SetQuantity(quantity, now); CreatedAtUtc = now; }
    public void SetQuantity(int quantity, DateTimeOffset now)
    { if (quantity is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(quantity)); Quantity = quantity; UpdatedAtUtc = now; }
}

public sealed class ShippingMethod
{
    private ShippingMethod() { }
    public Guid Id { get; private set; }
    public string Code { get; private set; } = null!;
    public string Name { get; private set; } = null!;
    public decimal Fee { get; private set; }
    public string CurrencyCode { get; private set; } = null!;
    public bool IsActive { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset UpdatedAtUtc { get; private set; }
    public byte[] RowVersion { get; private set; } = [];
    public ShippingMethod(Guid id, string code, string name, decimal fee, string currency, DateTimeOffset now)
    { Id = id; Code = code; Name = name; Fee = fee; CurrencyCode = currency; IsActive = true; CreatedAtUtc = now; UpdatedAtUtc = now; }
}

public enum CouponDiscountType { Fixed, Percentage }
public sealed class Coupon
{
    private Coupon() { }
    public Guid Id { get; private set; }
    public string Code { get; private set; } = null!;
    public CouponDiscountType DiscountType { get; private set; }
    public decimal DiscountValue { get; private set; }
    public decimal MinimumSubtotal { get; private set; }
    public DateTimeOffset ValidFromUtc { get; private set; }
    public DateTimeOffset ValidUntilUtc { get; private set; }
    public int? TotalUsageLimit { get; private set; }
    public int? PerUserUsageLimit { get; private set; }
    public int UsageCount { get; private set; }
    public bool IsEnabled { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset UpdatedAtUtc { get; private set; }
    public byte[] RowVersion { get; private set; } = [];
    public static Coupon Create(Guid id, string code, CouponDiscountType type, decimal value, decimal minimumSubtotal,
        DateTimeOffset validFromUtc, DateTimeOffset validUntilUtc, int? totalLimit, int? perUserLimit, DateTimeOffset now)
    {
        if (id == Guid.Empty || string.IsNullOrWhiteSpace(code) || code.Trim().Length > 100 || value <= 0 ||
            type == CouponDiscountType.Percentage && value > 100 || minimumSubtotal < 0 || validUntilUtc <= validFromUtc ||
            totalLimit is <= 0 || perUserLimit is <= 0 || now.Offset != TimeSpan.Zero ||
            validFromUtc.Offset != TimeSpan.Zero || validUntilUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("Invalid coupon.");
        return new Coupon { Id = id, Code = code.Trim().ToUpperInvariant(), DiscountType = type, DiscountValue = value,
            MinimumSubtotal = minimumSubtotal, ValidFromUtc = validFromUtc, ValidUntilUtc = validUntilUtc,
            TotalUsageLimit = totalLimit, PerUserUsageLimit = perUserLimit, IsEnabled = true, CreatedAtUtc = now, UpdatedAtUtc = now };
    }
    public decimal CalculateDiscount(decimal subtotal) => Math.Min(subtotal, decimal.Round(
        DiscountType == CouponDiscountType.Fixed ? DiscountValue : subtotal * DiscountValue / 100m,
        2, MidpointRounding.AwayFromZero));
    public void Use() { if (TotalUsageLimit is int limit && UsageCount >= limit) throw new InvalidOperationException("Coupon exhausted."); UsageCount++; }
}

public sealed class CouponUsage
{
    private CouponUsage() { }
    public Guid Id { get; private set; }
    public Guid CouponId { get; private set; }
    public Guid OrderId { get; private set; }
    public Guid UserId { get; private set; }
    public decimal DiscountAmount { get; private set; }
    public DateTimeOffset UsedAtUtc { get; private set; }
    public CouponUsage(Guid couponId, Guid orderId, Guid userId, decimal discount, DateTimeOffset now)
    { Id = Guid.NewGuid(); CouponId = couponId; OrderId = orderId; UserId = userId; DiscountAmount = discount; UsedAtUtc = now; }
}

public sealed class IdempotencyRecord
{
    private IdempotencyRecord() { }
    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public string Endpoint { get; private set; } = null!;
    public string Key { get; private set; } = null!;
    public string RequestHash { get; private set; } = null!;
    public string Status { get; private set; } = null!;
    public Guid? ResourceId { get; private set; }
    public int? ResponseStatusCode { get; private set; }
    public string? ResponseBody { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset ExpiresAtUtc { get; private set; }
    public IdempotencyRecord(Guid userId, string key, string hash, DateTimeOffset now, TimeSpan retention)
    { Id = Guid.NewGuid(); UserId = userId; Endpoint = "POST /api/v1/orders"; Key = key; RequestHash = hash;
      Status = "Processing"; CreatedAtUtc = now; ExpiresAtUtc = now.Add(retention); }
    public void Complete(Guid orderId, string response)
    { if (Status != "Processing") throw new InvalidOperationException("Idempotency record already completed.");
      Status = "Completed"; ResourceId = orderId; ResponseStatusCode = 202; ResponseBody = response; }
}
