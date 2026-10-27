namespace OrderFlow.Domain.Inventory;

public sealed class InventoryAdjustment
{
    private InventoryAdjustment() { }

    public Guid Id { get; private set; }
    public Guid InventoryId { get; private set; }
    public int QuantityDelta { get; private set; }
    public string ReasonCode { get; private set; } = null!;
    public string? Note { get; private set; }
    public Guid ActorUserId { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public string CorrelationId { get; private set; } = null!;

    internal static InventoryAdjustment Create(Guid id, Guid inventoryId, int quantityDelta, string reasonCode, string? note, Guid actorUserId, string correlationId, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(reasonCode) || reasonCode.Trim().Length > 50) throw new ArgumentException("Reason code must be 1 to 50 characters.", nameof(reasonCode));
        if (note?.Trim().Length > 500) throw new ArgumentException("Adjustment note is too long.", nameof(note));
        if (string.IsNullOrWhiteSpace(correlationId) || correlationId.Trim().Length > 100) throw new ArgumentException("Correlation ID must be 1 to 100 characters.", nameof(correlationId));
        ArgumentOutOfRangeException.ThrowIfEqual(actorUserId, Guid.Empty);
        if (now.Offset != TimeSpan.Zero) throw new ArgumentException("Timestamp must be UTC.", nameof(now));
        return new InventoryAdjustment { Id = id, InventoryId = inventoryId, QuantityDelta = quantityDelta, ReasonCode = reasonCode.Trim(), Note = note?.Trim(), ActorUserId = actorUserId, CorrelationId = correlationId.Trim(), CreatedAtUtc = now };
    }
}
