using System.Security.Cryptography;
using Stashboard.Api.Data;
using Stashboard.Core.Abstractions;
using Stashboard.Core.Entities;
using Stashboard.Core.Enums;

namespace Stashboard.Api.Notifications;

/// <inheritdoc cref="IDockerUpdateNotificationService"/>
public sealed class DockerUpdateNotificationService(
    IEmailSender emailSender,
    ITelegramSender telegramSender,
    IAppriseSender appriseSender,
    IAppriseSettingsService appriseSettings,
    IEncryptionService encryption,
    ILogger<DockerUpdateNotificationService> logger) : IDockerUpdateNotificationService
{
    public async Task NotifyIfNeededAsync(
        UserEntity user,
        WebResourceEntity? service,
        DockerWatchEntity watch,
        CancellationToken cancellationToken = default)
    {
        if (watch.UpdateStatus != DockerUpdateStatus.UpdateAvailable) return;
        if (string.IsNullOrEmpty(watch.LatestDigest)) return;

        // Independent channels — each has its own throttle key so a flaky SMTP
        // server doesn't permanently suppress the Telegram message, a Telegram
        // outage doesn't drop the email, and an Apprise outage drops neither.
        // Failures on any channel are logged and swallowed; the throttle key is
        // stamped only after the corresponding channel's send succeeds.
        await SendEmailIfNeededAsync(user, service, watch, cancellationToken);
        await SendTelegramIfNeededAsync(user, service, watch, cancellationToken);
        await SendAppriseIfNeededAsync(service, watch, cancellationToken);
    }

    // ── Apprise channel ──────────────────────────────────────────────────────

    private async Task SendAppriseIfNeededAsync(
        WebResourceEntity? service, DockerWatchEntity watch, CancellationToken cancellationToken)
    {
        if (!watch.AppriseNotificationsEnabled) return;
        var cfg = await appriseSettings.GetResolvedAsync(cancellationToken);
        if (!cfg.IsConfigured) return;

        if (string.Equals(watch.LastAppriseNotifiedDigest, watch.LatestDigest, StringComparison.OrdinalIgnoreCase))
            return;

        var title = $"Update available for {DisplayName(service, watch)}";
        try
        {
            await appriseSender.SendAsync(
                cfg.BaseUrl, cfg.Urls, title, BuildTelegramText(service, watch),
                AppriseNotificationType.Info, cancellationToken);
            watch.LastAppriseNotifiedDigest = watch.LatestDigest;
            watch.LastNotificationSentUtc = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send Docker update Apprise notification for watch {WatchId} (service {ServiceId})",
                watch.Id, service?.Id);
        }
    }

    // ── email channel ────────────────────────────────────────────────────────

    private async Task SendEmailIfNeededAsync(
        UserEntity user, WebResourceEntity? service, DockerWatchEntity watch, CancellationToken cancellationToken)
    {
        if (!watch.UpdateNotificationsEnabled) return;
        if (string.Equals(watch.LastNotifiedDigest, watch.LatestDigest, StringComparison.OrdinalIgnoreCase))
            return;

        var message = EmailTemplates.DockerUpdateAvailable(
            toEmail: user.Email,
            serviceName: DisplayName(service, watch),
            imageReference: watch.ImageReference,
            containerName: watch.ContainerName,
            currentDigest: watch.CurrentDigest,
            latestDigest: watch.LatestDigest!,
            releaseNotesUrl: watch.LatestReleaseUrl);

        try
        {
            await emailSender.SendAsync(message, cancellationToken);
            watch.LastNotifiedDigest = watch.LatestDigest;
            watch.LastNotificationSentUtc = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send Docker update email for watch {WatchId} (service {ServiceId})",
                watch.Id, service?.Id);
        }
    }

    // ── Telegram channel ─────────────────────────────────────────────────────

    private async Task SendTelegramIfNeededAsync(
        UserEntity user, WebResourceEntity? service, DockerWatchEntity watch, CancellationToken cancellationToken)
    {
        if (!watch.TelegramNotificationsEnabled) return;
        var botToken = ResolveBotToken(user);
        // User-level kill switch + presence check. Mirrors how
        // ServiceStatusNotificationService gates Telegram alerts.
        if (!user.TelegramNotificationsEnabled
            || string.IsNullOrWhiteSpace(botToken)
            || string.IsNullOrWhiteSpace(user.TelegramChatId))
            return;

        if (string.Equals(watch.LastTelegramNotifiedDigest, watch.LatestDigest, StringComparison.OrdinalIgnoreCase))
            return;

        var text = BuildTelegramText(service, watch);
        try
        {
            await telegramSender.SendMessageAsync(botToken!, user.TelegramChatId!, text, cancellationToken);
            watch.LastTelegramNotifiedDigest = watch.LatestDigest;
            watch.LastNotificationSentUtc = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send Docker update Telegram message for watch {WatchId} (service {ServiceId})",
                watch.Id, service?.Id);
        }
    }

    /// <summary>V3.6 — display name for the notification: the linked service's
    /// name when present, otherwise the watch label, falling back to the raw
    /// container name for a standalone tracked container.</summary>
    private static string DisplayName(WebResourceEntity? service, DockerWatchEntity watch) =>
        service?.Name
        ?? (string.IsNullOrWhiteSpace(watch.Label) ? watch.ContainerName : watch.Label);

    private static string BuildTelegramText(WebResourceEntity? service, DockerWatchEntity watch)
    {
        // V2.3 — append the release-notes link inline for GHCR images that
        // resolved a matching GitHub release. Kept as a plain URL so Telegram
        // auto-linkifies without us having to escape Markdown V2.
        var releaseLine = string.IsNullOrEmpty(watch.LatestReleaseUrl)
            ? string.Empty
            : $"\nRelease notes: {watch.LatestReleaseUrl}";

        return $"""
            🔔 Update available for {DisplayName(service, watch)}
            Image: {watch.ImageReference}
            Container: {watch.ContainerName}
            Current: {watch.CurrentDigest ?? "unknown"}
            Latest: {watch.LatestDigest ?? "unknown"}{releaseLine}

            Pull and recreate the container at your convenience.
            """;
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

    private static string? ShortenDigest(string? digest)
    {
        if (string.IsNullOrEmpty(digest)) return null;
        var colon = digest.IndexOf(':');
        if (colon < 0 || digest.Length <= colon + 13) return digest;
        return digest[..(colon + 13)] + "...";
    }
}
