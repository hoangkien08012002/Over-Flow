using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrderFlow.Domain.Catalog;
using OrderFlow.Domain.Inventory;
using OrderFlow.Domain.Ordering;
using OrderFlow.Infrastructure.Identity;
using OrderFlow.Infrastructure.Persistence;
using Xunit;

namespace OrderFlow.IntegrationTests;

public sealed class OrderingSqlTests
{
    [Fact]
    public async Task AC_02_07_checkout_replay_conflict_and_atomic_outbox()
    {
        var password = Environment.GetEnvironmentVariable("MSSQL_SA_PASSWORD");
        if (string.IsNullOrEmpty(password))
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                var path = Path.Combine(dir.FullName, ".env");
                if (File.Exists(path)) { password = File.ReadLines(path).FirstOrDefault(x => x.StartsWith("MSSQL_SA_PASSWORD=", StringComparison.Ordinal))?.Split('=', 2)[1]; break; }
            }
        }
        if (string.IsNullOrEmpty(password)) { Assert.Skip("SQL Server password is not configured."); return; }
        var connection = new SqlConnectionStringBuilder { DataSource = $"127.0.0.1,{Environment.GetEnvironmentVariable("ORDERFLOW_SQL_PORT") ?? "1433"}",
            InitialCatalog = $"OrderFlow_Phase6_{Guid.NewGuid():N}", UserID = "sa", Password = password, TrustServerCertificate = true }.ConnectionString;
        var options = new DbContextOptionsBuilder<OrderFlowDbContext>().UseSqlServer(connection).Options;
        await using var setup = new OrderFlowDbContext(options);
        var migrated = false;
        var ct = TestContext.Current.CancellationToken;
        try
        {
            await setup.Database.MigrateAsync(ct); migrated = true;
            var now = DateTimeOffset.UtcNow;
            var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = "phase6", NormalizedUserName = "PHASE6",
                Email = "phase6@example.invalid", NormalizedEmail = "PHASE6@EXAMPLE.INVALID", FirstName = "Phase", LastName = "Six", CreatedAtUtc = now };
            var category = Category.Create(Guid.NewGuid(), "Test", "phase6-test", null, now);
            var product = Product.Create(Guid.NewGuid(), category.Id, "P6-1", "Original", null, 10m, "VND", now);
            var inventory = Inventory.Create(Guid.NewGuid(), product.Id, 0, now);
            var adjustment = inventory.Adjust(10, "SEED", null, user.Id, "phase6-test", now);
            var cart = new Cart(user.Id, now);
            cart.Items.Add(new CartItem(cart.Id, product.Id, 2, now));
            var coupon = Coupon.Create(Guid.NewGuid(), "SAVE5", CouponDiscountType.Fixed, 5m, 10m,
                now.AddDays(-1), now.AddDays(1), 1, 1, now);
            setup.Users.Add(user); setup.Categories.Add(category); setup.Products.Add(product); setup.Inventories.Add(inventory);
            setup.InventoryAdjustments.Add(adjustment); setup.Carts.Add(cart); setup.Coupons.Add(coupon);
            await setup.SaveChangesAsync(ct);
            var shippingId = Guid.Parse("5b6e892f-486b-4471-9d84-4d33b13cbe01");
            var request = new CheckoutRequest(cart.Id, shippingId, "COD", "save5",
                new DeliveryAddress("Buyer", "0900000000", "Line 1", "Ward", "District", "Province", "VN"));
            await using var firstDb = new OrderFlowDbContext(options);
            var first = await NewCheckoutService(firstDb).CheckoutAsync(user.Id, "same-key-123", request, "phase6-test", ct);
            Assert.Equal(202, first.StatusCode);
            await using var replayDb = new OrderFlowDbContext(options);
            var replayService = NewCheckoutService(replayDb);
            var replay = await replayService.CheckoutAsync(user.Id, "same-key-123", request, "phase6-test", ct);
            Assert.Equal(first.Body, replay.Body);
            var different = await replayService.CheckoutAsync(user.Id, "same-key-123", request with { PaymentMethod = "CardSimulation" }, "phase6-test", ct);
            Assert.Equal("IDEMPOTENCY_KEY_REUSED", different.Code);
            Assert.Single(await setup.Orders.AsNoTracking().ToListAsync(ct));
            Assert.Single(await setup.OutboxMessages.AsNoTracking().Where(x => x.EventType == "OrderCreated").ToListAsync(ct));
            var order = await setup.Orders.AsNoTracking().Include(x => x.Items).Include(x => x.History).SingleAsync(ct);
            Assert.Equal(20m, order.Subtotal);
            Assert.Equal(30015m, order.GrandTotal);
            Assert.Equal(5m, order.DiscountTotal);
            Assert.Single(await setup.CouponUsages.AsNoTracking().ToListAsync(ct));
            Assert.Equal("Original", order.Items.Single().ProductNameSnapshot);
            Assert.Single(order.History);
            await using (var reserveDb = new OrderFlowDbContext(options))
            {
                var reservation = await new InventoryService(reserveDb, TimeProvider.System, Options.Create(new InventoryOptions()))
                    .ReserveAsync(order.Id, product.Id, 2, ct);
                Assert.Equal(OrderFlow.Application.Inventory.ReserveOutcome.Reserved, reservation.Outcome);
            }
            await using (var stateDb = new OrderFlowDbContext(options))
            {
                var awaiting = await stateDb.Orders.SingleAsync(x => x.Id == order.Id, ct);
                awaiting.Transition(OrderStatus.AwaitingPayment, DateTimeOffset.UtcNow, "phase6-test", eventId: Guid.NewGuid());
                await stateDb.SaveChangesAsync(ct);
            }
            product.Update(category.Id, "P6-1", "Renamed", null, 99m, "VND", DateTimeOffset.UtcNow);
            await setup.SaveChangesAsync(ct);
            var snapshot = await setup.OrderItems.AsNoTracking().SingleAsync(ct);
            Assert.Equal("Original", snapshot.ProductNameSnapshot);
            Assert.Equal(10m, snapshot.UnitPrice);
            await using (var factory = new OrderingFactory(connection))
            {
                using var stranger = factory.CreateClient();
                var registration = await stranger.PostAsJsonAsync("/api/v1/auth/register", new
                    { email = "stranger@example.invalid", password = "StrongPassword123!", firstName = "Other", lastName = "Customer" }, ct);
                Assert.Equal(HttpStatusCode.Created, registration.StatusCode);
                var login = await stranger.PostAsJsonAsync("/api/v1/auth/login", new
                    { email = "stranger@example.invalid", password = "StrongPassword123!" }, ct);
                using var loginBody = JsonDocument.Parse(await login.Content.ReadAsStringAsync(ct));
                stranger.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", loginBody.RootElement.GetProperty("accessToken").GetString());
                var foreign = await stranger.GetAsync($"/api/v1/orders/{order.Id}", ct);
                Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
                Assert.Contains("ORDER_NOT_FOUND", await foreign.Content.ReadAsStringAsync(ct));
            }
            var unauthorized = await replayService.CancelAsync(Guid.NewGuid(), order.Id, "No", "phase6-test", ct);
            Assert.Equal("ORDER_NOT_FOUND", unauthorized.Code);
            await using var cancelDb = new OrderFlowDbContext(options);
            var cancelService = NewCheckoutService(cancelDb);
            var cancel = await cancelService.CancelAsync(user.Id, order.Id, "Changed mind", "phase6-test", ct);
            Assert.Equal(200, cancel.StatusCode);
            Assert.Equal(200, (await cancelService.CancelAsync(user.Id, order.Id, "Again", "phase6-test", ct)).StatusCode);
            Assert.Single(await setup.OutboxMessages.AsNoTracking().Where(x => x.EventType == "OrderCancelled").ToListAsync(ct));
            Assert.Equal(0, (await setup.Inventories.AsNoTracking().SingleAsync(x => x.ProductId == product.Id, ct)).Reserved);
            Assert.Equal(ReservationStatus.Released,
                (await setup.InventoryReservations.AsNoTracking().SingleAsync(x => x.OrderId == order.Id, ct)).Status);
            await using (var invalidDb = new OrderFlowDbContext(options))
            {
                var cancelled = await invalidDb.Orders.Include(x => x.History).SingleAsync(x => x.Id == order.Id, ct);
                var historyCount = cancelled.History.Count;
                Assert.Throws<InvalidOperationException>(() => cancelled.Transition(OrderStatus.Paid, DateTimeOffset.UtcNow, "phase6-test"));
                await invalidDb.SaveChangesAsync(ct);
                Assert.Equal(OrderStatus.Cancelled, (await invalidDb.Orders.AsNoTracking().SingleAsync(x => x.Id == order.Id, ct)).Status);
                Assert.Equal(historyCount, await invalidDb.OrderStatusHistories.AsNoTracking().CountAsync(x => x.OrderId == order.Id, ct));
            }
            await using var raceDbA = new OrderFlowDbContext(options);
            await using var raceDbB = new OrderFlowDbContext(options);
            setup.CartItems.Add(new CartItem(cart.Id, product.Id, 2, now));
            await setup.SaveChangesAsync(ct);
            var raceRequest = request with { CouponCode = null };
            var raceA = NewCheckoutService(raceDbA)
                .CheckoutAsync(user.Id, "race-key-123", raceRequest, "phase6-race", ct);
            var raceB = NewCheckoutService(raceDbB)
                .CheckoutAsync(user.Id, "race-key-123", raceRequest, "phase6-race", ct);
            var race = await Task.WhenAll(raceA, raceB);
            Assert.Contains(race, x => x.StatusCode == 202);
            Assert.Equal(2, await setup.Orders.AsNoTracking().CountAsync(ct));
            Assert.Equal(2, await setup.OutboxMessages.AsNoTracking().CountAsync(x => x.EventType == "OrderCreated", ct));
            var secondUser = new ApplicationUser { Id = Guid.NewGuid(), UserName = "phase6-second", NormalizedUserName = "PHASE6-SECOND",
                Email = "phase6-second@example.invalid", NormalizedEmail = "PHASE6-SECOND@EXAMPLE.INVALID",
                FirstName = "Second", LastName = "Customer", CreatedAtUtc = now };
            var secondCart = new Cart(secondUser.Id, now);
            secondCart.Items.Add(new CartItem(secondCart.Id, product.Id, 2, now));
            var lastCoupon = Coupon.Create(Guid.NewGuid(), "LAST5", CouponDiscountType.Fixed, 5m, 10m,
                now.AddDays(-1), now.AddDays(1), 1, 1, now);
            setup.CartItems.Add(new CartItem(cart.Id, product.Id, 2, now));
            setup.Users.Add(secondUser); setup.Carts.Add(secondCart); setup.Coupons.Add(lastCoupon);
            await setup.SaveChangesAsync(ct);
            await using var couponDbA = new OrderFlowDbContext(options);
            await using var couponDbB = new OrderFlowDbContext(options);
            var couponRequest = request with { CouponCode = "LAST5" };
            var couponA = NewCheckoutService(couponDbA).CheckoutAsync(user.Id, "coupon-key-a", couponRequest, "coupon-race", ct);
            var couponB = NewCheckoutService(couponDbB).CheckoutAsync(secondUser.Id, "coupon-key-b",
                couponRequest with { CartId = secondCart.Id }, "coupon-race", ct);
            var couponResults = await Task.WhenAll(couponA, couponB);
            Assert.Single(couponResults, x => x.StatusCode == 202);
            Assert.Single(await setup.CouponUsages.AsNoTracking().Where(x => x.CouponId == lastCoupon.Id).ToListAsync(ct));
            Assert.Equal(3, await setup.Orders.AsNoTracking().CountAsync(ct));
            Assert.Equal(3, await setup.OutboxMessages.AsNoTracking().CountAsync(x => x.EventType == "OrderCreated", ct));
        }
        finally { if (migrated) await setup.Database.EnsureDeletedAsync(ct); }
    }

    private static CheckoutService NewCheckoutService(OrderFlowDbContext db) => new(db, TimeProvider.System,
        Options.Create(new OrderingOptions()), new InventoryService(db, TimeProvider.System, Options.Create(new InventoryOptions())));

    private sealed class OrderingFactory(string connection) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Warning));
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:OrderFlow"] = connection,
                ["Jwt:Issuer"] = "orderflow-tests", ["Jwt:Audience"] = "orderflow-api",
                ["Jwt:SigningKey"] = "TEST_ONLY_orderflow_integration_signing_key_32_bytes_min",
                ["Jwt:AccessTokenMinutes"] = "15", ["Jwt:RefreshTokenDays"] = "30"
            }));
        }
    }
}
