using Stashboard.Api.Data;
using Stashboard.Core.Entities;
using Stashboard.Core.Enums;

namespace Stashboard.Api.Notifications;

public sealed class ServiceStatusNotificationService(
    ITelegramSender telegramSender,
    ILogger<ServiceStatusNotificationService> logger) : IServiceStatusNotificationService
{
    public async Task NotifyIfNeededAsync(
        UserEntity user,
        WebResourceEntity service,
        ServiceStatus previousMainStatus,
        ServiceStatus previousAdditionalStatus,
        CancellationToken cancellationToken = default)
    {
        if (!service.OfflineNotificationsEnabled
            || !user.TelegramNotificationsEnabled
            || string.IsNullOrWhiteSpace(user.TelegramBotToken)
            || string.IsNullOrWhiteSpace(user.TelegramChatId))
            return;

        var messages = new List<string>();
        if (service.MainUrlHealthCheckEnabled
            && previousMainStatus != ServiceStatus.Down
            && service.CurrentStatus == ServiceStatus.Down)
        {
            messages.Add($"🔴 Service unavailable: {service.Name}\nMain URL: {service.MainUrl}\nError: {service.LastError ?? "unknown"}");
        }

        if (!string.IsNullOrWhiteSpace(service.AdditionalUrl)
            && service.AdditionalUrlHealthCheckEnabled
            && previousAdditionalStatus != ServiceStatus.Down
            && service.AdditionalUrlStatus == ServiceStatus.Down)
        {
            messages.Add($"🔴 Service unavailable: {service.Name}\nAdditional URL: {service.AdditionalUrl}\nError: {service.AdditionalUrlLastError ?? "unknown"}");
        }

        foreach (var message in messages)
        {
            try
            {
                await telegramSender.SendMessageAsync(user.TelegramBotToken!, user.TelegramChatId!, message, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to send Telegram notification for service {ServiceId}", service.Id);
            }
        }
    }
}
