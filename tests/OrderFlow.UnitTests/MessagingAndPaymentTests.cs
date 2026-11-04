using OrderFlow.Application.Messaging;
using OrderFlow.Application.Payments;
using OrderFlow.Domain.Ordering;
using OrderFlow.Domain.Payments;
using Xunit;

namespace OrderFlow.UnitTests;

public sealed class MessagingAndPaymentTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 4, 0, 0, 0, TimeSpan.Zero);

    private static Payment NewPayment()
    {
        var payment = Payment.Create(Guid.NewGuid(), Guid.NewGuid(), "CardSimulation", 10m, "VND", Now);
        payment.Begin(Now);
        return payment;
    }

    [Fact]
    public void Envelope_round_trips_the_documented_json_shape()
    {
        var orderId = Guid.NewGuid();
        var causation = Guid.NewGuid();
        var envelope = EventSerializer.Create(EventTypes.PaymentRequested, orderId, "corr-1", causation,
            new PaymentRequestedPayload(orderId, 12.5m, "VND", "COD", [Guid.NewGuid()]), Now);
        var json = EventSerializer.Serialize(envelope);

        foreach (var field in new[] { "\"eventId\"", "\"eventType\"", "\"eventVersion\":1", "\"occurredAtUtc\"", "\"correlationId\"", "\"causationId\"", "\"aggregateId\"", "\"producer\"", "\"payload\"", "\"orderId\"" })
            Assert.Contains(field, json);
        var parsed = EventSerializer.Parse(json);
        Assert.Equal((envelope.EventId, causation, orderId), (parsed.EventId, parsed.CausationId!.Value, parsed.AggregateId));
        Assert.Equal(12.5m, EventSerializer.ReadPayload<PaymentRequestedPayload>(parsed).Amount);
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("{}")]
    [InlineData("""{"eventId":"7f4c9d1e-3f7f-4f6a-9d3e-9a6a1c2b3d4e","eventType":"NotAnEvent","eventVersion":1,"correlationId":"c","aggregateId":"7f4c9d1e-3f7f-4f6a-9d3e-9a6a1c2b3d4e","producer":"p","payload":{}}""")]
    [InlineData("""{"eventId":"7f4c9d1e-3f7f-4f6a-9d3e-9a6a1c2b3d4e","eventType":"OrderCreated","eventVersion":2,"correlationId":"c","aggregateId":"7f4c9d1e-3f7f-4f6a-9d3e-9a6a1c2b3d4e","producer":"p","payload":{}}""")]
    public void Envelope_that_breaks_the_contract_is_rejected_as_permanent(string json) =>
        Assert.Throws<EventContractException>(() => EventSerializer.Parse(json));

    [Fact]
    public void Added_fields_within_a_version_are_tolerated_but_a_missing_order_id_is_not()
    {
        var withExtra = EventSerializer.Create(EventTypes.OrderCancelled, Guid.NewGuid(), "c", null, new { orderId = Guid.NewGuid(), userId = Guid.NewGuid(), reasonCode = "X", addedLater = 1 }, Now);
        Assert.Equal("X", EventSerializer.ReadPayload<OrderCancelledPayload>(withExtra).ReasonCode);
        var missing = EventSerializer.Create(EventTypes.OrderCancelled, Guid.NewGuid(), "c", null, new { reasonCode = "X" }, Now);
        Assert.Throws<EventContractException>(() => EventSerializer.ReadPayload<OrderCancelledPayload>(missing));
    }

    [Fact]
    public void Every_cataloged_event_has_a_topic_and_dead_letter_name()
    {
        Assert.Equal("order.created", EventTopics.For(EventTypes.OrderCreated));
        Assert.Equal("inventory.committed", EventTopics.For(EventTypes.InventoryCommitted));
        Assert.Equal("payment.failed.dlq", EventTopics.DeadLetter("payment.failed"));
        Assert.Null(EventTopics.For("Nope"));
        Assert.Equal(11, EventTopics.All.Count);
    }

    [Theory]
    [InlineData("COD", 100.13, true, "COD_ACCEPTED", true)]
    [InlineData("CardSimulation", 100.00, true, "APPROVED", false)]
    [InlineData("CardSimulation", 100.13, false, "CARD_DECLINED", false)]
    [InlineData("BankTransferSimulation", 100.50, true, "APPROVED", false)]
    [InlineData("BankTransferSimulation", 0.13, false, "BANK_TRANSFER_REJECTED", false)]
    public async Task FR_PAY_001_factory_selects_the_strategy_for_each_method(string method, double amount, bool accepted, string code, bool defers)
    {
        var options = new PaymentSimulationOptions();
        var factory = new PaymentStrategyFactory([new CodPaymentStrategy(), new CardSimulationStrategy(options), new BankTransferSimulationStrategy(options)]);
        var strategy = factory.Find(method)!;
        var paymentId = Guid.NewGuid();
        var attempt = new PaymentAttempt(paymentId, Guid.NewGuid(), (decimal)amount, "VND");
        var result = await strategy.ChargeAsync(attempt, TestContext.Current.CancellationToken);
        Assert.Equal((accepted, code, defers), (result.Accepted, result.ResultCode, strategy.DefersSettlement));
        // A rerun after a rollback must present the same provider identity.
        Assert.Equal(result.ProviderReference, (await strategy.ChargeAsync(attempt, TestContext.Current.CancellationToken)).ProviderReference);
        Assert.Null(factory.Find("Cash"));
    }

    [Fact]
    public void Payment_follows_the_state_machine_and_refuses_illegal_moves()
    {
        var payment = Payment.Create(Guid.NewGuid(), Guid.NewGuid(), "CardSimulation", 10m, "VND", Now);
        Assert.Throws<InvalidOperationException>(() => payment.Succeed("r", "APPROVED", false, Now));
        Assert.Throws<InvalidOperationException>(() => payment.BeginRefund(Now));
        payment.Begin(Now);
        Assert.Equal((PaymentStatus.Processing, 1), (payment.Status, payment.AttemptCount));
        payment.Succeed("r", "APPROVED", false, Now);
        Assert.Equal((PaymentStatus.Succeeded, SettlementStatus.Settled), (payment.Status, payment.SettlementStatus));
        Assert.Throws<InvalidOperationException>(() => payment.Fail("r", "X", Now));
        Assert.Throws<InvalidOperationException>(() => payment.VoidCollection(Now));
        payment.BeginRefund(Now);
        Assert.Throws<InvalidOperationException>(() => payment.BeginRefund(Now));
        payment.CompleteRefund(Now);
        Assert.Equal(PaymentStatus.Refunded, payment.Status);
        Assert.Throws<InvalidOperationException>(() => payment.FailRefund(Now));

        var failing = NewPayment();
        failing.Succeed("r", "APPROVED", false, Now);
        failing.BeginRefund(Now);
        failing.FailRefund(Now);
        Assert.Equal(PaymentStatus.RefundFailed, failing.Status);
    }

    [Fact]
    public void Cod_acceptance_defers_settlement_and_can_be_voided_before_collection()
    {
        var payment = NewPayment();
        payment.Succeed("r", "COD_ACCEPTED", true, Now);
        Assert.Equal((PaymentStatus.Succeeded, SettlementStatus.PendingCollection), (payment.Status, payment.SettlementStatus));
        payment.VoidCollection(Now);
        Assert.Equal(SettlementStatus.Voided, payment.SettlementStatus);
    }

    [Fact]
    public void Refund_settles_only_once()
    {
        var payment = NewPayment();
        payment.Succeed("r", "APPROVED", false, Now);
        var refund = Refund.Create(Guid.NewGuid(), payment, "key", "Reason", Now);
        Assert.Equal((RefundStatus.Pending, 10m), (refund.Status, refund.Amount));
        refund.Complete("ref", Now);
        Assert.Throws<InvalidOperationException>(() => refund.Fail("ref", Now));
    }

    [Fact]
    public void D_22_a_coupon_use_can_be_returned_only_once_and_never_below_zero()
    {
        var coupon = Coupon.Create(Guid.NewGuid(), "SAVE", CouponDiscountType.Fixed, 5m, 0m, Now.AddDays(-1), Now.AddDays(1), 1, 1, Now);
        coupon.Use();
        Assert.Throws<InvalidOperationException>(() => coupon.Use());
        coupon.ReturnUse(Now);
        Assert.Equal(0, coupon.UsageCount);
        Assert.Throws<InvalidOperationException>(() => coupon.ReturnUse(Now));
        coupon.Use();

        var usage = new CouponUsage(coupon.Id, Guid.NewGuid(), Guid.NewGuid(), 5m, Now);
        usage.Release(Now);
        Assert.Equal(Now, usage.ReleasedAtUtc);
        Assert.Throws<InvalidOperationException>(() => usage.Release(Now));
    }

    [Fact]
    public void Paid_order_allows_system_compensation_but_never_customer_cancellation()
    {
        var id = Guid.NewGuid();
        var order = Order.Create(id, Guid.NewGuid(), "OF-TEST", "COD", Guid.NewGuid(), "Standard", "{}", "VND", null, 3m,
            [new OrderItem(id, Guid.NewGuid(), "SKU", "Name", 10m, 1)], Now, "corr");
        order.Transition(OrderStatus.AwaitingPayment, Now, "corr");
        order.Transition(OrderStatus.Paid, Now, "corr");
        Assert.Throws<InvalidOperationException>(() => order.Cancel(order.UserId, "No", Now, "corr"));
        order.Transition(OrderStatus.Cancelled, Now, "corr", reason: "RESERVATION_EXPIRED");
        Assert.Equal(OrderStatus.Cancelled, order.Status);
    }
}
