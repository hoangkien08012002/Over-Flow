namespace OrderFlow.Domain.Catalog;

public sealed class Product
{
    private Product() { }

    public Guid Id { get; private set; }
    public Guid CategoryId { get; private set; }
    public string Sku { get; private set; } = null!;
    public string Name { get; private set; } = null!;
    public string? Description { get; private set; }
    public decimal Price { get; private set; }
    public string CurrencyCode { get; private set; } = null!;
    public bool IsActive { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset UpdatedAtUtc { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public static Product Create(Guid id, Guid categoryId, string sku, string name, string? description, decimal price, string currencyCode, DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(id, Guid.Empty);
        Validate(categoryId, sku, name, description, price, currencyCode, now);
        return new Product { Id = id, CategoryId = categoryId, Sku = sku.Trim(), Name = name.Trim(), Description = description?.Trim(), Price = price, CurrencyCode = currencyCode, IsActive = true, CreatedAtUtc = now, UpdatedAtUtc = now };
    }

    public void Update(Guid categoryId, string sku, string name, string? description, decimal price, string currencyCode, DateTimeOffset now)
    {
        Validate(categoryId, sku, name, description, price, currencyCode, now);
        CategoryId = categoryId;
        Sku = sku.Trim();
        Name = name.Trim();
        Description = description?.Trim();
        Price = price;
        CurrencyCode = currencyCode;
        UpdatedAtUtc = now;
    }

    public void SetActive(bool active, DateTimeOffset now)
    {
        RequireUtc(now);
        IsActive = active;
        UpdatedAtUtc = now;
    }

    private static void Validate(Guid categoryId, string sku, string name, string? description, decimal price, string currencyCode, DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(categoryId, Guid.Empty);
        if (string.IsNullOrWhiteSpace(sku) || sku.Trim().Length > 64) throw new ArgumentException("SKU must be 1 to 64 characters.", nameof(sku));
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 200) throw new ArgumentException("Product name must be 1 to 200 characters.", nameof(name));
        if (description?.Trim().Length > 4000) throw new ArgumentException("Product description is too long.", nameof(description));
        if (price < 0 || decimal.Round(price, 2) != price) throw new ArgumentOutOfRangeException(nameof(price));
        if (currencyCode.Length != 3 || currencyCode.Any(c => c is < 'A' or > 'Z')) throw new ArgumentException("Currency code must be three uppercase letters.", nameof(currencyCode));
        RequireUtc(now);
    }

    private static void RequireUtc(DateTimeOffset value)
    {
        if (value.Offset != TimeSpan.Zero) throw new ArgumentException("Timestamp must be UTC.", nameof(value));
    }
}
