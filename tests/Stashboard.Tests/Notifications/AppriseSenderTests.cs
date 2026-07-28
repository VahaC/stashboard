using System.Net;
using System.Text.Json;
using Moq;
using Moq.Protected;
using Stashboard.Api.Notifications;

namespace Stashboard.Tests.Notifications;

/// <summary>
/// V10.0 — the Apprise sender POSTs the expected JSON payload (joined URLs + title +
/// body + severity type) to the configured <c>/notify</c> endpoint, and a non-success
/// HTTP status surfaces as an exception so callers leave their throttle key unstamped.
/// </summary>
public class AppriseSenderTests
{
    private static (AppriseSender sender, List<HttpRequestMessage> requests, List<string> bodies) Build(
        HttpStatusCode status = HttpStatusCode.OK)
    {
        var requests = new List<HttpRequestMessage>();
        var bodies = new List<string>();
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .Returns(async (HttpRequestMessage req, CancellationToken _) =>
            {
                requests.Add(req);
                bodies.Add(req.Content is null ? "" : await req.Content.ReadAsStringAsync());
                return new HttpResponseMessage(status);
            });
        return (new AppriseSender(new HttpClient(handler.Object)), requests, bodies);
    }

    [Fact]
    public async Task SendAsync_PostsExpectedPayload_ToNotifyEndpoint()
    {
        var (sender, requests, bodies) = Build();

        await sender.SendAsync(
            "http://apprise:8000",
            ["discord://id/token", "ntfy://ntfy.sh/topic"],
            "Service unavailable: API",
            "🔴 Service unavailable: API",
            AppriseNotificationType.Failure);

        var req = Assert.Single(requests);
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal("http://apprise:8000/notify", req.RequestUri!.ToString());

        using var doc = JsonDocument.Parse(bodies[0]);
        var root = doc.RootElement;
        Assert.Equal("discord://id/token,ntfy://ntfy.sh/topic", root.GetProperty("urls").GetString());
        Assert.Equal("Service unavailable: API", root.GetProperty("title").GetString());
        Assert.Contains("Service unavailable: API", root.GetProperty("body").GetString());
        Assert.Equal("failure", root.GetProperty("type").GetString());
    }

    [Fact]
    public async Task SendAsync_MapsSeverityToAppriseType()
    {
        var (sender, _, bodies) = Build();

        await sender.SendAsync("http://apprise:8000", ["ntfy://ntfy.sh/t"], "t", "b", AppriseNotificationType.Info);

        using var doc = JsonDocument.Parse(bodies[0]);
        Assert.Equal("info", doc.RootElement.GetProperty("type").GetString());
    }

    [Fact]
    public async Task SendAsync_NonSuccessStatus_Throws()
    {
        var (sender, _, _) = Build(HttpStatusCode.FailedDependency); // 424 — a target rejected

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            sender.SendAsync("http://apprise:8000", ["ntfy://ntfy.sh/t"], "t", "b", AppriseNotificationType.Info));
    }

    [Theory]
    [InlineData("http://apprise:8000", "http://apprise:8000/notify")]
    [InlineData("http://apprise:8000/", "http://apprise:8000/notify")]
    [InlineData("http://apprise:8000/notify", "http://apprise:8000/notify")]
    [InlineData("http://apprise:8000/notify/", "http://apprise:8000/notify")]
    public void NotifyEndpoint_NormalisesBaseUrl(string baseUrl, string expected) =>
        Assert.Equal(expected, AppriseSender.NotifyEndpoint(baseUrl));
}
