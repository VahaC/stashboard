using System.Security.Cryptography;
using Stashboard.Api.Data;
using Stashboard.Core.Abstractions;
using Stashboard.Core.Entities;

namespace Stashboard.Api.Notifications;

/// <inheritdoc cref="IProxmoxUpdateNotificationService"/>
public sealed class ProxmoxUpdateNotificationService(
    IEmailSender emailSender,
    ITelegramSender telegramSender,
    IAppriseSender appriseSender,
    IAppriseSettingsService appriseSettings,
    IEncryptionService encryption,
    ILogger<ProxmoxUpdateNotificationService> logger) : IProxmoxUpdateNotificationService
{
    public async Task NotifyIfNeededAsync(
        UserEntity user,
        ProxmoxConnectionEntity connection,
        IReadOnlyList<ProxmoxGuestEntity> guests,
        CancellationToken cancellationToken = default)
    {
        // V6.7 — a disabled guest is excluded from the signature so turning
        // monitoring off stops repeat alerts for it immediately (its
        // PendingUpdates is also cleared on the next scan). The node row is
        // always MonitoringEnabled.
        var pending = guests.Where(g => g.MonitoringEnabled && g.PendingUpdates is > 0).ToList();
        if (pending.Count == 0) return;

        var signature = BuildSignature(pending);

        // Independent channels, each with its own throttle key (mirrors the Docker
        // notifier): a flaky SMTP server doesn't suppress Telegram or Apprise, and
        // vice-versa. The key is stamped only after the channel's send succeeds.
        await SendEmailIfNeededAsync(user, connection, pending, signature, cancellationToken);
        await SendTelegramIfNeededAsync(user, connection, pending, signature, cancellationToken);
        await SendAppriseIfNeededAsync(connection, pending, signature, cancellationToken);
    }

    // ── Apprise channel ───────────────────────────────────────────────────────

    private async Task SendAppriseIfNeededAsync(
        ProxmoxConnectionEntity connection, IReadOnlyList<ProxmoxGuestEntity> pending,
        string signature, CancellationToken cancellationToken)
    {
        if (!connection.AppriseNotificationsEnabled) return;
        var cfg = await appriseSettings.GetResolvedAsync(cancellationToken);
        if (!cfg.IsConfigured) return;

        if (string.Equals(connection.LastAppriseNotifiedSignature, signature, StringComparison.Ordinal)) return;

        try
        {
            await appriseSender.SendAsync(
                cfg.BaseUrl, cfg.Urls,
                $"Updates pending on Proxmox host {connection.Name}",
                BuildTelegramText(connection, pending),
                AppriseNotificationType.Info, cancellationToken);
            connection.LastAppriseNotifiedSignature = signature;
            connection.LastNotificationSentUtc = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send Proxmox update Apprise notification for connection {ConnectionId}", connection.Id);
        }
    }

    /// <summary>Stable signature of the pending-update state — a sorted list of
    /// "vmid:count" pairs. Re-notification fires only when this changes.</summary>
    public static string BuildSignature(IEnumerable<ProxmoxGuestEntity> pendingGuests) =>
        string.Join("|", pendingGuests
            .OrderBy(g => g.VmId)
            .Select(g => $"{g.VmId}:{g.PendingUpdates}"));

    // ── email channel ─────────────────────────────────────────────────────────

    private async Task SendEmailIfNeededAsync(
        UserEntity user, ProxmoxConnectionEntity connection,
        IReadOnlyList<ProxmoxGuestEntity> pending, string signature, CancellationToken cancellationToken)
    {
        if (!connection.UpdateNotificationsEnabled) return;
        if (string.Equals(connection.LastNotifiedSignature, signature, StringComparison.Ordinal)) return;

        var lines = pending.Select(FormatLine).ToList();
        var total = pending.Sum(g => g.PendingUpdates ?? 0);
        var message = EmailTemplates.ProxmoxUpdatesAvailable(user.Email, connection.Name, lines, total);

        try
        {
            await emailSender.SendAsync(message, cancellationToken);
            connection.LastNotifiedSignature = signature;
            connection.LastNotificationSentUtc = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send Proxmox update email for connection {ConnectionId}", connection.Id);
        }
    }

    // ── Telegram channel ────────────────────────────────────────────────────

    private async Task SendTelegramIfNeededAsync(
        UserEntity user, ProxmoxConnectionEntity connection,
        IReadOnlyList<ProxmoxGuestEntity> pending, string signature, CancellationToken cancellationToken)
    {
        if (!connection.TelegramNotificationsEnabled) return;
        var botToken = ResolveBotToken(user);
        if (!user.TelegramNotificationsEnabled
            || string.IsNullOrWhiteSpace(botToken)
            || string.IsNullOrWhiteSpace(user.TelegramChatId))
            return;

        if (string.Equals(connection.LastTelegramNotifiedSignature, signature, StringComparison.Ordinal)) return;

        var text = BuildTelegramText(connection, pending);
        try
        {
            await telegramSender.SendMessageAsync(botToken!, user.TelegramChatId!, text, cancellationToken);
            connection.LastTelegramNotifiedSignature = signature;
            connection.LastNotificationSentUtc = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send Proxmox update Telegram message for connection {ConnectionId}", connection.Id);
        }
    }

    private static string FormatLine(ProxmoxGuestEntity g)
    {
        var label = g.VmId == 0 ? $"{g.Name} (node)" : $"{g.Name} (CT {g.VmId})";
        return $"{label}: {g.PendingUpdates} update(s)";
    }

    private static string BuildTelegramText(ProxmoxConnectionEntity connection, IReadOnlyList<ProxmoxGuestEntity> pending)
    {
        var total = pending.Sum(g => g.PendingUpdates ?? 0);
        var body = string.Join("\n", pending.Select(g => $"• {FormatLine(g)}"));
        return $"""
            🔔 Updates pending on Proxmox host {connection.Name} ({total} total)
            {body}

            Apply them at your convenience.
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
}
