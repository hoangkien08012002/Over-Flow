namespace OrderFlow.Domain.Engagement;

public sealed class WishlistItem
{
    private WishlistItem() { }
    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public Guid ProductId { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public WishlistItem(Guid userId, Guid productId, DateTimeOffset now)
    {
        if (userId == Guid.Empty || productId == Guid.Empty || now.Offset != TimeSpan.Zero) throw new ArgumentException("Invalid wishlist item.");
        Id = Guid.NewGuid(); UserId = userId; ProductId = productId; CreatedAtUtc = now;
    }
}

public enum ReviewStatus { Published, Hidden }

public sealed class Review
{
    public const int MaxCommentLength = 2000;
    private Review() { }
    public Guid Id { get; private set; }
    public Guid ProductId { get; private set; }
    public Guid OrderId { get; private set; }
    public Guid OrderItemId { get; private set; }
    public Guid UserId { get; private set; }
    public int Rating { get; private set; }
    public string? Comment { get; private set; }
    public ReviewStatus Status { get; private set; }
    public string? ModerationReason { get; private set; }
    public Guid? ModeratedByUserId { get; private set; }
    public DateTimeOffset? ModeratedAtUtc { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public static Review Create(Guid id, Guid productId, Guid orderId, Guid orderItemId, Guid userId, int rating, string? comment, DateTimeOffset now)
    {
        if (id == Guid.Empty || productId == Guid.Empty || orderId == Guid.Empty || orderItemId == Guid.Empty || userId == Guid.Empty ||
            rating is < 1 or > 5 || comment?.Trim().Length > MaxCommentLength || now.Offset != TimeSpan.Zero)
            throw new ArgumentException("Invalid review.");
        return new Review { Id = id, ProductId = productId, OrderId = orderId, OrderItemId = orderItemId, UserId = userId, Rating = rating,
            Comment = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim(), Status = ReviewStatus.Published, CreatedAtUtc = now };
    }

    // Moderation only changes visibility: the customer's text stays, so the decision can be reviewed or reversed.
    public void Moderate(ReviewStatus status, string reason, Guid moderatorId, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length > 500 || moderatorId == Guid.Empty || now.Offset != TimeSpan.Zero)
            throw new ArgumentException("Invalid moderation.");
        Status = status;
        ModerationReason = reason.Trim();
        ModeratedByUserId = moderatorId;
        ModeratedAtUtc = now;
    }
}
