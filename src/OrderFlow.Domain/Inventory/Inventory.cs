namespace OrderFlow.Domain.Inventory;

public sealed class Inventory
{
    private Inventory() { }

    public Guid Id { get; private set; }
    public Guid ProductId { get; private set; }
    public int OnHand { get; private set; }
    public int Reserved { get; private set; }
    public int Available => OnHand - Reserved;
    public int LowStockThreshold { get; private set; }
    public byte[] RowVersion { get; private set; } = [];
    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public static Inventory Create(Guid id, Guid productId, int lowStockThreshold, DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(id, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfEqual(productId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfNegative(lowStockThreshold);
        RequireUtc(now);
        return new Inventory { Id = id, ProductId = productId, LowStockThreshold = lowStockThreshold, UpdatedAtUtc = now };
    }

    public InventoryAdjustment Adjust(int quantityDelta, string reasonCode, string? note, Guid actorUserId, string correlationId, DateTimeOffset now)
    {
        if (quantityDelta == 0) throw new ArgumentOutOfRangeException(nameof(quantityDelta));
        var nextOnHand = (long)OnHand + quantityDelta;
        if (nextOnHand < Reserved || nextOnHand > int.MaxValue) throw new InvalidOperationException("Adjustment would make inventory invalid.");
        var adjustment = InventoryAdjustment.Create(Guid.NewGuid(), Id, quantityDelta, reasonCode, note, actorUserId, correlationId, now);
        OnHand = (int)nextOnHand;
        UpdatedAtUtc = now;
        return adjustment;
    }

    public void SetLowStockThreshold(int threshold, DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(threshold);
        RequireUtc(now);
        LowStockThreshold = threshold;
        UpdatedAtUtc = now;
    }

    private static void RequireUtc(DateTimeOffset value)
    {
        if (value.Offset != TimeSpan.Zero) throw new ArgumentException("Timestamp must be UTC.", nameof(value));
    }
}
