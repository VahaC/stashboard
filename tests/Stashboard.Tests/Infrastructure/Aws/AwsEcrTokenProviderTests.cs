using System.Net;
using System.Text;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;
using Stashboard.Core.Abstractions;
using Stashboard.Infrastructure.Aws;

namespace Stashboard.Tests.Infrastructure.Aws;

/// <summary>
/// Unit tests for <see cref="AwsEcrTokenProvider"/>. Mocks the HTTP layer the
/// same way <c>OciRegistryClientTests</c> does. The SigV4 signer is exercised
/// implicitly — we don't reproduce the canonical request bytes here, just
/// assert the produced Authorization header looks right and that the
/// response parsing + cache TTL math match expectations.
/// </summary>
public class AwsEcrTokenProviderTests
{
    private const string AccessKeyId = "AKIAEXAMPLE0000000000";
    private const string SecretAccessKey = "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY";
    private const string Region = "eu-central-1";
    private static readonly DateTime AnchorUtc = new(2026, 5, 17, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task GetAuthorizationToken_HappyPath_DecodesAwsUsernameAndPassword()
    {
        var (provider, _) = BuildProvider(_ => Reply.AuthData("AWS:supersecret-password", "https://x.dkr.ecr.eu-central-1.amazonaws.com", AnchorUtc.AddHours(12)));

        var result = await provider.GetAuthorizationTokenAsync(AccessKeyId, SecretAccessKey, Region);

        Assert.Equal(AwsEcrTokenStatus.Ok, result.Status);
        Assert.NotNull(result.Credentials);
        Assert.Equal("AWS", result.Credentials!.Username);
        Assert.Equal("supersecret-password", result.Credentials.Password);
        Assert.NotNull(result.ProxyEndpoint);
    }

    [Fact]
    public async Task GetAuthorizationToken_PostsToCorrectHostAndSetsRequiredHeaders()
    {
        HttpRequestMessage? captured = null;
        var (provider, _) = BuildProvider(req => { captured = req; return Reply.AuthData("AWS:p", null, AnchorUtc.AddHours(12)); });

        await provider.GetAuthorizationTokenAsync(AccessKeyId, SecretAccessKey, Region);

        Assert.NotNull(captured);
        Assert.Equal(HttpMethod.Post, captured!.Method);
        Assert.Equal($"ecr.{Region}.amazonaws.com", captured.RequestUri!.Host);
        // SigV4 puts the marker and target into headers, not the body.
        Assert.True(captured.Headers.TryGetValues("X-Amz-Target", out var targets));
        Assert.Equal("AmazonEC2ContainerRegistry_V20150921.GetAuthorizationToken", targets.FirstOrDefault());
        Assert.True(captured.Headers.TryGetValues("X-Amz-Date", out _));
        Assert.True(captured.Headers.TryGetValues("Authorization", out var auth));
        Assert.StartsWith($"AWS4-HMAC-SHA256 Credential={AccessKeyId}/", auth.First());
        Assert.Contains($"/{Region}/ecr/aws4_request", auth.First());
    }

    [Fact]
    public async Task GetAuthorizationToken_ResultIsCachedUntilCloseToExpiry()
    {
        var calls = 0;
        var (provider, _) = BuildProvider(_ =>
        {
            calls++;
            return Reply.AuthData("AWS:cached-token", null, AnchorUtc.AddHours(12));
        });

        var first = await provider.GetAuthorizationTokenAsync(AccessKeyId, SecretAccessKey, Region);
        var second = await provider.GetAuthorizationTokenAsync(AccessKeyId, SecretAccessKey, Region);

        Assert.True(first.IsSuccess && second.IsSuccess);
        Assert.Equal("cached-token", first.Credentials!.Password);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task GetAuthorizationToken_DifferentAccessKey_DoesNotShareCache()
    {
        var calls = 0;
        var (provider, _) = BuildProvider(_ =>
        {
            calls++;
            return Reply.AuthData($"AWS:token-{calls}", null, AnchorUtc.AddHours(12));
        });

        await provider.GetAuthorizationTokenAsync(AccessKeyId, SecretAccessKey, Region);
        await provider.GetAuthorizationTokenAsync("AKIASECONDKEY00000000", SecretAccessKey, Region);

        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task GetAuthorizationToken_Forbidden_MapsToUnauthorized()
    {
        var (provider, _) = BuildProvider(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("{\"__type\":\"AccessDenied\"}"),
        });

        var result = await provider.GetAuthorizationTokenAsync(AccessKeyId, SecretAccessKey, Region);

        Assert.Equal(AwsEcrTokenStatus.Unauthorized, result.Status);
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task GetAuthorizationToken_BadRequestWithEcrErrorBody_MapsToUnauthorized()
    {
        var (provider, _) = BuildProvider(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("{\"__type\":\"UnrecognizedClientException\",\"message\":\"bad key\"}"),
        });

        var result = await provider.GetAuthorizationTokenAsync(AccessKeyId, SecretAccessKey, Region);

        Assert.Equal(AwsEcrTokenStatus.Unauthorized, result.Status);
        Assert.Contains("bad key", result.Error);
    }

    [Fact]
    public async Task GetAuthorizationToken_NetworkError_ReturnsNetworkError()
    {
        var (provider, _) = BuildProvider((_, _) => throw new HttpRequestException("dns fail"));

        var result = await provider.GetAuthorizationTokenAsync(AccessKeyId, SecretAccessKey, Region);

        Assert.Equal(AwsEcrTokenStatus.NetworkError, result.Status);
        Assert.Contains("dns fail", result.Error);
    }

    [Fact]
    public async Task GetAuthorizationToken_Timeout_ReturnsNetworkError()
    {
        var (provider, _) = BuildProvider((_, _) => throw new TaskCanceledException());

        var result = await provider.GetAuthorizationTokenAsync(AccessKeyId, SecretAccessKey, Region);

        Assert.Equal(AwsEcrTokenStatus.NetworkError, result.Status);
    }

    [Fact]
    public async Task GetAuthorizationToken_MalformedJson_ReturnsInvalidResponse()
    {
        var (provider, _) = BuildProvider(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("not-json"),
        });

        var result = await provider.GetAuthorizationTokenAsync(AccessKeyId, SecretAccessKey, Region);

        Assert.Equal(AwsEcrTokenStatus.InvalidResponse, result.Status);
    }

    [Theory]
    [InlineData("", SecretAccessKey, Region)]
    [InlineData(AccessKeyId, "", Region)]
    [InlineData(AccessKeyId, SecretAccessKey, "")]
    public async Task GetAuthorizationToken_BlankInputs_ReturnsInvalidResponseWithoutCall(
        string keyId, string secret, string region)
    {
        var calls = 0;
        var (provider, _) = BuildProvider(_ => { calls++; return new HttpResponseMessage(HttpStatusCode.OK); });

        var result = await provider.GetAuthorizationTokenAsync(keyId, secret, region);

        Assert.Equal(AwsEcrTokenStatus.InvalidResponse, result.Status);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void ParseTokenResponse_AcceptsUnixTimestampExpiry()
    {
        var unix = new DateTimeOffset(2026, 5, 17, 23, 59, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        var body = "{\"authorizationData\":[{\"authorizationToken\":\""
                   + Convert.ToBase64String(Encoding.UTF8.GetBytes("AWS:p")) + "\","
                   + $"\"expiresAt\":{unix}}}]}}";

        var result = AwsEcrTokenProvider.ParseTokenResponse(body);

        Assert.Equal(AwsEcrTokenStatus.Ok, result.Status);
        Assert.NotNull(result.ExpiresAtUtc);
        Assert.Equal(2026, result.ExpiresAtUtc!.Value.Year);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static class Reply
    {
        public static HttpResponseMessage AuthData(string usernameColonPassword, string? proxyEndpoint, DateTime expiresAtUtc)
        {
            var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(usernameColonPassword));
            var unix = new DateTimeOffset(expiresAtUtc, TimeSpan.Zero).ToUnixTimeSeconds();
            var endpointJson = proxyEndpoint is null ? "null" : $"\"{proxyEndpoint}\"";
            var body = $"{{\"authorizationData\":[{{\"authorizationToken\":\"{encoded}\",\"proxyEndpoint\":{endpointJson},\"expiresAt\":{unix}}}]}}";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/x-amz-json-1.1"),
            };
        }
    }

    private static (AwsEcrTokenProvider, IMemoryCache) BuildProvider(
        Func<HttpRequestMessage, HttpResponseMessage> responder) =>
        BuildProvider((req, _) => Task.FromResult(responder(req)));

    private static (AwsEcrTokenProvider, IMemoryCache) BuildProvider(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
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
        factory.Setup(f => f.CreateClient(AwsEcrTokenProvider.HttpClientName)).Returns(httpClient);

        var cache = new MemoryCache(new MemoryCacheOptions());
        // Frozen-time provider so the cache TTL math is deterministic.
        var time = new FrozenTimeProvider(AnchorUtc);
        var provider = new AwsEcrTokenProvider(factory.Object, cache, NullLogger<AwsEcrTokenProvider>.Instance, time);
        return (provider, cache);
    }

    private sealed class FrozenTimeProvider(DateTime anchor) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(anchor, TimeSpan.Zero);
    }
}



