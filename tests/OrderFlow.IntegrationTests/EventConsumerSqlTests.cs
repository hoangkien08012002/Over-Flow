using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using OrderFlow.Application.Messaging;
using OrderFlow.Domain.Inventory;
using OrderFlow.Domain.Ordering;
using OrderFlow.Domain.Payments;
using OrderFlow.Infrastructure.Messaging;
using OrderFlow.Infrastructure.Persistence;
using Xunit;

namespace OrderFlow.IntegrationTests;

// Exercises consumer decisions and race orderings directly against SQL Server, where arrival order can be controlled.
public sealed class EventConsumerSqlTests
{
    private sealed class MutableClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset current = now;
        public override DateTimeOffset GetUtcNow() => current;
        public void Advance(TimeSpan by) => current = current.Add(by);
    }

    private static async Task ReserveAndRequestPaymentAsync(MessagingHarness harness, IHost worker, Guid orderId)
    {
        Assert.True(await MessagingHarness.DeliverAsync(worker, "inventory", await harness.OutboxEventAsync(orderId, EventTypes.OrderCreated)));
        Assert.True(await MessagingHarness.DeliverAsync(worker, "ordering", await harness.OutboxEventAsync(orderId, EventTypes.InventoryReserved)));
    }

    private static async Task<CheckoutResult> CancelAsync(MessagingHarness harness, Guid orderId)
    {
        await using var db = harness.NewDb();
        return await MessagingHarness.NewCheckout(db).CancelAsync(harness.UserId, orderId, "Changed my mind", "cancel-test", MessagingHarness.Ct);
    }

    [Fact]
    public async Task AC_04_same_event_id_delivered_twice_has_one_effect_per_consumer()
    {
        await using var harness = await MessagingHarness.CreateAsync();
        var product = await harness!.SeedProductAsync(10m, 5);
        var orderId = await harness.CheckoutAsync(product, 2, "CardSimulation");
        using var worker = harness.BuildWorker();
        var created = await harness.OutboxEventAsync(orderId, EventTypes.OrderCreated);

        Assert.True(await MessagingHarness.DeliverAsync(worker, "inventory", created));
        Assert.False(await MessagingHarness.DeliverAsync(worker, "inventory", created));
        Assert.Equal((5, 2), await harness.StockAsync(product));
        await using (var db = harness.NewDb())
        {
            Assert.Equal(1, await db.InventoryReservations.CountAsync(x => x.OrderId == orderId, MessagingHarness.Ct));
            Assert.Equal(1, await db.OutboxMessages.CountAsync(x => x.AggregateId == orderId && x.EventType == EventTypes.InventoryReserved, MessagingHarness.Ct));
            Assert.Equal(1, await db.InboxMessages.CountAsync(x => x.EventId == created.EventId, MessagingHarness.Ct));
        }

        var reserved = await harness.OutboxEventAsync(orderId, EventTypes.InventoryReserved);
        Assert.Equal(created.EventId, reserved.CausationId);
        Assert.Equal(created.CorrelationId, reserved.CorrelationId);
        Assert.True(await MessagingHarness.DeliverAsync(worker, "ordering", reserved));
        Assert.False(await MessagingHarness.DeliverAsync(worker, "ordering", reserved));

        var requested = await harness.OutboxEventAsync(orderId, EventTypes.PaymentRequested);
        Assert.True(await MessagingHarness.DeliverAsync(worker, "payment", requested));
        Assert.False(await MessagingHarness.DeliverAsync(worker, "payment", requested));
        var succeeded = await harness.OutboxEventAsync(orderId, EventTypes.PaymentSucceeded);
        // Each logical consumer still processes the event once for itself.
        Assert.True(await MessagingHarness.DeliverAsync(worker, "ordering", succeeded));
        Assert.True(await MessagingHarness.DeliverAsync(worker, "inventory", succeeded));
        Assert.True(await MessagingHarness.DeliverAsync(worker, "notifications", succeeded));
        Assert.False(await MessagingHarness.DeliverAsync(worker, "inventory", succeeded));
        Assert.False(await MessagingHarness.DeliverAsync(worker, "notifications", succeeded));

        Assert.Equal((3, 0), await harness.StockAsync(product));
        await using var verify = harness.NewDb();
        Assert.Equal(1, await verify.Payments.CountAsync(x => x.OrderId == orderId, MessagingHarness.Ct));
        Assert.Equal(1, await verify.Notifications.CountAsync(x => x.SourceEventId == succeeded.EventId, MessagingHarness.Ct));
    }

    [Fact]
    public async Task FR_PAY_002_inventory_rejection_never_starts_payment_and_reserves_no_line()
    {
        await using var harness = await MessagingHarness.CreateAsync();
        var stocked = await harness!.SeedProductAsync(10m, 5);
        var scarce = await harness.SeedProductAsync(10m, 1);
        var orderId = await harness.CheckoutMultipleAsync((stocked, 2), (scarce, 3));
        using var worker = harness.BuildWorker();

        await MessagingHarness.DeliverAsync(worker, "inventory", await harness.OutboxEventAsync(orderId, EventTypes.OrderCreated));
        // All or nothing: the line with enough stock must not stay reserved.
        Assert.Equal((5, 0), await harness.StockAsync(stocked));
        Assert.Equal((1, 0), await harness.StockAsync(scarce));
        var rejected = await harness.OutboxEventAsync(orderId, EventTypes.InventoryRejected);
        var payload = EventSerializer.ReadPayload<InventoryRejectedPayload>(rejected);
        var line = Assert.Single(payload.Items);
        Assert.Equal((scarce, 3, 1, "INSUFFICIENT_STOCK"), (line.ProductId, line.RequestedQuantity, line.AvailableQuantity, line.ReasonCode));

        await MessagingHarness.DeliverAsync(worker, "ordering", rejected);
        await using var db = harness.NewDb();
        Assert.Equal(OrderStatus.Rejected, (await db.Orders.AsNoTracking().SingleAsync(x => x.Id == orderId, MessagingHarness.Ct)).Status);
        Assert.False(await db.OutboxMessages.AnyAsync(x => x.AggregateId == orderId && x.EventType == EventTypes.PaymentRequested, MessagingHarness.Ct));
        Assert.False(await db.Payments.AnyAsync(x => x.OrderId == orderId, MessagingHarness.Ct));
    }

    [Fact]
    public async Task AC_08_payment_failure_releases_once_marks_failed_and_notifies()
    {
        await using var harness = await MessagingHarness.CreateAsync();
        var product = await harness!.SeedProductAsync(100.13m, 5);
        var orderId = await harness.CheckoutAsync(product, 1, "CardSimulation");
        using var worker = harness.BuildWorker();
        await ReserveAndRequestPaymentAsync(harness, worker, orderId);
        await MessagingHarness.DeliverAsync(worker, "payment", await harness.OutboxEventAsync(orderId, EventTypes.PaymentRequested));
        var failed = await harness.OutboxEventAsync(orderId, EventTypes.PaymentFailed);
        Assert.Equal("CARD_DECLINED", EventSerializer.ReadPayload<PaymentResultPayload>(failed).ResultCode);

        await MessagingHarness.DeliverAsync(worker, "ordering", failed);
        await MessagingHarness.DeliverAsync(worker, "inventory", failed);
        await MessagingHarness.DeliverAsync(worker, "notifications", failed);
        Assert.False(await MessagingHarness.DeliverAsync(worker, "inventory", failed));

        Assert.Equal((5, 0), await harness.StockAsync(product));
        Assert.Equal([ReservationStatus.Released], await harness.ReservationsAsync(orderId));
        await using var db = harness.NewDb();
        var order = await db.Orders.AsNoTracking().Include(x => x.History).SingleAsync(x => x.Id == orderId, MessagingHarness.Ct);
        Assert.Equal(OrderStatus.PaymentFailed, order.Status);
        Assert.Equal("CARD_DECLINED", order.History.Single(x => x.ToStatus == OrderStatus.PaymentFailed).Reason);
        Assert.Equal(PaymentStatus.Failed, (await db.Payments.AsNoTracking().SingleAsync(x => x.OrderId == orderId, MessagingHarness.Ct)).Status);
        Assert.Equal(1, await db.OutboxMessages.CountAsync(x => x.AggregateId == orderId && x.EventType == EventTypes.InventoryReleased, MessagingHarness.Ct));
        Assert.Equal(1, await db.Notifications.CountAsync(x => x.OrderId == orderId && x.Type == EventTypes.PaymentFailed, MessagingHarness.Ct));
    }

    [Fact]
    public async Task Cancellation_before_reservation_stops_the_late_order_created()
    {
        await using var harness = await MessagingHarness.CreateAsync();
        var product = await harness!.SeedProductAsync(10m, 5);
        var orderId = await harness.CheckoutAsync(product, 1, "COD");
        using var worker = harness.BuildWorker();
        Assert.Equal(200, (await CancelAsync(harness, orderId)).StatusCode);

        await MessagingHarness.DeliverAsync(worker, "inventory", await harness.OutboxEventAsync(orderId, EventTypes.OrderCreated));

        Assert.Empty(await harness.ReservationsAsync(orderId));
        Assert.Equal((5, 0), await harness.StockAsync(product));
        await using var db = harness.NewDb();
        Assert.False(await db.OutboxMessages.AnyAsync(x => x.AggregateId == orderId && x.EventType == EventTypes.InventoryReserved, MessagingHarness.Ct));
    }

    [Fact]
    public async Task Cancellation_before_charge_stops_the_late_payment_request()
    {
        await using var harness = await MessagingHarness.CreateAsync();
        var product = await harness!.SeedProductAsync(10m, 5);
        var orderId = await harness.CheckoutAsync(product, 1, "CardSimulation");
        using var worker = harness.BuildWorker();
        await ReserveAndRequestPaymentAsync(harness, worker, orderId);
        Assert.Equal(200, (await CancelAsync(harness, orderId)).StatusCode);

        await MessagingHarness.DeliverAsync(worker, "payment", await harness.OutboxEventAsync(orderId, EventTypes.PaymentRequested));

        await using var db = harness.NewDb();
        Assert.False(await db.Payments.AnyAsync(x => x.OrderId == orderId, MessagingHarness.Ct));
        Assert.Equal((5, 0), await harness.StockAsync(product));
    }

    [Fact]
    public async Task FR_PAY_004_cancellation_after_charge_refunds_once_across_repeats()
    {
        await using var harness = await MessagingHarness.CreateAsync();
        var product = await harness!.SeedProductAsync(10m, 5);
        var orderId = await harness.CheckoutAsync(product, 1, "CardSimulation");
        using var worker = harness.BuildWorker();
        await ReserveAndRequestPaymentAsync(harness, worker, orderId);
        await MessagingHarness.DeliverAsync(worker, "payment", await harness.OutboxEventAsync(orderId, EventTypes.PaymentRequested));
        Assert.Equal(200, (await CancelAsync(harness, orderId)).StatusCode);
        var cancelled = await harness.OutboxEventAsync(orderId, EventTypes.OrderCancelled);

        Assert.True(await MessagingHarness.DeliverAsync(worker, "payment", cancelled));
        Assert.False(await MessagingHarness.DeliverAsync(worker, "payment", cancelled));
        // A separate command with its own identity, as Phase 10 staff cancellation will send, still finds the one refund.
        await using (var scope = worker.Services.CreateAsyncScope())
            Assert.NotNull(await scope.ServiceProvider.GetRequiredService<PaymentService>().RefundForOrderAsync(orderId, "staff-command-1", "Staff", "refund-test", null, MessagingHarness.Ct));

        // The late success event must not release, commit, or advance anything.
        var succeeded = await harness.OutboxEventAsync(orderId, EventTypes.PaymentSucceeded);
        await MessagingHarness.DeliverAsync(worker, "ordering", succeeded);
        await MessagingHarness.DeliverAsync(worker, "inventory", succeeded);

        await using var db = harness.NewDb();
        Assert.Equal(OrderStatus.Cancelled, (await db.Orders.AsNoTracking().SingleAsync(x => x.Id == orderId, MessagingHarness.Ct)).Status);
        var refund = await db.Refunds.AsNoTracking().SingleAsync(x => x.OrderId == orderId, MessagingHarness.Ct);
        Assert.Equal(RefundStatus.Succeeded, refund.Status);
        Assert.Equal(PaymentStatus.Refunded, (await db.Payments.AsNoTracking().SingleAsync(x => x.OrderId == orderId, MessagingHarness.Ct)).Status);
        Assert.Equal(1, await db.OutboxMessages.CountAsync(x => x.AggregateId == orderId && x.EventType == EventTypes.RefundCompleted, MessagingHarness.Ct));
        Assert.Equal((5, 0), await harness.StockAsync(product));
    }

    [Fact]
    public async Task Cancelling_an_accepted_cod_order_voids_collection_without_a_refund()
    {
        await using var harness = await MessagingHarness.CreateAsync();
        var product = await harness!.SeedProductAsync(10m, 5);
        var orderId = await harness.CheckoutAsync(product, 1, "COD");
        using var worker = harness.BuildWorker();
        await ReserveAndRequestPaymentAsync(harness, worker, orderId);
        await MessagingHarness.DeliverAsync(worker, "payment", await harness.OutboxEventAsync(orderId, EventTypes.PaymentRequested));
        await using (var pending = harness.NewDb())
            Assert.Equal(SettlementStatus.PendingCollection, (await pending.Payments.AsNoTracking().SingleAsync(x => x.OrderId == orderId, MessagingHarness.Ct)).SettlementStatus);
        Assert.Equal(200, (await CancelAsync(harness, orderId)).StatusCode);

        await MessagingHarness.DeliverAsync(worker, "payment", await harness.OutboxEventAsync(orderId, EventTypes.OrderCancelled));

        await using var db = harness.NewDb();
        Assert.Equal(SettlementStatus.Voided, (await db.Payments.AsNoTracking().SingleAsync(x => x.OrderId == orderId, MessagingHarness.Ct)).SettlementStatus);
        Assert.False(await db.Refunds.AnyAsync(x => x.OrderId == orderId, MessagingHarness.Ct));
        Assert.False(await db.OutboxMessages.AnyAsync(x => x.EventType == EventTypes.RefundCompleted, MessagingHarness.Ct));
    }

    [Fact]
    public async Task Expired_reservation_cancels_the_waiting_order_and_refunds_a_charge_already_taken()
    {
        await using var harness = await MessagingHarness.CreateAsync();
        var product = await harness!.SeedProductAsync(10m, 5);
        var orderId = await harness.CheckoutAsync(product, 1, "CardSimulation");
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        using var worker = harness.BuildWorker(x => x.Replace(ServiceDescriptor.Singleton<TimeProvider>(clock)));
        await ReserveAndRequestPaymentAsync(harness, worker, orderId);
        await MessagingHarness.DeliverAsync(worker, "payment", await harness.OutboxEventAsync(orderId, EventTypes.PaymentRequested));

        clock.Advance(TimeSpan.FromMinutes(16));
        await using (var scope = worker.Services.CreateAsyncScope())
            Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<OrderFlow.Application.Inventory.IReservationExpiryJob>().ExpireDueAsync(10, MessagingHarness.Ct));
        Assert.Equal((5, 0), await harness.StockAsync(product));

        await MessagingHarness.DeliverAsync(worker, "ordering", await harness.OutboxEventAsync(orderId, EventTypes.InventoryReleased));
        await MessagingHarness.DeliverAsync(worker, "payment", await harness.OutboxEventAsync(orderId, EventTypes.OrderCancelled));

        await using var db = harness.NewDb();
        var order = await db.Orders.AsNoTracking().Include(x => x.History).SingleAsync(x => x.Id == orderId, MessagingHarness.Ct);
        Assert.Equal(OrderStatus.Cancelled, order.Status);
        Assert.Equal(ReleaseReasons.ReservationExpired, order.History.Single(x => x.ToStatus == OrderStatus.Cancelled).Reason);
        Assert.Equal(PaymentStatus.Refunded, (await db.Payments.AsNoTracking().SingleAsync(x => x.OrderId == orderId, MessagingHarness.Ct)).Status);
    }

    [Fact]
    public async Task Payment_success_after_expiry_time_does_not_commit_stock_and_compensates_the_paid_order()
    {
        await using var harness = await MessagingHarness.CreateAsync();
        var product = await harness!.SeedProductAsync(10m, 5);
        var orderId = await harness.CheckoutAsync(product, 1, "CardSimulation");
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        using var worker = harness.BuildWorker(x => x.Replace(ServiceDescriptor.Singleton<TimeProvider>(clock)));
        await ReserveAndRequestPaymentAsync(harness, worker, orderId);
        await MessagingHarness.DeliverAsync(worker, "payment", await harness.OutboxEventAsync(orderId, EventTypes.PaymentRequested));
        var succeeded = await harness.OutboxEventAsync(orderId, EventTypes.PaymentSucceeded);
        await MessagingHarness.DeliverAsync(worker, "ordering", succeeded);

        // The reservation lapsed but the sweep has not run yet, so the commit path must discover it.
        clock.Advance(TimeSpan.FromMinutes(16));
        await MessagingHarness.DeliverAsync(worker, "inventory", succeeded);
        Assert.Equal([ReservationStatus.Expired], await harness.ReservationsAsync(orderId));
        Assert.Equal((5, 0), await harness.StockAsync(product));
        await MessagingHarness.DeliverAsync(worker, "ordering", await harness.OutboxEventAsync(orderId, EventTypes.InventoryReleased));
        await MessagingHarness.DeliverAsync(worker, "payment", await harness.OutboxEventAsync(orderId, EventTypes.OrderCancelled));

        await using var db = harness.NewDb();
        Assert.Equal(OrderStatus.Cancelled, (await db.Orders.AsNoTracking().SingleAsync(x => x.Id == orderId, MessagingHarness.Ct)).Status);
        Assert.Equal(PaymentStatus.Refunded, (await db.Payments.AsNoTracking().SingleAsync(x => x.OrderId == orderId, MessagingHarness.Ct)).Status);
    }

    [Fact]
    public async Task Commit_event_arriving_before_payment_event_still_reaches_processing()
    {
        await using var harness = await MessagingHarness.CreateAsync();
        var product = await harness!.SeedProductAsync(10m, 5);
        var orderId = await harness.CheckoutAsync(product, 1, "COD");
        using var worker = harness.BuildWorker();
        await ReserveAndRequestPaymentAsync(harness, worker, orderId);
        await MessagingHarness.DeliverAsync(worker, "payment", await harness.OutboxEventAsync(orderId, EventTypes.PaymentRequested));
        var succeeded = await harness.OutboxEventAsync(orderId, EventTypes.PaymentSucceeded);
        await MessagingHarness.DeliverAsync(worker, "inventory", succeeded);

        await MessagingHarness.DeliverAsync(worker, "ordering", await harness.OutboxEventAsync(orderId, EventTypes.InventoryCommitted));
        await MessagingHarness.DeliverAsync(worker, "ordering", succeeded);

        await using var db = harness.NewDb();
        var order = await db.Orders.AsNoTracking().Include(x => x.History).SingleAsync(x => x.Id == orderId, MessagingHarness.Ct);
        Assert.Equal(OrderStatus.Processing, order.Status);
        Assert.Contains(order.History, x => x.ToStatus == OrderStatus.Paid);
    }

    [Fact]
    public async Task D_22_customer_cancellation_returns_the_coupon_once_and_it_can_be_used_again()
    {
        await using var harness = await MessagingHarness.CreateAsync();
        var product = await harness!.SeedProductAsync(10m, 5);
        var coupon = await harness.SeedCouponAsync("BACK1");
        var orderId = await harness.CheckoutAsync(product, 1, "COD", couponCode: "back1");
        Assert.Equal((1, 1, 0), await harness.CouponStateAsync(coupon.Id));

        Assert.Equal(200, (await CancelAsync(harness, orderId)).StatusCode);
        Assert.Equal(200, (await CancelAsync(harness, orderId)).StatusCode);

        Assert.Equal((0, 0, 1), await harness.CouponStateAsync(coupon.Id));
        // Both the total and the per-user limit of 1 must allow the code again.
        var second = await harness.CheckoutAsync(product, 1, "COD", couponCode: "BACK1");
        Assert.NotEqual(orderId, second);
        Assert.Equal((1, 1, 1), await harness.CouponStateAsync(coupon.Id));
    }

    [Fact]
    public async Task D_22_rejection_and_payment_failure_return_the_coupon_and_a_repeat_event_does_not()
    {
        await using var harness = await MessagingHarness.CreateAsync();
        var scarce = await harness!.SeedProductAsync(10m, 1);
        var declined = await harness.SeedProductAsync(100.13m, 5);
        var coupon = await harness.SeedCouponAsync("BACK2", totalLimit: 5, perUserLimit: 5);
        using var worker = harness.BuildWorker();

        var rejected = await harness.CheckoutAsync(scarce, 3, "CardSimulation", couponCode: "BACK2");
        await MessagingHarness.DeliverAsync(worker, "inventory", await harness.OutboxEventAsync(rejected, EventTypes.OrderCreated));
        var rejectedEvent = await harness.OutboxEventAsync(rejected, EventTypes.InventoryRejected);
        await MessagingHarness.DeliverAsync(worker, "ordering", rejectedEvent);
        Assert.False(await MessagingHarness.DeliverAsync(worker, "ordering", rejectedEvent));
        Assert.Equal((0, 0, 1), await harness.CouponStateAsync(coupon.Id));

        // The 5 discount keeps the .13 ending that the simulation declines.
        var failed = await harness.CheckoutAsync(declined, 1, "CardSimulation", couponCode: "BACK2");
        await ReserveAndRequestPaymentAsync(harness, worker, failed);
        await MessagingHarness.DeliverAsync(worker, "payment", await harness.OutboxEventAsync(failed, EventTypes.PaymentRequested));
        await MessagingHarness.DeliverAsync(worker, "ordering", await harness.OutboxEventAsync(failed, EventTypes.PaymentFailed));
        Assert.Equal((0, 0, 2), await harness.CouponStateAsync(coupon.Id));
    }

    [Fact]
    public async Task D_22_expiry_returns_the_coupon_but_a_successful_order_keeps_it()
    {
        await using var harness = await MessagingHarness.CreateAsync();
        var product = await harness!.SeedProductAsync(10m, 5);
        var coupon = await harness.SeedCouponAsync("BACK3", totalLimit: 5, perUserLimit: 5);
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        using var worker = harness.BuildWorker(x => x.Replace(ServiceDescriptor.Singleton<TimeProvider>(clock)));

        var kept = await harness.CheckoutAsync(product, 1, "COD", couponCode: "BACK3");
        await ReserveAndRequestPaymentAsync(harness, worker, kept);
        await MessagingHarness.DeliverAsync(worker, "payment", await harness.OutboxEventAsync(kept, EventTypes.PaymentRequested));
        var succeeded = await harness.OutboxEventAsync(kept, EventTypes.PaymentSucceeded);
        await MessagingHarness.DeliverAsync(worker, "ordering", succeeded);
        await MessagingHarness.DeliverAsync(worker, "inventory", succeeded);
        await MessagingHarness.DeliverAsync(worker, "ordering", await harness.OutboxEventAsync(kept, EventTypes.InventoryCommitted));
        Assert.Equal((1, 1, 0), await harness.CouponStateAsync(coupon.Id));

        var expired = await harness.CheckoutAsync(product, 1, "COD", couponCode: "BACK3");
        await ReserveAndRequestPaymentAsync(harness, worker, expired);
        clock.Advance(TimeSpan.FromMinutes(16));
        await using (var scope = worker.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<OrderFlow.Application.Inventory.IReservationExpiryJob>().ExpireDueAsync(10, MessagingHarness.Ct);
        await MessagingHarness.DeliverAsync(worker, "ordering", await harness.OutboxEventAsync(expired, EventTypes.InventoryReleased));

        Assert.Equal((1, 1, 1), await harness.CouponStateAsync(coupon.Id));
    }

    [Fact]
    public async Task A_failed_handler_rolls_back_its_inbox_row_so_a_retry_can_apply()
    {
        await using var harness = await MessagingHarness.CreateAsync();
        var product = await harness!.SeedProductAsync(10m, 5);
        var orderId = await harness.CheckoutAsync(product, 1, "COD");
        using var worker = harness.BuildWorker();
        var created = await harness.OutboxEventAsync(orderId, EventTypes.OrderCreated);
        await using (var scope = worker.Services.CreateAsyncScope())
        {
            var processor = scope.ServiceProvider.GetRequiredService<InboxProcessor>();
            await Assert.ThrowsAsync<InvalidTimeZoneException>(() => processor.ProcessAsync("inventory", new ThrowingHandler(), created, MessagingHarness.Ct));
        }
        await using (var db = harness.NewDb()) Assert.False(await db.InboxMessages.AnyAsync(MessagingHarness.Ct));

        Assert.True(await MessagingHarness.DeliverAsync(worker, "inventory", created));
    }

    private sealed class ThrowingHandler : IEventHandler
    {
        public Task HandleAsync(EventEnvelope envelope, CancellationToken cancellationToken) => throw new InvalidTimeZoneException();
    }
}
