using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Stashboard.Api.Data;
using Stashboard.Api.Notifications;
using Stashboard.Core.Entities;
using Stashboard.Core.Enums;

namespace Stashboard.Tests.Notifications;

/// <summary>
/// Unit tests for <see cref="DockerUpdateNotificationService"/>. Both
/// <see cref="IEmailSender"/> and <see cref="ITelegramSender"/> are mocked
/// so we can assert on the captured payloads without real network traffic.
/// The two channels have independent throttle keys
/// (<c>LastNotifiedDigest</c> for email, <c>LastTelegramNotifiedDigest</c>
/// for Telegram) and per-channel toggles on both the watch and the user.
/// </summary>
public class DockerUpdateNotificationServiceTests
{
    private const string DigestOld = "sha256:aaaa00000000000000000000000000000000000000000000000000000000aaaa";
    private const string DigestNew = "sha256:bbbb11111111111111111111111111111111111111111111111111111111bbbb";

    private readonly Mock<IEmailSender> _emailMock = new();
    private readonly Mock<ITelegramSender> _telegramMock = new();
    private readonly Mock<Stashboard.Core.Abstractions.IEncryptionService> _encryptionMock = new();
    private readonly DockerUpdateNotificationService _service;

    public DockerUpdateNotificationServiceTests()
    {
        _encryptionMock.Setup(encryptionService => encryptionService.Decrypt(It.IsAny<string>()))
            .Returns<string>(value => value.StartsWith("enc:") ? value[4..] : value);
        _service = new DockerUpdateNotificationService(
            _emailMock.Object,
            _telegramMock.Object,
            _encryptionMock.Object,
            NullLogger<DockerUpdateNotificationService>.Instance);
    }

    // ── Email channel — positive path ────────────────────────────────────────

    [Fact]
    public async Task NotifyIfNeeded_FirstObservationOfNewDigest_SendsEmailAndStampsThrottleKey()
    {
        EmailMessage? captured = null;
        _emailMock.Setup(s => s.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()))
            .Callback<EmailMessage, CancellationToken>((m, _) => captured = m)
            .Returns(Task.CompletedTask);

        var user = User("alice@example.com");
        var service = Service("My Sonarr");
        var watch = Watch(latestDigest: DigestNew, currentDigest: DigestOld, lastNotifiedDigest: null);

        await _service.NotifyIfNeededAsync(user, service, watch);

        _emailMock.Verify(s => s.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.NotNull(captured);
        Assert.Equal("alice@example.com", captured!.To);
        Assert.Contains("My Sonarr", captured.Subject);
        Assert.Contains("My Sonarr", captured.TextBody);
        Assert.Contains("ghcr.io/owner/repo:v1", captured.TextBody);
        Assert.Contains("svc", captured.TextBody);
        Assert.Equal(DigestNew, watch.LastNotifiedDigest);
        Assert.NotNull(watch.LastNotificationSentUtc);
    }

    [Fact]
    public async Task NotifyIfNeeded_ShortensDigestsInEmail()
    {
        EmailMessage? captured = null;
        _emailMock.Setup(s => s.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()))
            .Callback<EmailMessage, CancellationToken>((m, _) => captured = m)
            .Returns(Task.CompletedTask);

        var user = User();
        var service = Service();
        var watch = Watch(latestDigest: DigestNew, currentDigest: DigestOld);

        await _service.NotifyIfNeededAsync(user, service, watch);

        Assert.NotNull(captured);
        Assert.Contains(DigestOld, captured!.TextBody);
        Assert.Contains(DigestNew, captured.TextBody);
    }

    // ── Email channel — suppression rules ────────────────────────────────────

    [Fact]
    public async Task NotifyIfNeeded_StatusNotUpdateAvailable_DoesNothing()
    {
        var user = User();
        var service = Service();
        var watch = Watch(latestDigest: DigestNew, status: DockerUpdateStatus.UpToDate);

        await _service.NotifyIfNeededAsync(user, service, watch);

        VerifyNoEmail();
        VerifyNoTelegram();
        Assert.Null(watch.LastNotifiedDigest);
    }

    [Fact]
    public async Task NotifyIfNeeded_EmailToggleDisabled_DoesNotEmail_ButTelegramStillEvaluated()
    {
        _telegramMock.Setup(t => t.SendMessageAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var user = UserWithTelegram();
        var service = Service();
        var watch = Watch(latestDigest: DigestNew, emailNotificationsEnabled: false, telegramNotificationsEnabled: true);

        await _service.NotifyIfNeededAsync(user, service, watch);

        VerifyNoEmail();
        _telegramMock.Verify(t => t.SendMessageAsync(It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task NotifyIfNeeded_LatestDigestMissing_DoesNothingForEitherChannel()
    {
        var user = UserWithTelegram();
        var service = Service();
        var watch = Watch(latestDigest: null, telegramNotificationsEnabled: true);

        await _service.NotifyIfNeededAsync(user, service, watch);

        VerifyNoEmail();
        VerifyNoTelegram();
    }

    [Fact]
    public async Task NotifyIfNeeded_SameEmailDigestAlreadyNotified_DoesNotResendEmail()
    {
        var user = User();
        var service = Service();
        var watch = Watch(latestDigest: DigestNew, lastNotifiedDigest: DigestNew);

        await _service.NotifyIfNeededAsync(user, service, watch);

        VerifyNoEmail();
    }

    [Fact]
    public async Task NotifyIfNeeded_EmailThrottleComparisonIsCaseInsensitive()
    {
        var user = User();
        var service = Service();
        var watch = Watch(latestDigest: "SHA256:abc", lastNotifiedDigest: "sha256:ABC");

        await _service.NotifyIfNeededAsync(user, service, watch);

        VerifyNoEmail();
    }

    [Fact]
    public async Task NotifyIfNeeded_DifferentEmailDigestFromPreviousNotification_SendsAgain()
    {
        _emailMock.Setup(s => s.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var user = User();
        var service = Service();
        var watch = Watch(latestDigest: DigestNew, lastNotifiedDigest: DigestOld);

        await _service.NotifyIfNeededAsync(user, service, watch);

        _emailMock.Verify(s => s.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(DigestNew, watch.LastNotifiedDigest);
    }

    [Fact]
    public async Task NotifyIfNeeded_SmtpThrows_LeavesEmailThrottleKeyUnset()
    {
        _emailMock.Setup(s => s.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("smtp down"));

        var user = User();
        var service = Service();
        var watch = Watch(latestDigest: DigestNew);

        await _service.NotifyIfNeededAsync(user, service, watch);

        Assert.Null(watch.LastNotifiedDigest);
        Assert.Null(watch.LastNotificationSentUtc);
    }

    // ── Telegram channel — positive path ─────────────────────────────────────

    [Fact]
    public async Task NotifyIfNeeded_TelegramEnabledOnBothSides_SendsAndStampsTelegramThrottle()
    {
        string? capturedToken = null, capturedChat = null, capturedText = null;
        _telegramMock.Setup(t => t.SendMessageAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, CancellationToken>((token, chat, text, _) =>
            {
                capturedToken = token; capturedChat = chat; capturedText = text;
            })
            .Returns(Task.CompletedTask);

        var user = UserWithTelegram(botToken: "TOKEN", chatId: "12345");
        var service = Service("Plex");
        var watch = Watch(latestDigest: DigestNew, currentDigest: DigestOld, telegramNotificationsEnabled: true);

        await _service.NotifyIfNeededAsync(user, service, watch);

        Assert.Equal("TOKEN", capturedToken);
        Assert.Equal("12345", capturedChat);
        Assert.NotNull(capturedText);
        Assert.Contains("Plex", capturedText!);
        Assert.Contains("ghcr.io/owner/repo:v1", capturedText);
        // Digests should be shortened — full 64-char hex should not appear in a Telegram message either.
        Assert.Contains(DigestNew, capturedText);

        Assert.Equal(DigestNew, watch.LastTelegramNotifiedDigest);
        Assert.NotNull(watch.LastNotificationSentUtc);
    }

    // ── Telegram channel — suppression rules ─────────────────────────────────

    [Fact]
    public async Task NotifyIfNeeded_TelegramToggleOffOnWatch_DoesNotSendTelegram()
    {
        var user = UserWithTelegram();
        var service = Service();
        var watch = Watch(latestDigest: DigestNew, telegramNotificationsEnabled: false);

        await _service.NotifyIfNeededAsync(user, service, watch);

        VerifyNoTelegram();
        Assert.Null(watch.LastTelegramNotifiedDigest);
    }

    [Fact]
    public async Task NotifyIfNeeded_TelegramToggleOnButUserKillSwitchOff_DoesNotSendTelegram()
    {
        var user = UserWithTelegram(notificationsEnabled: false);
        var service = Service();
        var watch = Watch(latestDigest: DigestNew, telegramNotificationsEnabled: true);

        await _service.NotifyIfNeededAsync(user, service, watch);

        VerifyNoTelegram();
        Assert.Null(watch.LastTelegramNotifiedDigest);
    }

    [Theory]
    [InlineData(null, "12345")]
    [InlineData("TOKEN", null)]
    [InlineData("", "12345")]
    [InlineData("TOKEN", "")]
    public async Task NotifyIfNeeded_TelegramConfigMissing_DoesNotSendTelegram(string? botToken, string? chatId)
    {
        var user = UserWithTelegram(botToken: botToken, chatId: chatId);
        var service = Service();
        var watch = Watch(latestDigest: DigestNew, telegramNotificationsEnabled: true);

        await _service.NotifyIfNeededAsync(user, service, watch);

        VerifyNoTelegram();
    }

    [Fact]
    public async Task NotifyIfNeeded_SameTelegramDigestAlreadyNotified_DoesNotResendTelegram()
    {
        var user = UserWithTelegram();
        var service = Service();
        var watch = Watch(
            latestDigest: DigestNew,
            telegramNotificationsEnabled: true,
            lastTelegramNotifiedDigest: DigestNew);

        await _service.NotifyIfNeededAsync(user, service, watch);

        VerifyNoTelegram();
    }

    [Fact]
    public async Task NotifyIfNeeded_TelegramThrows_LeavesTelegramThrottleKeyUnset()
    {
        _telegramMock.Setup(t => t.SendMessageAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("bot api down"));

        var user = UserWithTelegram();
        var service = Service();
        var watch = Watch(latestDigest: DigestNew, telegramNotificationsEnabled: true);

        await _service.NotifyIfNeededAsync(user, service, watch);

        Assert.Null(watch.LastTelegramNotifiedDigest);
    }

    // ── Channel independence ─────────────────────────────────────────────────

    [Fact]
    public async Task NotifyIfNeeded_BothChannelsEnabled_HitBothInOneCall()
    {
        _emailMock.Setup(s => s.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _telegramMock.Setup(t => t.SendMessageAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var user = UserWithTelegram();
        var service = Service();
        var watch = Watch(latestDigest: DigestNew, telegramNotificationsEnabled: true);

        await _service.NotifyIfNeededAsync(user, service, watch);

        _emailMock.Verify(s => s.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()), Times.Once);
        _telegramMock.Verify(t => t.SendMessageAsync(It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(DigestNew, watch.LastNotifiedDigest);
        Assert.Equal(DigestNew, watch.LastTelegramNotifiedDigest);
    }

    [Fact]
    public async Task NotifyIfNeeded_EmailFails_TelegramStillSendsAndStampsItsOwnKey()
    {
        _emailMock.Setup(s => s.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("smtp down"));
        _telegramMock.Setup(t => t.SendMessageAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var user = UserWithTelegram();
        var service = Service();
        var watch = Watch(latestDigest: DigestNew, telegramNotificationsEnabled: true);

        await _service.NotifyIfNeededAsync(user, service, watch);

        Assert.Null(watch.LastNotifiedDigest);            // email retry on next tick
        Assert.Equal(DigestNew, watch.LastTelegramNotifiedDigest);
    }

    [Fact]
    public async Task NotifyIfNeeded_SameLatestDigest_StampedIndependentlyPerChannel()
    {
        // Email already notified for DigestNew but Telegram still hasn't — only
        // Telegram should fire this tick.
        _telegramMock.Setup(t => t.SendMessageAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var user = UserWithTelegram();
        var service = Service();
        var watch = Watch(
            latestDigest: DigestNew,
            telegramNotificationsEnabled: true,
            lastNotifiedDigest: DigestNew,
            lastTelegramNotifiedDigest: null);

        await _service.NotifyIfNeededAsync(user, service, watch);

        VerifyNoEmail();
        _telegramMock.Verify(t => t.SendMessageAsync(It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(DigestNew, watch.LastTelegramNotifiedDigest);
    }

    // ── V2.3: GitHub release-notes link in notifications ─────────────────────

    [Fact]
    public async Task NotifyIfNeeded_LatestReleaseUrlSet_EmailIncludesReleaseNotesLine()
    {
        EmailMessage? captured = null;
        _emailMock.Setup(s => s.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()))
            .Callback<EmailMessage, CancellationToken>((m, _) => captured = m)
            .Returns(Task.CompletedTask);

        var user = User();
        var service = Service();
        var watch = Watch(latestDigest: DigestNew, currentDigest: DigestOld,
            latestReleaseUrl: "https://github.com/owner/repo/releases/tag/v2.0.0");

        await _service.NotifyIfNeededAsync(user, service, watch);

        Assert.NotNull(captured);
        Assert.Contains("Release notes: https://github.com/owner/repo/releases/tag/v2.0.0", captured!.TextBody);
        Assert.Contains("github.com/owner/repo/releases/tag/v2.0.0", captured.HtmlBody);
    }

    [Fact]
    public async Task NotifyIfNeeded_LatestReleaseUrlNull_EmailOmitsReleaseNotesLine()
    {
        EmailMessage? captured = null;
        _emailMock.Setup(s => s.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()))
            .Callback<EmailMessage, CancellationToken>((m, _) => captured = m)
            .Returns(Task.CompletedTask);

        await _service.NotifyIfNeededAsync(
            User(), Service(),
            Watch(latestDigest: DigestNew, currentDigest: DigestOld));

        Assert.NotNull(captured);
        Assert.DoesNotContain("Release notes:", captured!.TextBody);
    }

    [Fact]
    public async Task NotifyIfNeeded_LatestReleaseUrlSet_TelegramIncludesReleaseNotesLine()
    {
        string? captured = null;
        _telegramMock.Setup(t => t.SendMessageAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, CancellationToken>((_, _, text, _) => captured = text)
            .Returns(Task.CompletedTask);

        var watch = Watch(latestDigest: DigestNew, telegramNotificationsEnabled: true,
            latestReleaseUrl: "https://github.com/owner/repo/releases/tag/v2.0.0");

        await _service.NotifyIfNeededAsync(UserWithTelegram(), Service(), watch);

        Assert.NotNull(captured);
        Assert.Contains("Release notes: https://github.com/owner/repo/releases/tag/v2.0.0", captured!);
    }

    // ── factories ────────────────────────────────────────────────────────────

    private void VerifyNoEmail() =>
        _emailMock.Verify(s => s.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()), Times.Never);

    private void VerifyNoTelegram() =>
        _telegramMock.Verify(t => t.SendMessageAsync(It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);

    private static UserEntity User(string email = "owner@example.com") => new()
    {
        Id = Guid.NewGuid(),
        Email = email,
        NormalizedEmail = email.ToUpperInvariant(),
        PasswordHash = "x",
        SecurityStamp = Guid.NewGuid().ToString("N"),
        CreatedUtc = DateTime.UtcNow,
    };

    private static UserEntity UserWithTelegram(
        string? botToken = "TOKEN",
        string? chatId = "12345",
        bool notificationsEnabled = true)
    {
        var user = User();
        user.TelegramBotTokenEncrypted = botToken is null ? null : $"enc:{botToken}";
        user.TelegramChatId = chatId;
        user.TelegramNotificationsEnabled = notificationsEnabled;
        return user;
    }

    private static WebResourceEntity Service(string name = "Sonarr") => new()
    {
        Id = Guid.NewGuid(),
        UserId = Guid.NewGuid(),
        Name = name,
        MainUrl = "https://example.com",
    };

    private static DockerWatchEntity Watch(
        string? latestDigest,
        string? currentDigest = null,
        string? lastNotifiedDigest = null,
        string? lastTelegramNotifiedDigest = null,
        DockerUpdateStatus status = DockerUpdateStatus.UpdateAvailable,
        bool emailNotificationsEnabled = true,
        bool telegramNotificationsEnabled = false,
        string? latestReleaseUrl = null) => new()
    {
        Id = Guid.NewGuid(),
        WebResourceId = Guid.NewGuid(),
        UserId = Guid.NewGuid(),
        Label = "app",
        Enabled = true,
        ImageReference = "ghcr.io/owner/repo:v1",
        RegistryHost = "ghcr.io",
        Repository = "owner/repo",
        Tag = "v1",
        ContainerName = "svc",
        UpdateStatus = status,
        CurrentDigest = currentDigest,
        LatestDigest = latestDigest,
        LatestReleaseUrl = latestReleaseUrl,
        LastNotifiedDigest = lastNotifiedDigest,
        LastTelegramNotifiedDigest = lastTelegramNotifiedDigest,
        UpdateNotificationsEnabled = emailNotificationsEnabled,
        TelegramNotificationsEnabled = telegramNotificationsEnabled,
    };
}
