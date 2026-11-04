using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrderFlow.Application.Inventory;
using OrderFlow.Domain.Catalog;
using OrderFlow.Domain.Inventory;
using OrderFlow.Domain.Ordering;
using OrderFlow.Infrastructure.Identity;
using OrderFlow.Infrastructure.Persistence;
using Xunit;

namespace OrderFlow.IntegrationTests;

public sealed class OrderingApiSqlTests
{
    private static readonly Guid ShippingMethodId = Guid.Parse("5b6e892f-486b-4471-9d84-4d33b13cbe01");
    private static readonly DeliveryAddress Address = new("Nguyễn Hoàng Kiên", "0900000000", "Số 1 Đường Trần Phú", "Phường Điện Biên", "Quận Ba Đình", "Thanh Hóa", "VN");

    [Fact]
    public Task FR_CART_001_002_003_cart_lifecycle_recalculates_and_hides_foreign_items() => WithDatabaseAsync(async (connection, _, setup, seed, ct) =>
    {
        await using var factory = new OrderingFactory(connection);
        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/cart", ct)).StatusCode);
        using var customer = await CustomerClientAsync(factory, "cart-owner@example.invalid", ct);
        using var stranger = await CustomerClientAsync(factory, "cart-stranger@example.invalid", ct);

        await AddItemAsync(customer, seed.Stocked.Id, 2, ct);
        await AddItemAsync(customer, seed.WithoutStock.Id, 1, ct);
        using (var cart = await AddItemAsync(customer, seed.Stocked.Id, 1, ct))
        {
            Assert.Equal(2, cart.RootElement.GetProperty("items").GetArrayLength());
            Assert.Equal(3, Line(cart, seed.Stocked.Id).GetProperty("quantity").GetInt32());
            Assert.Equal(50m, cart.RootElement.GetProperty("subtotal").GetDecimal());
            Assert.False(Line(cart, seed.Stocked.Id).GetProperty("unavailable").GetBoolean());
            Assert.True(Line(cart, seed.WithoutStock.Id).GetProperty("unavailable").GetBoolean());
        }
        Assert.Equal(1, await setup.Carts.AsNoTracking().CountAsync(ct));

        await setup.Database.ExecuteSqlInterpolatedAsync($"UPDATE dbo.Products SET Price = 15 WHERE Id = {seed.Stocked.Id}", ct);
        using (var cart = await ReadJsonAsync(await customer.GetAsync("/api/v1/cart", ct), ct))
        {
            Assert.Equal(15m, Line(cart, seed.Stocked.Id).GetProperty("currentUnitPrice").GetDecimal());
            Assert.Equal(65m, cart.RootElement.GetProperty("subtotal").GetDecimal());
        }
        await setup.Database.ExecuteSqlInterpolatedAsync($"UPDATE dbo.Products SET IsActive = 0 WHERE Id = {seed.Stocked.Id}", ct);
        using (var cart = await ReadJsonAsync(await customer.GetAsync("/api/v1/cart", ct), ct))
            Assert.True(Line(cart, seed.Stocked.Id).GetProperty("unavailable").GetBoolean());
        Assert.Equal(HttpStatusCode.BadRequest, (await customer.PostAsJsonAsync("/api/v1/cart/items", new { productId = seed.Stocked.Id, quantity = 1 }, ct)).StatusCode);
        await setup.Database.ExecuteSqlInterpolatedAsync($"UPDATE dbo.Products SET IsActive = 1 WHERE Id = {seed.Stocked.Id}", ct);

        foreach (var quantity in new[] { 0, 101, 98 })
            Assert.Equal(HttpStatusCode.BadRequest, (await customer.PostAsJsonAsync("/api/v1/cart/items", new { productId = seed.Stocked.Id, quantity }, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await customer.PostAsJsonAsync("/api/v1/cart/items", new { productId = Guid.NewGuid(), quantity = 1 }, ct)).StatusCode);

        var itemId = await setup.CartItems.AsNoTracking().Where(x => x.ProductId == seed.WithoutStock.Id).Select(x => x.Id).SingleAsync(ct);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.PutAsJsonAsync($"/api/v1/cart/items/{itemId}", new { quantity = 5 }, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.DeleteAsync($"/api/v1/cart/items/{itemId}", ct)).StatusCode);
        Assert.Equal(1, (await setup.CartItems.AsNoTracking().SingleAsync(x => x.Id == itemId, ct)).Quantity);

        Assert.Equal(HttpStatusCode.OK, (await customer.PutAsJsonAsync($"/api/v1/cart/items/{itemId}", new { quantity = 5 }, ct)).StatusCode);
        Assert.Equal(5, (await setup.CartItems.AsNoTracking().SingleAsync(x => x.Id == itemId, ct)).Quantity);
        Assert.Equal(HttpStatusCode.BadRequest, (await customer.PutAsJsonAsync($"/api/v1/cart/items/{itemId}", new { quantity = 0 }, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await customer.DeleteAsync($"/api/v1/cart/items/{itemId}", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await customer.DeleteAsync($"/api/v1/cart/items/{itemId}", ct)).StatusCode);
    });

    [Fact]
    public Task FR_ORD_001_002_005_checkout_ignores_client_prices_and_serves_owner_only() => WithDatabaseAsync(async (connection, _, _, seed, ct) =>
    {
        await using var factory = new OrderingFactory(connection);
        using var customer = await CustomerClientAsync(factory, "order-owner@example.invalid", ct);
        using var stranger = await CustomerClientAsync(factory, "order-stranger@example.invalid", ct);
        Guid cartId;
        using (var cart = await AddItemAsync(customer, seed.Stocked.Id, 2, ct)) cartId = cart.RootElement.GetProperty("id").GetGuid();

        using (var missingKey = await ReadJsonAsync(await customer.PostAsJsonAsync("/api/v1/orders", CheckoutBody(cartId), ct), ct, HttpStatusCode.BadRequest))
            Assert.Equal("IDEMPOTENCY_KEY_REQUIRED", missingKey.RootElement.GetProperty("code").GetString());

        var tampered = new { cartId, shippingMethodId = ShippingMethodId, paymentMethod = "COD", couponCode = (string?)null, deliveryAddress = Address,
            unitPrice = 1m, subtotal = 1m, discountTotal = 999m, shippingFee = 0m, grandTotal = 1m,
            items = new[] { new { productId = seed.Stocked.Id, quantity = 1, unitPrice = 1m } } };
        var accepted = await PostOrderAsync(customer, "http-order-key-1", tampered, ct);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        Guid orderId;
        using (var order = await ReadJsonAsync(accepted, ct, HttpStatusCode.Accepted))
        {
            orderId = order.RootElement.GetProperty("id").GetGuid();
            Assert.Equal($"/api/v1/orders/{orderId}", accepted.Headers.Location?.ToString());
            Assert.Equal("PendingInventory", order.RootElement.GetProperty("status").GetString());
            Assert.Equal(20m, order.RootElement.GetProperty("subtotal").GetDecimal());
            Assert.Equal(0m, order.RootElement.GetProperty("discountTotal").GetDecimal());
            Assert.Equal(30000m, order.RootElement.GetProperty("shippingFee").GetDecimal());
            Assert.Equal(30020m, order.RootElement.GetProperty("grandTotal").GetDecimal());
        }

        using (var detail = await ReadJsonAsync(await customer.GetAsync($"/api/v1/orders/{orderId}", ct), ct))
        {
            var address = detail.RootElement.GetProperty("deliveryAddress");
            Assert.Equal(Address.RecipientName, address.GetProperty("recipientName").GetString());
            Assert.Equal(Address.Phone, address.GetProperty("phone").GetString());
            Assert.Equal(Address.Line1, address.GetProperty("line1").GetString());
            Assert.Equal(Address.Ward, address.GetProperty("ward").GetString());
            Assert.Equal(Address.District, address.GetProperty("district").GetString());
            Assert.Equal(Address.Province, address.GetProperty("province").GetString());
            Assert.Equal(Address.CountryCode, address.GetProperty("countryCode").GetString());
            var item = detail.RootElement.GetProperty("items").EnumerateArray().Single();
            Assert.Equal(seed.Stocked.Name, item.GetProperty("productNameSnapshot").GetString());
            Assert.Equal(seed.Stocked.Sku, item.GetProperty("skuSnapshot").GetString());
            Assert.Equal(10m, item.GetProperty("unitPrice").GetDecimal());
            // D-25: order detail exposes a DTO with readable statuses, not domain entities.
            Assert.False(item.TryGetProperty("orderId", out _));
            var history = detail.RootElement.GetProperty("history").EnumerateArray().Single();
            Assert.Equal("PendingInventory", history.GetProperty("toStatus").GetString());
            Assert.Equal(JsonValueKind.Null, history.GetProperty("fromStatus").ValueKind);
        }

        using (var list = await ReadJsonAsync(await customer.GetAsync("/api/v1/orders?status=PendingInventory&page=1&pageSize=5", ct), ct))
        {
            Assert.Equal(1, list.RootElement.GetProperty("totalCount").GetInt32());
            Assert.Equal(1, list.RootElement.GetProperty("totalPages").GetInt32());
            Assert.Equal(orderId, list.RootElement.GetProperty("items").EnumerateArray().Single().GetProperty("id").GetGuid());
        }
        using (var filtered = await ReadJsonAsync(await customer.GetAsync("/api/v1/orders?status=Paid", ct), ct))
            Assert.Equal(0, filtered.RootElement.GetProperty("totalCount").GetInt32());
        Assert.Equal(HttpStatusCode.BadRequest, (await customer.GetAsync("/api/v1/orders?status=Nope", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await customer.GetAsync("/api/v1/orders?pageSize=101", ct)).StatusCode);

        using (var foreignList = await ReadJsonAsync(await stranger.GetAsync("/api/v1/orders", ct), ct))
            Assert.Equal(0, foreignList.RootElement.GetProperty("totalCount").GetInt32());
        foreach (var response in new[] { await stranger.GetAsync($"/api/v1/orders/{orderId}", ct),
            await stranger.PostAsJsonAsync($"/api/v1/orders/{orderId}/cancel", new { reason = "Not mine" }, ct) })
        {
            using var hidden = await ReadJsonAsync(response, ct, HttpStatusCode.NotFound);
            Assert.Equal("ORDER_NOT_FOUND", hidden.RootElement.GetProperty("code").GetString());
        }
    });

    [Fact]
    public Task AC_07_http_cancel_after_payment_conflicts_without_partial_state() => WithDatabaseAsync(async (connection, options, setup, seed, ct) =>
    {
        await using var factory = new OrderingFactory(connection);
        using var customer = await CustomerClientAsync(factory, "paid-owner@example.invalid", ct);
        Guid cartId;
        using (var cart = await AddItemAsync(customer, seed.Stocked.Id, 1, ct)) cartId = cart.RootElement.GetProperty("id").GetGuid();
        Guid orderId;
        using (var order = await ReadJsonAsync(await PostOrderAsync(customer, "paid-order-key-1", CheckoutBody(cartId), ct), ct, HttpStatusCode.Accepted))
            orderId = order.RootElement.GetProperty("id").GetGuid();
        await using (var stateDb = new OrderFlowDbContext(options))
        {
            var paid = await stateDb.Orders.SingleAsync(x => x.Id == orderId, ct);
            paid.Transition(OrderStatus.AwaitingPayment, DateTimeOffset.UtcNow, "ac-07", eventId: Guid.NewGuid());
            paid.Transition(OrderStatus.Paid, DateTimeOffset.UtcNow, "ac-07", eventId: Guid.NewGuid());
            await stateDb.SaveChangesAsync(ct);
        }

        Assert.Equal(HttpStatusCode.BadRequest, (await customer.PostAsJsonAsync($"/api/v1/orders/{orderId}/cancel", new { reason = new string('x', 501) }, ct)).StatusCode);
        using (var conflict = await ReadJsonAsync(await customer.PostAsJsonAsync($"/api/v1/orders/{orderId}/cancel", new { reason = "Too late" }, ct), ct, HttpStatusCode.Conflict))
            Assert.Equal("INVALID_STATE_TRANSITION", conflict.RootElement.GetProperty("code").GetString());

        Assert.Equal(OrderStatus.Paid, (await setup.Orders.AsNoTracking().SingleAsync(x => x.Id == orderId, ct)).Status);
        Assert.Equal(3, await setup.OrderStatusHistories.AsNoTracking().CountAsync(x => x.OrderId == orderId, ct));
        Assert.False(await setup.OutboxMessages.AsNoTracking().AnyAsync(x => x.EventType == "OrderCancelled", ct));
    });

    [Fact]
    public Task FR_ORD_001_address_snapshot_fits_vietnamese_limits_and_rejects_oversized_json() => WithDatabaseAsync(async (connection, _, setup, seed, ct) =>
    {
        await using var factory = new OrderingFactory(connection);
        using var customer = await CustomerClientAsync(factory, "address-owner@example.invalid", ct);
        Guid cartId;
        using (var cart = await AddItemAsync(customer, seed.Stocked.Id, 1, ct)) cartId = cart.RootElement.GetProperty("id").GetGuid();
        static string Text(char c, int length) => new(c, length);

        var longest = new DeliveryAddress(Text('ễ', 150), Text('0', 30), Text('ễ', 300), Text('ễ', 100), Text('ễ', 100), Text('ễ', 100), "VN");
        Guid orderId;
        using (var order = await ReadJsonAsync(await PostOrderAsync(customer, "address-key-long", CheckoutBody(cartId, longest), ct), ct, HttpStatusCode.Accepted))
            orderId = order.RootElement.GetProperty("id").GetGuid();
        using (var detail = await ReadJsonAsync(await customer.GetAsync($"/api/v1/orders/{orderId}", ct), ct))
            Assert.Equal(longest.Line1, detail.RootElement.GetProperty("deliveryAddress").GetProperty("line1").GetString());

        // Control characters stay escaped, so these valid field lengths still exceed the snapshot column.
        var oversized = longest with { RecipientName = Text('\u0001', 150), Line1 = Text('\u0001', 300), Ward = Text('\u0001', 100) };
        using (var rejected = await ReadJsonAsync(await PostOrderAsync(customer, "address-key-over", CheckoutBody(cartId, oversized), ct), ct, HttpStatusCode.BadRequest))
            Assert.Equal("VALIDATION_FAILED", rejected.RootElement.GetProperty("code").GetString());
        Assert.Equal(1, await setup.Orders.AsNoTracking().CountAsync(ct));
        Assert.Equal(1, await setup.IdempotencyRecords.AsNoTracking().CountAsync(ct));
    });

    [Fact]
    public Task FR_ORD_003_checkout_failure_before_commit_rolls_back_every_record() => WithDatabaseAsync(async (_, options, setup, seed, ct) =>
    {
        var now = DateTimeOffset.UtcNow;
        var cart = new Cart(seed.User.Id, now);
        cart.Items.Add(new CartItem(cart.Id, seed.Stocked.Id, 2, now));
        var coupon = Coupon.Create(Guid.NewGuid(), "SAVE5", CouponDiscountType.Fixed, 5m, 10m, now.AddDays(-1), now.AddDays(1), 1, 1, now);
        setup.Carts.Add(cart); setup.Coupons.Add(coupon);
        await setup.SaveChangesAsync(ct);
        var request = new CheckoutRequest(cart.Id, ShippingMethodId, "COD", "save5", Address);

        // The first save stores the idempotency record; the second writes order, coupon usage, and outbox.
        await using (var failingDb = new OrderFlowDbContext(FailingAfterSave(options, 2)))
            await Assert.ThrowsAsync<InjectedFailureException>(() => NewCheckoutService(failingDb).CheckoutAsync(seed.User.Id, "atomic-key-1", request, "atomic", ct));

        Assert.False(await setup.Orders.AsNoTracking().AnyAsync(ct));
        Assert.False(await setup.OrderItems.AsNoTracking().AnyAsync(ct));
        Assert.False(await setup.OrderStatusHistories.AsNoTracking().AnyAsync(ct));
        Assert.False(await setup.OutboxMessages.AsNoTracking().AnyAsync(x => x.EventType == "OrderCreated", ct));
        Assert.False(await setup.IdempotencyRecords.AsNoTracking().AnyAsync(ct));
        Assert.False(await setup.CouponUsages.AsNoTracking().AnyAsync(ct));
        Assert.Equal(0, (await setup.Coupons.AsNoTracking().SingleAsync(ct)).UsageCount);
        Assert.Equal(1, await setup.CartItems.AsNoTracking().CountAsync(ct));

        await using var retryDb = new OrderFlowDbContext(options);
        Assert.Equal(202, (await NewCheckoutService(retryDb).CheckoutAsync(seed.User.Id, "atomic-key-1", request, "atomic", ct)).StatusCode);
        Assert.Equal(1, await setup.OutboxMessages.AsNoTracking().CountAsync(x => x.EventType == "OrderCreated", ct));
        Assert.Equal(1, (await setup.Coupons.AsNoTracking().SingleAsync(ct)).UsageCount);
        Assert.False(await setup.CartItems.AsNoTracking().AnyAsync(ct));
    });

    [Fact]
    public Task FR_ORD_006_cancel_failure_before_commit_keeps_reservation_active() => WithDatabaseAsync(async (_, options, setup, seed, ct) =>
    {
        var now = DateTimeOffset.UtcNow;
        var cart = new Cart(seed.User.Id, now);
        cart.Items.Add(new CartItem(cart.Id, seed.Stocked.Id, 2, now));
        setup.Carts.Add(cart);
        await setup.SaveChangesAsync(ct);
        Guid orderId;
        await using (var checkoutDb = new OrderFlowDbContext(options))
        {
            var created = await NewCheckoutService(checkoutDb).CheckoutAsync(seed.User.Id, "cancel-atomic-1", new CheckoutRequest(cart.Id, ShippingMethodId, "COD", null, Address), "atomic", ct);
            using var body = JsonDocument.Parse(created.Body!);
            orderId = body.RootElement.GetProperty("id").GetGuid();
        }
        await using (var reserveDb = new OrderFlowDbContext(options))
        {
            var reservation = await new InventoryService(reserveDb, TimeProvider.System, Options.Create(new InventoryOptions())).ReserveAsync(orderId, seed.Stocked.Id, 2, ct);
            Assert.Equal(ReserveOutcome.Reserved, reservation.Outcome);
            var awaiting = await reserveDb.Orders.SingleAsync(x => x.Id == orderId, ct);
            awaiting.Transition(OrderStatus.AwaitingPayment, DateTimeOffset.UtcNow, "atomic", eventId: Guid.NewGuid());
            await reserveDb.SaveChangesAsync(ct);
        }

        await using (var failingDb = new OrderFlowDbContext(FailingAfterSave(options, 1)))
            await Assert.ThrowsAsync<InjectedFailureException>(() => NewCheckoutService(failingDb).CancelAsync(seed.User.Id, orderId, "Changed mind", "atomic", ct));

        Assert.Equal(OrderStatus.AwaitingPayment, (await setup.Orders.AsNoTracking().SingleAsync(x => x.Id == orderId, ct)).Status);
        Assert.Equal(ReservationStatus.Active, (await setup.InventoryReservations.AsNoTracking().SingleAsync(x => x.OrderId == orderId, ct)).Status);
        Assert.Equal(2, (await setup.Inventories.AsNoTracking().SingleAsync(x => x.ProductId == seed.Stocked.Id, ct)).Reserved);
        Assert.False(await setup.OutboxMessages.AsNoTracking().AnyAsync(x => x.EventType == "OrderCancelled", ct));

        await using var retryDb = new OrderFlowDbContext(options);
        Assert.Equal(200, (await NewCheckoutService(retryDb).CancelAsync(seed.User.Id, orderId, "Changed mind", "atomic", ct)).StatusCode);
        Assert.Equal(0, (await setup.Inventories.AsNoTracking().SingleAsync(x => x.ProductId == seed.Stocked.Id, ct)).Reserved);
        Assert.Equal(1, await setup.OutboxMessages.AsNoTracking().CountAsync(x => x.EventType == "OrderCancelled", ct));
    });

    [Fact]
    public Task FR_ORD_001_003_checkout_empties_cart_and_new_key_cannot_repeat_order() => WithDatabaseAsync(async (connection, _, setup, seed, ct) =>
    {
        await using var factory = new OrderingFactory(connection);
        using var customer = await CustomerClientAsync(factory, "empty-cart@example.invalid", ct);
        await AddItemAsync(customer, seed.WithoutStock.Id, 1, ct);
        Guid cartId;
        using (var cart = await AddItemAsync(customer, seed.Stocked.Id, 2, ct)) cartId = cart.RootElement.GetProperty("id").GetGuid();

        var unknownShipping = new CheckoutRequest(cartId, Guid.NewGuid(), "COD", null, Address);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostOrderAsync(customer, "empty-cart-bad", unknownShipping, ct)).StatusCode);
        Assert.Equal(2, await setup.CartItems.AsNoTracking().CountAsync(ct));

        var accepted = await PostOrderAsync(customer, "empty-cart-key-1", CheckoutBody(cartId), ct);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var acceptedBody = await accepted.Content.ReadAsStringAsync(ct);
        using (var cart = await ReadJsonAsync(await customer.GetAsync("/api/v1/cart", ct), ct))
        {
            Assert.Equal(cartId, cart.RootElement.GetProperty("id").GetGuid());
            Assert.Equal(0, cart.RootElement.GetProperty("items").GetArrayLength());
            Assert.Equal(0m, cart.RootElement.GetProperty("subtotal").GetDecimal());
        }
        Assert.Equal(1, await setup.Carts.AsNoTracking().CountAsync(ct));

        var replay = await PostOrderAsync(customer, "empty-cart-key-1", CheckoutBody(cartId), ct);
        Assert.Equal(HttpStatusCode.Accepted, replay.StatusCode);
        Assert.Equal(acceptedBody, await replay.Content.ReadAsStringAsync(ct));

        using (var rejected = await ReadJsonAsync(await PostOrderAsync(customer, "empty-cart-key-2", CheckoutBody(cartId), ct), ct, HttpStatusCode.BadRequest))
            Assert.Equal("VALIDATION_FAILED", rejected.RootElement.GetProperty("code").GetString());
        Assert.Equal(1, await setup.Orders.AsNoTracking().CountAsync(ct));
        Assert.Equal(1, await setup.OutboxMessages.AsNoTracking().CountAsync(x => x.EventType == "OrderCreated", ct));

        using (var refilled = await AddItemAsync(customer, seed.Stocked.Id, 1, ct))
            Assert.Equal(cartId, refilled.RootElement.GetProperty("id").GetGuid());
    });

    [Fact]
    public Task FR_ORD_001_concurrent_checkouts_of_one_cart_with_different_keys_create_one_order() => WithDatabaseAsync(async (_, options, setup, seed, ct) =>
    {
        var now = DateTimeOffset.UtcNow;
        var cart = new Cart(seed.User.Id, now);
        setup.Carts.Add(cart);
        const int rounds = 5;
        for (var round = 1; round <= rounds; round++)
        {
            setup.CartItems.Add(new CartItem(cart.Id, seed.Stocked.Id, 1, now));
            await setup.SaveChangesAsync(ct);
            var keys = new[] { $"two-tabs-{round}-a", $"two-tabs-{round}-b" };
            var results = await Task.WhenAll(keys.Select(async key =>
            {
                await using var db = new OrderFlowDbContext(options);
                return await NewCheckoutService(db).CheckoutAsync(seed.User.Id, key, new CheckoutRequest(cart.Id, ShippingMethodId, "COD", null, Address), "two-tabs", ct);
            }));
            Assert.Single(results, x => x.StatusCode == 202);
            Assert.Single(results, x => x.StatusCode == 400 && x.Code == "VALIDATION_FAILED");
            Assert.Equal(round, await setup.Orders.AsNoTracking().CountAsync(ct));
            Assert.Equal(round, await setup.OutboxMessages.AsNoTracking().CountAsync(x => x.EventType == "OrderCreated", ct));
            Assert.False(await setup.CartItems.AsNoTracking().AnyAsync(ct));
        }
    });

    [Fact]
    public Task FR_CART_002_concurrent_item_changes_do_not_return_server_errors() => WithDatabaseAsync(async (connection, _, _, seed, ct) =>
    {
        await using var factory = new OrderingFactory(connection);
        using var customer = await CustomerClientAsync(factory, "cart-race@example.invalid", ct);
        for (var round = 0; round < 10; round++)
        {
            Guid itemId;
            using (var cart = await AddItemAsync(customer, seed.Stocked.Id, 1, ct)) itemId = Line(cart, seed.Stocked.Id).GetProperty("id").GetGuid();
            var deletes = Task.WhenAll(customer.DeleteAsync($"/api/v1/cart/items/{itemId}", ct), customer.DeleteAsync($"/api/v1/cart/items/{itemId}", ct));
            var update = customer.PutAsJsonAsync($"/api/v1/cart/items/{itemId}", new { quantity = 2 }, ct);
            var deleted = await deletes;
            Assert.Single(deleted, x => x.StatusCode == HttpStatusCode.NoContent);
            Assert.Single(deleted, x => x.StatusCode == HttpStatusCode.NotFound);
            Assert.Contains((await update).StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.NotFound });
        }
    });

    private sealed record Seed(ApplicationUser User, Product Stocked, Product WithoutStock);

    private static async Task WithDatabaseAsync(Func<string, DbContextOptions<OrderFlowDbContext>, OrderFlowDbContext, Seed, CancellationToken, Task> test)
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
        var ct = TestContext.Current.CancellationToken;
        await using var setup = new OrderFlowDbContext(options);
        var migrated = false;
        try
        {
            await setup.Database.MigrateAsync(ct); migrated = true;
            var now = DateTimeOffset.UtcNow;
            var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = "phase6-seed", NormalizedUserName = "PHASE6-SEED",
                Email = "phase6-seed@example.invalid", NormalizedEmail = "PHASE6-SEED@EXAMPLE.INVALID", FirstName = "Seed", LastName = "User", CreatedAtUtc = now };
            var category = Category.Create(Guid.NewGuid(), "Test", "phase6-api", null, now);
            var stocked = Product.Create(Guid.NewGuid(), category.Id, "P6-STOCK", "Stocked", null, 10m, "VND", now);
            var withoutStock = Product.Create(Guid.NewGuid(), category.Id, "P6-NONE", "Without stock", null, 20m, "VND", now);
            var inventory = Inventory.Create(Guid.NewGuid(), stocked.Id, 0, now);
            var adjustment = inventory.Adjust(100, "SEED", null, user.Id, "phase6-api", now);
            setup.Users.Add(user); setup.Categories.Add(category); setup.Products.AddRange(stocked, withoutStock);
            setup.Inventories.Add(inventory); setup.InventoryAdjustments.Add(adjustment);
            await setup.SaveChangesAsync(ct);
            await test(connection, options, setup, new Seed(user, stocked, withoutStock), ct);
        }
        finally { if (migrated) await setup.Database.EnsureDeletedAsync(ct); }
    }

    private static object CheckoutBody(Guid cartId, DeliveryAddress? address = null) =>
        new CheckoutRequest(cartId, ShippingMethodId, "COD", null, address ?? Address);

    private static async Task<HttpResponseMessage> PostOrderAsync(HttpClient client, string key, object body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/orders") { Content = JsonContent.Create(body, body.GetType()) };
        request.Headers.Add("Idempotency-Key", key);
        return await client.SendAsync(request, ct);
    }

    private static async Task<JsonDocument> AddItemAsync(HttpClient client, Guid productId, int quantity, CancellationToken ct) =>
        await ReadJsonAsync(await client.PostAsJsonAsync("/api/v1/cart/items", new { productId, quantity }, ct), ct);

    private static JsonElement Line(JsonDocument cart, Guid productId) =>
        cart.RootElement.GetProperty("items").EnumerateArray().Single(x => x.GetProperty("productId").GetGuid() == productId);

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken ct, HttpStatusCode expected = HttpStatusCode.OK)
    {
        Assert.Equal(expected, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    }

    private static async Task<HttpClient> CustomerClientAsync(OrderingFactory factory, string email, CancellationToken ct)
    {
        var client = factory.CreateClient();
        var registration = await client.PostAsJsonAsync("/api/v1/auth/register", new { email, password = "StrongPassword123!", firstName = "Test", lastName = "Customer" }, ct);
        Assert.Equal(HttpStatusCode.Created, registration.StatusCode);
        using var login = await ReadJsonAsync(await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "StrongPassword123!" }, ct), ct);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.RootElement.GetProperty("accessToken").GetString());
        return client;
    }

    private static CheckoutService NewCheckoutService(OrderFlowDbContext db) => new(db, TimeProvider.System,
        Options.Create(new OrderingOptions()), new InventoryService(db, TimeProvider.System, Options.Create(new InventoryOptions())));

    private static DbContextOptions<OrderFlowDbContext> FailingAfterSave(DbContextOptions<OrderFlowDbContext> options, int saveNumber) =>
        new DbContextOptionsBuilder<OrderFlowDbContext>(options).AddInterceptors(new FailAfterSaveInterceptor(saveNumber)).Options;

    private sealed class InjectedFailureException : Exception;

    // Throws once SQL has run but before the caller commits, which simulates a crash inside the transaction.
    private sealed class FailAfterSaveInterceptor(int saveNumber) : SaveChangesInterceptor
    {
        private int saves;
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default) =>
            ++saves == saveNumber ? throw new InjectedFailureException() : base.SavedChangesAsync(eventData, result, cancellationToken);
    }

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
