using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using OrderFlow.Domain.Catalog;
using OrderFlow.Domain.Inventory;
using OrderFlow.Infrastructure.Persistence;
using Xunit;

namespace OrderFlow.IntegrationTests;

public sealed class CatalogInventorySqlTests
{
    [Fact]
    public async Task Migration_creates_schema_and_stable_roles()
    {
        await WithDatabase(async db =>
        {
            Assert.Equal(4, await db.Roles.CountAsync(TestContext.Current.CancellationToken));
            Assert.True(await db.Database.CanConnectAsync(TestContext.Current.CancellationToken));
            Assert.Equal(2, (await db.Database.GetAppliedMigrationsAsync(TestContext.Current.CancellationToken)).Count());
        });
    }

    [Fact]
    public async Task Duplicate_sku_is_rejected_by_sql_server()
    {
        await WithDatabase(async db =>
        {
            var category = NewCategory();
            db.Categories.Add(category);
            db.Products.Add(NewProduct(category.Id, "SAME-SKU"));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            db.Products.Add(NewProduct(category.Id, "SAME-SKU"));
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(TestContext.Current.CancellationToken));
        });
    }

    [Fact]
    public async Task Sql_constraints_reject_negative_and_over_reserved_inventory()
    {
        await WithDatabase(async db =>
        {
            var category = NewCategory();
            var product = NewProduct(category.Id, "STOCK-1");
            var inventory = Inventory.Create(Guid.NewGuid(), product.Id, 2, DateTimeOffset.UtcNow);
            db.Categories.Add(category);
            db.Products.Add(product);
            db.Inventories.Add(inventory);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);

            await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE Inventories SET OnHand = -1 WHERE Id = {inventory.Id}", TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE Inventories SET Reserved = 1 WHERE Id = {inventory.Id}", TestContext.Current.CancellationToken));
            Assert.Equal(0, (await db.Inventories.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken)).Available);
        });
    }

    [Fact]
    public async Task Rowversion_rejects_a_conflicting_inventory_update()
    {
        await WithDatabase(async db =>
        {
            var category = NewCategory();
            var product = NewProduct(category.Id, "STOCK-2");
            var inventory = Inventory.Create(Guid.NewGuid(), product.Id, 2, DateTimeOffset.UtcNow);
            db.Categories.Add(category);
            db.Products.Add(product);
            db.Inventories.Add(inventory);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);

            await using var first = NewContext(db.Database.GetConnectionString()!);
            await using var second = NewContext(db.Database.GetConnectionString()!);
            var one = await first.Inventories.SingleAsync(TestContext.Current.CancellationToken);
            var two = await second.Inventories.SingleAsync(TestContext.Current.CancellationToken);
            one.SetLowStockThreshold(3, DateTimeOffset.UtcNow);
            two.SetLowStockThreshold(4, DateTimeOffset.UtcNow);
            await first.SaveChangesAsync(TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync(TestContext.Current.CancellationToken));
        });
    }

    private static async Task WithDatabase(Func<OrderFlowDbContext, Task> test)
    {
        var password = Environment.GetEnvironmentVariable("MSSQL_SA_PASSWORD") ?? ReadLocalPassword();
        if (string.IsNullOrWhiteSpace(password))
        {
            Assert.Skip("Set MSSQL_SA_PASSWORD or create .env to run SQL Server integration tests.");
            return;
        }

        var port = Environment.GetEnvironmentVariable("ORDERFLOW_SQL_PORT") ?? "1433";
        var connection = new SqlConnectionStringBuilder
        {
            DataSource = $"127.0.0.1,{port}",
            InitialCatalog = $"OrderFlow_Phase2_{Guid.NewGuid():N}",
            UserID = "sa",
            Password = password,
            TrustServerCertificate = true
        }.ConnectionString;
        await using var db = NewContext(connection);
        var migrated = false;
        try
        {
            await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
            migrated = true;
            await test(db);
        }
        finally
        {
            if (migrated)
            {
                await db.Database.EnsureDeletedAsync(TestContext.Current.CancellationToken);
            }
        }
    }

    private static string? ReadLocalPassword()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var file = Path.Combine(directory.FullName, ".env");
            if (!File.Exists(file)) continue;
            return File.ReadLines(file)
                .FirstOrDefault(line => line.StartsWith("MSSQL_SA_PASSWORD=", StringComparison.Ordinal))?
                .Split('=', 2)[1];
        }
        return null;
    }

    private static OrderFlowDbContext NewContext(string connection) => new(
        new DbContextOptionsBuilder<OrderFlowDbContext>().UseSqlServer(connection).Options);

    private static Category NewCategory() => Category.Create(Guid.NewGuid(), "Hardware", $"hardware-{Guid.NewGuid():N}", null, DateTimeOffset.UtcNow);

    private static Product NewProduct(Guid categoryId, string sku) => Product.Create(Guid.NewGuid(), categoryId, sku, "Widget", null, 10.25m, "VND", DateTimeOffset.UtcNow);
}
