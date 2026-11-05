using OrderFlow.Domain.Engagement;
using OrderFlow.Domain.Ordering;
using OrderFlow.Domain.Payments;
using OrderFlow.Domain.Returns;
using OrderFlow.Domain.Shipping;
using Xunit;

namespace OrderFlow.UnitTests;

public sealed class ExtendedDomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 4, 0, 0, 0, TimeSpan.Zero);
    private static readonly Guid Actor = Guid.NewGuid();

    private static Order NewOrder()
    {
        var id = Guid.NewGuid();
        return Order.Create(id, Guid.NewGuid(), "OF-1", "COD", Guid.NewGuid(), "Standard", "{}", "VND", null, 0m, [new OrderItem(id, Guid.NewGuid(), "SKU", "Name", 10m, 1)], Now, "c");
    }

    private static Order Advance(params OrderStatus[] path)
    {
        var order = NewOrder();
        foreach (var status in path) order.Transition(status, Now, "c");
        return order;
    }

    [Fact]
    public void Staff_cancellation_is_allowed_only_from_paid_or_processing_and_needs_a_reason()
    {
        foreach (var path in new[] { new[] { OrderStatus.AwaitingPayment, OrderStatus.Paid }, [OrderStatus.AwaitingPayment, OrderStatus.Paid, OrderStatus.Processing] })
        {
            var order = Advance(path);
            Assert.Throws<InvalidOperationException>(() => order.CancelByStaff(Actor, " ", Now, "c"));
            order.CancelByStaff(Actor, "Damaged", Now, "c");
            Assert.Equal(OrderStatus.Cancelled, order.Status);
            order.CancelByStaff(Actor, "Repeat", Now, "c");
            Assert.Equal(path.Length + 2, order.History.Count);
        }
        Assert.Throws<InvalidOperationException>(() => NewOrder().CancelByStaff(Actor, "Too early", Now, "c"));
        Assert.Throws<InvalidOperationException>(() => Advance(OrderStatus.AwaitingPayment, OrderStatus.Paid, OrderStatus.Processing, OrderStatus.Shipped).CancelByStaff(Actor, "Too late", Now, "c"));
    }

    [Fact]
    public void Only_a_delivered_order_can_become_refunded()
    {
        var order = Advance(OrderStatus.AwaitingPayment, OrderStatus.Paid, OrderStatus.Processing, OrderStatus.Shipped);
        Assert.Throws<InvalidOperationException>(() => order.Transition(OrderStatus.Refunded, Now, "c"));
        order.Transition(OrderStatus.Delivered, Now, "c");
        order.Transition(OrderStatus.Refunded, Now, "c");
        Assert.Equal(OrderStatus.Refunded, order.Status);
    }

    [Theory]
    [InlineData(ShipmentStatus.Pending, ShipmentStatus.Shipped)]
    [InlineData(ShipmentStatus.Pending, ShipmentStatus.Delivered)]
    [InlineData(ShipmentStatus.ReadyToShip, ShipmentStatus.Delivered)]
    public void Shipment_rejects_skipped_steps(ShipmentStatus from, ShipmentStatus to)
    {
        var shipment = Shipment.Create(Guid.NewGuid(), Guid.NewGuid(), Actor, Now, "c");
        if (from == ShipmentStatus.ReadyToShip) shipment.TransitionTo(from, Actor, null, Now, "c");
        Assert.Throws<InvalidOperationException>(() => shipment.TransitionTo(to, Actor, null, Now, "c"));
    }

    [Fact]
    public void Shipment_follows_its_state_machine_and_records_history_once()
    {
        var shipment = Shipment.Create(Guid.NewGuid(), Guid.NewGuid(), Actor, Now, "c");
        Assert.True(shipment.TransitionTo(ShipmentStatus.ReadyToShip, Actor, null, Now, "c"));
        Assert.False(shipment.TransitionTo(ShipmentStatus.ReadyToShip, Actor, null, Now, "c"));
        Assert.True(shipment.TransitionTo(ShipmentStatus.Shipped, Actor, null, Now, "c"));
        Assert.Throws<InvalidOperationException>(() => shipment.TransitionTo(ShipmentStatus.Cancelled, Actor, null, Now, "c"));
        Assert.True(shipment.TransitionTo(ShipmentStatus.DeliveryFailed, Actor, "Nobody home", Now, "c"));
        Assert.Equal(4, shipment.History.Count);
    }

    [Fact]
    public void Return_follows_its_state_machine()
    {
        var request = ReturnRequest.Create(Guid.NewGuid(), Guid.NewGuid(), Actor, "Wrong size", [(Guid.NewGuid(), Guid.NewGuid(), 1)], Now, "c");
        Assert.Throws<InvalidOperationException>(() => request.TransitionTo(ReturnStatus.Received, Actor, null, null, Now, "c"));
        request.TransitionTo(ReturnStatus.Approved, Actor, null, null, Now, "c");
        Assert.Throws<InvalidOperationException>(() => request.TransitionTo(ReturnStatus.Rejected, Actor, null, "No", Now, "c"));
        request.TransitionTo(ReturnStatus.Received, Actor, null, null, Now, "c");
        Assert.Throws<InvalidOperationException>(() => request.TransitionTo(ReturnStatus.Closed, Actor, null, null, Now, "c"));
        request.TransitionTo(ReturnStatus.Refunded, null, Guid.NewGuid(), null, Now, "c");
        request.TransitionTo(ReturnStatus.Closed, Actor, null, null, Now, "c");
        Assert.False(request.TransitionTo(ReturnStatus.Closed, Actor, null, null, Now, "c"));
        Assert.Throws<ArgumentException>(() => ReturnRequest.Create(Guid.NewGuid(), Guid.NewGuid(), Actor, " ", [(Guid.NewGuid(), Guid.NewGuid(), 1)], Now, "c"));
        Assert.Throws<ArgumentException>(() => ReturnRequest.Create(Guid.NewGuid(), Guid.NewGuid(), Actor, "Empty", [], Now, "c"));
    }

    [Fact]
    public void Refund_reopens_only_from_failed_and_keeps_its_identity()
    {
        var payment = Payment.Create(Guid.NewGuid(), Guid.NewGuid(), "CardSimulation", 10m, "VND", Now);
        payment.Begin(Now);
        payment.Succeed("r", "APPROVED", false, Now);
        payment.BeginRefund(Now);
        var refund = Refund.Create(Guid.NewGuid(), payment, "key", "Reason", Now);
        Assert.Throws<InvalidOperationException>(() => refund.Reopen(Now));
        Assert.Throws<InvalidOperationException>(() => payment.RetryRefund(Now));
        refund.RecordAttempt(Now);
        refund.Fail("r", Now);
        payment.FailRefund(Now);
        refund.Reopen(Now);
        payment.RetryRefund(Now);
        Assert.Equal(RefundStatus.Pending, refund.Status);
        Assert.Equal(PaymentStatus.RefundPending, payment.Status);
        Assert.Equal(0, refund.AttemptCount);
        Assert.Equal("key", refund.IdempotencyKey);
    }

    [Fact]
    public void Collection_applies_only_to_a_pending_cod_payment()
    {
        var cod = Payment.Create(Guid.NewGuid(), Guid.NewGuid(), "COD", 10m, "VND", Now);
        cod.Begin(Now);
        cod.Succeed("r", "COD_ACCEPTED", true, Now);
        cod.MarkCollected(Now);
        cod.MarkCollected(Now);
        Assert.Equal(SettlementStatus.Collected, cod.SettlementStatus);
        var card = Payment.Create(Guid.NewGuid(), Guid.NewGuid(), "CardSimulation", 10m, "VND", Now);
        card.Begin(Now);
        card.Succeed("r", "APPROVED", false, Now);
        card.MarkCollected(Now);
        Assert.Equal(SettlementStatus.Settled, card.SettlementStatus);
    }

    [Fact]
    public void Coupon_update_validates_and_keeps_its_code()
    {
        var coupon = Coupon.Create(Guid.NewGuid(), "save", CouponDiscountType.Fixed, 5m, 0m, Now, Now.AddDays(1), 5, 1, Now);
        coupon.Use();
        coupon.Use();
        Assert.Throws<ArgumentException>(() => coupon.Update(CouponDiscountType.Fixed, 5m, 0m, Now, Now.AddDays(1), 1, 1, true, Now));
        Assert.Throws<ArgumentException>(() => coupon.Update(CouponDiscountType.Percentage, 101m, 0m, Now, Now.AddDays(1), 5, 1, true, Now));
        coupon.Update(CouponDiscountType.Percentage, 50m, 0m, Now, Now.AddDays(1), 2, 1, false, Now);
        Assert.Equal("SAVE", coupon.Code);
        Assert.False(coupon.IsEnabled);
    }

    [Fact]
    public void Review_validates_rating_and_moderation_keeps_the_text()
    {
        Assert.Throws<ArgumentException>(() => Review.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Actor, 0, null, Now));
        var review = Review.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Actor, 4, " Good ", Now);
        Assert.Equal("Good", review.Comment);
        Assert.Throws<ArgumentException>(() => review.Moderate(ReviewStatus.Hidden, " ", Actor, Now));
        review.Moderate(ReviewStatus.Hidden, "Spam", Actor, Now);
        Assert.Equal(ReviewStatus.Hidden, review.Status);
        Assert.Equal("Good", review.Comment);
    }
}
