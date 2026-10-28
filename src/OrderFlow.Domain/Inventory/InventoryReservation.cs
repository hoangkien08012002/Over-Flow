namespace OrderFlow.Domain.Inventory;

public enum ReservationStatus { Active, Committed, Released, Expired }

public sealed class InventoryReservation
{
    private InventoryReservation() { }

    public Guid Id { get; private set; }
    public Guid OrderId { get; private set; }
    public Guid ProductId { get; private set; }
    public int Quantity { get; private set; }
    public ReservationStatus Status { get; private set; }
    public DateTimeOffset ExpiresAtUtc { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset UpdatedAtUtc { get; private set; }
    public DateTimeOffset? CommittedAtUtc { get; private set; }
    public DateTimeOffset? ReleasedAtUtc { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public static InventoryReservation Create(Guid id, Guid orderId, Guid productId, int quantity, DateTimeOffset expiresAtUtc, DateTimeOffset now)
    {
        if (id == Guid.Empty || orderId == Guid.Empty || productId == Guid.Empty || quantity <= 0 ||
            now.Offset != TimeSpan.Zero || expiresAtUtc.Offset != TimeSpan.Zero || expiresAtUtc <= now)
            throw new ArgumentException("Invalid reservation.");
        return new InventoryReservation { Id = id, OrderId = orderId, ProductId = productId, Quantity = quantity,
            Status = ReservationStatus.Active, ExpiresAtUtc = expiresAtUtc, CreatedAtUtc = now, UpdatedAtUtc = now };
    }

    public bool TransitionTo(ReservationStatus target, DateTimeOffset now)
    {
        if (target == ReservationStatus.Active || now.Offset != TimeSpan.Zero) throw new ArgumentException("Invalid transition.");
        if (Status == target) return false;
        if (Status != ReservationStatus.Active) throw new InvalidOperationException("Reservation is terminal.");
        if (target == ReservationStatus.Expired && now < ExpiresAtUtc) throw new InvalidOperationException("Reservation has not expired.");
        if (target == ReservationStatus.Committed && now >= ExpiresAtUtc) throw new InvalidOperationException("Reservation has expired.");
        Status = target;
        UpdatedAtUtc = now;
        if (target == ReservationStatus.Committed) CommittedAtUtc = now;
        else ReleasedAtUtc = now;
        return true;
    }
}
