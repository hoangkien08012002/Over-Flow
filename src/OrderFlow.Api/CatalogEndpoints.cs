using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using OrderFlow.Domain.Catalog;
using OrderFlow.Domain.Inventory;
using OrderFlow.Infrastructure.Persistence;

namespace OrderFlow.Api;

public static class CatalogEndpoints
{
    public static void MapCatalogEndpoints(this WebApplication app)
    {
        var categories = app.MapGroup("/api/v1/categories");
        categories.MapGet("/", ListCategories);
        categories.MapPost("/", CreateCategory).RequireAuthorization("Admin");
        categories.MapPut("/{id:guid}", UpdateCategory).RequireAuthorization("Admin");
        categories.MapPut("/{id:guid}/activation", SetCategoryActivation).RequireAuthorization("Admin");

        var products = app.MapGroup("/api/v1/products");
        products.MapGet("/", ListProducts);
        products.MapGet("/{id:guid}", GetProduct);
        products.MapPost("/", CreateProduct).RequireAuthorization("Admin");
        products.MapPut("/{id:guid}", UpdateProduct).RequireAuthorization("Admin");
        products.MapPut("/{id:guid}/activation", SetProductActivation).RequireAuthorization("Admin");
    }

    private static async Task<IResult> ListCategories(OrderFlowDbContext db, HttpContext context, CancellationToken cancellationToken)
    {
        var includeInactive = IsAdmin(context) && context.Request.Query["includeInactive"] == "true";
        var rows = await db.Categories.AsNoTracking()
            .Where(x => includeInactive || x.IsActive)
            .OrderBy(x => x.Name).ThenBy(x => x.Id)
            .ToListAsync(cancellationToken);
        return Results.Ok(rows.Select(ToCategoryResponse));
    }

    private static async Task<IResult> CreateCategory(CategoryRequest request, OrderFlowDbContext db, HttpContext context, CancellationToken cancellationToken)
    {
        if (!ValidCategory(request)) return Error(context, 400, "VALIDATION_FAILED", "Invalid category");
        var category = Category.Create(Guid.NewGuid(), request.Name!, request.Slug!, request.Description, DateTimeOffset.UtcNow);
        if (!request.IsActive) category.SetActive(false, DateTimeOffset.UtcNow);
        db.Categories.Add(category);
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception)) { return Error(context, 409, "SLUG_ALREADY_EXISTS", "Category slug already exists"); }
        return Results.Created($"/api/v1/categories/{category.Id}", ToCategoryResponse(category));
    }

    private static async Task<IResult> UpdateCategory(Guid id, CategoryRequest request, OrderFlowDbContext db, HttpContext context, CancellationToken cancellationToken)
    {
        if (!ValidCategory(request) || !TryVersion(request.Version, out var version)) return Error(context, 400, "VALIDATION_FAILED", "Invalid category or version");
        var category = await db.Categories.FindAsync([id], cancellationToken);
        if (category is null) return Error(context, 404, "RESOURCE_NOT_FOUND", "Category not found");
        if (version is not null) db.Entry(category).Property(x => x.RowVersion).OriginalValue = version;
        category.Update(request.Name!, request.Slug!, request.Description, DateTimeOffset.UtcNow);
        category.SetActive(request.IsActive, DateTimeOffset.UtcNow);
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException) { return Error(context, 409, "CONCURRENCY_CONFLICT", "Category was changed"); }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception)) { return Error(context, 409, "SLUG_ALREADY_EXISTS", "Category slug already exists"); }
        return Results.Ok(ToCategoryResponse(category));
    }

    private static async Task<IResult> SetCategoryActivation(Guid id, ActivationRequest request, OrderFlowDbContext db, HttpContext context, CancellationToken cancellationToken)
    {
        if (!TryVersion(request.Version, out var version)) return Error(context, 400, "VALIDATION_FAILED", "Invalid version");
        var category = await db.Categories.FindAsync([id], cancellationToken);
        if (category is null) return Error(context, 404, "RESOURCE_NOT_FOUND", "Category not found");
        if (version is not null) db.Entry(category).Property(x => x.RowVersion).OriginalValue = version;
        category.SetActive(request.IsActive, DateTimeOffset.UtcNow);
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException) { return Error(context, 409, "CONCURRENCY_CONFLICT", "Category was changed"); }
        return Results.Ok(ToCategoryResponse(category));
    }

    private static async Task<IResult> ListProducts(OrderFlowDbContext db, HttpContext context, CancellationToken cancellationToken)
    {
        var query = context.Request.Query;
        if (!int.TryParse(query["page"].FirstOrDefault() ?? "1", out var page) || page < 1 ||
            !int.TryParse(query["pageSize"].FirstOrDefault() ?? "20", out var pageSize) || pageSize is < 1 or > 100 ||
            !decimal.TryParse(query["minPrice"].FirstOrDefault() ?? "0", System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var minPrice) || minPrice < 0 ||
            !decimal.TryParse(query["maxPrice"].FirstOrDefault() ?? "9999999999999999.99", System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var maxPrice) || maxPrice < minPrice || maxPrice > 9999999999999999.99m)
            return Error(context, 400, "VALIDATION_FAILED", "Invalid paging or price range");
        if (!Guid.TryParse(query["categoryId"].FirstOrDefault() ?? Guid.Empty.ToString(), out var categoryId) ||
            !bool.TryParse(query["isAvailable"].FirstOrDefault() ?? "false", out var available) ||
            !bool.TryParse(query["includeInactive"].FirstOrDefault() ?? "false", out var includeInactive))
            return Error(context, 400, "VALIDATION_FAILED", "Invalid filter");
        if (query.ContainsKey("isAvailable") == false) available = false;
        var sort = query["sort"].FirstOrDefault() ?? "name";
        if (sort is not ("name" or "name_desc" or "price" or "price_desc" or "newest"))
            return Error(context, 400, "VALIDATION_FAILED", "Unsupported sort");
        var search = query["search"].FirstOrDefault()?.Trim();
        if (search?.Length > 200) return Error(context, 400, "VALIDATION_FAILED", "Search is too long");
        includeInactive &= IsAdmin(context);

        var products = from product in db.Products.AsNoTracking()
                       join category in db.Categories.AsNoTracking() on product.CategoryId equals category.Id
                       join inventory in db.Inventories.AsNoTracking() on product.Id equals inventory.ProductId into stocks
                       from inventory in stocks.DefaultIfEmpty()
                       where (includeInactive || (product.IsActive && category.IsActive)) &&
                             (categoryId == Guid.Empty || product.CategoryId == categoryId) &&
                             product.Price >= minPrice && product.Price <= maxPrice &&
                             (search == null || product.Name.Contains(search) || product.Sku.Contains(search)) &&
                             (!available || (inventory != null && inventory.OnHand > inventory.Reserved))
                       select new { product, category.Name, Available = inventory == null ? 0 : inventory.OnHand - inventory.Reserved };
        products = sort switch
        {
            "name_desc" => products.OrderByDescending(x => x.product.Name).ThenBy(x => x.product.Id),
            "price" => products.OrderBy(x => x.product.Price).ThenBy(x => x.product.Id),
            "price_desc" => products.OrderByDescending(x => x.product.Price).ThenBy(x => x.product.Id),
            "newest" => products.OrderByDescending(x => x.product.CreatedAtUtc).ThenBy(x => x.product.Id),
            _ => products.OrderBy(x => x.product.Name).ThenBy(x => x.product.Id)
        };
        var totalCount = await products.CountAsync(cancellationToken);
        var rows = await products.Skip((int)Math.Min((long)(page - 1) * pageSize, int.MaxValue)).Take(pageSize).ToListAsync(cancellationToken);
        return Results.Ok(new { items = rows.Select(x => ToProductResponse(x.product, x.Name, x.Available)), page, pageSize, totalCount, totalPages = (int)Math.Ceiling((double)totalCount / pageSize) });
    }

    private static async Task<IResult> GetProduct(Guid id, OrderFlowDbContext db, HttpContext context, CancellationToken cancellationToken)
    {
        var canViewInactive = IsAdmin(context) || context.User.IsInRole("Support");
        var row = await (from product in db.Products.AsNoTracking()
                         join category in db.Categories.AsNoTracking() on product.CategoryId equals category.Id
                         join inventory in db.Inventories.AsNoTracking() on product.Id equals inventory.ProductId into stocks
                         from inventory in stocks.DefaultIfEmpty()
                         where product.Id == id && (canViewInactive || (product.IsActive && category.IsActive))
                         select new { product, CategoryName = category.Name, Available = inventory == null ? 0 : inventory.OnHand - inventory.Reserved })
            .SingleOrDefaultAsync(cancellationToken);
        return row is null ? Error(context, 404, "RESOURCE_NOT_FOUND", "Product not found") : Results.Ok(ToProductResponse(row.product, row.CategoryName, row.Available));
    }

    private static async Task<IResult> CreateProduct(ProductRequest request, OrderFlowDbContext db, HttpContext context, CancellationToken cancellationToken)
    {
        if (!ValidProduct(request)) return Error(context, 400, "VALIDATION_FAILED", "Invalid product");
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        if (!await db.Categories.AsNoTracking().AnyAsync(x => x.Id == request.CategoryId && x.IsActive, cancellationToken))
            return Error(context, 400, "VALIDATION_FAILED", "Active category is required");
        var product = Product.Create(Guid.NewGuid(), request.CategoryId, request.Sku!, request.Name!, request.Description, request.Price, request.CurrencyCode!, DateTimeOffset.UtcNow, request.IsActive);
        db.Products.Add(product);
        db.Inventories.Add(Inventory.Create(Guid.NewGuid(), product.Id, 0, DateTimeOffset.UtcNow));
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception)) { return Error(context, 409, "SKU_ALREADY_EXISTS", "Product SKU already exists"); }
        await transaction.CommitAsync(cancellationToken);
        return Results.Created($"/api/v1/products/{product.Id}", await ProductResponseAsync(db, product, cancellationToken));
    }

    private static async Task<IResult> UpdateProduct(Guid id, ProductRequest request, OrderFlowDbContext db, HttpContext context, CancellationToken cancellationToken)
    {
        if (!ValidProduct(request) || !TryVersion(request.Version, out var version)) return Error(context, 400, "VALIDATION_FAILED", "Invalid product or version");
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        if (!await db.Categories.AsNoTracking().AnyAsync(x => x.Id == request.CategoryId && x.IsActive, cancellationToken))
            return Error(context, 400, "VALIDATION_FAILED", "Active category is required");
        var product = await db.Products.FindAsync([id], cancellationToken);
        if (product is null) return Error(context, 404, "RESOURCE_NOT_FOUND", "Product not found");
        if (version is not null) db.Entry(product).Property(x => x.RowVersion).OriginalValue = version;
        product.Update(request.CategoryId, request.Sku!, request.Name!, request.Description, request.Price, request.CurrencyCode!, DateTimeOffset.UtcNow);
        product.SetActive(request.IsActive, DateTimeOffset.UtcNow);
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException) { return Error(context, 409, "CONCURRENCY_CONFLICT", "Product was changed"); }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception)) { return Error(context, 409, "SKU_ALREADY_EXISTS", "Product SKU already exists"); }
        await transaction.CommitAsync(cancellationToken);
        return Results.Ok(await ProductResponseAsync(db, product, cancellationToken));
    }

    private static async Task<IResult> SetProductActivation(Guid id, ActivationRequest request, OrderFlowDbContext db, HttpContext context, CancellationToken cancellationToken)
    {
        if (!TryVersion(request.Version, out var version)) return Error(context, 400, "VALIDATION_FAILED", "Invalid version");
        var product = await db.Products.FindAsync([id], cancellationToken);
        if (product is null) return Error(context, 404, "RESOURCE_NOT_FOUND", "Product not found");
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        if (request.IsActive && !await db.Categories.AsNoTracking().AnyAsync(x => x.Id == product.CategoryId && x.IsActive, cancellationToken))
            return Error(context, 400, "VALIDATION_FAILED", "Active category is required");
        if (version is not null) db.Entry(product).Property(x => x.RowVersion).OriginalValue = version;
        product.SetActive(request.IsActive, DateTimeOffset.UtcNow);
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException) { return Error(context, 409, "CONCURRENCY_CONFLICT", "Product was changed"); }
        await transaction.CommitAsync(cancellationToken);
        return Results.Ok(await ProductResponseAsync(db, product, cancellationToken));
    }

    private static async Task<ProductResponse> ProductResponseAsync(OrderFlowDbContext db, Product product, CancellationToken cancellationToken)
    {
        var categoryName = await db.Categories.AsNoTracking().Where(x => x.Id == product.CategoryId).Select(x => x.Name).SingleAsync(cancellationToken);
        var available = await db.Inventories.AsNoTracking().Where(x => x.ProductId == product.Id).Select(x => x.OnHand - x.Reserved).SingleOrDefaultAsync(cancellationToken);
        return ToProductResponse(product, categoryName, available);
    }

    private static CategoryResponse ToCategoryResponse(Category x) => new(x.Id, x.Name, x.Slug, x.Description, x.IsActive, Convert.ToBase64String(x.RowVersion));
    private static ProductResponse ToProductResponse(Product x, string categoryName, int available) =>
        new(x.Id, x.CategoryId, categoryName, x.Sku, x.Name, x.Description, x.Price, x.CurrencyCode, x.IsActive, available, Convert.ToBase64String(x.RowVersion));
    private static bool IsAdmin(HttpContext context) => context.User.IsInRole("Admin");
    private static IResult Error(HttpContext context, int status, string code, string title) => Results.Json(new
    {
        type = $"https://orderflow/errors/{code.ToLowerInvariant()}", title, status, code,
        correlationId = context.Items[CorrelationIdMiddleware.ItemKey]?.ToString() ?? ""
    }, statusCode: status, contentType: "application/problem+json");
    private static bool IsUniqueViolation(DbUpdateException exception) => exception.InnerException is SqlException sql && sql.Number is 2601 or 2627;
    private static bool TryVersion(string? input, out byte[]? value)
    {
        value = null;
        if (input is null) return true;
        try { value = Convert.FromBase64String(input); return value.Length == 8; }
        catch (FormatException) { return false; }
    }
    private static bool ValidCategory(CategoryRequest x) =>
        !string.IsNullOrWhiteSpace(x.Name) && x.Name.Trim().Length <= 150 &&
        !string.IsNullOrWhiteSpace(x.Slug) && x.Slug.Trim().Length <= 180 && (x.Description is null || x.Description.Trim().Length <= 1000);
    private static bool ValidProduct(ProductRequest x) =>
        x.CategoryId != Guid.Empty && !string.IsNullOrWhiteSpace(x.Sku) && x.Sku.Trim().Length <= 64 &&
        !string.IsNullOrWhiteSpace(x.Name) && x.Name.Trim().Length <= 200 && (x.Description is null || x.Description.Trim().Length <= 4000) &&
        x.Price >= 0 && decimal.Round(x.Price, 2) == x.Price && x.CurrencyCode is { Length: 3 } && x.CurrencyCode.All(c => c is >= 'A' and <= 'Z');
}

public sealed record CategoryRequest(string? Name, string? Slug, string? Description, bool IsActive, string? Version = null);
public sealed record ActivationRequest(bool IsActive, string? Version = null);
public sealed record ProductRequest(Guid CategoryId, string? Sku, string? Name, string? Description, decimal Price, string? CurrencyCode, bool IsActive, string? Version = null);
public sealed record CategoryResponse(Guid Id, string Name, string Slug, string? Description, bool IsActive, string Version);
public sealed record ProductResponse(Guid Id, Guid CategoryId, string CategoryName, string Sku, string Name, string? Description, decimal Price, string CurrencyCode, bool IsActive, int Available, string Version);
