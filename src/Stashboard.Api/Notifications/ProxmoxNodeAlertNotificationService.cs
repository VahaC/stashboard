using System.Globalization;
using System.Security.Cryptography;
using Stashboard.Api.Data;
using Stashboard.Core.Abstractions;
using Stashboard.Core.Entities;
using Stashboard.Core.Enums;

namespace Stashboard.Api.Notifications;

/// <inheritdoc cref="IProxmoxNodeAlertNotificationService"/>
public sealed class ProxmoxNodeAlertNotificationService(
    IEmailSender emailSender,
    ITelegramSender telegramSender,
    IEncryptionService encryption,
    ILogger<ProxmoxNodeAlertNotificationService> logger) : IProxmoxNodeAlertNotificationService
{
    public async Task NotifyIfNeededAsync(
        UserEntity user,
        ProxmoxConnectionEntity connection,
        ProxmoxNodeAlertSettingsEntity settings,
        IReadOnlyList<ProxmoxNodeAlertStateEntity> active,
        string signature,
        CancellationToken cancellationToken = default)
    {
        // Two independent channels, each with its own throttle key (mirrors the
        // update notifier): a flaky SMTP server doesn't suppress Telegram, and
        // vice-versa. The key is stamped only after the channel's send succeeds.
        await SendEmailIfNeededAsync(user, connection, settings, active, signature, cancellationToken);
        await SendTelegramIfNeededAsync(user, connection, settings, active, signature, cancellationToken);
    }

    // ── email channel ─────────────────────────────────────────────────────────

    private async Task SendEmailIfNeededAsync(
        UserEntity user, ProxmoxConnectionEntity connection, ProxmoxNodeAlertSettingsEntity settings,
        IReadOnlyList<ProxmoxNodeAlertStateEntity> active, string signature, CancellationToken cancellationToken)
    {
        // Reuse the host's update-email toggle as the "email me about this host"
        // switch — the same channel the update alerts already use.
        if (!connection.UpdateNotificationsEnabled) return;
        // Normalise null ⇒ "" so the very first evaluation with nothing active
        // doesn't fire a spurious "all clear".
        if (string.Equals(settings.LastNotifiedSignature ?? "", signature, StringComparison.Ordinal)) return;

        var allClear = active.Count == 0;
        var lines = active.Select(Describe).ToList();
        var message = EmailTemplates.ProxmoxNodeAlerts(user.Email, connection.Name, lines, allClear);

        try
        {
            await emailSender.SendAsync(message, cancellationToken);
            settings.LastNotifiedSignature = signature;
            settings.LastNotificationSentUtc = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send Proxmox node-alert email for connection {ConnectionId}", connection.Id);
        }
    }

    // ── Telegram channel ──────────────────────────────────────────────────────

    private async Task SendTelegramIfNeededAsync(
        UserEntity user, ProxmoxConnectionEntity connection, ProxmoxNodeAlertSettingsEntity settings,
        IReadOnlyList<ProxmoxNodeAlertStateEntity> active, string signature, CancellationToken cancellationToken)
    {
        if (!connection.TelegramNotificationsEnabled) return;
        var botToken = ResolveBotToken(user);
        if (!user.TelegramNotificationsEnabled
            || string.IsNullOrWhiteSpace(botToken)
            || string.IsNullOrWhiteSpace(user.TelegramChatId))
            return;

        if (string.Equals(settings.LastTelegramNotifiedSignature ?? "", signature, StringComparison.Ordinal)) return;

        var text = BuildTelegramText(connection, active);
        try
        {
            await telegramSender.SendMessageAsync(botToken!, user.TelegramChatId!, text, cancellationToken);
            settings.LastTelegramNotifiedSignature = signature;
            settings.LastNotificationSentUtc = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send Proxmox node-alert Telegram message for connection {ConnectionId}", connection.Id);
        }
    }

    // ── formatting (shared by both channels) ──────────────────────────────────

    /// <summary>One human-readable alert line carrying severity, metric, value,
    /// threshold and first-seen — the fields the roadmap requires an alert to
    /// carry.</summary>
    internal static string Describe(ProxmoxNodeAlertStateEntity s)
    {
        var severity = s.ActiveLevel == HealthLevel.Crit ? "CRITICAL" : "WARNING";
        var since = s.FirstSeenUtc is { } f
            ? $", since {f.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} UTC"
            : string.Empty;
        return $"{CategoryLabel(s.Category)} {severity} — {Detail(s)}{since}";
    }

    private static string Detail(ProxmoxNodeAlertStateEntity s)
    {
        var metric = s.Metric ?? CategoryLabel(s.Category);
        return s.Category switch
        {
            ProxmoxAlertCategory.Cpu or ProxmoxAlertCategory.Memory or ProxmoxAlertCategory.Storage =>
                $"{metric} at {Fmt(s.Value)}% (threshold {Fmt(s.Threshold)}%)",
            ProxmoxAlertCategory.Thermal =>
                $"{metric} at {Fmt(s.Value)}°C (threshold {Fmt(s.Threshold)}°C)",
            ProxmoxAlertCategory.Network =>
                $"{metric} +{Fmt(s.Value)} since last check (threshold {Fmt(s.Threshold)})",
            ProxmoxAlertCategory.Smart =>
                s.Value is null
                    ? $"{metric} SMART health is not PASSED"
                    : $"{metric} SSD wearout at {Fmt(s.Value)}% (threshold {Fmt(s.Threshold)}%)",
            _ => metric,
        };
    }

    private static string CategoryLabel(ProxmoxAlertCategory c) => c switch
    {
        ProxmoxAlertCategory.Cpu => "CPU",
        ProxmoxAlertCategory.Memory => "Memory",
        ProxmoxAlertCategory.Storage => "Storage",
        ProxmoxAlertCategory.Thermal => "Thermal",
        ProxmoxAlertCategory.Smart => "SMART",
        ProxmoxAlertCategory.Network => "Network",
        _ => c.ToString(),
    };

    private static string Fmt(double? v) =>
        v is null ? "?" : Math.Round(v.Value).ToString(CultureInfo.InvariantCulture);

    private static string BuildTelegramText(
        ProxmoxConnectionEntity connection, IReadOnlyList<ProxmoxNodeAlertStateEntity> active)
    {
        if (active.Count == 0)
            return $"""
                ✅ Node health recovered on Proxmox host {connection.Name}
                All previously alerting metrics are back to normal.
                """;

        var hasCrit = active.Any(a => a.ActiveLevel == HealthLevel.Crit);
        var icon = hasCrit ? "🔴" : "🟠";
        var body = string.Join("\n", active.Select(a => $"• {Describe(a)}"));
        return $"""
            {icon} Node health alert on Proxmox host {connection.Name}
            {body}
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
