using System.Reflection;
using System.Text.Json;

namespace OrderFlow.Application.Messaging;

public sealed record EventEnvelope(Guid EventId, string EventType, int EventVersion, DateTimeOffset OccurredAtUtc,
    string CorrelationId, Guid? CausationId, Guid AggregateId, string Producer, JsonElement Payload);

public static class EventTypes
{
    public const string OrderCreated = "OrderCreated";
    public const string OrderCancelled = "OrderCancelled";
    public const string InventoryReserved = "InventoryReserved";
    public const string InventoryRejected = "InventoryRejected";
    public const string InventoryCommitted = "InventoryReservationCommitted";
    public const string InventoryReleased = "InventoryReservationReleased";
    public const string InventoryLow = "InventoryLow";
    public const string PaymentRequested = "PaymentRequested";
    public const string PaymentSucceeded = "PaymentSucceeded";
    public const string PaymentFailed = "PaymentFailed";
    public const string RefundCompleted = "RefundCompleted";
    public const string RefundRequested = "RefundRequested";
    public const string ShipmentStatusChanged = "ShipmentStatusChanged";
}

public static class EventTopics
{
    public const int SupportedVersion = 1;
    private static readonly Dictionary<string, string> Topics = new(StringComparer.Ordinal)
    {
        [EventTypes.OrderCreated] = "order.created",
        [EventTypes.OrderCancelled] = "order.cancelled",
        [EventTypes.InventoryReserved] = "inventory.reserved",
        [EventTypes.InventoryRejected] = "inventory.rejected",
        [EventTypes.InventoryCommitted] = "inventory.committed",
        [EventTypes.InventoryReleased] = "inventory.released",
        [EventTypes.InventoryLow] = "inventory.low",
        [EventTypes.PaymentRequested] = "payment.requested",
        [EventTypes.PaymentSucceeded] = "payment.succeeded",
        [EventTypes.PaymentFailed] = "payment.failed",
        [EventTypes.RefundCompleted] = "refund.completed",
        [EventTypes.RefundRequested] = "refund.requested",
        [EventTypes.ShipmentStatusChanged] = "shipment.status-changed"
    };

    public static IReadOnlyCollection<string> All => Topics.Values;
    public static string? For(string eventType) => Topics.GetValueOrDefault(eventType);
    public static string DeadLetter(string topic) => $"{topic}.dlq";
}

public interface IOrderEventPayload { Guid OrderId { get; } }

public sealed record OrderCreatedItem(Guid ProductId, int Quantity, decimal UnitPrice);
public sealed record OrderCreatedPayload(Guid OrderId, Guid UserId, string OrderNumber, string CurrencyCode, decimal GrandTotal,
    string PaymentMethod, IReadOnlyList<OrderCreatedItem> Items) : IOrderEventPayload;
public sealed record OrderCancelledPayload(Guid OrderId, Guid UserId, string ReasonCode) : IOrderEventPayload;
public sealed record ReservedItem(Guid ProductId, int Quantity);
public sealed record InventoryReservedPayload(Guid OrderId, IReadOnlyList<Guid> ReservationIds, DateTimeOffset ExpiresAtUtc,
    IReadOnlyList<ReservedItem> Items) : IOrderEventPayload;
public sealed record RejectedItem(Guid ProductId, int RequestedQuantity, int AvailableQuantity, string ReasonCode);
public sealed record InventoryRejectedPayload(Guid OrderId, IReadOnlyList<RejectedItem> Items) : IOrderEventPayload;
public sealed record InventoryCommittedPayload(Guid OrderId, IReadOnlyList<Guid> ReservationIds) : IOrderEventPayload;
public sealed record InventoryReleasedPayload(Guid OrderId, string ReasonCode, int ReleasedCount) : IOrderEventPayload;
public sealed record InventoryLowPayload(Guid ProductId, int Available, int LowStockThreshold);
public sealed record PaymentRequestedPayload(Guid OrderId, decimal Amount, string CurrencyCode, string PaymentMethod,
    IReadOnlyList<Guid> ReservationIds) : IOrderEventPayload;
public sealed record PaymentResultPayload(Guid OrderId, Guid PaymentId, decimal Amount, string CurrencyCode, string PaymentMethod,
    string ProviderReference, string ResultCode) : IOrderEventPayload;
public sealed record RefundCompletedPayload(Guid OrderId, Guid PaymentId, Guid RefundId, decimal Amount, string CurrencyCode,
    string ProviderReference) : IOrderEventPayload;
public sealed record RefundRequestedPayload(Guid OrderId, Guid ReturnRequestId, string ReasonCode) : IOrderEventPayload;
// Status is Shipped, Delivered, or DeliveryFailed: the changes the customer and the order care about.
public sealed record ShipmentStatusChangedPayload(Guid OrderId, Guid ShipmentId, string Status, string TrackingCode) : IOrderEventPayload;

public static class CancellationReasons
{
    public const string CustomerRequest = "CUSTOMER_REQUEST";
    public const string StaffRequest = "STAFF_REQUEST";
}

public static class ReleaseReasons
{
    public const string PaymentFailed = "PAYMENT_FAILED";
    public const string OrderCancelled = "ORDER_CANCELLED";
    public const string ReservationExpired = "RESERVATION_EXPIRED";
}

// A message that can never succeed, so retrying it only delays the partition.
public sealed class EventContractException(string message) : Exception(message);

public static class EventSerializer
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    private static readonly string Producer = Assembly.GetEntryAssembly()?.GetName().Name ?? "OrderFlow";

    public static EventEnvelope Create<T>(string eventType, Guid aggregateId, string correlationId, Guid? causationId, T payload, DateTimeOffset now) =>
        new(Guid.NewGuid(), eventType, EventTopics.SupportedVersion, now, correlationId, causationId, aggregateId, Producer,
            JsonSerializer.SerializeToElement(payload, Options));

    public static string Serialize(EventEnvelope envelope) => JsonSerializer.Serialize(envelope, Options);

    public static EventEnvelope Parse(string json)
    {
        EventEnvelope? envelope;
        try { envelope = JsonSerializer.Deserialize<EventEnvelope>(json, Options); }
        catch (JsonException) { throw new EventContractException("Envelope is not valid JSON."); }
        if (envelope is null || envelope.EventId == Guid.Empty || envelope.AggregateId == Guid.Empty ||
            string.IsNullOrWhiteSpace(envelope.EventType) || string.IsNullOrWhiteSpace(envelope.CorrelationId) ||
            envelope.Payload.ValueKind != JsonValueKind.Object)
            throw new EventContractException("Envelope is missing required fields.");
        if (EventTopics.For(envelope.EventType) is null) throw new EventContractException("Unknown event type.");
        if (envelope.EventVersion != EventTopics.SupportedVersion) throw new EventContractException("Unsupported event version.");
        return envelope;
    }

    public static T ReadPayload<T>(EventEnvelope envelope) where T : class
    {
        T? payload;
        try { payload = envelope.Payload.Deserialize<T>(Options); }
        catch (JsonException) { throw new EventContractException($"{envelope.EventType} payload is invalid."); }
        if (payload is null || payload is IOrderEventPayload { OrderId: var id } && id == Guid.Empty)
            throw new EventContractException($"{envelope.EventType} payload is missing required fields.");
        return payload;
    }
}
