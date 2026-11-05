using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OrderFlow.Application.Messaging;
using OrderFlow.Domain.Inventory;
using OrderFlow.Domain.Ordering;
using OrderFlow.Domain.Payments;
using OrderFlow.Domain.Returns;
using OrderFlow.Domain.Shipping;
using OrderFlow.Infrastructure.Persistence;
using Xunit;

namespace OrderFlow.IntegrationTests;

internal sealed class ApiFactory(string connection, Dictionary<string, string?>? settings = null, string environment = "Development") : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment);
        builder.ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Warning));
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:OrderFlow"] = connection,
                ["Jwt:Issuer"] = "orderflow-tests", ["Jwt:Audience"] = "orderflow-api",
                ["Jwt:SigningKey"] = "TEST_ONLY_orderflow_integration_signing_key_32_bytes_min",
                ["Jwt:AccessTokenMinutes"] = "15", ["Jwt:RefreshTokenDays"] = "30"
            });
            if (settings is not null) config.AddInMemoryCollection(settings);
        });
    }
}

// Drives real HTTP endpoints, then applies each committed event through the Worker's consumers, without needing a broker.
internal sealed class FeatureScene : IAsyncDisposable
{
    private const string Password = "StrongPassword123!";
    private FeatureScene(MessagingHarness harness, ApiFactory api, IHost worker) { Harness = harness; Api = api; Worker = worker; }
    public MessagingHarness Harness { get; }
    public ApiFactory Api { get; }
    public IHost Worker { get; private set; }
    public HttpClient Admin { get; private set; } = null!;
    public HttpClient Customer { get; private set; } = null!;
    public HttpClient Stranger { get; private set; } = null!;
    public Guid CustomerId { get; private set; }
    public static CancellationToken Ct => MessagingHarness.Ct;

    public static async Task<FeatureScene?> CreateAsync(Dictionary<string, string?>? workerSettings = null)
    {
        var harness = await MessagingHarness.CreateAsync();
        if (harness is null) return null;
        var scene = new FeatureScene(harness, new ApiFactory(harness.Connection), harness.BuildWorker(settings: workerSettings));
        scene.Admin = await scene.ClientAsync("admin@example.invalid", "Admin", "InventoryManager");
        scene.Customer = await scene.ClientAsync("customer@example.invalid");
        scene.Stranger = await scene.ClientAsync("stranger@example.invalid");
        await using var db = harness.NewDb();
        scene.CustomerId = await db.Users.AsNoTracking().Where(x => x.Email == "customer@example.invalid").Select(x => x.Id).SingleAsync(Ct);
        harness.ActAs(scene.CustomerId);
        return scene;
    }

    public void ReplaceWorker(IHost worker) => Worker = worker;

    public async Task<HttpClient> ClientAsync(string email, params string[] roles)
    {
        var client = Api.CreateClient();
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/v1/auth/register", new { email, password = Password, firstName = "Test", lastName = "User" }, Ct)).StatusCode);
        if (roles.Length > 0)
        {
            await using var db = Harness.NewDb();
            var userId = await db.Users.AsNoTracking().Where(x => x.Email == email).Select(x => x.Id).SingleAsync(Ct);
            var roleIds = await db.Roles.AsNoTracking().Where(x => roles.Contains(x.Name!)).Select(x => x.Id).ToListAsync(Ct);
            db.UserRoles.AddRange(roleIds.Select(roleId => new IdentityUserRole<Guid> { UserId = userId, RoleId = roleId }));
            await db.SaveChangesAsync(Ct);
        }
        using var login = await Json(await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = Password }, Ct));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.RootElement.GetProperty("accessToken").GetString());
        return client;
    }

    public static async Task<JsonDocument> Json(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == expected, $"Expected {expected}, got {response.StatusCode}: {body}");
        return JsonDocument.Parse(body);
    }

    public static async Task Code(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        using var body = await Json(response, status);
        Assert.Equal(code, body.RootElement.GetProperty("code").GetString());
    }

    public Task<bool> Deliver(string consumer, EventEnvelope envelope) => MessagingHarness.DeliverAsync(Worker, consumer, envelope);
    public Task<EventEnvelope> Event(Guid aggregateId, string type) => Harness.OutboxEventAsync(aggregateId, type);

    public async Task<Guid> ProcessingOrderAsync(Guid productId, int quantity, string paymentMethod, string? couponCode = null)
    {
        var orderId = await Harness.CheckoutAsync(productId, quantity, paymentMethod, couponCode: couponCode);
        await Deliver("inventory", await Event(orderId, EventTypes.OrderCreated));
        await Deliver("ordering", await Event(orderId, EventTypes.InventoryReserved));
        await Deliver("payment", await Event(orderId, EventTypes.PaymentRequested));
        var paid = await Event(orderId, EventTypes.PaymentSucceeded);
        await Deliver("ordering", paid);
        await Deliver("inventory", paid);
        await Deliver("ordering", await Event(orderId, EventTypes.InventoryCommitted));
        await AssertStatus(orderId, OrderStatus.Processing);
        return orderId;
    }

    public async Task<Guid> ShipAsync(Guid orderId, bool deliver)
    {
        using var created = await Json(await Admin.PostAsJsonAsync("/api/v1/shipments", new { orderId }, Ct), HttpStatusCode.Created);
        var shipmentId = created.RootElement.GetProperty("id").GetGuid();
        foreach (var status in deliver ? new[] { "ReadyToShip", "Shipped", "Delivered" } : ["ReadyToShip", "Shipped"])
        {
            (await Json(await Admin.PostAsJsonAsync($"/api/v1/shipments/{shipmentId}/status", new { status }, Ct))).Dispose();
            if (status == "ReadyToShip") continue;
            var changed = await Event(orderId, EventTypes.ShipmentStatusChanged);
            await Deliver("ordering", changed);
            await Deliver("payment", changed);
        }
        return shipmentId;
    }

    public async Task AssertStatus(Guid orderId, OrderStatus status)
    {
        await using var db = Harness.NewDb();
        Assert.Equal(status, await db.Orders.AsNoTracking().Where(x => x.Id == orderId).Select(x => x.Status).SingleAsync(Ct));
    }

    public async ValueTask DisposeAsync()
    {
        Worker.Dispose();
        await Api.DisposeAsync();
        await Harness.DisposeAsync();
    }
}

public sealed class ExtendedFeaturesTests
{
    private static CancellationToken Ct => FeatureScene.Ct;
    private static readonly Dictionary<string, string?> FastRefundRetry = new() { ["Refunds:RetryDelaySeconds"] = "0", ["Refunds:MaxAttempts"] = "2", ["Payments:RefundFailCents"] = "27" };

    [Fact]
    public async Task Shipping_lifecycle_moves_order_settles_cod_and_notifies_once()
    {
        await using var scene = await FeatureScene.CreateAsync();
        if (scene is null) return;
        var product = await scene.Harness.SeedProductAsync(100m, 10);
        var orderId = await scene.ProcessingOrderAsync(product, 1, "COD");

        Assert.Equal(HttpStatusCode.Forbidden, (await scene.Customer.PostAsJsonAsync("/api/v1/shipments", new { orderId }, Ct)).StatusCode);
        using var created = await FeatureScene.Json(await scene.Admin.PostAsJsonAsync("/api/v1/shipments", new { orderId }, Ct), HttpStatusCode.Created);
        var shipmentId = created.RootElement.GetProperty("id").GetGuid();
        Assert.Equal("Pending", created.RootElement.GetProperty("status").GetString());
        (await FeatureScene.Json(await scene.Admin.PostAsJsonAsync("/api/v1/shipments", new { orderId }, Ct))).Dispose();
        await FeatureScene.Code(await scene.Admin.PostAsJsonAsync($"/api/v1/shipments/{shipmentId}/status", new { status = "Delivered" }, Ct), HttpStatusCode.Conflict, "INVALID_STATE_TRANSITION");
        await FeatureScene.Code(await scene.Admin.PostAsJsonAsync($"/api/v1/shipments/{shipmentId}/status", new { status = "Cancelled" }, Ct), HttpStatusCode.BadRequest, "VALIDATION_FAILED");

        (await FeatureScene.Json(await scene.Admin.PostAsJsonAsync($"/api/v1/shipments/{shipmentId}/status", new { status = "ReadyToShip" }, Ct))).Dispose();
        (await FeatureScene.Json(await scene.Admin.PostAsJsonAsync($"/api/v1/shipments/{shipmentId}/status", new { status = "Shipped" }, Ct))).Dispose();
        // Repeating a dispatch writes no second event.
        (await FeatureScene.Json(await scene.Admin.PostAsJsonAsync($"/api/v1/shipments/{shipmentId}/status", new { status = "Shipped" }, Ct))).Dispose();
        await using (var db = scene.Harness.NewDb())
            Assert.Equal(1, await db.OutboxMessages.CountAsync(x => x.AggregateId == orderId && x.EventType == EventTypes.ShipmentStatusChanged, Ct));
        var shipped = await scene.Event(orderId, EventTypes.ShipmentStatusChanged);
        Assert.True(await scene.Deliver("ordering", shipped));
        Assert.False(await scene.Deliver("ordering", shipped));
        await scene.AssertStatus(orderId, OrderStatus.Shipped);
        Assert.True(await scene.Deliver("notifications", shipped));

        // A shipped order can no longer be cancelled by staff.
        await FeatureScene.Code(await scene.Admin.PostAsJsonAsync($"/api/v1/admin/orders/{orderId}/cancel", new { reason = "Too late" }, Ct), HttpStatusCode.Conflict, "INVALID_STATE_TRANSITION");

        (await FeatureScene.Json(await scene.Admin.PostAsJsonAsync($"/api/v1/shipments/{shipmentId}/status", new { status = "Delivered" }, Ct))).Dispose();
        var delivered = await scene.Event(orderId, EventTypes.ShipmentStatusChanged);
        await scene.Deliver("ordering", delivered);
        await scene.Deliver("payment", delivered);
        await scene.Deliver("payment", delivered);
        await scene.AssertStatus(orderId, OrderStatus.Delivered);
        await using (var db = scene.Harness.NewDb())
        {
            Assert.Equal(SettlementStatus.Collected, (await db.Payments.AsNoTracking().SingleAsync(x => x.OrderId == orderId, Ct)).SettlementStatus);
            Assert.Equal(1, await db.Notifications.CountAsync(x => x.OrderId == orderId && x.Type == EventTypes.ShipmentStatusChanged, Ct));
        }

        using (var own = await FeatureScene.Json(await scene.Customer.GetAsync($"/api/v1/orders/{orderId}/shipment", Ct)))
            Assert.Equal(4, own.RootElement.GetProperty("history").GetArrayLength());
        await FeatureScene.Code(await scene.Stranger.GetAsync($"/api/v1/orders/{orderId}/shipment", Ct), HttpStatusCode.NotFound, "ORDER_NOT_FOUND");
    }

    [Fact]
    public async Task Staff_cancel_of_a_processing_order_compensates_stock_shipment_coupon_and_payment_once()
    {
        await using var scene = await FeatureScene.CreateAsync();
        if (scene is null) return;
        var product = await scene.Harness.SeedProductAsync(100m, 10);
        var coupon = await scene.Harness.SeedCouponAsync("STAFFCANCEL");
        var orderId = await scene.ProcessingOrderAsync(product, 2, "CardSimulation", "STAFFCANCEL");
        Assert.Equal((8, 0), await scene.Harness.StockAsync(product));
        using (var shipment = await FeatureScene.Json(await scene.Admin.PostAsJsonAsync("/api/v1/shipments", new { orderId }, Ct), HttpStatusCode.Created)) { }

        Assert.Equal(HttpStatusCode.Forbidden, (await scene.Customer.PostAsJsonAsync($"/api/v1/admin/orders/{orderId}/cancel", new { reason = "Mine" }, Ct)).StatusCode);
        await FeatureScene.Code(await scene.Admin.PostAsJsonAsync($"/api/v1/admin/orders/{orderId}/cancel", new { reason = " " }, Ct), HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        (await FeatureScene.Json(await scene.Admin.PostAsJsonAsync($"/api/v1/admin/orders/{orderId}/cancel", new { reason = "Warehouse damaged the item" }, Ct))).Dispose();
        (await FeatureScene.Json(await scene.Admin.PostAsJsonAsync($"/api/v1/admin/orders/{orderId}/cancel", new { reason = "Repeat" }, Ct))).Dispose();

        await scene.AssertStatus(orderId, OrderStatus.Cancelled);
        Assert.Equal((10, 0), await scene.Harness.StockAsync(product));
        Assert.Equal((0, 0, 1), await scene.Harness.CouponStateAsync(coupon.Id));
        var cancelled = await scene.Event(orderId, EventTypes.OrderCancelled);
        Assert.True(await scene.Deliver("inventory", cancelled));
        Assert.True(await scene.Deliver("payment", cancelled));
        Assert.False(await scene.Deliver("payment", cancelled));
        Assert.Equal((10, 0), await scene.Harness.StockAsync(product));
        await using var db = scene.Harness.NewDb();
        Assert.Equal(1, await db.OutboxMessages.CountAsync(x => x.AggregateId == orderId && x.EventType == EventTypes.OrderCancelled, Ct));
        Assert.Equal(ShipmentStatus.Cancelled, (await db.Shipments.AsNoTracking().SingleAsync(x => x.OrderId == orderId, Ct)).Status);
        Assert.Equal(PaymentStatus.Refunded, (await db.Payments.AsNoTracking().SingleAsync(x => x.OrderId == orderId, Ct)).Status);
        Assert.Equal(1, await db.Refunds.CountAsync(x => x.OrderId == orderId, Ct));
        Assert.Equal(1, await db.InventoryAdjustments.CountAsync(x => x.ReasonCode == "ORDER_CANCELLED_RESTOCK", Ct));
        var history = await db.OrderStatusHistories.AsNoTracking().SingleAsync(x => x.OrderId == orderId && x.ToStatus == OrderStatus.Cancelled, Ct);
        Assert.Equal("Warehouse damaged the item", history.Reason);
        Assert.NotNull(history.ActorUserId);
        var audit = await db.AuditEntries.AsNoTracking().Where(x => x.Action == "order.staff-cancel" && x.TargetId == orderId.ToString()).ToListAsync(Ct);
        Assert.Equal(2, audit.Count);
        Assert.Contains(audit, x => x.Reason == "Warehouse damaged the item");
    }

    [Fact]
    public async Task Staff_cancel_of_a_paid_order_releases_held_stock_and_a_late_commit_does_nothing()
    {
        await using var scene = await FeatureScene.CreateAsync();
        if (scene is null) return;
        var product = await scene.Harness.SeedProductAsync(100m, 10);
        var orderId = await scene.Harness.CheckoutAsync(product, 2, "COD");
        await scene.Deliver("inventory", await scene.Event(orderId, EventTypes.OrderCreated));
        await scene.Deliver("ordering", await scene.Event(orderId, EventTypes.InventoryReserved));
        await scene.Deliver("payment", await scene.Event(orderId, EventTypes.PaymentRequested));
        var paid = await scene.Event(orderId, EventTypes.PaymentSucceeded);
        await scene.Deliver("ordering", paid);
        await scene.AssertStatus(orderId, OrderStatus.Paid);
        Assert.Equal((10, 2), await scene.Harness.StockAsync(product));

        (await FeatureScene.Json(await scene.Admin.PostAsJsonAsync($"/api/v1/admin/orders/{orderId}/cancel", new { reason = "Customer called" }, Ct))).Dispose();
        Assert.Equal((10, 0), await scene.Harness.StockAsync(product));
        await scene.Deliver("inventory", paid);
        Assert.Equal((10, 0), await scene.Harness.StockAsync(product));
        Assert.Equal([ReservationStatus.Released], await scene.Harness.ReservationsAsync(orderId));
        await scene.Deliver("payment", await scene.Event(orderId, EventTypes.OrderCancelled));
        await using var db = scene.Harness.NewDb();
        Assert.Equal(SettlementStatus.Voided, (await db.Payments.AsNoTracking().SingleAsync(x => x.OrderId == orderId, Ct)).SettlementStatus);
        Assert.Equal(0, await db.Refunds.CountAsync(x => x.OrderId == orderId, Ct));
    }

    [Fact]
    public async Task Staff_cancel_is_refused_before_payment_completes()
    {
        await using var scene = await FeatureScene.CreateAsync();
        if (scene is null) return;
        var product = await scene.Harness.SeedProductAsync(100m, 10);
        var orderId = await scene.Harness.CheckoutAsync(product, 1, "COD");
        await FeatureScene.Code(await scene.Admin.PostAsJsonAsync($"/api/v1/admin/orders/{orderId}/cancel", new { reason = "Not yet" }, Ct), HttpStatusCode.Conflict, "INVALID_STATE_TRANSITION");
        await scene.AssertStatus(orderId, OrderStatus.PendingInventory);
    }

    [Fact]
    public async Task Return_flow_refunds_the_whole_order_once_and_restocks_chosen_lines()
    {
        await using var scene = await FeatureScene.CreateAsync();
        if (scene is null) return;
        var product = await scene.Harness.SeedProductAsync(100m, 10);
        var orderId = await scene.ProcessingOrderAsync(product, 2, "COD");
        await FeatureScene.Code(await scene.Customer.PostAsJsonAsync($"/api/v1/orders/{orderId}/returns", new { reason = "Early" }, Ct), HttpStatusCode.Conflict, "RETURN_NOT_ELIGIBLE");
        await scene.ShipAsync(orderId, deliver: true);

        await FeatureScene.Code(await scene.Stranger.PostAsJsonAsync($"/api/v1/orders/{orderId}/returns", new { reason = "Not mine" }, Ct), HttpStatusCode.NotFound, "ORDER_NOT_FOUND");
        await FeatureScene.Code(await scene.Customer.PostAsJsonAsync($"/api/v1/orders/{orderId}/returns", new { reason = " " }, Ct), HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        var requests = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => scene.Customer.PostAsJsonAsync($"/api/v1/orders/{orderId}/returns", new { reason = "Wrong size" }, Ct)));
        Assert.Single(requests, x => x.StatusCode == HttpStatusCode.Created);
        var duplicate = Assert.Single(requests, x => x.StatusCode == HttpStatusCode.Conflict);
        await FeatureScene.Code(duplicate, HttpStatusCode.Conflict, "RETURN_ALREADY_EXISTS");

        Guid returnId, orderItemId;
        using (var list = await FeatureScene.Json(await scene.Customer.GetAsync($"/api/v1/orders/{orderId}/returns", Ct)))
        {
            var request = list.RootElement[0];
            returnId = request.GetProperty("id").GetGuid();
            orderItemId = request.GetProperty("items")[0].GetProperty("orderItemId").GetGuid();
            Assert.Equal("Requested", request.GetProperty("status").GetString());
        }
        var receive = new { items = new[] { new { orderItemId, restock = true } } };
        await FeatureScene.Code(await scene.Admin.PostAsJsonAsync($"/api/v1/admin/returns/{returnId}/receive", receive, Ct), HttpStatusCode.Conflict, "INVALID_STATE_TRANSITION");
        Assert.Equal(HttpStatusCode.Forbidden, (await scene.Customer.PostAsync($"/api/v1/admin/returns/{returnId}/approve", null, Ct)).StatusCode);
        (await FeatureScene.Json(await scene.Admin.PostAsync($"/api/v1/admin/returns/{returnId}/approve", null, Ct))).Dispose();
        await FeatureScene.Code(await scene.Admin.PostAsJsonAsync($"/api/v1/admin/returns/{returnId}/receive", new { items = Array.Empty<object>() }, Ct), HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        (await FeatureScene.Json(await scene.Admin.PostAsJsonAsync($"/api/v1/admin/returns/{returnId}/receive", receive, Ct))).Dispose();
        (await FeatureScene.Json(await scene.Admin.PostAsJsonAsync($"/api/v1/admin/returns/{returnId}/receive", receive, Ct))).Dispose();
        Assert.Equal((10, 0), await scene.Harness.StockAsync(product));

        var refundRequested = await scene.Event(orderId, EventTypes.RefundRequested);
        Assert.True(await scene.Deliver("payment", refundRequested));
        Assert.False(await scene.Deliver("payment", refundRequested));
        var refunded = await scene.Event(orderId, EventTypes.RefundCompleted);
        await scene.Deliver("ordering", refunded);
        await scene.Deliver("returns", refunded);
        await scene.Deliver("returns", refunded);
        await scene.AssertStatus(orderId, OrderStatus.Refunded);
        using (var closed = await FeatureScene.Json(await scene.Admin.PostAsync($"/api/v1/admin/returns/{returnId}/close", null, Ct)))
        {
            Assert.Equal("Closed", closed.RootElement.GetProperty("status").GetString());
            Assert.Equal(["Requested", "Approved", "Received", "Refunded", "Closed"],
                closed.RootElement.GetProperty("history").EnumerateArray().Select(x => x.GetProperty("toStatus").GetString()!).ToArray());
        }
        await using var db = scene.Harness.NewDb();
        var payment = await db.Payments.AsNoTracking().SingleAsync(x => x.OrderId == orderId, Ct);
        Assert.Equal(PaymentStatus.Refunded, payment.Status);
        Assert.Equal(SettlementStatus.Collected, payment.SettlementStatus);
        var refund = await db.Refunds.AsNoTracking().SingleAsync(x => x.OrderId == orderId, Ct);
        Assert.Equal(payment.Amount, refund.Amount);
        // The whole order comes back: two items at 100 plus the 30000 shipping fee.
        Assert.Equal(30200m, refund.Amount);
    }

    [Fact]
    public async Task Return_is_refused_after_the_window_and_a_rejected_request_can_be_followed_by_another()
    {
        await using var scene = await FeatureScene.CreateAsync();
        if (scene is null) return;
        var product = await scene.Harness.SeedProductAsync(100m, 10);
        var orderId = await scene.ProcessingOrderAsync(product, 1, "COD");
        await scene.ShipAsync(orderId, deliver: true);

        Guid returnId;
        using (var created = await FeatureScene.Json(await scene.Customer.PostAsJsonAsync($"/api/v1/orders/{orderId}/returns", new { reason = "Changed my mind" }, Ct), HttpStatusCode.Created))
            returnId = created.RootElement.GetProperty("id").GetGuid();
        await FeatureScene.Code(await scene.Admin.PostAsJsonAsync($"/api/v1/admin/returns/{returnId}/reject", new { reason = " " }, Ct), HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        (await FeatureScene.Json(await scene.Admin.PostAsJsonAsync($"/api/v1/admin/returns/{returnId}/reject", new { reason = "Item was used" }, Ct))).Dispose();

        await using (var db = scene.Harness.NewDb())
        {
            // Seven days less an hour is inside the window.
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE dbo.OrderStatusHistories SET ChangedAtUtc = {DateTimeOffset.UtcNow.AddDays(-7).AddHours(1)} WHERE OrderId = {orderId} AND ToStatus = 'Delivered'", Ct);
        }
        (await FeatureScene.Json(await scene.Customer.PostAsJsonAsync($"/api/v1/orders/{orderId}/returns", new { reason = "Second try" }, Ct), HttpStatusCode.Created)).Dispose();

        var second = await scene.ProcessingOrderAsync(product, 1, "COD");
        await scene.ShipAsync(second, deliver: true);
        await using (var db = scene.Harness.NewDb())
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE dbo.OrderStatusHistories SET ChangedAtUtc = {DateTimeOffset.UtcNow.AddDays(-7).AddHours(-1)} WHERE OrderId = {second} AND ToStatus = 'Delivered'", Ct);
        await FeatureScene.Code(await scene.Customer.PostAsJsonAsync($"/api/v1/orders/{second}/returns", new { reason = "Too late" }, Ct), HttpStatusCode.Conflict, "RETURN_WINDOW_CLOSED");
    }

    [Fact]
    public async Task D23_failed_refund_retries_with_one_identity_then_queues_for_an_admin_who_can_retry()
    {
        await using var scene = await FeatureScene.CreateAsync(FastRefundRetry);
        if (scene is null) return;
        var product = await scene.Harness.SeedProductAsync(100.27m, 10);
        var orderId = await scene.ProcessingOrderAsync(product, 1, "CardSimulation");
        (await FeatureScene.Json(await scene.Admin.PostAsJsonAsync($"/api/v1/admin/orders/{orderId}/cancel", new { reason = "Damaged" }, Ct))).Dispose();
        await scene.Deliver("payment", await scene.Event(orderId, EventTypes.OrderCancelled));

        await using (var db = scene.Harness.NewDb())
        {
            var pending = await db.Refunds.AsNoTracking().SingleAsync(x => x.OrderId == orderId, Ct);
            Assert.Equal(RefundStatus.Pending, pending.Status);
            Assert.Equal(1, pending.AttemptCount);
            Assert.NotNull(pending.NextAttemptAtUtc);
            Assert.Equal(PaymentStatus.RefundPending, (await db.Payments.AsNoTracking().SingleAsync(x => x.OrderId == orderId, Ct)).Status);
        }
        await RetryAsync(scene.Worker);
        await using (var db = scene.Harness.NewDb())
        {
            var failed = await db.Refunds.AsNoTracking().SingleAsync(x => x.OrderId == orderId, Ct);
            Assert.Equal(RefundStatus.Failed, failed.Status);
            Assert.Equal(2, failed.AttemptCount);
            Assert.Equal(PaymentStatus.RefundFailed, (await db.Payments.AsNoTracking().SingleAsync(x => x.OrderId == orderId, Ct)).Status);
            Assert.False(await db.OutboxMessages.AnyAsync(x => x.AggregateId == orderId && x.EventType == EventTypes.RefundCompleted, Ct));
        }
        // Nothing is due while the refund waits for an Admin.
        await RetryAsync(scene.Worker);

        Guid refundId;
        Assert.Equal(HttpStatusCode.Forbidden, (await scene.Customer.GetAsync("/api/v1/admin/failed-refunds", Ct)).StatusCode);
        using (var queue = await FeatureScene.Json(await scene.Admin.GetAsync("/api/v1/admin/failed-refunds", Ct)))
        {
            Assert.Equal(1, queue.RootElement.GetProperty("totalCount").GetInt32());
            refundId = queue.RootElement.GetProperty("items")[0].GetProperty("id").GetGuid();
        }
        Assert.Equal(HttpStatusCode.Accepted, (await scene.Admin.PostAsync($"/api/v1/admin/failed-refunds/{refundId}/retry", null, Ct)).StatusCode);
        await FeatureScene.Code(await scene.Admin.PostAsync($"/api/v1/admin/failed-refunds/{refundId}/retry", null, Ct), HttpStatusCode.Conflict, "INVALID_STATE_TRANSITION");

        // The provider has recovered: the same refund row completes and exactly one event is written.
        using var recovered = scene.Harness.BuildWorker(settings: new Dictionary<string, string?> { ["Refunds:RetryDelaySeconds"] = "0" });
        await RetryAsync(recovered);
        await using (var db = scene.Harness.NewDb())
        {
            var done = await db.Refunds.AsNoTracking().SingleAsync(x => x.OrderId == orderId, Ct);
            Assert.Equal(RefundStatus.Succeeded, done.Status);
            Assert.Equal(refundId, done.Id);
            Assert.Equal($"order-cancelled:{orderId:N}", done.IdempotencyKey);
            Assert.Equal(PaymentStatus.Refunded, (await db.Payments.AsNoTracking().SingleAsync(x => x.OrderId == orderId, Ct)).Status);
            Assert.Equal(1, await db.OutboxMessages.CountAsync(x => x.AggregateId == orderId && x.EventType == EventTypes.RefundCompleted, Ct));
        }
    }

    private static async Task RetryAsync(IHost worker)
    {
        await using var scope = worker.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<PaymentService>().RetryDueRefundsAsync(Ct);
    }

    [Fact]
    public async Task Coupon_and_shipping_administration_enforce_roles_uniqueness_versions_and_affect_checkout()
    {
        await using var scene = await FeatureScene.CreateAsync();
        if (scene is null) return;
        var body = new { code = " summer ", discountType = "Fixed", discountValue = 10m, minimumSubtotal = 0m, validFromUtc = DateTimeOffset.UtcNow.AddDays(-1), validUntilUtc = DateTimeOffset.UtcNow.AddDays(1), totalUsageLimit = 5, perUserUsageLimit = 1 };
        Assert.Equal(HttpStatusCode.Forbidden, (await scene.Customer.PostAsJsonAsync("/api/v1/admin/coupons", body, Ct)).StatusCode);
        await FeatureScene.Code(await scene.Admin.PostAsJsonAsync("/api/v1/admin/coupons", body with { discountValue = -1m }, Ct), HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        Guid couponId; string version;
        using (var created = await FeatureScene.Json(await scene.Admin.PostAsJsonAsync("/api/v1/admin/coupons", body, Ct), HttpStatusCode.Created))
        {
            couponId = created.RootElement.GetProperty("id").GetGuid();
            version = created.RootElement.GetProperty("version").GetString()!;
            Assert.Equal("SUMMER", created.RootElement.GetProperty("code").GetString());
        }
        await FeatureScene.Code(await scene.Admin.PostAsJsonAsync("/api/v1/admin/coupons", body, Ct), HttpStatusCode.Conflict, "COUPON_CODE_ALREADY_EXISTS");

        var product = await scene.Harness.SeedProductAsync(100m, 10);
        using (var disabled = await FeatureScene.Json(await scene.Admin.PutAsJsonAsync($"/api/v1/admin/coupons/{couponId}/activation", new { isEnabled = false, version }, Ct)))
            Assert.False(disabled.RootElement.GetProperty("isEnabled").GetBoolean());
        await FeatureScene.Code(await scene.Admin.PutAsJsonAsync($"/api/v1/admin/coupons/{couponId}/activation", new { isEnabled = true, version }, Ct), HttpStatusCode.Conflict, "CONCURRENCY_CONFLICT");
        (await FeatureScene.Json(await scene.Admin.PutAsJsonAsync($"/api/v1/admin/coupons/{couponId}/activation", new { isEnabled = true }, Ct))).Dispose();
        await scene.Harness.CheckoutAsync(product, 1, "COD", couponCode: "SUMMER");
        using (var detail = await FeatureScene.Json(await scene.Admin.GetAsync($"/api/v1/admin/coupons/{couponId}", Ct)))
        {
            Assert.Equal(1, detail.RootElement.GetProperty("activeUsages").GetInt32());
            Assert.Equal(10m, detail.RootElement.GetProperty("discountGiven").GetDecimal());
        }
        // A limit below what has been used would make the coupon's own count invalid.
        await FeatureScene.Code(await scene.Admin.PutAsJsonAsync($"/api/v1/admin/coupons/{couponId}", new { code = "SUMMER", discountType = "Fixed", discountValue = 10m, minimumSubtotal = 0m, validFromUtc = DateTimeOffset.UtcNow.AddDays(-1), validUntilUtc = DateTimeOffset.UtcNow.AddDays(1), totalUsageLimit = 0, perUserUsageLimit = 1 }, Ct), HttpStatusCode.BadRequest, "VALIDATION_FAILED");

        var method = new { code = "express", name = "Express", fee = 50000m, currencyCode = "VND" };
        Assert.Equal(HttpStatusCode.Forbidden, (await scene.Customer.PostAsJsonAsync("/api/v1/admin/shipping-methods", method, Ct)).StatusCode);
        Guid methodId;
        using (var created = await FeatureScene.Json(await scene.Admin.PostAsJsonAsync("/api/v1/admin/shipping-methods", method, Ct), HttpStatusCode.Created))
        {
            methodId = created.RootElement.GetProperty("id").GetGuid();
            Assert.Equal("EXPRESS", created.RootElement.GetProperty("code").GetString());
        }
        await FeatureScene.Code(await scene.Admin.PostAsJsonAsync("/api/v1/admin/shipping-methods", method, Ct), HttpStatusCode.Conflict, "SHIPPING_CODE_ALREADY_EXISTS");
        using var anonymous = scene.Api.CreateClient();
        using (var active = await FeatureScene.Json(await anonymous.GetAsync("/api/v1/shipping-methods", Ct)))
            Assert.Equal(2, active.RootElement.GetArrayLength());
        (await FeatureScene.Json(await scene.Admin.PutAsJsonAsync($"/api/v1/admin/shipping-methods/{methodId}", new { name = "Express", fee = 60000m, currencyCode = "VND", isActive = false }, Ct))).Dispose();
        using (var active = await FeatureScene.Json(await anonymous.GetAsync("/api/v1/shipping-methods", Ct)))
            Assert.Equal(1, active.RootElement.GetArrayLength());
        using var audit = await FeatureScene.Json(await scene.Admin.GetAsync($"/api/v1/admin/audit-logs?targetType=Coupon&targetId={couponId}", Ct));
        Assert.Contains(audit.RootElement.GetProperty("items").EnumerateArray(), x => x.GetProperty("action").GetString() == "coupon.create");
        Assert.Equal(HttpStatusCode.Forbidden, (await scene.Customer.GetAsync("/api/v1/admin/audit-logs", Ct)).StatusCode);
    }

    [Fact]
    public async Task Wishlist_is_private_and_idempotent()
    {
        await using var scene = await FeatureScene.CreateAsync();
        if (scene is null) return;
        var product = await scene.Harness.SeedProductAsync(100m, 10);
        using var anonymous = scene.Api.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/wishlist", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await scene.Customer.PutAsync($"/api/v1/wishlist/{Guid.NewGuid()}", null, Ct)).StatusCode);
        var adds = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => scene.Customer.PutAsync($"/api/v1/wishlist/{product}", null, Ct)));
        Assert.All(adds, x => Assert.Equal(HttpStatusCode.NoContent, x.StatusCode));
        using (var list = await FeatureScene.Json(await scene.Customer.GetAsync("/api/v1/wishlist", Ct)))
        {
            var item = Assert.Single(list.RootElement.GetProperty("items").EnumerateArray());
            Assert.True(item.GetProperty("isAvailable").GetBoolean());
        }
        using (var other = await FeatureScene.Json(await scene.Stranger.GetAsync("/api/v1/wishlist", Ct)))
            Assert.Empty(other.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(HttpStatusCode.NoContent, (await scene.Customer.DeleteAsync($"/api/v1/wishlist/{product}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await scene.Customer.DeleteAsync($"/api/v1/wishlist/{product}", Ct)).StatusCode);
        using var empty = await FeatureScene.Json(await scene.Customer.GetAsync("/api/v1/wishlist", Ct));
        Assert.Empty(empty.RootElement.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task Reviews_require_a_delivered_purchase_allow_one_per_line_and_moderation_keeps_the_text()
    {
        await using var scene = await FeatureScene.CreateAsync();
        if (scene is null) return;
        var product = await scene.Harness.SeedProductAsync(100m, 10);
        var orderId = await scene.ProcessingOrderAsync(product, 1, "COD");
        await using var read = scene.Harness.NewDb();
        var orderItemId = await read.OrderItems.AsNoTracking().Where(x => x.OrderId == orderId).Select(x => x.Id).SingleAsync(Ct);
        var body = new { orderItemId, rating = 5, comment = "Great" };

        await FeatureScene.Code(await scene.Customer.PostAsJsonAsync($"/api/v1/products/{product}/reviews", body, Ct), HttpStatusCode.Conflict, "REVIEW_NOT_ELIGIBLE");
        await scene.ShipAsync(orderId, deliver: true);
        await FeatureScene.Code(await scene.Stranger.PostAsJsonAsync($"/api/v1/products/{product}/reviews", body, Ct), HttpStatusCode.NotFound, "RESOURCE_NOT_FOUND");
        await FeatureScene.Code(await scene.Customer.PostAsJsonAsync($"/api/v1/products/{product}/reviews", body with { rating = 6 }, Ct), HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        var other = await scene.Harness.SeedProductAsync(50m, 1);
        await FeatureScene.Code(await scene.Customer.PostAsJsonAsync($"/api/v1/products/{other}/reviews", body, Ct), HttpStatusCode.NotFound, "RESOURCE_NOT_FOUND");
        var attempts = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => scene.Customer.PostAsJsonAsync($"/api/v1/products/{product}/reviews", body, Ct)));
        var created = Assert.Single(attempts, x => x.StatusCode == HttpStatusCode.Created);
        await FeatureScene.Code(Assert.Single(attempts, x => x.StatusCode == HttpStatusCode.Conflict), HttpStatusCode.Conflict, "REVIEW_ALREADY_EXISTS");
        Guid reviewId;
        using (var document = await FeatureScene.Json(created, HttpStatusCode.Created)) reviewId = document.RootElement.GetProperty("id").GetGuid();

        using var anonymous = scene.Api.CreateClient();
        using (var list = await FeatureScene.Json(await anonymous.GetAsync($"/api/v1/products/{product}/reviews", Ct)))
        {
            Assert.Equal(5, list.RootElement.GetProperty("summary").GetProperty("averageRating").GetDouble());
            var row = Assert.Single(list.RootElement.GetProperty("items").EnumerateArray());
            Assert.Equal("Test", row.GetProperty("authorName").GetString());
            Assert.False(row.TryGetProperty("userId", out _));
        }
        Assert.Equal(HttpStatusCode.Forbidden, (await scene.Customer.PostAsJsonAsync($"/api/v1/admin/reviews/{reviewId}/moderate", new { status = "Hidden", reason = "x" }, Ct)).StatusCode);
        await FeatureScene.Code(await scene.Admin.PostAsJsonAsync($"/api/v1/admin/reviews/{reviewId}/moderate", new { status = "Hidden", reason = "" }, Ct), HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        (await FeatureScene.Json(await scene.Admin.PostAsJsonAsync($"/api/v1/admin/reviews/{reviewId}/moderate", new { status = "Hidden", reason = "Off topic" }, Ct))).Dispose();
        using (var hidden = await FeatureScene.Json(await anonymous.GetAsync($"/api/v1/products/{product}/reviews", Ct)))
        {
            Assert.Empty(hidden.RootElement.GetProperty("items").EnumerateArray());
            Assert.Equal(JsonValueKind.Null, hidden.RootElement.GetProperty("summary").GetProperty("averageRating").ValueKind);
        }
        using var moderated = await FeatureScene.Json(await scene.Admin.GetAsync("/api/v1/admin/reviews?status=Hidden", Ct));
        var kept = Assert.Single(moderated.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal("Great", kept.GetProperty("comment").GetString());
        Assert.Equal("Off topic", kept.GetProperty("moderationReason").GetString());
    }
}

public sealed class OperationalBehaviorTests
{
    private static CancellationToken Ct => FeatureScene.Ct;

    [Fact]
    public async Task Health_endpoints_separate_liveness_from_readiness_and_only_sql_gates_readiness()
    {
        await using var harness = await MessagingHarness.CreateAsync();
        if (harness is null) return;
        await using var api = new ApiFactory(harness.Connection, new Dictionary<string, string?> { ["Redis:ConnectionString"] = "127.0.0.1:1", ["Kafka:BootstrapServers"] = "127.0.0.1:1" });
        using var client = api.CreateClient();
        using (var live = await FeatureScene.Json(await client.GetAsync("/health/live", Ct)))
        {
            Assert.Equal("Healthy", live.RootElement.GetProperty("status").GetString());
            Assert.Empty(live.RootElement.GetProperty("checks").EnumerateObject());
        }
        using (var ready = await FeatureScene.Json(await client.GetAsync("/health/ready", Ct)))
        {
            Assert.Equal("Degraded", ready.RootElement.GetProperty("status").GetString());
            var checks = ready.RootElement.GetProperty("checks");
            Assert.Equal("Healthy", checks.GetProperty("sql").GetProperty("status").GetString());
            Assert.Equal("Degraded", checks.GetProperty("redis").GetProperty("status").GetString());
            Assert.Equal("Degraded", checks.GetProperty("kafka").GetProperty("status").GetString());
        }
        // Losing SQL Server after startup takes the instance out of rotation but does not ask for a restart.
        await using (var db = harness.NewDb()) await db.Database.EnsureDeletedAsync(Ct);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live", Ct)).StatusCode);
        var failed = await client.GetAsync("/health/ready", Ct);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        var body = await failed.Content.ReadAsStringAsync(Ct);
        Assert.DoesNotContain("Password", body);
        Assert.DoesNotContain("127.0.0.1", body);
    }

    [Fact]
    public async Task Responses_carry_security_headers_and_cors_admits_only_configured_origins()
    {
        await using var harness = await MessagingHarness.CreateAsync();
        if (harness is null) return;
        await using var api = new ApiFactory(harness.Connection, new Dictionary<string, string?> { ["Cors:AllowedOrigins:0"] = "http://localhost:5173" });
        using var client = api.CreateClient();
        var response = await client.GetAsync("/api/v1/shipping-methods", Ct);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Contains("default-src 'none'", response.Headers.GetValues("Content-Security-Policy").Single());

        using var allowed = new HttpRequestMessage(HttpMethod.Options, "/api/v1/auth/refresh");
        allowed.Headers.Add("Origin", "http://localhost:5173");
        allowed.Headers.Add("Access-Control-Request-Method", "POST");
        allowed.Headers.Add("Access-Control-Request-Headers", "x-refresh-token-transport");
        var preflight = await client.SendAsync(allowed, Ct);
        Assert.Equal("http://localhost:5173", preflight.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Equal("true", preflight.Headers.GetValues("Access-Control-Allow-Credentials").Single());

        using var denied = new HttpRequestMessage(HttpMethod.Options, "/api/v1/auth/refresh");
        denied.Headers.Add("Origin", "https://evil.example");
        denied.Headers.Add("Access-Control-Request-Method", "POST");
        Assert.False((await client.SendAsync(denied, Ct)).Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Search_and_checkout_rate_limits_answer_with_the_standard_problem()
    {
        await using var harness = await MessagingHarness.CreateAsync();
        if (harness is null) return;
        await using var api = new ApiFactory(harness.Connection, new Dictionary<string, string?> { ["RateLimits:Search"] = "2" });
        using var client = api.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/products", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/products", Ct)).StatusCode);
        await FeatureScene.Code(await client.GetAsync("/api/v1/products", Ct), HttpStatusCode.TooManyRequests, "RATE_LIMITED");
    }

    [Fact]
    public async Task D20_browser_refresh_token_travels_only_in_an_http_only_cookie()
    {
        await using var harness = await MessagingHarness.CreateAsync();
        if (harness is null) return;
        await using var api = new ApiFactory(harness.Connection);
        using var client = api.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        const string email = "cookie@example.invalid", password = "StrongPassword123!";
        (await client.PostAsJsonAsync("/api/v1/auth/register", new { email, password, firstName = "Cookie", lastName = "User" }, Ct)).EnsureSuccessStatusCode();

        async Task<HttpResponseMessage> Send(string path, object body, string? cookie = null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
            request.Headers.Add("X-Refresh-Token-Transport", "cookie");
            if (cookie is not null) request.Headers.Add("Cookie", cookie);
            return await client.SendAsync(request, Ct);
        }
        static string CookieFrom(HttpResponseMessage response)
        {
            var header = response.Headers.GetValues("Set-Cookie").Single();
            Assert.Contains("httponly", header, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("samesite=strict", header, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("path=/api/v1/auth", header, StringComparison.OrdinalIgnoreCase);
            return header.Split(';')[0];
        }

        var login = await Send("/api/v1/auth/login", new { email, password });
        var first = CookieFrom(login);
        using (var body = await FeatureScene.Json(login))
        {
            Assert.False(body.RootElement.TryGetProperty("refreshToken", out _));
            Assert.True(body.RootElement.TryGetProperty("accessToken", out _));
        }
        var refreshed = await Send("/api/v1/auth/refresh", new { }, first);
        var second = CookieFrom(refreshed);
        Assert.NotEqual(first, second);
        using (var body = await FeatureScene.Json(refreshed)) Assert.False(body.RootElement.TryGetProperty("refreshToken", out _));

        // Without the transport header the cookie is ignored, so a forged cross-site POST cannot use it.
        using (var plain = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/refresh") { Content = JsonContent.Create(new { }) })
        {
            plain.Headers.Add("Cookie", second);
            await FeatureScene.Code(await client.SendAsync(plain, Ct), HttpStatusCode.Unauthorized, "REFRESH_TOKEN_REVOKED");
        }
        Assert.Equal(HttpStatusCode.NoContent, (await Send("/api/v1/auth/logout", new { }, second)).StatusCode);
        await FeatureScene.Code(await Send("/api/v1/auth/refresh", new { }, second), HttpStatusCode.Unauthorized, "REFRESH_TOKEN_REVOKED");
    }

    [Fact]
    public async Task Production_refuses_unsafe_configuration_and_marks_the_refresh_cookie_secure()
    {
        await using var harness = await MessagingHarness.CreateAsync();
        if (harness is null) return;
        await using (var wildcard = new ApiFactory(harness.Connection, new Dictionary<string, string?> { ["Cors:AllowedOrigins:0"] = "*" }, "Production"))
            Assert.ThrowsAny<Exception>(() => wildcard.CreateClient());
        await using (var plainHttp = new ApiFactory(harness.Connection, new Dictionary<string, string?> { ["Cors:AllowedOrigins:0"] = "http://shop.example" }, "Production"))
            Assert.ThrowsAny<Exception>(() => plainHttp.CreateClient());
        await using var api = new ApiFactory(harness.Connection, new Dictionary<string, string?> { ["Cors:AllowedOrigins:0"] = "https://shop.example" }, "Production");
        using var client = api.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        const string email = "secure@example.invalid", password = "StrongPassword123!";
        (await client.PostAsJsonAsync("/api/v1/auth/register", new { email, password, firstName = "Secure", lastName = "User" }, Ct)).EnsureSuccessStatusCode();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login") { Content = JsonContent.Create(new { email, password }) };
        request.Headers.Add("X-Refresh-Token-Transport", "cookie");
        var login = await client.SendAsync(request, Ct);
        Assert.Contains("secure", login.Headers.GetValues("Set-Cookie").Single(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/swagger/index.html", Ct)).StatusCode);
    }
}
