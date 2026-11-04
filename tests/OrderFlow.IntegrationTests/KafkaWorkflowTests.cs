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
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OrderFlow.Application.Messaging;
using OrderFlow.Application.Notifications;
using OrderFlow.Domain.Inventory;
using OrderFlow.Domain.Ordering;
using OrderFlow.Domain.Payments;
using OrderFlow.Infrastructure.Messaging;
using OrderFlow.Infrastructure.Persistence;
using Xunit;

namespace OrderFlow.IntegrationTests;

// The real Worker against real Kafka and SQL Server; skipped when the broker is not running.
public sealed class KafkaWorkflowTests
{
    private sealed class ToggleProvider : INotificationProvider
    {
        public volatile bool Fail = true;
        public string Channel => "Test";
        public Task SendAsync(NotificationMessage message, CancellationToken cancellationToken) =>
            Fail ? throw new TimeoutException("Provider is down") : Task.CompletedTask;
    }

    // Publishes every message twice, which is what a publisher crash after the broker ack produces.
    private sealed class DuplicatingProducer(IEventProducer inner) : IEventProducer
    {
        public async Task PublishAsync(OutgoingMessage message, CancellationToken cancellationToken)
        {
            await inner.PublishAsync(message, cancellationToken);
            await inner.PublishAsync(message, cancellationToken);
        }
    }

    [Fact]
    public async Task Full_cod_checkout_reaches_processing_with_a_traceable_event_chain()
    {
        await using var harness = await MessagingHarness.CreateAsync(requireKafka: true);
        var product = await harness!.SeedProductAsync(10m, 5);
        var orderId = await harness.CheckoutAsync(product, 2, "COD", "trace-full-checkout");
        using var worker = await harness.StartWorkerAsync();

        await harness.WaitForStatusAsync(orderId, OrderStatus.Processing);
        await worker.StopAsync(MessagingHarness.Ct);

        Assert.Equal((3, 0), await harness.StockAsync(product));
        Assert.Equal([ReservationStatus.Committed], await harness.ReservationsAsync(orderId));
        await using var db = harness.NewDb();
        var payment = await db.Payments.AsNoTracking().SingleAsync(x => x.OrderId == orderId, MessagingHarness.Ct);
        Assert.Equal((PaymentStatus.Succeeded, SettlementStatus.PendingCollection), (payment.Status, payment.SettlementStatus));
        var history = await db.OrderStatusHistories.AsNoTracking().Where(x => x.OrderId == orderId).OrderBy(x => x.ChangedAtUtc).ToListAsync(MessagingHarness.Ct);
        Assert.Equal([OrderStatus.PendingInventory, OrderStatus.AwaitingPayment, OrderStatus.Paid, OrderStatus.Processing], history.Select(x => x.ToStatus).ToArray());
        Assert.All(history, x => Assert.Equal("trace-full-checkout", x.CorrelationId));
        Assert.All(history.Skip(1), x => Assert.NotNull(x.EventId));
        var events = await db.OutboxMessages.AsNoTracking().Where(x => x.AggregateId == orderId).ToListAsync(MessagingHarness.Ct);
        Assert.All(events, x => { Assert.NotNull(x.ProcessedAtUtc); Assert.Equal("trace-full-checkout", x.CorrelationId); });
        Assert.All(events.Where(x => x.EventType != EventTypes.OrderCreated), x => Assert.Contains(events, parent => parent.Id == x.CausationId));
        Assert.Contains(events, x => x.EventType == EventTypes.InventoryCommitted);
    }

    [Fact]
    public async Task AC_08_declined_card_ends_payment_failed_with_stock_released_and_notified()
    {
        await using var harness = await MessagingHarness.CreateAsync(requireKafka: true);
        var product = await harness!.SeedProductAsync(100.13m, 5);
        var orderId = await harness.CheckoutAsync(product, 1, "CardSimulation");
        using var worker = await harness.StartWorkerAsync();

        await harness.WaitForStatusAsync(orderId, OrderStatus.PaymentFailed);
        await harness.WaitForAsync(db => db.InventoryReservations.AsNoTracking().Where(x => x.OrderId == orderId).Select(x => x.Status).SingleAsync(MessagingHarness.Ct),
            x => x == ReservationStatus.Released, "reservation release");
        await harness.WaitForAsync(db => db.Notifications.AsNoTracking().CountAsync(x => x.OrderId == orderId && x.Type == EventTypes.PaymentFailed, MessagingHarness.Ct), x => x == 1, "notification");
        await worker.StopAsync(MessagingHarness.Ct);
        Assert.Equal((5, 0), await harness.StockAsync(product));
    }

    [Fact]
    public async Task AC_04_every_message_delivered_twice_still_has_one_effect()
    {
        await using var harness = await MessagingHarness.CreateAsync(requireKafka: true);
        var product = await harness!.SeedProductAsync(10m, 5);
        var orderId = await harness.CheckoutAsync(product, 2, "CardSimulation");
        using var worker = await harness.StartWorkerAsync(services =>
        {
            services.AddSingleton<KafkaEventProducer>();
            services.Replace(ServiceDescriptor.Singleton<IEventProducer>(sp => new DuplicatingProducer(sp.GetRequiredService<KafkaEventProducer>())));
        });

        await harness.WaitForStatusAsync(orderId, OrderStatus.Processing);
        // Every consumer has also seen the repeats once the last notification is settled and the topics drain.
        await harness.WaitForAsync(db => db.Notifications.AsNoTracking().CountAsync(x => x.OrderId == orderId, MessagingHarness.Ct), x => x >= 1, "notification");
        await Task.Delay(TimeSpan.FromSeconds(3), MessagingHarness.Ct);
        await worker.StopAsync(MessagingHarness.Ct);

        Assert.Equal((3, 0), await harness.StockAsync(product));
        await using var db = harness.NewDb();
        Assert.Equal(1, await db.Payments.CountAsync(x => x.OrderId == orderId, MessagingHarness.Ct));
        Assert.Equal(1, await db.Notifications.CountAsync(x => x.OrderId == orderId && x.Type == EventTypes.PaymentSucceeded, MessagingHarness.Ct));
        Assert.Equal(1, await db.OutboxMessages.CountAsync(x => x.AggregateId == orderId && x.EventType == EventTypes.PaymentRequested, MessagingHarness.Ct));
        Assert.Equal(1, await db.OutboxMessages.CountAsync(x => x.AggregateId == orderId && x.EventType == EventTypes.InventoryCommitted, MessagingHarness.Ct));
        Assert.Equal(4, await db.OrderStatusHistories.CountAsync(x => x.OrderId == orderId, MessagingHarness.Ct));
    }

    [Fact]
    public async Task AC_03_event_committed_during_a_kafka_outage_is_published_after_recovery()
    {
        await using var harness = await MessagingHarness.CreateAsync(requireKafka: true);
        var product = await harness!.SeedProductAsync(10m, 5);
        var orderId = await harness.CheckoutAsync(product, 1, "COD");
        // A dead port stands in for the broker here; the end-to-end script stops the real container.
        using (var outage = await harness.StartWorkerAsync(settings: new() { ["Kafka:BootstrapServers"] = "127.0.0.1:1", ["Kafka:PublishTimeoutMilliseconds"] = "1000" }))
        {
            var row = await harness.WaitForAsync(db => db.OutboxMessages.AsNoTracking().SingleAsync(x => x.AggregateId == orderId, MessagingHarness.Ct), x => x.AttemptCount >= 1 && x.LastError is not null, "failed publish attempt");
            Assert.Null(row.ProcessedAtUtc);
            await outage.StopAsync(MessagingHarness.Ct);
        }
        await using (var db = harness.NewDb())
            Assert.Equal(OrderStatus.PendingInventory, (await db.Orders.AsNoTracking().SingleAsync(x => x.Id == orderId, MessagingHarness.Ct)).Status);

        using var recovered = await harness.StartWorkerAsync();
        await harness.WaitForStatusAsync(orderId, OrderStatus.Processing);
        await recovered.StopAsync(MessagingHarness.Ct);
    }

    [Fact]
    public async Task Worker_restart_continues_incomplete_processing()
    {
        await using var harness = await MessagingHarness.CreateAsync(requireKafka: true);
        var product = await harness!.SeedProductAsync(10m, 5);
        var orderId = await harness.CheckoutAsync(product, 1, "CardSimulation");
        using (var first = await harness.StartWorkerAsync())
        {
            await harness.WaitForAsync(db => db.InboxMessages.AsNoTracking().AnyAsync(MessagingHarness.Ct), x => x, "first consumed event");
            await first.StopAsync(MessagingHarness.Ct);
        }
        using var second = await harness.StartWorkerAsync();
        await harness.WaitForStatusAsync(orderId, OrderStatus.Processing);
        await second.StopAsync(MessagingHarness.Ct);
        Assert.Equal((4, 0), await harness.StockAsync(product));
    }

    [Fact]
    public async Task AC_10_repeated_notification_failure_reaches_failure_store_and_dlq_then_admin_replay_keeps_the_event_id()
    {
        await using var harness = await MessagingHarness.CreateAsync(requireKafka: true);
        var product = await harness!.SeedProductAsync(10m, 5);
        var orderId = await harness.CheckoutAsync(product, 1, "COD");
        var provider = new ToggleProvider();
        using var worker = await harness.StartWorkerAsync(x => x.Replace(ServiceDescriptor.Singleton<INotificationProvider>(provider)));

        // A failing notification must not hold up the order.
        await harness.WaitForStatusAsync(orderId, OrderStatus.Processing);
        var failed = await harness.WaitForAsync(db => db.FailedMessages.AsNoTracking().SingleOrDefaultAsync(x => x.EventType == EventTypes.PaymentSucceeded, MessagingHarness.Ct),
            x => x?.DeadLetteredAtUtc is not null, "failure record");
        Assert.Equal("notifications", failed!.ConsumerName);
        Assert.Equal(harness.Topic("payment.succeeded"), failed.SourceTopic);
        Assert.Equal(3, failed.AttemptCount);
        Assert.Equal("TimeoutException", failed.ErrorSummary);
        var succeeded = await harness.OutboxEventAsync(orderId, EventTypes.PaymentSucceeded);
        Assert.Equal(succeeded.EventId, failed.EventId);
        Assert.Equal(succeeded.EventId, EventSerializer.Parse(await harness.ReadDeadLetterAsync("payment.succeeded")).EventId);

        await using var api = new AdminApiFactory(harness.Connection);
        using var anonymous = api.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/admin/failed-messages", MessagingHarness.Ct)).StatusCode);
        using var customer = await SignedInClientAsync(api, "dlq-customer@example.invalid", admin: false);
        Assert.Equal(HttpStatusCode.Forbidden, (await customer.PostAsync($"/api/v1/admin/failed-messages/{failed.Id}/reprocess", null, MessagingHarness.Ct)).StatusCode);
        using var admin = await SignedInClientAsync(api, "dlq-admin@example.invalid", admin: true);
        using (var list = JsonDocument.Parse(await admin.GetStringAsync("/api/v1/admin/failed-messages?status=Failed", MessagingHarness.Ct)))
        {
            var item = list.RootElement.GetProperty("items").EnumerateArray().Single(x => x.GetProperty("id").GetGuid() == failed.Id);
            Assert.False(item.TryGetProperty("rawMessage", out _));
        }
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsync($"/api/v1/admin/failed-messages/{Guid.NewGuid()}/reprocess", null, MessagingHarness.Ct)).StatusCode);

        provider.Fail = false;
        Assert.Equal(HttpStatusCode.Accepted, (await admin.PostAsync($"/api/v1/admin/failed-messages/{failed.Id}/reprocess", null, MessagingHarness.Ct)).StatusCode);

        await harness.WaitForAsync(db => db.Notifications.AsNoTracking().CountAsync(x => x.SourceEventId == succeeded.EventId, MessagingHarness.Ct), x => x == 1, "replayed notification");
        await harness.WaitForAsync(db => db.FailedMessages.AsNoTracking().Where(x => x.Id == failed.Id).Select(x => x.Status).SingleAsync(MessagingHarness.Ct), x => x == FailedMessageStatus.Replayed, "replayed status");
        await worker.StopAsync(MessagingHarness.Ct);
        Assert.Equal((4, 0), await harness.StockAsync(product));
        await using var db = harness.NewDb();
        Assert.Equal(1, await db.Payments.CountAsync(MessagingHarness.Ct));
    }

    [Fact]
    public async Task Malformed_and_future_version_messages_go_straight_to_the_failure_store_without_blocking_the_partition()
    {
        await using var harness = await MessagingHarness.CreateAsync(requireKafka: true);
        var product = await harness!.SeedProductAsync(10m, 5);
        using var worker = await harness.StartWorkerAsync();
        await harness.ProduceRawAsync("order.created", "poison", "{not json");
        var future = EventSerializer.Serialize(EventSerializer.Create(EventTypes.OrderCreated, Guid.NewGuid(), "poison-test", null, new { }, DateTimeOffset.UtcNow) with { EventVersion = 99 });
        await harness.ProduceRawAsync("order.created", "poison", future);

        var rows = await harness.WaitForAsync(db => db.FailedMessages.AsNoTracking().Where(x => x.ConsumerName == "inventory").ToListAsync(MessagingHarness.Ct), x => x.Count == 2, "poison records");
        Assert.All(rows, x => { Assert.Equal(1, x.AttemptCount); Assert.Null(x.EventId); });
        Assert.Contains(rows, x => x.ErrorSummary == "Envelope is not valid JSON.");
        Assert.Contains(rows, x => x.ErrorSummary == "Unsupported event version.");

        // The partition keeps moving: a real order placed after the poison still completes.
        var orderId = await harness.CheckoutAsync(product, 1, "COD");
        await harness.WaitForStatusAsync(orderId, OrderStatus.Processing);

        await using var api = new AdminApiFactory(harness.Connection);
        using var admin = await SignedInClientAsync(api, "poison-admin@example.invalid", admin: true);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync($"/api/v1/admin/failed-messages/{rows[0].Id}/reprocess", null, MessagingHarness.Ct)).StatusCode);
        await worker.StopAsync(MessagingHarness.Ct);
    }

    [Fact]
    public async Task D_24_replay_is_refused_once_the_event_is_older_than_inbox_retention_even_after_a_recent_failure()
    {
        await using var harness = await MessagingHarness.CreateAsync();
        var oldEvent = EventSerializer.Serialize(EventSerializer.Create(EventTypes.InventoryLow, Guid.NewGuid(), "replay-window", null, new InventoryLowPayload(Guid.NewGuid(), 1, 2), DateTimeOffset.UtcNow.AddDays(-31)));
        var recentEvent = EventSerializer.Serialize(EventSerializer.Create(EventTypes.InventoryLow, Guid.NewGuid(), "replay-window", null, new InventoryLowPayload(Guid.NewGuid(), 1, 2), DateTimeOffset.UtcNow.AddDays(-29)));
        var tooOld = new FailedMessage { Id = Guid.NewGuid(), ConsumerName = "notifications", SourceTopic = "inventory.low", Partition = 0, Offset = 1, EventId = EventSerializer.Parse(oldEvent).EventId,
            EventType = EventTypes.InventoryLow, RawMessage = oldEvent, AttemptCount = 1, ErrorSummary = "Test", FailedAtUtc = DateTimeOffset.UtcNow };
        var withinWindow = new FailedMessage { Id = Guid.NewGuid(), ConsumerName = "notifications", SourceTopic = "inventory.low", Partition = 0, Offset = 2, EventId = EventSerializer.Parse(recentEvent).EventId,
            EventType = EventTypes.InventoryLow, RawMessage = recentEvent, AttemptCount = 1, ErrorSummary = "Test", FailedAtUtc = DateTimeOffset.UtcNow };
        await using (var db = harness!.NewDb())
        {
            db.FailedMessages.AddRange(tooOld, withinWindow);
            await db.SaveChangesAsync(MessagingHarness.Ct);
        }
        await using var api = new AdminApiFactory(harness.Connection);
        using var admin = await SignedInClientAsync(api, "window-admin@example.invalid", admin: true);

        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync($"/api/v1/admin/failed-messages/{tooOld.Id}/reprocess", null, MessagingHarness.Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await admin.PostAsync($"/api/v1/admin/failed-messages/{withinWindow.Id}/reprocess", null, MessagingHarness.Ct)).StatusCode);
    }

    private static async Task<HttpClient> SignedInClientAsync(AdminApiFactory api, string email, bool admin)
    {
        var client = api.CreateClient();
        const string password = "StrongPassword123!";
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/v1/auth/register", new { email, password, firstName = "Test", lastName = "User" }, MessagingHarness.Ct)).StatusCode);
        if (admin)
        {
            using var scope = api.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<OrderFlowDbContext>();
            var userId = await db.Users.Where(x => x.NormalizedEmail == email.ToUpperInvariant()).Select(x => x.Id).SingleAsync(MessagingHarness.Ct);
            var roleId = await db.Roles.Where(x => x.Name == "Admin").Select(x => x.Id).SingleAsync(MessagingHarness.Ct);
            db.UserRoles.Add(new IdentityUserRole<Guid> { UserId = userId, RoleId = roleId });
            await db.SaveChangesAsync(MessagingHarness.Ct);
        }
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password }, MessagingHarness.Ct);
        using var tokens = JsonDocument.Parse(await login.Content.ReadAsStringAsync(MessagingHarness.Ct));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.RootElement.GetProperty("accessToken").GetString());
        return client;
    }

    private sealed class AdminApiFactory(string connection) : WebApplicationFactory<Program>
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
