using OrderFlow.Domain.Inventory;

namespace OrderFlow.Application.Inventory;

public sealed record InventorySnapshot(Guid ProductId, int OnHand, int Reserved, int Available, int LowStockThreshold, string Version);
public sealed record ReservationSnapshot(Guid Id, Guid OrderId, Guid ProductId, int Quantity, ReservationStatus Status, DateTimeOffset ExpiresAtUtc);
public enum ReserveOutcome { Reserved, AlreadyReserved, InsufficientStock, ProductUnavailable }
public sealed record ReserveResult(ReserveOutcome Outcome, ReservationSnapshot? Reservation);
public sealed record ReservationLine(Guid ProductId, int Quantity);
public sealed record RejectedLine(Guid ProductId, int RequestedQuantity, int AvailableQuantity, string ReasonCode);
public sealed record OrderReservationResult(IReadOnlyList<ReservationSnapshot> Reservations, IReadOnlyList<RejectedLine> Rejected);
public enum OrderCommitOutcome { Committed, AlreadyCommitted, NoReservations, NotCommittable }
public sealed record OrderCommitResult(OrderCommitOutcome Outcome, IReadOnlyList<Guid> ReservationIds, int NewlyExpired);

public interface IInventoryService
{
    Task<InventorySnapshot?> GetAsync(Guid productId, CancellationToken cancellationToken);
    Task<IReadOnlyList<ReservationSnapshot>> GetReservationsAsync(Guid productId, CancellationToken cancellationToken);
    Task<InventorySnapshot?> AdjustAsync(Guid productId, int quantityDelta, string reasonCode, string? note,
        Guid actorUserId, string correlationId, CancellationToken cancellationToken);
    Task<InventorySnapshot?> SetLowStockThresholdAsync(Guid productId, int threshold, CancellationToken cancellationToken);
    Task<ReserveResult> ReserveAsync(Guid orderId, Guid productId, int quantity, CancellationToken cancellationToken);
    Task<ReservationSnapshot?> TransitionAsync(Guid orderId, Guid productId, ReservationStatus target, CancellationToken cancellationToken);
    // Must be called within the ordering transaction so release and cancellation commit together.
    Task<int> ReleaseForCancellationAsync(Guid orderId, CancellationToken cancellationToken);
    // Must be called within the consumer transaction: every line is reserved or none is, and the result commits with the Inbox record.
    Task<OrderReservationResult> ReserveOrderAsync(Guid orderId, IReadOnlyList<ReservationLine> lines, string correlationId, Guid? causationId, CancellationToken cancellationToken);
    // Must be called within the consumer transaction: every reservation commits or none does.
    Task<OrderCommitResult> CommitOrderAsync(Guid orderId, CancellationToken cancellationToken);
}

public interface IReservationExpiryJob
{
    Task<int> ExpireDueAsync(int batchSize, CancellationToken cancellationToken);
}
