namespace OrderFlow.Domain.Shipping;

public enum ShipmentStatus { Pending, ReadyToShip, Shipped, Delivered, DeliveryFailed, Cancelled }

public sealed class Shipment
{
    private Shipment() { }
    public Guid Id { get; private set; }
    public Guid OrderId { get; private set; }
    public ShipmentStatus Status { get; private set; }
    public string TrackingCode { get; private set; } = null!;
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset UpdatedAtUtc { get; private set; }
    public byte[] RowVersion { get; private set; } = [];
    public List<ShipmentStatusHistory> History { get; private set; } = [];

    public static Shipment Create(Guid id, Guid orderId, Guid actorId, DateTimeOffset now, string correlationId)
    {
        if (id == Guid.Empty || orderId == Guid.Empty || now.Offset != TimeSpan.Zero) throw new ArgumentException("Invalid shipment.");
        var shipment = new Shipment { Id = id, OrderId = orderId, Status = ShipmentStatus.Pending, TrackingCode = $"OFT-{id:N}"[..24].ToUpperInvariant(), CreatedAtUtc = now, UpdatedAtUtc = now };
        shipment.History.Add(new ShipmentStatusHistory(Guid.NewGuid(), id, null, ShipmentStatus.Pending, now, actorId, null, correlationId));
        return shipment;
    }

    // Returns false for a repeat of the current status, so a retried command has no second effect.
    public bool TransitionTo(ShipmentStatus next, Guid actorId, string? reason, DateTimeOffset now, string correlationId)
    {
        if (Status == next) return false;
        var allowed = (Status, next) switch
        {
            (ShipmentStatus.Pending, ShipmentStatus.ReadyToShip or ShipmentStatus.Cancelled) => true,
            (ShipmentStatus.ReadyToShip, ShipmentStatus.Shipped or ShipmentStatus.Cancelled) => true,
            (ShipmentStatus.Shipped, ShipmentStatus.Delivered or ShipmentStatus.DeliveryFailed) => true,
            _ => false
        };
        if (!allowed) throw new InvalidOperationException("INVALID_STATE_TRANSITION");
        History.Add(new ShipmentStatusHistory(Guid.NewGuid(), Id, Status, next, now, actorId, reason, correlationId));
        Status = next;
        UpdatedAtUtc = now;
        return true;
    }
}

public sealed class ShipmentStatusHistory(Guid id, Guid shipmentId, ShipmentStatus? fromStatus, ShipmentStatus toStatus,
    DateTimeOffset changedAtUtc, Guid actorUserId, string? reason, string correlationId)
{
    public Guid Id { get; private set; } = id;
    public Guid ShipmentId { get; private set; } = shipmentId;
    public ShipmentStatus? FromStatus { get; private set; } = fromStatus;
    public ShipmentStatus ToStatus { get; private set; } = toStatus;
    public DateTimeOffset ChangedAtUtc { get; private set; } = changedAtUtc;
    public Guid ActorUserId { get; private set; } = actorUserId;
    public string? Reason { get; private set; } = reason;
    public string CorrelationId { get; private set; } = correlationId;
}
