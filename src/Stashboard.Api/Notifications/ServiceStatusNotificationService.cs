using System.Security.Cryptography;
using Stashboard.Api.Data;
using Stashboard.Core.Abstractions;
using Stashboard.Core.Entities;
using Stashboard.Core.Enums;

namespace Stashboard.Api.Notifications;

public sealed class ServiceStatusNotificationService(
    ITelegramSender telegramSender,
    IAppriseSender appriseSender,
    IAppriseSettingsService appriseSettings,
    IEncryptionService encryption,
    ILogger<ServiceStatusNotificationService> logger) : IServiceStatusNotificationService
{
    public async Task NotifyIfNeededAsync(
        UserEntity user,
        WebResourceEntity service,
        ServiceStatus previousMainStatus,
        ServiceStatus previousAdditionalStatus,
        CancellationToken cancellationToken = default)
    {
        // The per-service master switch gates every channel; offline alerts here are
        // transition-based (fired on a Down edge), so there is no per-channel throttle
        // key — the transition is the dedup, exactly as the Telegram path always worked.
        if (!service.OfflineNotificationsEnabled) return;

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

        if (messages.Count == 0) return;

        // Independent channels: an Apprise outage never drops the Telegram message and
        // vice-versa. Each channel checks its own configuration on its own.
        await SendTelegramAsync(user, service, messages, cancellationToken);
        await SendAppriseAsync(service, messages, cancellationToken);
    }

    private async Task SendTelegramAsync(
        UserEntity user, WebResourceEntity service, IReadOnlyList<string> messages, CancellationToken cancellationToken)
    {
        var botToken = ResolveBotToken(user);
        if (!user.TelegramNotificationsEnabled
            || string.IsNullOrWhiteSpace(botToken)
            || string.IsNullOrWhiteSpace(user.TelegramChatId))
            return;

        foreach (var message in messages)
        {
            try
            {
                await telegramSender.SendMessageAsync(botToken!, user.TelegramChatId!, message, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to send Telegram notification for service {ServiceId}", service.Id);
            }
        }
    }

    private async Task SendAppriseAsync(
        WebResourceEntity service, IReadOnlyList<string> messages, CancellationToken cancellationToken)
    {
        var cfg = await appriseSettings.GetResolvedAsync(cancellationToken);
        if (!cfg.IsConfigured) return;

        foreach (var message in messages)
        {
            try
            {
                await appriseSender.SendAsync(
                    cfg.BaseUrl, cfg.Urls, $"Service unavailable: {service.Name}", message,
                    AppriseNotificationType.Failure, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to send Apprise notification for service {ServiceId}", service.Id);
            }
        }
    }

    private string? ResolveBotToken(UserEntity user)
    {
        if (!string.IsNullOrWhiteSpace(user.TelegramBotToken))
            return user.TelegramBotToken;

        if (string.IsNullOrWhiteSpace(user.TelegramBotTokenEncrypted))
            return null;

        try
        {
            return encryption.Decrypt(user.TelegramBotTokenEncrypted);
        }
        catch (CryptographicException ex)
        {
            logger.LogWarning(ex, "Failed to decrypt Telegram bot token for user {UserId}", user.Id);
            return user.TelegramBotTokenEncrypted;
        }
        catch (FormatException ex)
        {
            logger.LogWarning(ex, "Failed to decode encrypted Telegram bot token for user {UserId}", user.Id);
            return user.TelegramBotTokenEncrypted;
        }
    }
}
