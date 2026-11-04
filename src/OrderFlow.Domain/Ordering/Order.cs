namespace OrderFlow.Domain.Ordering;

public enum OrderStatus { PendingInventory, AwaitingPayment, Paid, Processing, Shipped, Delivered, Rejected, PaymentFailed, Cancelled, Refunded }

public sealed class Order
{
    private Order() { }
    public Guid Id { get; private set; }
    public string OrderNumber { get; private set; } = null!;
    public Guid UserId { get; private set; }
    public OrderStatus Status { get; private set; }
    public decimal Subtotal { get; private set; }
    public decimal DiscountTotal { get; private set; }
    public decimal ShippingFee { get; private set; }
    public decimal GrandTotal { get; private set; }
    public string CurrencyCode { get; private set; } = null!;
    public string PaymentMethod { get; private set; } = null!;
    public Guid ShippingMethodId { get; private set; }
    public string ShippingMethodName { get; private set; } = null!;
    public string AddressSnapshotJson { get; private set; } = null!;
    public Guid? CouponId { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset UpdatedAtUtc { get; private set; }
    public byte[] RowVersion { get; private set; } = [];
    public List<OrderItem> Items { get; private set; } = [];
    public List<OrderStatusHistory> History { get; private set; } = [];

    public static Order Create(Guid id, Guid userId, string number, string paymentMethod, Guid shippingMethodId,
        string shippingMethodName, string addressJson, string currency, Guid? couponId, decimal shippingFee,
        IEnumerable<OrderItem> items, DateTimeOffset now, string correlationId)
    {
        var orderItems = items.ToList();
        if (id == Guid.Empty || userId == Guid.Empty || orderItems.Count == 0 || shippingFee < 0 || now.Offset != TimeSpan.Zero)
            throw new ArgumentException("Invalid order creation data.");
        var subtotal = orderItems.Sum(x => x.LineSubtotal);
        var discount = orderItems.Sum(x => x.DiscountAmount);
        if (discount > subtotal) throw new ArgumentException("Discount exceeds subtotal.");
        var order = new Order { Id = id, UserId = userId, OrderNumber = number, Status = OrderStatus.PendingInventory,
            PaymentMethod = paymentMethod, ShippingMethodId = shippingMethodId, ShippingMethodName = shippingMethodName,
            AddressSnapshotJson = addressJson, CurrencyCode = currency, CouponId = couponId,
            Subtotal = subtotal, DiscountTotal = discount, ShippingFee = shippingFee,
            GrandTotal = decimal.Round(Math.Max(0, subtotal - discount) + shippingFee, 2, MidpointRounding.AwayFromZero),
            CreatedAtUtc = now, UpdatedAtUtc = now, Items = orderItems };
        order.History.Add(new OrderStatusHistory(Guid.NewGuid(), id, null, OrderStatus.PendingInventory, now, userId, null, null, correlationId));
        return order;
    }

    public void Cancel(Guid actorId, string reason, DateTimeOffset now, string correlationId)
    {
        if (Status == OrderStatus.Cancelled) return;
        if (Status is not (OrderStatus.PendingInventory or OrderStatus.AwaitingPayment))
            throw new InvalidOperationException("INVALID_STATE_TRANSITION");
        Transition(OrderStatus.Cancelled, now, correlationId, actorId, null, reason);
    }

    public void Transition(OrderStatus next, DateTimeOffset now, string correlationId, Guid? actorId = null, Guid? eventId = null, string? reason = null)
    {
        if (now.Offset != TimeSpan.Zero || string.IsNullOrWhiteSpace(correlationId) || reason?.Length > 500)
            throw new ArgumentException("Invalid transition metadata.");
        var allowed = (Status, next) switch
        {
            (OrderStatus.PendingInventory, OrderStatus.AwaitingPayment or OrderStatus.Rejected or OrderStatus.Cancelled) => true,
            (OrderStatus.AwaitingPayment, OrderStatus.Paid or OrderStatus.PaymentFailed or OrderStatus.Cancelled) => true,
            // Customers cannot reach this edge: Cancel() stops at AwaitingPayment. It is the compensation path after paying for an expired reservation.
            (OrderStatus.Paid, OrderStatus.Processing or OrderStatus.Cancelled) => true,
            (OrderStatus.Processing, OrderStatus.Shipped) => true,
            (OrderStatus.Shipped, OrderStatus.Delivered) => true,
            _ => false
        };
        if (!allowed) throw new InvalidOperationException("INVALID_STATE_TRANSITION");
        var previous = Status;
        Status = next;
        UpdatedAtUtc = now;
        History.Add(new OrderStatusHistory(Guid.NewGuid(), Id, previous, next, now, actorId, eventId, reason, correlationId));
    }
}

public sealed class OrderItem
{
    private OrderItem() { }
    public Guid Id { get; private set; }
    public Guid OrderId { get; private set; }
    public Guid ProductId { get; private set; }
    public string SkuSnapshot { get; private set; } = null!;
    public string ProductNameSnapshot { get; private set; } = null!;
    public decimal UnitPrice { get; private set; }
    public int Quantity { get; private set; }
    public decimal LineSubtotal { get; private set; }
    public decimal DiscountAmount { get; private set; }
    public OrderItem(Guid orderId, Guid productId, string sku, string name, decimal price, int quantity, decimal discount = 0)
    {
        if (quantity <= 0 || price < 0 || discount < 0) throw new ArgumentException("Invalid order item.");
        Id = Guid.NewGuid(); OrderId = orderId; ProductId = productId; SkuSnapshot = sku; ProductNameSnapshot = name;
        UnitPrice = price; Quantity = quantity; LineSubtotal = decimal.Round(price * quantity, 2, MidpointRounding.AwayFromZero);
        DiscountAmount = discount;
        if (discount > LineSubtotal) throw new ArgumentException("Discount exceeds line subtotal.");
    }
}

public sealed class OrderStatusHistory(Guid id, Guid orderId, OrderStatus? fromStatus, OrderStatus toStatus,
    DateTimeOffset changedAtUtc, Guid? actorUserId, Guid? eventId, string? reason, string correlationId)
{
    public Guid Id { get; private set; } = id;
    public Guid OrderId { get; private set; } = orderId;
    public OrderStatus? FromStatus { get; private set; } = fromStatus;
    public OrderStatus ToStatus { get; private set; } = toStatus;
    public DateTimeOffset ChangedAtUtc { get; private set; } = changedAtUtc;
    public Guid? ActorUserId { get; private set; } = actorUserId;
    public Guid? EventId { get; private set; } = eventId;
    public string? Reason { get; private set; } = reason;
    public string CorrelationId { get; private set; } = correlationId;
}
