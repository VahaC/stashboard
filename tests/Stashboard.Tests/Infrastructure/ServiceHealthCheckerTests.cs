using System.Net;
using System.Security.Authentication;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;
using Stashboard.Core.Abstractions;
using Stashboard.Core.Entities;
using Stashboard.Core.Enums;
using Stashboard.Infrastructure.Services;

namespace Stashboard.Tests.Infrastructure;

/// <summary>
/// Unit tests for <see cref="ServiceHealthChecker"/> using a mock <see cref="HttpMessageHandler"/>.
/// These tests verify the real implementation logic — no mocking of the interface itself.
/// </summary>
public class ServiceHealthCheckerTests
{
    private static HttpClient BuildClient(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responseFactory)
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Returns((HttpRequestMessage req, CancellationToken cancellationToken) => responseFactory(req, cancellationToken));
        return new HttpClient(handler.Object);
    }

    private static HttpClient BuildClient(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        => BuildClient((req, _) => Task.FromResult(responseFactory(req)));

    private static ServiceHealthChecker BuildChecker(HttpStatusCode responseCode)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient("healthcheck"))
            .Returns(BuildClient(_ => new HttpResponseMessage(responseCode)));
        factory.Setup(f => f.CreateClient("healthcheck-insecure"))
            .Returns(BuildClient(_ => new HttpResponseMessage(responseCode)));

        return new ServiceHealthChecker(factory.Object, NullLogger<ServiceHealthChecker>.Instance);
    }

    private static ServiceHealthChecker BuildCheckerPerClient(HttpClient regularClient, HttpClient insecureClient)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient("healthcheck")).Returns(regularClient);
        factory.Setup(f => f.CreateClient("healthcheck-insecure")).Returns(insecureClient);
        return new ServiceHealthChecker(factory.Object, NullLogger<ServiceHealthChecker>.Instance);
    }

    private static ServiceHealthChecker BuildCheckerPerUrl(Dictionary<string, HttpStatusCode> urlMap)
    {
        var client = BuildClient(req =>
            new HttpResponseMessage(urlMap.TryGetValue(req.RequestUri!.ToString(), out var code) ? code : HttpStatusCode.OK));
        return BuildCheckerPerClient(client, client);
    }

    // ── CheckUrlAsync ────────────────────────────────────────────────────────

    [Fact]
    public async Task CheckUrlAsync_Returns_Up_For_200()
    {
        var checker = BuildChecker(HttpStatusCode.OK);
        var result = await checker.CheckUrlAsync("https://example.com", HealthCheckMethod.Get, null);
        Assert.Equal(ServiceStatus.Up, result.Status);
        Assert.NotNull(result.ResponseTimeMs);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task CheckUrlAsync_Returns_Down_For_500()
    {
        var checker = BuildChecker(HttpStatusCode.InternalServerError);
        var result = await checker.CheckUrlAsync("https://example.com", HealthCheckMethod.Get, null);
        Assert.Equal(ServiceStatus.Down, result.Status);
        Assert.Equal("HTTP 500", result.Error);
    }

    [Fact]
    public async Task CheckUrlAsync_Returns_Down_For_InvalidUrl()
    {
        var checker = BuildChecker(HttpStatusCode.OK);
        var result = await checker.CheckUrlAsync("not-a-url", HealthCheckMethod.Get, null);
        Assert.Equal(ServiceStatus.Down, result.Status);
        Assert.Equal("Invalid URL", result.Error);
    }

    [Theory]
    [InlineData("200-299", 200, true)]
    [InlineData("200-299", 301, false)]
    [InlineData("204", 204, true)]
    [InlineData("204", 200, false)]
    [InlineData("200,204,301-302", 301, true)]
    [InlineData("200,204,301-302", 400, false)]
    public async Task CheckUrlAsync_RespectsExpectedStatusRange(string range, int httpCode, bool expectedUp)
    {
        var checker = BuildChecker((HttpStatusCode)httpCode);
        var result = await checker.CheckUrlAsync("https://example.com", HealthCheckMethod.Get, range);
        Assert.Equal(expectedUp ? ServiceStatus.Up : ServiceStatus.Down, result.Status);
    }

    [Fact]
    public async Task CheckUrlAsync_WhenCertificateFailsButInsecureRetrySucceeds_ReturnsNeedsAttention()
    {
        var regularClient = BuildClient(_ => throw new HttpRequestException("SSL certificate error", new AuthenticationException("certificate")));
        var insecureClient = BuildClient(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var checker = BuildCheckerPerClient(regularClient, insecureClient);

        var result = await checker.CheckUrlAsync("https://192.168.1.197:8006", HealthCheckMethod.Get, null);

        Assert.Equal(ServiceStatus.NeedsAttention, result.Status);
        Assert.Equal("Certificate validation was ignored.", result.Error);
    }

    [Fact]
    public async Task CheckUrlAsync_WhenRequestThrows_ReturnsInnermostErrorMessage()
    {
        var regularClient = BuildClient(_ => throw new HttpRequestException(
            "An error occurred while sending the request.",
            new InvalidOperationException("Connection refused")));
        var checker = BuildCheckerPerClient(regularClient, regularClient);

        var result = await checker.CheckUrlAsync("https://example.com", HealthCheckMethod.Get, null);

        Assert.Equal(ServiceStatus.Down, result.Status);
        Assert.Equal("Connection refused", result.Error);
    }

    [Fact]
    public async Task CheckUrlAsync_WhenHeadThrowsButGetSucceeds_ReturnsUp()
    {
        var regularClient = BuildClient((req, _) => req.Method == HttpMethod.Head
            ? Task.FromException<HttpResponseMessage>(new HttpRequestException("Request failed"))
            : Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        var checker = BuildCheckerPerClient(regularClient, regularClient);

        var result = await checker.CheckUrlAsync("https://example.com", HealthCheckMethod.Head, null);

        Assert.Equal(ServiceStatus.Up, result.Status);
        Assert.Null(result.Error);
    }

    [Theory]
    [InlineData(HttpStatusCode.MethodNotAllowed)]
    [InlineData(HttpStatusCode.NotImplemented)]
    public async Task CheckUrlAsync_WhenHeadUnsupported_FallsBackToGet(HttpStatusCode statusCode)
    {
        var regularClient = BuildClient(req => req.Method == HttpMethod.Head
            ? new HttpResponseMessage(statusCode)
            : new HttpResponseMessage(HttpStatusCode.OK));
        var checker = BuildCheckerPerClient(regularClient, regularClient);

        var result = await checker.CheckUrlAsync("https://example.com", HealthCheckMethod.Head, null);

        Assert.Equal(ServiceStatus.Up, result.Status);
        Assert.Null(result.Error);
    }

    // ── CheckAsync — no AdditionalUrl ────────────────────────────────────────

    [Fact]
    public async Task CheckAsync_NoAdditionalUrl_ReturnsMainResultOnly()
    {
        var checker = BuildChecker(HttpStatusCode.OK);
        var entity = new WebResourceEntity { MainUrl = "https://example.com", HealthCheckMethod = HealthCheckMethod.Get };

        var result = await checker.CheckAsync(entity);

        Assert.Equal(ServiceStatus.Up, result.Main.Status);
        Assert.Null(result.Additional);
    }

    [Fact]
    public async Task CheckAsync_MainHealthCheckDisabled_ReturnsUnknownWithoutSendingRequest()
    {
        var checker = BuildChecker(HttpStatusCode.OK);
        var entity = new WebResourceEntity
        {
            MainUrl = "https://example.com",
            MainUrlHealthCheckEnabled = false,
            HealthCheckMethod = HealthCheckMethod.Get,
        };

        var result = await checker.CheckAsync(entity);

        Assert.Equal(ServiceStatus.Unknown, result.Main.Status);
        Assert.Null(result.Main.ResponseTimeMs);
        Assert.Null(result.Main.Error);
    }

    [Fact]
    public async Task CheckAsync_NoAdditionalUrl_UsesHealthCheckUrl_WhenSet()
    {
        var checker = BuildCheckerPerUrl(new Dictionary<string, HttpStatusCode>
        {
            ["https://health.example.com/"] = HttpStatusCode.OK,
            ["https://main.example.com/"] = HttpStatusCode.InternalServerError,
        });
        var entity = new WebResourceEntity
        {
            MainUrl = "https://main.example.com",
            HealthCheckUrl = "https://health.example.com",
            HealthCheckMethod = HealthCheckMethod.Get,
        };

        var result = await checker.CheckAsync(entity);

        Assert.Equal(ServiceStatus.Up, result.Main.Status);
        Assert.Null(result.Additional);
    }

    // ── CheckAsync — with AdditionalUrl ──────────────────────────────────────

    [Fact]
    public async Task CheckAsync_WithAdditionalUrl_ReturnsBothResults()
    {
        var checker = BuildCheckerPerUrl(new Dictionary<string, HttpStatusCode>
        {
            ["https://main.example.com/"] = HttpStatusCode.OK,
            ["https://additional.example.com/"] = HttpStatusCode.ServiceUnavailable,
        });
        var entity = new WebResourceEntity
        {
            MainUrl = "https://main.example.com",
            AdditionalUrl = "https://additional.example.com",
            HealthCheckMethod = HealthCheckMethod.Get,
        };

        var result = await checker.CheckAsync(entity);

        Assert.Equal(ServiceStatus.Up, result.Main.Status);
        Assert.NotNull(result.Additional);
        Assert.Equal(ServiceStatus.Down, result.Additional!.Status);
        Assert.Equal("HTTP 503", result.Additional.Error);
    }

    [Fact]
    public async Task CheckAsync_WithDisabledAdditionalUrlHealthCheck_ReturnsUnknownAdditionalStatus()
    {
        var checker = BuildChecker(HttpStatusCode.OK);
        var entity = new WebResourceEntity
        {
            MainUrl = "https://main.example.com",
            AdditionalUrl = "https://mirror.example.com",
            AdditionalUrlHealthCheckEnabled = false,
            HealthCheckMethod = HealthCheckMethod.Get,
        };

        var result = await checker.CheckAsync(entity);

        Assert.NotNull(result.Additional);
        Assert.Equal(ServiceStatus.Unknown, result.Additional!.Status);
        Assert.Null(result.Additional.ResponseTimeMs);
        Assert.Null(result.Additional.Error);
    }

    [Fact]
    public async Task CheckAsync_WithAdditionalUrl_BothUp_ReturnsBothUp()
    {
        var checker = BuildChecker(HttpStatusCode.OK);
        var entity = new WebResourceEntity
        {
            MainUrl = "https://main.example.com",
            AdditionalUrl = "https://mirror.example.com",
            HealthCheckMethod = HealthCheckMethod.Get,
        };

        var result = await checker.CheckAsync(entity);

        Assert.Equal(ServiceStatus.Up, result.Main.Status);
        Assert.NotNull(result.Additional);
        Assert.Equal(ServiceStatus.Up, result.Additional!.Status);
        Assert.Null(result.Additional.Error);
    }

    [Fact]
    public async Task CheckAsync_WithAdditionalUrl_DoesNotMutateEntity()
    {
        var checker = BuildCheckerPerUrl(new Dictionary<string, HttpStatusCode>
        {
            ["https://main.example.com/"] = HttpStatusCode.OK,
            ["https://additional.example.com/"] = HttpStatusCode.ServiceUnavailable,
        });
        var entity = new WebResourceEntity
        {
            MainUrl = "https://main.example.com",
            AdditionalUrl = "https://additional.example.com",
            HealthCheckMethod = HealthCheckMethod.Get,
            AdditionalUrlStatus = ServiceStatus.Unknown,
        };

        await checker.CheckAsync(entity);

        // ServiceHealthChecker must not write back to the entity — that is the caller's responsibility.
        Assert.Equal(ServiceStatus.Unknown, entity.AdditionalUrlStatus);
    }
}
