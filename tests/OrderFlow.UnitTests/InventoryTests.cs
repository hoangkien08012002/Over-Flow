using OrderFlow.Domain.Inventory;
using Xunit;

namespace OrderFlow.UnitTests;

public sealed class InventoryTests
{
    [Fact]
    public void Adjustment_is_audited_and_cannot_make_on_hand_negative()
    {
        var now = DateTimeOffset.UtcNow;
        var inventory = Inventory.Create(Guid.NewGuid(), Guid.NewGuid(), 2, now);
        var actor = Guid.NewGuid();

        var adjustment = inventory.Adjust(3, "RESTOCK", "Counted", actor, "request-1", now);

        Assert.Equal(3, inventory.OnHand);
        Assert.Equal(3, inventory.Available);
        Assert.Equal(inventory.Id, adjustment.InventoryId);
        Assert.Equal(actor, adjustment.ActorUserId);
        Assert.Throws<InvalidOperationException>(() => inventory.Adjust(-4, "CORRECTION", null, actor, "request-2", now));
        Assert.Equal(3, inventory.OnHand);
    }
}
