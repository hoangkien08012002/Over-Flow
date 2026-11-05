namespace OrderFlow.Domain.Returns;

public enum ReturnStatus { Requested, Approved, Rejected, Received, Refunded, Closed }

public sealed class ReturnRequest
{
    private ReturnRequest() { }
    public Guid Id { get; private set; }
    public Guid OrderId { get; private set; }
    public Guid UserId { get; private set; }
    public ReturnStatus Status { get; private set; }
    public string Reason { get; private set; } = null!;
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset UpdatedAtUtc { get; private set; }
    public byte[] RowVersion { get; private set; } = [];
    public List<ReturnItem> Items { get; private set; } = [];
    public List<ReturnStatusHistory> History { get; private set; } = [];

    // The first release returns a whole order, because a payment has one full refund. Each line records whether its stock came back.
    public static ReturnRequest Create(Guid id, Guid orderId, Guid userId, string reason, IEnumerable<(Guid OrderItemId, Guid ProductId, int Quantity)> lines, DateTimeOffset now, string correlationId)
    {
        var items = lines.Select(x => new ReturnItem(Guid.NewGuid(), id, x.OrderItemId, x.ProductId, x.Quantity)).ToList();
        if (id == Guid.Empty || orderId == Guid.Empty || userId == Guid.Empty || string.IsNullOrWhiteSpace(reason) || reason.Trim().Length > 500 || items.Count == 0 || now.Offset != TimeSpan.Zero)
            throw new ArgumentException("Invalid return request.");
        var request = new ReturnRequest { Id = id, OrderId = orderId, UserId = userId, Status = ReturnStatus.Requested, Reason = reason.Trim(), CreatedAtUtc = now, UpdatedAtUtc = now, Items = items };
        request.History.Add(new ReturnStatusHistory(Guid.NewGuid(), id, null, ReturnStatus.Requested, now, userId, null, null, correlationId));
        return request;
    }

    // Returns false for a repeat of the current status, so a retried command or event has no second effect.
    public bool TransitionTo(ReturnStatus next, Guid? actorId, Guid? eventId, string? reason, DateTimeOffset now, string correlationId)
    {
        if (Status == next) return false;
        var allowed = (Status, next) switch
        {
            (ReturnStatus.Requested, ReturnStatus.Approved or ReturnStatus.Rejected) => true,
            (ReturnStatus.Approved, ReturnStatus.Received) => true,
            (ReturnStatus.Received, ReturnStatus.Refunded) => true,
            (ReturnStatus.Refunded, ReturnStatus.Closed) => true,
            _ => false
        };
        if (!allowed) throw new InvalidOperationException("INVALID_STATE_TRANSITION");
        History.Add(new ReturnStatusHistory(Guid.NewGuid(), Id, Status, next, now, actorId, eventId, reason, correlationId));
        Status = next;
        UpdatedAtUtc = now;
        return true;
    }
}

public sealed class ReturnItem
{
    private ReturnItem() { }
    public Guid Id { get; private set; }
    public Guid ReturnRequestId { get; private set; }
    public Guid OrderItemId { get; private set; }
    public Guid ProductId { get; private set; }
    public int Quantity { get; private set; }
    public bool? Restocked { get; private set; }

    public ReturnItem(Guid id, Guid returnRequestId, Guid orderItemId, Guid productId, int quantity)
    {
        if (quantity <= 0) throw new ArgumentException("Invalid return item.");
        Id = id; ReturnRequestId = returnRequestId; OrderItemId = orderItemId; ProductId = productId; Quantity = quantity;
    }

    public void RecordReceipt(bool restocked) => Restocked = restocked;
}

public sealed class ReturnStatusHistory(Guid id, Guid returnRequestId, ReturnStatus? fromStatus, ReturnStatus toStatus,
    DateTimeOffset changedAtUtc, Guid? actorUserId, Guid? eventId, string? reason, string correlationId)
{
    public Guid Id { get; private set; } = id;
    public Guid ReturnRequestId { get; private set; } = returnRequestId;
    public ReturnStatus? FromStatus { get; private set; } = fromStatus;
    public ReturnStatus ToStatus { get; private set; } = toStatus;
    public DateTimeOffset ChangedAtUtc { get; private set; } = changedAtUtc;
    public Guid? ActorUserId { get; private set; } = actorUserId;
    public Guid? EventId { get; private set; } = eventId;
    public string? Reason { get; private set; } = reason;
    public string CorrelationId { get; private set; } = correlationId;
}
