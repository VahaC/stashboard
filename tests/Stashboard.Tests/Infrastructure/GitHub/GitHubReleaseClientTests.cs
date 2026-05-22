using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;
using Stashboard.Core.Abstractions;
using Stashboard.Infrastructure.GitHub;

namespace Stashboard.Tests.Infrastructure.GitHub;

/// <summary>
/// Unit tests for <see cref="GitHubReleaseClient"/>. Mocks the HTTP layer
/// with the same pattern as <c>OciRegistryClientTests</c>. Covers happy
/// path, all failure modes the orchestrator branches on, body truncation,
/// and the Authorization header propagation.
/// </summary>
public class GitHubReleaseClientTests
{
    private const string SampleHtmlUrl = "https://github.com/owner/repo/releases/tag/v1.2.3";

    [Fact]
    public async Task GetReleaseByTag_PublicRepo_ReturnsHtmlUrlAndBody()
    {
        var harness = Harness.Build(_ => Reply.Json($$"""
            {"html_url":"{{SampleHtmlUrl}}","body":"## v1.2.3\n- shiny feature"}
        """));

        var result = await harness.Client.GetReleaseByTagAsync("owner", "repo", "v1.2.3", null);

        Assert.Equal(GitHubReleaseStatus.Ok, result.Status);
        Assert.Equal(SampleHtmlUrl, result.HtmlUrl);
        Assert.Contains("shiny feature", result.Body);
        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task GetReleaseByTag_BuildsCorrectUrlAndUrlEncodesSegments()
    {
        Uri? captured = null;
        var harness = Harness.Build(req =>
        {
            captured = req.RequestUri;
            return Reply.Json($$"""{"html_url":"{{SampleHtmlUrl}}","body":""}""");
        });

        await harness.Client.GetReleaseByTagAsync("home-assistant", "core", "2024.04.1", null);

        Assert.NotNull(captured);
        Assert.Equal("api.github.com", captured!.Host);
        Assert.Equal("/repos/home-assistant/core/releases/tags/2024.04.1", captured.AbsolutePath);
    }

    [Fact]
    public async Task GetReleaseByTag_PatPresent_SendsBearerAuthorization()
    {
        string? authHeader = null;
        var harness = Harness.Build(req =>
        {
            authHeader = req.Headers.Authorization?.ToString();
            return Reply.Json($$"""{"html_url":"{{SampleHtmlUrl}}","body":""}""");
        });

        await harness.Client.GetReleaseByTagAsync("owner", "repo", "v1", "ghp_secret-token");

        Assert.Equal("Bearer ghp_secret-token", authHeader);
    }

    [Fact]
    public async Task GetReleaseByTag_NoPat_DoesNotSendAuthorizationHeader()
    {
        HttpRequestMessage? captured = null;
        var harness = Harness.Build(req =>
        {
            captured = req;
            return Reply.Json($$"""{"html_url":"{{SampleHtmlUrl}}","body":""}""");
        });

        await harness.Client.GetReleaseByTagAsync("owner", "repo", "v1", null);

        Assert.NotNull(captured);
        Assert.Null(captured!.Headers.Authorization);
    }

    [Fact]
    public async Task GetReleaseByTag_AlwaysSetsApiVersionAndAcceptHeaders()
    {
        HttpRequestMessage? captured = null;
        var harness = Harness.Build(req =>
        {
            captured = req;
            return Reply.Json($$"""{"html_url":"{{SampleHtmlUrl}}","body":""}""");
        });

        await harness.Client.GetReleaseByTagAsync("owner", "repo", "v1", null);

        Assert.NotNull(captured);
        Assert.True(captured!.Headers.TryGetValues("X-GitHub-Api-Version", out var versions));
        Assert.Equal("2022-11-28", versions.FirstOrDefault());
        Assert.Contains("application/vnd.github+json",
            captured.Headers.Accept.Select(a => a.MediaType));
    }

    [Fact]
    public async Task GetReleaseByTag_NotFound_ReturnsNotFound()
    {
        var harness = Harness.Build(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var result = await harness.Client.GetReleaseByTagAsync("owner", "repo", "v9.9.9", null);

        Assert.Equal(GitHubReleaseStatus.NotFound, result.Status);
        Assert.False(result.IsSuccess);
        Assert.Null(result.HtmlUrl);
    }

    [Fact]
    public async Task GetReleaseByTag_Unauthorized_ReturnsUnauthorized()
    {
        var harness = Harness.Build(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));

        var result = await harness.Client.GetReleaseByTagAsync("owner", "repo", "v1", "bad-pat");

        Assert.Equal(GitHubReleaseStatus.Unauthorized, result.Status);
    }

    [Fact]
    public async Task GetReleaseByTag_ForbiddenWithRateLimitHeader_MapsToRateLimited()
    {
        var harness = Harness.Build(_ =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.Forbidden);
            resp.Headers.Add("X-RateLimit-Remaining", "0");
            return resp;
        });

        var result = await harness.Client.GetReleaseByTagAsync("owner", "repo", "v1", null);

        Assert.Equal(GitHubReleaseStatus.RateLimited, result.Status);
    }

    [Fact]
    public async Task GetReleaseByTag_PlainForbidden_MapsToUnauthorized()
    {
        var harness = Harness.Build(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));

        var result = await harness.Client.GetReleaseByTagAsync("owner", "repo", "v1", "scoped-too-narrow");

        Assert.Equal(GitHubReleaseStatus.Unauthorized, result.Status);
    }

    [Fact]
    public async Task GetReleaseByTag_429_MapsToRateLimited()
    {
        var harness = Harness.Build(_ => new HttpResponseMessage((HttpStatusCode)429));

        var result = await harness.Client.GetReleaseByTagAsync("owner", "repo", "v1", null);

        Assert.Equal(GitHubReleaseStatus.RateLimited, result.Status);
    }

    [Fact]
    public async Task GetReleaseByTag_HttpRequestException_ReturnsNetworkError()
    {
        var harness = Harness.Build((_, _) => throw new HttpRequestException("dns fail"));

        var result = await harness.Client.GetReleaseByTagAsync("owner", "repo", "v1", null);

        Assert.Equal(GitHubReleaseStatus.NetworkError, result.Status);
        Assert.Contains("dns fail", result.Error);
    }

    [Fact]
    public async Task GetReleaseByTag_Timeout_ReturnsNetworkError()
    {
        var harness = Harness.Build((_, _) => throw new TaskCanceledException());

        var result = await harness.Client.GetReleaseByTagAsync("owner", "repo", "v1", null);

        Assert.Equal(GitHubReleaseStatus.NetworkError, result.Status);
    }

    [Fact]
    public async Task GetReleaseByTag_MalformedJson_ReturnsInvalidResponse()
    {
        var harness = Harness.Build(_ => Reply.Json("not-json"));

        var result = await harness.Client.GetReleaseByTagAsync("owner", "repo", "v1", null);

        Assert.Equal(GitHubReleaseStatus.InvalidResponse, result.Status);
    }

    [Fact]
    public async Task GetReleaseByTag_MissingHtmlUrl_ReturnsInvalidResponse()
    {
        var harness = Harness.Build(_ => Reply.Json("""{"body":"hello"}"""));

        var result = await harness.Client.GetReleaseByTagAsync("owner", "repo", "v1", null);

        Assert.Equal(GitHubReleaseStatus.InvalidResponse, result.Status);
    }

    [Fact]
    public async Task GetReleaseByTag_LongBody_TruncatedToMaxLength()
    {
        var longBody = new string('x', GitHubReleaseClient.MaxBodyLength * 2);
        var harness = Harness.Build(_ => Reply.Json($$"""
            {"html_url":"{{SampleHtmlUrl}}","body":"{{longBody}}"}
        """));

        var result = await harness.Client.GetReleaseByTagAsync("owner", "repo", "v1", null);

        Assert.Equal(GitHubReleaseStatus.Ok, result.Status);
        Assert.Equal(GitHubReleaseClient.MaxBodyLength, result.Body!.Length);
        Assert.EndsWith("…", result.Body);
    }

    [Theory]
    [InlineData("", "repo", "v1")]
    [InlineData("owner", "", "v1")]
    [InlineData("owner", "repo", "")]
    public async Task GetReleaseByTag_BlankInputs_ReturnsInvalidResponseWithoutCall(
        string owner, string repo, string tag)
    {
        var calls = 0;
        var harness = Harness.Build(_ => { calls++; return new HttpResponseMessage(HttpStatusCode.OK); });

        var result = await harness.Client.GetReleaseByTagAsync(owner, repo, tag, null);

        Assert.Equal(GitHubReleaseStatus.InvalidResponse, result.Status);
        Assert.Equal(0, calls);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static class Reply
    {
        public static HttpResponseMessage Json(string body) =>
            new(HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            };
    }

    private sealed class Harness
    {
        public required GitHubReleaseClient Client { get; init; }

        public static Harness Build(Func<HttpRequestMessage, HttpResponseMessage> responder) =>
            Build((req, _) => Task.FromResult(responder(req)));

        public static Harness Build(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        {
            var handler = new Mock<HttpMessageHandler>();
            handler.Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>())
                .Returns((HttpRequestMessage req, CancellationToken ct) => responder(req, ct));

            var httpClient = new HttpClient(handler.Object);
            var factory = new Mock<IHttpClientFactory>();
            factory.Setup(f => f.CreateClient(GitHubReleaseClient.HttpClientName)).Returns(httpClient);

            var client = new GitHubReleaseClient(factory.Object, NullLogger<GitHubReleaseClient>.Instance);
            return new Harness { Client = client };
        }
    }
}
