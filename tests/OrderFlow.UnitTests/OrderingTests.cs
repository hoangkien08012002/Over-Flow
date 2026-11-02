using OrderFlow.Domain.Ordering;
using Xunit;

namespace OrderFlow.UnitTests;

public sealed class OrderingTests
{
    [Fact]
    public void FR_ORD_002_004_snapshot_totals_and_transitions()
    {
        var now = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid();
        var item = new OrderItem(id, Guid.NewGuid(), "SKU-1", "Original name", 10.25m, 2, 2m);
        var order = Order.Create(id, Guid.NewGuid(), "OF-TEST", "COD", Guid.NewGuid(), "Standard", "{}", "VND",
            null, 3m, [item], now, "test-correlation");
        Assert.Equal(20.50m, order.Subtotal);
        Assert.Equal(21.50m, order.GrandTotal);
        Assert.Equal("Original name", order.Items[0].ProductNameSnapshot);
        Assert.Throws<InvalidOperationException>(() => order.Transition(OrderStatus.Processing, now, "test-correlation"));
        Assert.Equal(OrderStatus.PendingInventory, order.Status);
        Assert.Single(order.History);
        order.Transition(OrderStatus.AwaitingPayment, now, "test-correlation", eventId: Guid.NewGuid());
        order.Cancel(order.UserId, "Changed my mind", now, "test-correlation");
        order.Cancel(order.UserId, "Changed my mind", now, "test-correlation");
        Assert.Equal(OrderStatus.Cancelled, order.Status);
        Assert.Equal(3, order.History.Count);
        Assert.Throws<InvalidOperationException>(() => order.Transition(OrderStatus.Paid, now, "test-correlation"));
    }
}
