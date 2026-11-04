namespace OrderFlow.Application.Notifications;

public sealed record NotificationMessage(Guid NotificationId, Guid? UserId, Guid? OrderId, string Type, string Text);

public interface INotificationProvider
{
    string Channel { get; }
    Task SendAsync(NotificationMessage message, CancellationToken cancellationToken);
}
