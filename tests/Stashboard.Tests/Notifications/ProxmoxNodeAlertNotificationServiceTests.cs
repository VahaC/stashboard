using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Stashboard.Api.Data;
using Stashboard.Api.Notifications;
using Stashboard.Core.Abstractions;
using Stashboard.Core.Entities;
using Stashboard.Core.Enums;

namespace Stashboard.Tests.Notifications;

/// <summary>
/// V6.8.1 — unit tests for <see cref="ProxmoxNodeAlertNotificationService"/>.
/// Mocked email/Telegram senders; asserts the per-channel signature throttle
/// (no duplicate notification for an unchanged alert set), the recovered
/// "all clear", channel independence, and the alert-line content (severity +
/// value + threshold + first-seen).
/// </summary>
public class ProxmoxNodeAlertNotificationServiceTests
{
    private readonly Mock<IEmailSender> _emailMock = new();
    private readonly Mock<ITelegramSender> _telegramMock = new();
    private readonly Mock<IEncryptionService> _encryptionMock = new();
    private readonly ProxmoxNodeAlertNotificationService _service;

    public ProxmoxNodeAlertNotificationServiceTests()
    {
        _encryptionMock.Setup(e => e.Decrypt(It.IsAny<string>()))
            .Returns<string>(v => v.StartsWith("enc:") ? v[4..] : v);
        _service = new ProxmoxNodeAlertNotificationService(
            _emailMock.Object, _telegramMock.Object, _encryptionMock.Object,
            NullLogger<ProxmoxNodeAlertNotificationService>.Instance);
    }

    // ── email throttle ────────────────────────────────────────────────────────

    [Fact]
    public async Task FirstActiveAlert_SendsEmail_AndStampsSignature()
    {
        EmailMessage? captured = null;
        _emailMock.Setup(s => s.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()))
            .Callback<EmailMessage, CancellationToken>((m, _) => captured = m)
            .Returns(Task.CompletedTask);

        var settings = Settings();
        var active = new[] { State(ProxmoxAlertCategory.Cpu, HealthLevel.Crit, "CPU", 96, 95, DateTime.UtcNow) };
        const string sig = "Cpu:Crit:96";

        await _service.NotifyIfNeededAsync(User(), Connection(), settings, active, sig);

        _emailMock.Verify(s => s.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(sig, settings.LastNotifiedSignature);
        Assert.NotNull(settings.LastNotificationSentUtc);
        Assert.NotNull(captured);
        // The line carries severity, metric, value and threshold.
        Assert.Contains("CRITICAL", captured!.TextBody);
        Assert.Contains("96", captured.TextBody);
        Assert.Contains("95", captured.TextBody);
    }

    [Fact]
    public async Task SameSignatureAlreadyNotified_DoesNotResend()
    {
        const string sig = "Cpu:Crit:96";
        var settings = Settings(lastNotified: sig);
        var active = new[] { State(ProxmoxAlertCategory.Cpu, HealthLevel.Crit, "CPU", 96, 95, DateTime.UtcNow) };

        await _service.NotifyIfNeededAsync(User(), Connection(), settings, active, sig);

        VerifyNoEmail();
    }

    [Fact]
    public async Task ChangedSignature_SendsAgain()
    {
        _emailMock.Setup(s => s.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var settings = Settings(lastNotified: "Cpu:Warn:88");
        var active = new[] { State(ProxmoxAlertCategory.Cpu, HealthLevel.Crit, "CPU", 96, 95, DateTime.UtcNow) };
        const string sig = "Cpu:Crit:96";

        await _service.NotifyIfNeededAsync(User(), Connection(), settings, active, sig);

        _emailMock.Verify(s => s.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(sig, settings.LastNotifiedSignature);
    }

    [Fact]
    public async Task FirstRunWithNothingActive_DoesNotSendSpuriousAllClear()
    {
        var settings = Settings(lastNotified: null);

        await _service.NotifyIfNeededAsync(User(), Connection(), settings, [], signature: "");

        VerifyNoEmail();
    }

    [Fact]
    public async Task AllRecovered_SendsAllClear_AndClearsSignature()
    {
        EmailMessage? captured = null;
        _emailMock.Setup(s => s.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()))
            .Callback<EmailMessage, CancellationToken>((m, _) => captured = m)
            .Returns(Task.CompletedTask);

        var settings = Settings(lastNotified: "Cpu:Crit:96");

        await _service.NotifyIfNeededAsync(User(), Connection(), settings, [], signature: "");

        _emailMock.Verify(s => s.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal("", settings.LastNotifiedSignature);
        Assert.NotNull(captured);
        Assert.Contains("recovered", captured!.Subject, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EmailToggleOff_DoesNotEmail()
    {
        var settings = Settings();
        var active = new[] { State(ProxmoxAlertCategory.Cpu, HealthLevel.Crit, "CPU", 96, 95, DateTime.UtcNow) };

        await _service.NotifyIfNeededAsync(User(), Connection(emailEnabled: false), settings, active, "Cpu:Crit:96");

        VerifyNoEmail();
        Assert.Null(settings.LastNotifiedSignature);
    }

    [Fact]
    public async Task EmailThrows_LeavesSignatureUnset_ForRetry()
    {
        _emailMock.Setup(s => s.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("smtp down"));
        var settings = Settings();
        var active = new[] { State(ProxmoxAlertCategory.Cpu, HealthLevel.Crit, "CPU", 96, 95, DateTime.UtcNow) };

        await _service.NotifyIfNeededAsync(User(), Connection(), settings, active, "Cpu:Crit:96");

        Assert.Null(settings.LastNotifiedSignature);
    }

    // ── Telegram + channel independence ───────────────────────────────────────

    [Fact]
    public async Task TelegramEnabled_SendsAndStampsOwnSignature()
    {
        string? text = null;
        _telegramMock.Setup(t => t.SendMessageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, CancellationToken>((_, _, t2, _) => text = t2)
            .Returns(Task.CompletedTask);

        var settings = Settings();
        var active = new[] { State(ProxmoxAlertCategory.Thermal, HealthLevel.Warn, "Core 0", 82, 80, DateTime.UtcNow) };
        const string sig = "Thermal:Warn:82";

        await _service.NotifyIfNeededAsync(UserWithTelegram(), Connection(telegramEnabled: true), settings, active, sig);

        _telegramMock.Verify(t => t.SendMessageAsync("TOKEN", "12345", It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(sig, settings.LastTelegramNotifiedSignature);
        Assert.NotNull(text);
        Assert.Contains("Thermal", text!);
    }

    [Fact]
    public async Task EmailFails_TelegramStillSends_PerChannelKeys()
    {
        _emailMock.Setup(s => s.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("smtp down"));
        _telegramMock.Setup(t => t.SendMessageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var settings = Settings();
        var active = new[] { State(ProxmoxAlertCategory.Cpu, HealthLevel.Crit, "CPU", 96, 95, DateTime.UtcNow) };
        const string sig = "Cpu:Crit:96";

        await _service.NotifyIfNeededAsync(UserWithTelegram(), Connection(telegramEnabled: true), settings, active, sig);

        Assert.Null(settings.LastNotifiedSignature);              // email retries next tick
        Assert.Equal(sig, settings.LastTelegramNotifiedSignature); // telegram stamped
    }

    // ── factories ─────────────────────────────────────────────────────────────

    private void VerifyNoEmail() =>
        _emailMock.Verify(s => s.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()), Times.Never);

    private static UserEntity User(string email = "owner@example.com") => new()
    {
        Id = Guid.NewGuid(),
        Email = email,
        NormalizedEmail = email.ToUpperInvariant(),
        PasswordHash = "x",
        SecurityStamp = Guid.NewGuid().ToString("N"),
        CreatedUtc = DateTime.UtcNow,
    };

    private static UserEntity UserWithTelegram(string? botToken = "TOKEN", string? chatId = "12345")
    {
        var user = User();
        user.TelegramBotTokenEncrypted = botToken is null ? null : $"enc:{botToken}";
        user.TelegramChatId = chatId;
        user.TelegramNotificationsEnabled = true;
        return user;
    }

    private static ProxmoxConnectionEntity Connection(bool emailEnabled = true, bool telegramEnabled = false) => new()
    {
        Id = Guid.NewGuid(),
        UserId = Guid.NewGuid(),
        Name = "home",
        ApiBaseUrl = "https://pve.lan:8006",
        NodeName = "pve",
        ApiTokenId = "root@pam!stash",
        UpdateNotificationsEnabled = emailEnabled,
        TelegramNotificationsEnabled = telegramEnabled,
    };

    private static ProxmoxNodeAlertSettingsEntity Settings(string? lastNotified = null, string? lastTelegram = null) => new()
    {
        Id = Guid.NewGuid(),
        ProxmoxConnectionId = Guid.NewGuid(),
        Enabled = true,
        LastNotifiedSignature = lastNotified,
        LastTelegramNotifiedSignature = lastTelegram,
    };

    private static ProxmoxNodeAlertStateEntity State(
        ProxmoxAlertCategory category, HealthLevel level, string metric, double? value, double? threshold, DateTime? firstSeen) => new()
    {
        Id = Guid.NewGuid(),
        ProxmoxConnectionId = Guid.NewGuid(),
        Category = category,
        ActiveLevel = level,
        Metric = metric,
        Value = value,
        Threshold = threshold,
        FirstSeenUtc = firstSeen,
    };
}
