using OrderFlow.Domain.Inventory;

namespace OrderFlow.Application.Inventory;

public sealed record InventorySnapshot(Guid ProductId, int OnHand, int Reserved, int Available, int LowStockThreshold, string Version);
public sealed record ReservationSnapshot(Guid Id, Guid OrderId, Guid ProductId, int Quantity, ReservationStatus Status, DateTimeOffset ExpiresAtUtc);
public enum ReserveOutcome { Reserved, AlreadyReserved, InsufficientStock, ProductUnavailable }
public sealed record ReserveResult(ReserveOutcome Outcome, ReservationSnapshot? Reservation);

public interface IInventoryService
{
    Task<InventorySnapshot?> GetAsync(Guid productId, CancellationToken cancellationToken);
    Task<IReadOnlyList<ReservationSnapshot>> GetReservationsAsync(Guid productId, CancellationToken cancellationToken);
    Task<InventorySnapshot?> AdjustAsync(Guid productId, int quantityDelta, string reasonCode, string? note,
        Guid actorUserId, string correlationId, CancellationToken cancellationToken);
    Task<InventorySnapshot?> SetLowStockThresholdAsync(Guid productId, int threshold, CancellationToken cancellationToken);
    Task<ReserveResult> ReserveAsync(Guid orderId, Guid productId, int quantity, CancellationToken cancellationToken);
    Task<ReservationSnapshot?> TransitionAsync(Guid orderId, Guid productId, ReservationStatus target, CancellationToken cancellationToken);
}

public interface IReservationExpiryJob
{
    Task<int> ExpireDueAsync(int batchSize, CancellationToken cancellationToken);
}
