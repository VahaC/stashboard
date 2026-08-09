using System.Net;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;
using Stashboard.Core.Abstractions;
using Stashboard.Infrastructure.Docker;

namespace Stashboard.Tests.Infrastructure.Docker;

/// <summary>
/// Unit tests for <see cref="OciRegistryClient"/>. The HTTP layer is mocked
/// with <see cref="HttpMessageHandler"/> + Moq (same pattern as
/// <c>ServiceHealthCheckerTests</c>); the cache is a real in-memory cache so
/// the token-caching path is exercised end-to-end.
/// </summary>
public class OciRegistryClientTests
{
    private const string PublicDigest = "sha256:abc123def4567890abc123def4567890abc123def4567890abc123def4567890";
    private const string ManifestListMediaType = "application/vnd.docker.distribution.manifest.list.v2+json";

    // ── Anonymous flow ───────────────────────────────────────────────────────

    [Fact]
    public async Task GetManifestDigest_AnonymousSuccess_ReturnsDigest()
    {
        var harness = Harness.Build(request =>
        {
            Assert.Equal(HttpMethod.Head, request.Method);
            return Reply.OkWithDigest(PublicDigest);
        });

        var result = await harness.Client.GetManifestDigestAsync(
            "ghcr.io", "owner/repo", "latest", credentials: null);

        Assert.Equal(RegistryManifestStatus.Ok, result.Status);
        Assert.Equal(PublicDigest, result.Digest);
        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task GetManifestDigest_DockerHub_MapsToRegistry1Host()
    {
        Uri? requestedUri = null;
        var harness = Harness.Build(request =>
        {
            requestedUri = request.RequestUri;
            return Reply.OkWithDigest(PublicDigest);
        });

        await harness.Client.GetManifestDigestAsync(
            "docker.io", "library/nginx", "latest", credentials: null);

        Assert.NotNull(requestedUri);
        Assert.Equal("registry-1.docker.io", requestedUri!.Host);
        Assert.Equal("/v2/library/nginx/manifests/latest", requestedUri.AbsolutePath);
    }

    [Fact]
    public async Task GetManifestDigest_SendsAllManifestMediaTypesInAccept()
    {
        HttpRequestMessage? captured = null;
        var harness = Harness.Build(request =>
        {
            captured = request;
            return Reply.OkWithDigest(PublicDigest);
        });

        await harness.Client.GetManifestDigestAsync("ghcr.io", "owner/repo", "v1", credentials: null);

        Assert.NotNull(captured);
        var accept = captured!.Headers.Accept.Select(h => h.MediaType).ToList();
        Assert.Contains("application/vnd.oci.image.index.v1+json", accept);
        Assert.Contains("application/vnd.docker.distribution.manifest.list.v2+json", accept);
        Assert.Contains("application/vnd.oci.image.manifest.v1+json", accept);
        Assert.Contains("application/vnd.docker.distribution.manifest.v2+json", accept);
    }

    [Fact]
    public async Task GetManifestDigest_OkWithoutDigestHeader_ReturnsInvalidResponse()
    {
        var harness = Harness.Build(_ => new HttpResponseMessage(HttpStatusCode.OK));

        var result = await harness.Client.GetManifestDigestAsync(
            "ghcr.io", "owner/repo", "latest", credentials: null);

        Assert.Equal(RegistryManifestStatus.InvalidResponse, result.Status);
        Assert.Null(result.Digest);
    }

    // ── Bearer challenge / auth flow ─────────────────────────────────────────

    [Fact]
    public async Task GetManifestDigest_401ThenToken_RetriesWithBearer()
    {
        var calls = 0;
        Uri? tokenUrl = null;
        string? authorizationOnRetry = null;
        var harness = Harness.Build(request =>
        {
            calls++;
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath.EndsWith("/token"))
            {
                tokenUrl = request.RequestUri;
                return Reply.Json("""{"token":"mytoken","expires_in":300}""");
            }
            // Manifest HEAD: first call → 401 with challenge; second → 200 with digest.
            if (calls == 1)
                return Reply.UnauthorizedWithBearerChallenge(
                    "realm=\"https://auth.docker.io/token\",service=\"registry.docker.io\",scope=\"repository:library/nginx:pull\"");
            authorizationOnRetry = request.Headers.Authorization?.ToString();
            return Reply.OkWithDigest(PublicDigest);
        });

        var result = await harness.Client.GetManifestDigestAsync(
            "docker.io", "library/nginx", "latest", credentials: null);

        Assert.Equal(RegistryManifestStatus.Ok, result.Status);
        Assert.Equal(PublicDigest, result.Digest);
        Assert.Equal("Bearer mytoken", authorizationOnRetry);
        Assert.NotNull(tokenUrl);
        Assert.Contains("service=registry.docker.io", tokenUrl!.Query);
        Assert.Contains("scope=repository%3Alibrary%2Fnginx%3Apull", tokenUrl.Query);
    }

    [Fact]
    public async Task GetManifestDigest_PrivateRegistry_SendsBasicAuthToTokenEndpoint()
    {
        string? tokenAuthHeader = null;
        var harness = Harness.Build(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/token"))
            {
                tokenAuthHeader = request.Headers.Authorization?.ToString();
                return Reply.Json("""{"token":"abc","expires_in":300}""");
            }
            // First manifest call → 401 challenge.
            if (request.Headers.Authorization is null)
                return Reply.UnauthorizedWithBearerChallenge(
                    "realm=\"https://ghcr.io/token\",service=\"ghcr.io\",scope=\"repository:owner/private:pull\"");
            return Reply.OkWithDigest(PublicDigest);
        });

        var result = await harness.Client.GetManifestDigestAsync(
            "ghcr.io", "owner/private", "latest",
            credentials: new RegistryCredentials("octo", "ghp_token"));

        Assert.Equal(RegistryManifestStatus.Ok, result.Status);
        Assert.NotNull(tokenAuthHeader);
        // 'octo:ghp_token' in base64.
        Assert.Equal("Basic b2N0bzpnaHBfdG9rZW4=", tokenAuthHeader);
    }

    [Fact]
    public async Task GetManifestDigest_401NotBearer_ReturnsUnauthorized()
    {
        var harness = Harness.Build(_ =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.Unauthorized);
            resp.Headers.WwwAuthenticate.Add(new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", "realm=\"foo\""));
            return resp;
        });

        var result = await harness.Client.GetManifestDigestAsync(
            "ghcr.io", "owner/repo", "latest", credentials: null);

        Assert.Equal(RegistryManifestStatus.Unauthorized, result.Status);
    }

    [Fact]
    public async Task GetManifestDigest_TokenEndpointFails_ReturnsUnauthorized()
    {
        var harness = Harness.Build(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/token"))
                return new HttpResponseMessage(HttpStatusCode.Forbidden);
            return Reply.UnauthorizedWithBearerChallenge(
                "realm=\"https://auth.docker.io/token\",service=\"registry.docker.io\",scope=\"repository:library/private:pull\"");
        });

        var result = await harness.Client.GetManifestDigestAsync(
            "docker.io", "library/private", "latest", credentials: null);

        Assert.Equal(RegistryManifestStatus.Unauthorized, result.Status);
    }

    [Fact]
    public async Task GetManifestDigest_RetryStill401_ReportsUnauthorized()
    {
        var harness = Harness.Build(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/token"))
                return Reply.Json("""{"token":"t","expires_in":300}""");
            return Reply.UnauthorizedWithBearerChallenge(
                "realm=\"https://auth.docker.io/token\",service=\"registry.docker.io\",scope=\"repository:library/nginx:pull\"");
        });

        var result = await harness.Client.GetManifestDigestAsync(
            "docker.io", "library/nginx", "latest", credentials: null);

        Assert.Equal(RegistryManifestStatus.Unauthorized, result.Status);
    }

    // ── Token cache ──────────────────────────────────────────────────────────

    [Fact]
    public async Task GetManifestDigest_SecondCallSameRepo_ReusesCachedToken()
    {
        var tokenCalls = 0;
        var harness = Harness.Build(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/token"))
            {
                tokenCalls++;
                return Reply.Json("""{"token":"cached","expires_in":300}""");
            }
            if (request.Headers.Authorization?.Parameter == "cached")
                return Reply.OkWithDigest(PublicDigest);
            return Reply.UnauthorizedWithBearerChallenge(
                "realm=\"https://auth.docker.io/token\",service=\"registry.docker.io\",scope=\"repository:library/nginx:pull\"");
        });

        await harness.Client.GetManifestDigestAsync("docker.io", "library/nginx", "latest", credentials: null);
        await harness.Client.GetManifestDigestAsync("docker.io", "library/nginx", "latest", credentials: null);

        Assert.Equal(1, tokenCalls);
    }

    [Fact]
    public async Task GetManifestDigest_DifferentRepos_DoNotShareToken()
    {
        var tokenCalls = 0;
        var harness = Harness.Build(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/token"))
            {
                tokenCalls++;
                return Reply.Json("""{"token":"x","expires_in":300}""");
            }
            if (request.Headers.Authorization is not null)
                return Reply.OkWithDigest(PublicDigest);
            return Reply.UnauthorizedWithBearerChallenge(
                "realm=\"https://auth.docker.io/token\",service=\"registry.docker.io\",scope=\"repository:library/nginx:pull\"");
        });

        await harness.Client.GetManifestDigestAsync("docker.io", "library/nginx", "latest", credentials: null);
        await harness.Client.GetManifestDigestAsync("docker.io", "library/postgres", "latest", credentials: null);

        Assert.Equal(2, tokenCalls);
    }

    // ── Failure mapping ──────────────────────────────────────────────────────

    [Fact]
    public async Task GetManifestDigest_404_ReturnsNotFound()
    {
        var harness = Harness.Build(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var result = await harness.Client.GetManifestDigestAsync(
            "ghcr.io", "missing/image", "latest", credentials: null);

        Assert.Equal(RegistryManifestStatus.NotFound, result.Status);
    }

    [Fact]
    public async Task GetManifestDigest_429_ReturnsRateLimited()
    {
        var harness = Harness.Build(_ => new HttpResponseMessage((HttpStatusCode)429));

        var result = await harness.Client.GetManifestDigestAsync(
            "docker.io", "library/nginx", "latest", credentials: null);

        Assert.Equal(RegistryManifestStatus.RateLimited, result.Status);
    }

    [Fact]
    public async Task GetManifestDigest_500_ReturnsInvalidResponse()
    {
        var harness = Harness.Build(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var result = await harness.Client.GetManifestDigestAsync(
            "ghcr.io", "owner/repo", "latest", credentials: null);

        Assert.Equal(RegistryManifestStatus.InvalidResponse, result.Status);
        Assert.Contains("500", result.Error);
    }

    [Fact]
    public async Task GetManifestDigest_HttpRequestException_ReturnsNetworkError()
    {
        var harness = Harness.Build((_, _) => throw new HttpRequestException("dns fail"));

        var result = await harness.Client.GetManifestDigestAsync(
            "ghcr.io", "owner/repo", "latest", credentials: null);

        Assert.Equal(RegistryManifestStatus.NetworkError, result.Status);
        Assert.Contains("dns fail", result.Error);
    }

    [Fact]
    public async Task GetManifestDigest_RequestTimeout_ReturnsNetworkError()
    {
        var harness = Harness.Build((_, _) => throw new TaskCanceledException());

        var result = await harness.Client.GetManifestDigestAsync(
            "ghcr.io", "owner/repo", "latest", credentials: null);

        Assert.Equal(RegistryManifestStatus.NetworkError, result.Status);
    }

    // ── Token payload edge cases ─────────────────────────────────────────────

    [Fact]
    public async Task GetManifestDigest_TokenPayloadUsesAccessTokenField_StillSucceeds()
    {
        // Some registries return access_token instead of token. Both must work.
        var harness = Harness.Build(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/token"))
                return Reply.Json("""{"access_token":"alt-key","expires_in":120}""");
            if (request.Headers.Authorization?.Parameter == "alt-key")
                return Reply.OkWithDigest(PublicDigest);
            return Reply.UnauthorizedWithBearerChallenge(
                "realm=\"https://ghcr.io/token\",service=\"ghcr.io\",scope=\"repository:owner/repo:pull\"");
        });

        var result = await harness.Client.GetManifestDigestAsync(
            "ghcr.io", "owner/repo", "latest", credentials: null);

        Assert.Equal(RegistryManifestStatus.Ok, result.Status);
    }

    [Fact]
    public async Task GetManifestDigest_TokenPayloadMalformed_ReturnsUnauthorized()
    {
        var harness = Harness.Build(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/token"))
                return Reply.Json("not-valid-json");
            return Reply.UnauthorizedWithBearerChallenge(
                "realm=\"https://ghcr.io/token\",service=\"ghcr.io\",scope=\"repository:owner/repo:pull\"");
        });

        var result = await harness.Client.GetManifestDigestAsync(
            "ghcr.io", "owner/repo", "latest", credentials: null);

        Assert.Equal(RegistryManifestStatus.Unauthorized, result.Status);
    }

    [Fact]
    public async Task GetManifestDigest_BearerChallengeMissingRealm_ReturnsUnauthorized()
    {
        var harness = Harness.Build(_ => Reply.UnauthorizedWithBearerChallenge(
            "service=\"registry.docker.io\",scope=\"repository:library/nginx:pull\""));

        var result = await harness.Client.GetManifestDigestAsync(
            "docker.io", "library/nginx", "latest", credentials: null);

        Assert.Equal(RegistryManifestStatus.Unauthorized, result.Status);
    }

    // ── ListTagsAsync (V2.1) ─────────────────────────────────────────────────

    [Fact]
    public async Task ListTags_AnonymousSuccess_ReturnsTagsInRegistryOrder()
    {
        var harness = Harness.Build(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.EndsWith("/v2/owner/repo/tags/list?n=200", request.RequestUri!.AbsoluteUri);
            return Reply.Json("""{"name":"owner/repo","tags":["v1.0.0","v1.1.0","v2.0.0-rc1"]}""");
        });

        var result = await harness.Client.ListTagsAsync("ghcr.io", "owner/repo", credentials: null);

        Assert.Equal(RegistryManifestStatus.Ok, result.Status);
        Assert.Equal(new[] { "v1.0.0", "v1.1.0", "v2.0.0-rc1" }, result.Tags);
    }

    [Fact]
    public async Task ListTags_DockerHub_RewritesHostToRegistry1()
    {
        Uri? requestedUri = null;
        var harness = Harness.Build(request =>
        {
            requestedUri = request.RequestUri;
            return Reply.Json("""{"tags":["1","1.27","latest"]}""");
        });

        await harness.Client.ListTagsAsync("docker.io", "library/nginx", credentials: null);

        Assert.NotNull(requestedUri);
        Assert.Equal("registry-1.docker.io", requestedUri!.Host);
        Assert.Equal("/v2/library/nginx/tags/list", requestedUri.AbsolutePath);
    }

    [Fact]
    public async Task ListTags_RespectsPageSizeQueryParam()
    {
        Uri? requestedUri = null;
        var harness = Harness.Build(request =>
        {
            requestedUri = request.RequestUri;
            return Reply.Json("""{"tags":["a"]}""");
        });

        await harness.Client.ListTagsAsync("ghcr.io", "owner/repo", credentials: null, pageSize: 50);

        Assert.Contains("n=50", requestedUri!.Query);
    }

    [Fact]
    public async Task ListTags_404_ReturnsNotFound()
    {
        var harness = Harness.Build(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var result = await harness.Client.ListTagsAsync("ghcr.io", "missing/repo", credentials: null);

        Assert.Equal(RegistryManifestStatus.NotFound, result.Status);
        Assert.Empty(result.Tags);
    }

    [Fact]
    public async Task ListTags_429_ReturnsRateLimited()
    {
        var harness = Harness.Build(_ => new HttpResponseMessage((HttpStatusCode)429));

        var result = await harness.Client.ListTagsAsync("docker.io", "library/nginx", credentials: null);

        Assert.Equal(RegistryManifestStatus.RateLimited, result.Status);
    }

    [Fact]
    public async Task ListTags_HttpRequestException_ReturnsNetworkError()
    {
        var harness = Harness.Build((_, _) => throw new HttpRequestException("dns fail"));

        var result = await harness.Client.ListTagsAsync("ghcr.io", "owner/repo", credentials: null);

        Assert.Equal(RegistryManifestStatus.NetworkError, result.Status);
        Assert.Contains("dns fail", result.Error);
    }

    [Fact]
    public async Task ListTags_MalformedJson_ReturnsInvalidResponse()
    {
        var harness = Harness.Build(_ => Reply.Json("not-json"));

        var result = await harness.Client.ListTagsAsync("ghcr.io", "owner/repo", credentials: null);

        Assert.Equal(RegistryManifestStatus.InvalidResponse, result.Status);
    }

    [Fact]
    public async Task ListTags_MissingTagsField_ReturnsInvalidResponse()
    {
        var harness = Harness.Build(_ => Reply.Json("""{"name":"owner/repo"}"""));

        var result = await harness.Client.ListTagsAsync("ghcr.io", "owner/repo", credentials: null);

        Assert.Equal(RegistryManifestStatus.InvalidResponse, result.Status);
    }

    [Fact]
    public async Task ListTags_NullTagsField_ReturnsEmptyOk()
    {
        // Some registries return tags: null for an existing-but-empty repo.
        // Treat that as success-with-no-tags rather than an error.
        var harness = Harness.Build(_ => Reply.Json("""{"name":"owner/repo","tags":null}"""));

        var result = await harness.Client.ListTagsAsync("ghcr.io", "owner/repo", credentials: null);

        Assert.Equal(RegistryManifestStatus.Ok, result.Status);
        Assert.Empty(result.Tags);
    }

    [Fact]
    public async Task ListTags_401ThenToken_RetriesWithBearer()
    {
        var calls = 0;
        string? retryAuth = null;
        var harness = Harness.Build(request =>
        {
            calls++;
            if (request.RequestUri!.AbsolutePath.EndsWith("/token"))
                return Reply.Json("""{"token":"tags-tok","expires_in":300}""");
            if (calls == 1)
                return Reply.UnauthorizedWithBearerChallenge(
                    "realm=\"https://ghcr.io/token\",service=\"ghcr.io\",scope=\"repository:owner/repo:pull\"");
            retryAuth = request.Headers.Authorization?.ToString();
            return Reply.Json("""{"tags":["v1"]}""");
        });

        var result = await harness.Client.ListTagsAsync("ghcr.io", "owner/repo", credentials: null);

        Assert.Equal(RegistryManifestStatus.Ok, result.Status);
        Assert.Equal("Bearer tags-tok", retryAuth);
    }

    [Fact]
    public async Task ListTags_FollowsLinkHeader_ConcatenatesAllPages()
    {
        var requested = new List<string>();
        var harness = Harness.Build(request =>
        {
            requested.Add(request.RequestUri!.PathAndQuery);
            // Page 1 advertises a relative next link; page 2 has none.
            if (request.RequestUri.Query.Contains("last="))
                return Reply.Json("""{"tags":["v2.0.0","v2.1.0"]}""");
            return Reply.JsonWithNextLink(
                """{"tags":["v1.0.0","v1.1.0"]}""",
                "/v2/owner/repo/tags/list?n=200&last=v1.1.0");
        });

        var result = await harness.Client.ListTagsAsync("ghcr.io", "owner/repo", credentials: null);

        Assert.Equal(RegistryManifestStatus.Ok, result.Status);
        Assert.Equal(new[] { "v1.0.0", "v1.1.0", "v2.0.0", "v2.1.0" }, result.Tags);
        Assert.Equal(2, requested.Count);
        Assert.Contains("last=v1.1.0", requested[1]);
    }

    [Fact]
    public async Task ListTags_NoLinkHeader_StopsAfterFirstPage()
    {
        var calls = 0;
        var harness = Harness.Build(_ =>
        {
            calls++;
            return Reply.Json("""{"tags":["v1.0.0"]}""");
        });

        var result = await harness.Client.ListTagsAsync("ghcr.io", "owner/repo", credentials: null);

        Assert.Equal(new[] { "v1.0.0" }, result.Tags);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ListTags_LaterPageFails_ReturnsTagsGatheredSoFar()
    {
        var harness = Harness.Build(request =>
        {
            if (request.RequestUri!.Query.Contains("last="))
                return new HttpResponseMessage((HttpStatusCode)429);
            return Reply.JsonWithNextLink(
                """{"tags":["v1.0.0"]}""",
                "/v2/owner/repo/tags/list?n=200&last=v1.0.0");
        });

        var result = await harness.Client.ListTagsAsync("ghcr.io", "owner/repo", credentials: null);

        // First page succeeded → best-effort success with what we collected.
        Assert.Equal(RegistryManifestStatus.Ok, result.Status);
        Assert.Equal(new[] { "v1.0.0" }, result.Tags);
    }

    [Fact]
    public async Task ListTags_PaginationCappedByPageCount()
    {
        // Every page advertises a next link → the cap (MaxTagPages = 20) must
        // stop the loop instead of following forever.
        var calls = 0;
        var harness = Harness.Build(request =>
        {
            calls++;
            return Reply.JsonWithNextLink(
                $$"""{"tags":["v{{calls}}.0.0"]}""",
                $"/v2/owner/repo/tags/list?n=200&last=page{calls}");
        });

        var result = await harness.Client.ListTagsAsync("ghcr.io", "owner/repo", credentials: null);

        Assert.Equal(RegistryManifestStatus.Ok, result.Status);
        Assert.Equal(20, calls);
        Assert.Equal(20, result.Tags.Count);
    }

    // ── V2.4: Basic-only auth strategy (Nexus / Gitea Packages) ──────────────

    [Fact]
    public async Task GetManifestDigest_BasicAuth_SendsBasicHeaderAndNeverFollowsBearerChallenge()
    {
        var requests = new List<HttpRequestMessage>();
        var harness = Harness.Build(req => { requests.Add(req); return Reply.OkWithDigest(PublicDigest); });

        var result = await harness.Client.GetManifestDigestAsync(
            "registry.nexus.local", "team/app", "1.2.3",
            new RegistryAuthContext(
                Stashboard.Core.Enums.RegistryAuthType.Basic,
                new RegistryCredentials("svc-account", "hunter2")));

        Assert.Equal(RegistryManifestStatus.Ok, result.Status);
        Assert.Single(requests);
        var auth = requests[0].Headers.Authorization;
        Assert.NotNull(auth);
        Assert.Equal("Basic", auth!.Scheme);
        var decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(auth.Parameter!));
        Assert.Equal("svc-account:hunter2", decoded);
    }

    [Fact]
    public async Task GetManifestDigest_BasicAuth_DoesNotRetryOnBearerChallenge()
    {
        // Nexus that incorrectly advertises Bearer should not pull us into a
        // token round-trip; we sent Basic, we get whatever response we get.
        var calls = 0;
        var harness = Harness.Build(_ =>
        {
            calls++;
            return Reply.UnauthorizedWithBearerChallenge("realm=\"https://nexus/auth\"");
        });

        var result = await harness.Client.GetManifestDigestAsync(
            "registry.nexus.local", "team/app", "1.2.3",
            new RegistryAuthContext(
                Stashboard.Core.Enums.RegistryAuthType.Basic,
                new RegistryCredentials("svc-account", "hunter2")));

        Assert.Equal(RegistryManifestStatus.Unauthorized, result.Status);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ListTags_BasicAuth_SkipsBearerAndUsesBasicHeader()
    {
        var requests = new List<HttpRequestMessage>();
        var harness = Harness.Build(req =>
        {
            requests.Add(req);
            return Reply.Json("""{"tags":["1","2"]}""");
        });

        var result = await harness.Client.ListTagsAsync(
            "registry.nexus.local", "team/app",
            new RegistryAuthContext(
                Stashboard.Core.Enums.RegistryAuthType.Basic,
                new RegistryCredentials("svc-account", "hunter2")));

        Assert.Equal(RegistryManifestStatus.Ok, result.Status);
        Assert.Single(requests);
        Assert.Equal("Basic", requests[0].Headers.Authorization!.Scheme);
    }

    // ── V2.4: AWS ECR strategy — pulls token from provider, then Basic ───────

    [Fact]
    public async Task GetManifestDigest_AwsEcr_FetchesTokenFromProviderAndUsesItAsBasic()
    {
        HttpRequestMessage? captured = null;
        var ecrMock = new Mock<IAwsEcrTokenProvider>();
        ecrMock.Setup(p => p.GetAuthorizationTokenAsync("AKIA", "secret", "eu-central-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AwsEcrTokenResult(
                AwsEcrTokenStatus.Ok,
                new RegistryCredentials("AWS", "ecr-temp-token"),
                null, DateTime.UtcNow.AddHours(12), null));

        var harness = Harness.WithEcr(ecrMock.Object, req => { captured = req; return Reply.OkWithDigest(PublicDigest); });

        var result = await harness.Client.GetManifestDigestAsync(
            "123456789012.dkr.ecr.eu-central-1.amazonaws.com", "my-app", "latest",
            new RegistryAuthContext(
                Stashboard.Core.Enums.RegistryAuthType.AwsEcr,
                Credentials: null,
                AwsAccessKeyId: "AKIA",
                AwsSecretAccessKey: "secret",
                AwsRegion: "eu-central-1"));

        Assert.Equal(RegistryManifestStatus.Ok, result.Status);
        Assert.NotNull(captured);
        Assert.Equal("Basic", captured!.Headers.Authorization!.Scheme);
        var decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(captured.Headers.Authorization.Parameter!));
        Assert.Equal("AWS:ecr-temp-token", decoded);
    }

    [Fact]
    public async Task GetManifestDigest_AwsEcr_TokenProviderUnauthorized_PropagatedAsRegistryUnauthorized()
    {
        var ecrMock = new Mock<IAwsEcrTokenProvider>();
        ecrMock.Setup(p => p.GetAuthorizationTokenAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AwsEcrTokenResult(
                AwsEcrTokenStatus.Unauthorized, null, null, null, "bad iam key"));

        var harness = Harness.WithEcr(ecrMock.Object, _ => Reply.OkWithDigest(PublicDigest));

        var result = await harness.Client.GetManifestDigestAsync(
            "123456789012.dkr.ecr.eu-central-1.amazonaws.com", "my-app", "latest",
            new RegistryAuthContext(
                Stashboard.Core.Enums.RegistryAuthType.AwsEcr,
                Credentials: null,
                AwsAccessKeyId: "AKIA",
                AwsSecretAccessKey: "secret",
                AwsRegion: "eu-central-1"));

        Assert.Equal(RegistryManifestStatus.Unauthorized, result.Status);
        Assert.Contains("bad iam key", result.Error);
    }

    [Fact]
    public async Task GetManifestDigest_AwsEcr_MissingRegion_FailsBeforeHittingTheNetwork()
    {
        var ecrMock = new Mock<IAwsEcrTokenProvider>(MockBehavior.Strict);
        var httpCalls = 0;
        var harness = Harness.WithEcr(ecrMock.Object, _ => { httpCalls++; return Reply.OkWithDigest(PublicDigest); });

        var result = await harness.Client.GetManifestDigestAsync(
            "123456789012.dkr.ecr.eu-central-1.amazonaws.com", "my-app", "latest",
            new RegistryAuthContext(
                Stashboard.Core.Enums.RegistryAuthType.AwsEcr,
                Credentials: null,
                AwsAccessKeyId: "AKIA",
                AwsSecretAccessKey: "secret",
                AwsRegion: null));

        Assert.Equal(RegistryManifestStatus.Unauthorized, result.Status);
        Assert.Equal(0, httpCalls);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static class Reply
    {
        public static HttpResponseMessage OkWithDigest(string digest)
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Array.Empty<byte>())
                {
                    Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(ManifestListMediaType) },
                },
            };
            resp.Headers.Add("Docker-Content-Digest", digest);
            return resp;
        }

        public static HttpResponseMessage Json(string body) =>
            new(HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            };

        public static HttpResponseMessage JsonWithNextLink(string body, string nextLink)
        {
            var resp = Json(body);
            resp.Headers.Add("Link", $"<{nextLink}>; rel=\"next\"");
            return resp;
        }

        public static HttpResponseMessage UnauthorizedWithBearerChallenge(string parameter)
        {
            var resp = new HttpResponseMessage(HttpStatusCode.Unauthorized);
            resp.Headers.WwwAuthenticate.Add(new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", parameter));
            return resp;
        }
    }

    private sealed class Harness
    {
        public required OciRegistryClient Client { get; init; }

        public static Harness Build(Func<HttpRequestMessage, HttpResponseMessage> responder) =>
            Build((req, _) => Task.FromResult(responder(req)));

        public static Harness Build(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) =>
            BuildInternal(responder, new Mock<IAwsEcrTokenProvider>(MockBehavior.Strict).Object);

        /// <summary>V2.4 — variant that wires in a real ECR mock for tests
        /// that exercise the AwsEcr strategy.</summary>
        public static Harness WithEcr(IAwsEcrTokenProvider ecr, Func<HttpRequestMessage, HttpResponseMessage> responder) =>
            BuildInternal((req, _) => Task.FromResult(responder(req)), ecr);

        private static Harness BuildInternal(
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder,
            IAwsEcrTokenProvider ecr)
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
            factory.Setup(f => f.CreateClient(OciRegistryClient.HttpClientName)).Returns(httpClient);

            var cache = new MemoryCache(new MemoryCacheOptions());
            var client = new OciRegistryClient(factory.Object, cache, ecr, NullLogger<OciRegistryClient>.Instance);
            return new Harness { Client = client };
        }
    }
}

