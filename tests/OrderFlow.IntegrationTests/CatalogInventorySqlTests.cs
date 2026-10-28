using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using OrderFlow.Domain.Catalog;
using OrderFlow.Domain.Inventory;
using OrderFlow.Infrastructure.Persistence;
using Microsoft.Extensions.Options;
using OrderFlow.Application.Inventory;
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
            Assert.Equal(3, (await db.Database.GetAppliedMigrationsAsync(TestContext.Current.CancellationToken)).Count());
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

    [Fact]
    public async Task AC_01_two_orders_competing_for_last_unit_leave_one_reservation()
    {
        await WithDatabase(async db =>
        {
            var product = await SeedInventoryAsync(db, 1);
            var connection = db.Database.GetConnectionString()!;
            for (var round = 0; round < 5; round++)
            {
                await using var first = NewContext(connection);
                await using var second = NewContext(connection);
                var service1 = NewService(first);
                var service2 = NewService(second);
                var results = await Task.WhenAll(
                    service1.ReserveAsync(Guid.NewGuid(), product.Id, 1, TestContext.Current.CancellationToken),
                    service2.ReserveAsync(Guid.NewGuid(), product.Id, 1, TestContext.Current.CancellationToken));
                Assert.Single(results, x => x.Outcome == ReserveOutcome.Reserved);
                Assert.Single(results, x => x.Outcome == ReserveOutcome.InsufficientStock);
                var snapshot = await NewService(db).GetAsync(product.Id, TestContext.Current.CancellationToken);
                Assert.Equal(0, snapshot!.Available);
                Assert.Equal(1, snapshot.Reserved);
                var winner = results.Single(x => x.Outcome == ReserveOutcome.Reserved).Reservation!;
                await NewService(db).TransitionAsync(winner.OrderId, product.Id, ReservationStatus.Released, TestContext.Current.CancellationToken);
            }
        });
    }

    [Fact]
    public async Task Adjustment_reservation_and_terminal_replays_preserve_quantities_and_audit()
    {
        await WithDatabase(async db =>
        {
            var product = await SeedInventoryAsync(db, 0);
            var service = NewService(db);
            var actor = Guid.NewGuid();
            db.Users.Add(new OrderFlow.Infrastructure.Identity.ApplicationUser { Id = actor, UserName = $"inv-{actor:N}", FirstName = "Inventory", LastName = "Manager", CreatedAtUtc = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            await service.AdjustAsync(product.Id, 2, "RECEIPT", null, actor, "adjust-1", TestContext.Current.CancellationToken);
            await service.AdjustAsync(product.Id, -1, "CORRECTION", null, actor, "adjust-2", TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.AdjustAsync(product.Id, -2, "CORRECTION", null, actor, "adjust-3", TestContext.Current.CancellationToken));
            Assert.Equal(2, await db.InventoryAdjustments.CountAsync(TestContext.Current.CancellationToken));

            var orderId = Guid.NewGuid();
            var reserved = await service.ReserveAsync(orderId, product.Id, 1, TestContext.Current.CancellationToken);
            Assert.Equal(ReserveOutcome.Reserved, reserved.Outcome);
            Assert.Equal(ReserveOutcome.AlreadyReserved, (await service.ReserveAsync(orderId, product.Id, 1, TestContext.Current.CancellationToken)).Outcome);
            Assert.Single(await db.OutboxMessages.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken));
            await service.TransitionAsync(orderId, product.Id, ReservationStatus.Committed, TestContext.Current.CancellationToken);
            await service.TransitionAsync(orderId, product.Id, ReservationStatus.Committed, TestContext.Current.CancellationToken);
            var stock = await service.GetAsync(product.Id, TestContext.Current.CancellationToken);
            Assert.Equal(0, stock!.OnHand);
            Assert.Equal(0, stock.Reserved);
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.TransitionAsync(orderId, product.Id, ReservationStatus.Released, TestContext.Current.CancellationToken));
        });
    }

    [Fact]
    public async Task Expiry_and_release_restore_availability_once()
    {
        await WithDatabase(async db =>
        {
            var product = await SeedInventoryAsync(db, 2);
            var clock = new MutableClock(DateTimeOffset.UtcNow);
            var service = NewService(db, clock);
            var expiredOrder = Guid.NewGuid();
            var releasedOrder = Guid.NewGuid();
            await service.ReserveAsync(expiredOrder, product.Id, 1, TestContext.Current.CancellationToken);
            await service.ReserveAsync(releasedOrder, product.Id, 1, TestContext.Current.CancellationToken);
            Assert.Single(await db.OutboxMessages.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken));
            await service.TransitionAsync(releasedOrder, product.Id, ReservationStatus.Released, TestContext.Current.CancellationToken);
            await service.TransitionAsync(releasedOrder, product.Id, ReservationStatus.Released, TestContext.Current.CancellationToken);
            clock.Advance(TimeSpan.FromMinutes(16));
            Assert.Equal(1, await service.ExpireDueAsync(10, TestContext.Current.CancellationToken));
            Assert.Equal(0, await service.ExpireDueAsync(10, TestContext.Current.CancellationToken));
            var stock = await service.GetAsync(product.Id, TestContext.Current.CancellationToken);
            Assert.Equal(2, stock!.Available);
            Assert.Equal(0, stock.Reserved);
        });
    }

    private static InventoryService NewService(OrderFlowDbContext db, TimeProvider? clock = null) =>
        new(db, clock ?? TimeProvider.System, Options.Create(new InventoryOptions()));

    private static async Task<Product> SeedInventoryAsync(OrderFlowDbContext db, int onHand)
    {
        var category = NewCategory();
        var product = NewProduct(category.Id, $"INV-{Guid.NewGuid():N}");
        var inventory = Inventory.Create(Guid.NewGuid(), product.Id, 0, DateTimeOffset.UtcNow);
        db.Categories.Add(category);
        db.Products.Add(product);
        db.Inventories.Add(inventory);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        if (onHand > 0)
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE Inventories SET OnHand = {onHand} WHERE Id = {inventory.Id}", TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();
        return product;
    }

    private sealed class MutableClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
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
