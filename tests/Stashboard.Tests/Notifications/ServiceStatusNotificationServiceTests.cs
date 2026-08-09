using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Stashboard.Api.Data;
using Stashboard.Api.Notifications;
using Stashboard.Core.Abstractions;
using Stashboard.Core.Entities;
using Stashboard.Core.Enums;

namespace Stashboard.Tests.Notifications;

public class ServiceStatusNotificationServiceTests
{
    private readonly Mock<ITelegramSender> _telegramSender = new();
    private readonly Mock<IAppriseSender> _appriseSender = new();
    private readonly Mock<IAppriseSettingsService> _appriseSettings = new();
    private readonly Mock<IEncryptionService> _encryption = new();
    private readonly ServiceStatusNotificationService _sut;

    public ServiceStatusNotificationServiceTests()
    {
        _encryption.Setup(encryptionService => encryptionService.Decrypt(It.IsAny<string>()))
            .Returns<string>(value => value.StartsWith("enc:") ? value[4..] : value);
        // Apprise unconfigured by default; ConfigureApprise() opts a test into it.
        _appriseSettings.Setup(s => s.GetResolvedAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResolvedAppriseSettings(false, "", []));
        _sut = new ServiceStatusNotificationService(
            _telegramSender.Object, _appriseSender.Object, _appriseSettings.Object,
            _encryption.Object, NullLogger<ServiceStatusNotificationService>.Instance);
    }

    private void ConfigureApprise() =>
        _appriseSettings.Setup(s => s.GetResolvedAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResolvedAppriseSettings(true, "http://apprise:8000", ["ntfy://ntfy.sh/topic"]));

    [Fact]
    public async Task NotifyIfNeededAsync_WhenMainTransitionsToDownAndAppriseConfigured_SendsAppriseEvenWithoutTelegram()
    {
        ConfigureApprise();
        var user = new UserEntity(); // no Telegram configured
        var service = new WebResourceEntity
        {
            Id = Guid.NewGuid(),
            Name = "API",
            MainUrl = "https://api.example.com",
            MainUrlHealthCheckEnabled = true,
            CurrentStatus = ServiceStatus.Down,
            LastError = "HTTP 503",
        };

        await _sut.NotifyIfNeededAsync(user, service, ServiceStatus.Up, ServiceStatus.Unknown);

        _appriseSender.Verify(a => a.SendAsync(
            "http://apprise:8000",
            It.Is<IReadOnlyList<string>>(u => u.Count == 1 && u[0] == "ntfy://ntfy.sh/topic"),
            It.Is<string>(t => t.Contains("API")),
            It.Is<string>(b => b.Contains("Main URL")),
            AppriseNotificationType.Failure,
            It.IsAny<CancellationToken>()), Times.Once);
        _telegramSender.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task NotifyIfNeededAsync_WhenServiceOfflineNotificationsDisabled_SendsNoApprise()
    {
        ConfigureApprise();
        var service = new WebResourceEntity
        {
            Name = "API",
            MainUrl = "https://api.example.com",
            MainUrlHealthCheckEnabled = true,
            OfflineNotificationsEnabled = false,
            CurrentStatus = ServiceStatus.Down,
        };

        await _sut.NotifyIfNeededAsync(new UserEntity(), service, ServiceStatus.Up, ServiceStatus.Unknown);

        _appriseSender.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task NotifyIfNeededAsync_WhenMainStatusTransitionsToDown_SendsTelegramMessage()
    {
        var user = new UserEntity
        {
            TelegramBotTokenEncrypted = "enc:bot-token",
            TelegramChatId = "123456",
            TelegramNotificationsEnabled = true,
        };
        var service = new WebResourceEntity
        {
            Id = Guid.NewGuid(),
            Name = "API",
            MainUrl = "https://api.example.com",
            CurrentStatus = ServiceStatus.Down,
            LastError = "HTTP 503",
        };

        await _sut.NotifyIfNeededAsync(user, service, ServiceStatus.Up, ServiceStatus.Unknown);

        _telegramSender.Verify(s => s.SendMessageAsync(
            "bot-token",
            "123456",
            It.Is<string>(message => message.Contains("API") && message.Contains("Main URL")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task NotifyIfNeededAsync_WhenNotificationsDisabled_DoesNothing()
    {
        var user = new UserEntity();
        var service = new WebResourceEntity
        {
            Name = "API",
            MainUrl = "https://api.example.com",
            CurrentStatus = ServiceStatus.Down,
        };

        await _sut.NotifyIfNeededAsync(user, service, ServiceStatus.Up, ServiceStatus.Unknown);

        _telegramSender.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task NotifyIfNeededAsync_WhenAdditionalStatusTransitionsToDown_SendsTelegramMessage()
    {
        var user = new UserEntity
        {
            TelegramBotTokenEncrypted = "enc:bot-token",
            TelegramChatId = "123456",
            TelegramNotificationsEnabled = true,
        };
        var service = new WebResourceEntity
        {
            Id = Guid.NewGuid(),
            Name = "API",
            MainUrl = "https://api.example.com",
            AdditionalUrl = "https://mirror.example.com",
            CurrentStatus = ServiceStatus.Up,
            AdditionalUrlStatus = ServiceStatus.Down,
            AdditionalUrlLastError = "HTTP 503",
        };

        await _sut.NotifyIfNeededAsync(user, service, ServiceStatus.Up, ServiceStatus.Up);

        _telegramSender.Verify(s => s.SendMessageAsync(
            "bot-token",
            "123456",
            It.Is<string>(message => message.Contains("Additional URL") && message.Contains("mirror.example.com")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task NotifyIfNeededAsync_WhenServiceOfflineNotificationsDisabled_DoesNothing()
    {
        var user = new UserEntity
        {
            TelegramBotTokenEncrypted = "enc:bot-token",
            TelegramChatId = "123456",
            TelegramNotificationsEnabled = true,
        };
        var service = new WebResourceEntity
        {
            Name = "API",
            MainUrl = "https://api.example.com",
            MainUrlHealthCheckEnabled = true,
            OfflineNotificationsEnabled = false,
            CurrentStatus = ServiceStatus.Down,
        };

        await _sut.NotifyIfNeededAsync(user, service, ServiceStatus.Up, ServiceStatus.Unknown);

        _telegramSender.VerifyNoOtherCalls();
    }
}


