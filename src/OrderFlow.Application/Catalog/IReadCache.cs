namespace OrderFlow.Application.Catalog;

public sealed record CacheEntryPolicy(string Name, TimeSpan MinTtl, TimeSpan MaxTtl);

// Non-authoritative read cache. Inventory, order, payment, coupon and idempotency decisions must read SQL Server instead.
public interface IReadCache
{
    // Falls back to load when the cache cannot serve the read; only exceptions from load reach the caller.
    Task<T?> GetOrLoadAsync<T>(CacheEntryPolicy policy, string key, Func<CancellationToken, Task<T?>> load, CancellationToken cancellationToken) where T : class;

    // Call after the SQL commit. Takes no token because a client disconnect must not skip invalidation; it never throws for a cache failure.
    Task InvalidateAsync(IReadOnlyCollection<string> keys);
}

public static class CatalogCache
{
    public const string CategoriesKey = "categories:v1";
    public static readonly CacheEntryPolicy Product = new("product", TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15));
    public static readonly CacheEntryPolicy Categories = new("categories", TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(60));

    public static string ProductKey(Guid productId) => $"product:{productId}:v1";
}

public sealed record CachedCategory(Guid Id, string Name, string Slug, string? Description, bool IsActive, string Version);

// Excludes available stock: the API contract reports it from SQL Server on every read.
public sealed record CachedProduct(Guid Id, Guid CategoryId, string CategoryName, bool CategoryIsActive, string Sku, string Name, string? Description,
    decimal Price, string CurrencyCode, bool IsActive, string Version);
