namespace Stashboard.Api.Notifications;

public interface ITelegramSender
{
    Task SendMessageAsync(string botToken, string chatId, string message, CancellationToken cancellationToken = default);
}
