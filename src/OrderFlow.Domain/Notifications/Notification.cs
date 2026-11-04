namespace OrderFlow.Domain.Notifications;

public sealed class Notification
{
    private Notification() { }
    public Guid Id { get; private set; }
    public Guid? UserId { get; private set; }
    public Guid? OrderId { get; private set; }
    public string Type { get; private set; } = null!;
    public string Channel { get; private set; } = null!;
    public string Text { get; private set; } = null!;
    public Guid SourceEventId { get; private set; }
    public DateTimeOffset SentAtUtc { get; private set; }

    public static Notification Create(Guid id, Guid? userId, Guid? orderId, string type, string channel, string text, Guid sourceEventId, DateTimeOffset now)
    {
        if (id == Guid.Empty || sourceEventId == Guid.Empty || string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(text) || now.Offset != TimeSpan.Zero)
            throw new ArgumentException("Invalid notification.");
        return new Notification { Id = id, UserId = userId, OrderId = orderId, Type = type, Channel = channel, Text = text,
            SourceEventId = sourceEventId, SentAtUtc = now };
    }
}
