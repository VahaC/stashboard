using Microsoft.AspNetCore.Http;
using Moq;
using Stashboard.Api.Notifications;

namespace Stashboard.Tests.Notifications;

public class AccountNotificationServiceTests
{
    private readonly Mock<IEmailSender> _sender = new();
    private readonly AccountNotificationService _sut;

    public AccountNotificationServiceTests()
    {
        _sender.Setup(s => s.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _sut = new AccountNotificationService(_sender.Object, StubSettings("https://app.example.com/"), new HttpContextAccessor());
    }

    private static IEmailSettingsService StubSettings(string appBaseUrl)
    {
        var m = new Mock<IEmailSettingsService>();
        m.Setup(s => s.GetResolvedAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResolvedEmailSettings(
                "LogOnly", "", 587, true, "", "", "no-reply@stashboard.local", "Stashboard", appBaseUrl));
        return m.Object;
    }

    [Fact]
    public async Task SendEmailConfirmationAsync_BuildsExpectedLinkAndDispatches()
    {
        EmailMessage? captured = null;
        _sender.Setup(s => s.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()))
            .Callback<EmailMessage, CancellationToken>((m, _) => captured = m)
            .Returns(Task.CompletedTask);

        await _sut.SendEmailConfirmationAsync("u@x.com", "tok+1=", CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Equal("u@x.com", captured!.To);
        Assert.Contains("https://app.example.com/confirm-email?", captured.HtmlBody);
        Assert.Contains("email=u%40x.com", captured.HtmlBody);
        Assert.Contains("token=tok%2B1%3D", captured.HtmlBody);
    }

    [Fact]
    public async Task SendPasswordResetAsync_BuildsExpectedLinkAndDispatches()
    {
        EmailMessage? captured = null;
        _sender.Setup(s => s.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()))
            .Callback<EmailMessage, CancellationToken>((m, _) => captured = m)
            .Returns(Task.CompletedTask);

        await _sut.SendPasswordResetAsync("u@x.com", "abc", CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Contains("https://app.example.com/reset-password?", captured!.HtmlBody);
        Assert.Contains("email=u%40x.com", captured.HtmlBody);
        Assert.Contains("token=abc", captured.HtmlBody);
    }

    [Fact]
    public async Task SendEmailChangeConfirmationAsync_BuildsExpectedLinkAndDispatches()
    {
        EmailMessage? captured = null;
        _sender.Setup(s => s.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()))
            .Callback<EmailMessage, CancellationToken>((m, _) => captured = m)
            .Returns(Task.CompletedTask);

        await _sut.SendEmailChangeConfirmationAsync("new@x.com", "ch-tok", CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Equal("new@x.com", captured!.To);
        Assert.Contains("https://app.example.com/confirm-email-change?", captured.HtmlBody);
        Assert.Contains("token=ch-tok", captured.HtmlBody);
        Assert.DoesNotContain("email=", captured.HtmlBody);
    }

    [Fact]
    public async Task TrailingSlashOnAppBaseUrl_DoesNotProduceDoubleSlash()
    {
        EmailMessage? captured = null;
        _sender.Setup(s => s.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()))
            .Callback<EmailMessage, CancellationToken>((m, _) => captured = m)
            .Returns(Task.CompletedTask);

        await _sut.SendEmailConfirmationAsync("u@x.com", "t", CancellationToken.None);

        Assert.NotNull(captured);
        Assert.DoesNotContain("//confirm-email", captured!.HtmlBody);
    }

    [Fact]
    public async Task RequestHost_WhenConfiguredBaseUrlIsLocalhost_UsesRequestDomain()
    {
        EmailMessage? captured = null;
        _sender.Setup(s => s.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()))
            .Callback<EmailMessage, CancellationToken>((m, _) => captured = m)
            .Returns(Task.CompletedTask);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Scheme = "https";
        httpContext.Request.Host = new HostString("api.stashboard.com");

        var sut = new AccountNotificationService(
            _sender.Object,
            StubSettings("http://localhost:5173"),
            new HttpContextAccessor { HttpContext = httpContext });

        await sut.SendEmailConfirmationAsync("u@x.com", "t", CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Contains("https://api.stashboard.com/confirm-email?", captured!.HtmlBody);
    }

    [Fact]
    public async Task ConfiguredBaseUrl_WhenNotLocalhost_RemainsUnchanged()
    {
        EmailMessage? captured = null;
        _sender.Setup(s => s.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()))
            .Callback<EmailMessage, CancellationToken>((m, _) => captured = m)
            .Returns(Task.CompletedTask);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Scheme = "https";
        httpContext.Request.Host = new HostString("api.stashboard.com");

        var sut = new AccountNotificationService(
            _sender.Object,
            StubSettings("https://app.custom-domain.com"),
            new HttpContextAccessor { HttpContext = httpContext });

        await sut.SendEmailConfirmationAsync("u@x.com", "t", CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Contains("https://app.custom-domain.com/confirm-email?", captured!.HtmlBody);
    }
}
