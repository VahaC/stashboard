using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Stashboard.Core.Abstractions;
using Stashboard.Core.Enums;

namespace Stashboard.Infrastructure.Docker;

/// <summary>
/// OCI Distribution v2 client supporting anonymous and Bearer-token flows.
/// Tested against Docker Hub and GHCR; should also work with any OCI-compliant
/// registry that follows the same auth pattern.
/// </summary>
/// <remarks>
/// Strategy implemented (Strategy + Template Method patterns over the HTTP exchange):
/// 1. HEAD <c>/v2/{repo}/manifests/{ref}</c> with cached token, if any.
/// 2. On 401, parse <c>WWW-Authenticate</c>, fetch a fresh token from the realm
///    (with Basic auth when private creds were supplied), cache it, retry once.
/// 3. Read <c>Docker-Content-Digest</c> from the successful response.
///
/// The <c>Accept</c> header advertises both single-arch and multi-arch manifest
/// media types, so the registry returns the <i>list</i> digest for multi-arch
/// images — which is what the local Docker daemon records against the running
/// container, so the comparison is digest-to-digest of the same logical object.
/// </remarks>
public sealed class OciRegistryClient(
    IHttpClientFactory httpClientFactory,
    IMemoryCache tokenCache,
    IAwsEcrTokenProvider awsEcrTokenProvider,
    ILogger<OciRegistryClient> logger) : IRegistryClient
{
    public const string HttpClientName = "registry";

    private const string DockerHubRegistry = "docker.io";
    private const string DockerHubApiHost = "registry-1.docker.io";
    private const int DefaultTokenLifetimeSeconds = 300;
    private const int TokenCacheSafetyMarginSeconds = 30;
    private const int TokenCacheFloorSeconds = 30;

    // Pagination safety rails for tag listing: registries expose follow-on
    // pages via a `Link: <…>; rel="next"` header (RFC 5988). We cap both the
    // page count and the total tags so a pathological repository can't stall
    // the background scan or balloon memory.
    private const int MaxTagPages = 20;
    private const int MaxTagsTotal = 5000;

    private static readonly string[] ManifestAcceptHeaders =
    {
        "application/vnd.oci.image.index.v1+json",
        "application/vnd.docker.distribution.manifest.list.v2+json",
        "application/vnd.oci.image.manifest.v1+json",
        "application/vnd.docker.distribution.manifest.v2+json",
    };

    public async Task<RegistryManifestResult> GetManifestDigestAsync(
        string registryHost,
        string repository,
        string reference,
        RegistryAuthContext auth,
        CancellationToken cancellationToken = default)
    {
        var apiHost = ResolveApiHost(registryHost);
        var url = $"https://{apiHost}/v2/{repository}/manifests/{Uri.EscapeDataString(reference)}";
        var fetchedAt = DateTime.UtcNow;

        // V2.4 — resolve dynamic credentials (currently: AWS ECR temp token)
        // before we touch the HTTP client so a failure short-circuits with a
        // clean Unauthorized result and the cache-key calculations downstream
        // use the same plaintext credentials the HTTP call will use.
        var resolved = await ResolveCredentialsAsync(auth, fetchedAt, cancellationToken);
        if (resolved.Error is not null) return resolved.Error;
        var credentials = resolved.Credentials;
        var client = httpClientFactory.CreateClient(HttpClientName);

        try
        {
            // Basic auth strategy: skip the Bearer round-trip entirely.
            // Nexus / Gitea Packages either reject Bearer outright or quietly
            // ignore the token and want every request signed with Basic.
            if (auth.AuthType is RegistryAuthType.Basic or RegistryAuthType.AwsEcr)
            {
                using var basic = await SendManifestHeadAsync(client, url, bearerToken: null, basicCredentials: credentials, cancellationToken);
                return BuildResult(basic, fetchedAt);
            }

            var cachedToken = TryGetCachedToken(apiHost, repository, credentials);
            using var first = await SendManifestHeadAsync(client, url, cachedToken, basicCredentials: null, cancellationToken);
            if (first.StatusCode != HttpStatusCode.Unauthorized)
                return BuildResult(first, fetchedAt);

            var challenge = first.Headers.WwwAuthenticate.FirstOrDefault();
            if (challenge is null || !string.Equals(challenge.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase))
                return new RegistryManifestResult(RegistryManifestStatus.Unauthorized, null, null,
                    "Registry returned 401 without a Bearer challenge.", fetchedAt);

            var freshToken = await AcquireTokenAsync(client, challenge.Parameter, credentials, cancellationToken);
            if (freshToken is null)
                return new RegistryManifestResult(RegistryManifestStatus.Unauthorized, null, null,
                    "Registry token acquisition failed.", fetchedAt);

            CacheToken(apiHost, repository, credentials, freshToken);

            using var retry = await SendManifestHeadAsync(client, url, freshToken.AccessToken, basicCredentials: null, cancellationToken);
            return BuildResult(retry, fetchedAt);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new RegistryManifestResult(RegistryManifestStatus.NetworkError, null, null,
                "Registry request timed out.", fetchedAt);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Registry HTTP request to {Url} failed", url);
            return new RegistryManifestResult(RegistryManifestStatus.NetworkError, null, null,
                ex.Message, fetchedAt);
        }
    }

    public async Task<RegistryTagListResult> ListTagsAsync(
        string registryHost,
        string repository,
        RegistryAuthContext auth,
        int pageSize = 200,
        CancellationToken cancellationToken = default)
    {
        var apiHost = ResolveApiHost(registryHost);
        var pageQuery = pageSize > 0 ? $"?n={pageSize}" : string.Empty;
        var firstUrl = $"https://{apiHost}/v2/{repository}/tags/list{pageQuery}";
        var fetchedAt = DateTime.UtcNow;
        var empty = Array.Empty<string>();

        var resolved = await ResolveCredentialsAsync(auth, fetchedAt, cancellationToken);
        if (resolved.TagError is not null) return resolved.TagError;
        var credentials = resolved.Credentials;
        var client = httpClientFactory.CreateClient(HttpClientName);
        var isBasic = auth.AuthType is RegistryAuthType.Basic or RegistryAuthType.AwsEcr;

        try
        {
            // ── first page (with one-shot Bearer challenge handling) ──
            string? bearer = null;
            HttpResponseMessage response;
            if (isBasic)
            {
                response = await SendTagListGetAsync(client, firstUrl, bearerToken: null, basicCredentials: credentials, cancellationToken);
            }
            else
            {
                bearer = TryGetCachedToken(apiHost, repository, credentials);
                response = await SendTagListGetAsync(client, firstUrl, bearer, basicCredentials: null, cancellationToken);
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    var challenge = response.Headers.WwwAuthenticate.FirstOrDefault();
                    response.Dispose();
                    if (challenge is null || !string.Equals(challenge.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase))
                        return new RegistryTagListResult(RegistryManifestStatus.Unauthorized, empty,
                            "Registry returned 401 without a Bearer challenge.", fetchedAt);

                    var freshToken = await AcquireTokenAsync(client, challenge.Parameter, credentials, cancellationToken);
                    if (freshToken is null)
                        return new RegistryTagListResult(RegistryManifestStatus.Unauthorized, empty,
                            "Registry token acquisition failed.", fetchedAt);

                    CacheToken(apiHost, repository, credentials, freshToken);
                    bearer = freshToken.AccessToken;
                    response = await SendTagListGetAsync(client, firstUrl, bearer, basicCredentials: null, cancellationToken);
                }
            }

            // ── accumulate pages following Link: rel="next" ──
            var accumulated = new List<string>();
            var pages = 0;
            while (true)
            {
                string? nextUrl;
                using (response)
                {
                    if (response.StatusCode != HttpStatusCode.OK)
                    {
                        // First page error → surface it; a later page failing
                        // returns the tags gathered so far (best-effort).
                        if (pages == 0) return await BuildTagListResultAsync(response, fetchedAt, cancellationToken);
                        break;
                    }

                    var body = await response.Content.ReadAsStringAsync(cancellationToken);
                    var tags = ParseTagsPayload(body);
                    if (tags is null)
                    {
                        if (pages == 0)
                            return new RegistryTagListResult(RegistryManifestStatus.InvalidResponse, empty,
                                "Registry tags/list response did not include a tags array.", fetchedAt);
                        break;
                    }

                    accumulated.AddRange(tags);
                    nextUrl = ResolveNextPageUrl(response, apiHost);
                }

                pages++;
                if (nextUrl is null || pages >= MaxTagPages || accumulated.Count >= MaxTagsTotal) break;

                response = isBasic
                    ? await SendTagListGetAsync(client, nextUrl, bearerToken: null, basicCredentials: credentials, cancellationToken)
                    : await SendTagListGetAsync(client, nextUrl, bearer, basicCredentials: null, cancellationToken);
            }

            return new RegistryTagListResult(RegistryManifestStatus.Ok, accumulated, null, fetchedAt);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new RegistryTagListResult(RegistryManifestStatus.NetworkError, empty,
                "Registry request timed out.", fetchedAt);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Registry tag-list HTTP request to {Url} failed", firstUrl);
            return new RegistryTagListResult(RegistryManifestStatus.NetworkError, empty,
                ex.Message, fetchedAt);
        }
    }

    /// <summary>
    /// Extracts the <c>rel="next"</c> target from an RFC 5988 <c>Link</c> header,
    /// if present. Registries usually return a path-only URL
    /// (<c>/v2/{name}/tags/list?n=…&amp;last=…</c>), so a relative value is
    /// resolved against the same API host the first page used.
    /// </summary>
    private static string? ResolveNextPageUrl(HttpResponseMessage response, string apiHost)
    {
        if (!response.Headers.TryGetValues("Link", out var headers)) return null;

        foreach (var header in headers)
        {
            foreach (var segment in header.Split(','))
            {
                var part = segment.Trim();
                if (part.IndexOf("rel=\"next\"", StringComparison.OrdinalIgnoreCase) < 0
                    && part.IndexOf("rel=next", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                var start = part.IndexOf('<');
                var end = part.IndexOf('>');
                if (start < 0 || end <= start) continue;

                var raw = part[(start + 1)..end].Trim();
                if (raw.Length == 0) continue;
                // Uri.TryCreate(..., UriKind.Absolute) on Linux treats a root-relative
                // path (starting with '/') as a local file path and silently drops the
                // query string, so only trust the parse when it actually yielded an
                // http(s) scheme — otherwise resolve it against apiHost ourselves.
                if (Uri.TryCreate(raw, UriKind.Absolute, out var abs)
                    && (abs.Scheme == Uri.UriSchemeHttp || abs.Scheme == Uri.UriSchemeHttps))
                    return abs.ToString();
                return $"https://{apiHost}{(raw.StartsWith('/') ? raw : "/" + raw)}";
            }
        }
        return null;
    }

    // ── HTTP exchange ────────────────────────────────────────────────────────

    private static async Task<HttpResponseMessage> SendManifestHeadAsync(
        HttpClient client,
        string url,
        string? bearerToken,
        RegistryCredentials? basicCredentials,
        CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Head, url);
        foreach (var mediaType in ManifestAcceptHeaders)
            request.Headers.Accept.ParseAdd(mediaType);
        ApplyAuthHeader(request, bearerToken, basicCredentials);
        return await client.SendAsync(request, cancellationToken);
    }

    private static RegistryManifestResult BuildResult(HttpResponseMessage response, DateTime fetchedAt)
    {
        switch ((int)response.StatusCode)
        {
            case 200:
                var digest = ExtractDigestHeader(response);
                if (string.IsNullOrEmpty(digest))
                    return new RegistryManifestResult(RegistryManifestStatus.InvalidResponse, null, null,
                        "Registry response missing Docker-Content-Digest header.", fetchedAt);
                var contentType = response.Content.Headers.ContentType?.MediaType;
                return new RegistryManifestResult(RegistryManifestStatus.Ok, digest, contentType, null, fetchedAt);

            case 401:
                return new RegistryManifestResult(RegistryManifestStatus.Unauthorized, null, null,
                    "Registry returned 401 even after authentication.", fetchedAt);

            case 404:
                return new RegistryManifestResult(RegistryManifestStatus.NotFound, null, null,
                    "Image or tag not found.", fetchedAt);

            case 429:
                return new RegistryManifestResult(RegistryManifestStatus.RateLimited, null, null,
                    "Registry rate-limited the request.", fetchedAt);

            default:
                return new RegistryManifestResult(RegistryManifestStatus.InvalidResponse, null, null,
                    $"Registry returned unexpected status {(int)response.StatusCode}.", fetchedAt);
        }
    }

    private static string? ExtractDigestHeader(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Docker-Content-Digest", out var values)
            ? values.FirstOrDefault()
            : null;

    private static async Task<HttpResponseMessage> SendTagListGetAsync(
        HttpClient client,
        string url,
        string? bearerToken,
        RegistryCredentials? basicCredentials,
        CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.ParseAdd("application/json");
        ApplyAuthHeader(request, bearerToken, basicCredentials);
        return await client.SendAsync(request, cancellationToken);
    }

    private static void ApplyAuthHeader(HttpRequestMessage request, string? bearerToken, RegistryCredentials? basicCredentials)
    {
        // Bearer wins if both are somehow present — we never combine the two
        // on the same request.
        if (!string.IsNullOrEmpty(bearerToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
            return;
        }
        if (basicCredentials is null) return;
        var basic = Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{basicCredentials.Username}:{basicCredentials.Password}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
    }

    private static async Task<RegistryTagListResult> BuildTagListResultAsync(
        HttpResponseMessage response,
        DateTime fetchedAt,
        CancellationToken cancellationToken)
    {
        var empty = Array.Empty<string>();
        switch ((int)response.StatusCode)
        {
            case 200:
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                var tags = ParseTagsPayload(body);
                if (tags is null)
                    return new RegistryTagListResult(RegistryManifestStatus.InvalidResponse, empty,
                        "Registry tags/list response did not include a tags array.", fetchedAt);
                return new RegistryTagListResult(RegistryManifestStatus.Ok, tags, null, fetchedAt);

            case 401:
                return new RegistryTagListResult(RegistryManifestStatus.Unauthorized, empty,
                    "Registry returned 401 even after authentication.", fetchedAt);

            case 404:
                return new RegistryTagListResult(RegistryManifestStatus.NotFound, empty,
                    "Repository not found.", fetchedAt);

            case 429:
                return new RegistryTagListResult(RegistryManifestStatus.RateLimited, empty,
                    "Registry rate-limited the request.", fetchedAt);

            default:
                return new RegistryTagListResult(RegistryManifestStatus.InvalidResponse, empty,
                    $"Registry returned unexpected status {(int)response.StatusCode}.", fetchedAt);
        }
    }

    private static IReadOnlyList<string>? ParseTagsPayload(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("tags", out var tagsElement))
                return null;
            if (tagsElement.ValueKind == JsonValueKind.Null)
                return Array.Empty<string>();
            if (tagsElement.ValueKind != JsonValueKind.Array)
                return null;

            var list = new List<string>(tagsElement.GetArrayLength());
            foreach (var item in tagsElement.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    var s = item.GetString();
                    if (!string.IsNullOrEmpty(s)) list.Add(s);
                }
            }
            return list;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string ResolveApiHost(string registryHost) =>
        string.Equals(registryHost, DockerHubRegistry, StringComparison.OrdinalIgnoreCase)
            ? DockerHubApiHost
            : registryHost;

    // ── V2.4 auth resolution (Auto / Basic / AwsEcr) ─────────────────────────

    /// <summary>
    /// Resolves the effective HTTP-Basic credentials for the call, kicking off the
    /// AWS ECR <c>GetAuthorizationToken</c> flow when the strategy is <see cref="RegistryAuthType.AwsEcr"/>.
    /// Returns either a `Credentials` payload to use, or a pre-built error result
    /// (manifest- or tag-list-shaped) for the caller to short-circuit on.
    /// </summary>
    private async Task<ResolvedCredentials> ResolveCredentialsAsync(
        RegistryAuthContext auth,
        DateTime fetchedAt,
        CancellationToken cancellationToken)
    {
        if (auth.AuthType != RegistryAuthType.AwsEcr)
            return new ResolvedCredentials(auth.Credentials);

        if (string.IsNullOrEmpty(auth.AwsAccessKeyId) || string.IsNullOrEmpty(auth.AwsSecretAccessKey)
            || string.IsNullOrEmpty(auth.AwsRegion))
        {
            const string message = "AWS ECR auth selected but access key id, secret access key or region is missing.";
            return new ResolvedCredentials(null,
                new RegistryManifestResult(RegistryManifestStatus.Unauthorized, null, null, message, fetchedAt),
                new RegistryTagListResult(RegistryManifestStatus.Unauthorized, Array.Empty<string>(), message, fetchedAt));
        }

        var token = await awsEcrTokenProvider.GetAuthorizationTokenAsync(
            auth.AwsAccessKeyId, auth.AwsSecretAccessKey, auth.AwsRegion, cancellationToken);
        if (!token.IsSuccess)
        {
            var (manifestStatus, message) = token.Status switch
            {
                AwsEcrTokenStatus.Unauthorized => (RegistryManifestStatus.Unauthorized, token.Error ?? "AWS ECR rejected the credentials."),
                AwsEcrTokenStatus.NetworkError => (RegistryManifestStatus.NetworkError, token.Error ?? "AWS ECR unreachable."),
                AwsEcrTokenStatus.InvalidResponse => (RegistryManifestStatus.InvalidResponse, token.Error ?? "AWS ECR returned an unexpected response."),
                _ => (RegistryManifestStatus.InvalidResponse, token.Error ?? "AWS ECR token acquisition failed."),
            };
            return new ResolvedCredentials(null,
                new RegistryManifestResult(manifestStatus, null, null, message, fetchedAt),
                new RegistryTagListResult(manifestStatus, Array.Empty<string>(), message, fetchedAt));
        }

        return new ResolvedCredentials(token.Credentials);
    }

    private readonly record struct ResolvedCredentials(
        RegistryCredentials? Credentials,
        RegistryManifestResult? Error = null,
        RegistryTagListResult? TagError = null);

    // ── Bearer token acquisition ─────────────────────────────────────────────

    /// <param name="challengeParameter">Raw <c>WWW-Authenticate</c> parameter, e.g.
    /// <c>realm="https://auth.docker.io/token",service="registry.docker.io",scope="repository:library/nginx:pull"</c>.</param>
    private async Task<BearerToken?> AcquireTokenAsync(
        HttpClient client,
        string? challengeParameter,
        RegistryCredentials? credentials,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(challengeParameter)) return null;
        var parsed = ParseChallenge(challengeParameter);
        if (!parsed.TryGetValue("realm", out var realm) || string.IsNullOrEmpty(realm))
        {
            logger.LogWarning("Registry Bearer challenge missing realm: {Challenge}", challengeParameter);
            return null;
        }

        var tokenUrl = new UriBuilder(realm);
        var query = new List<string>(2);
        if (parsed.TryGetValue("service", out var service) && !string.IsNullOrEmpty(service))
            query.Add($"service={Uri.EscapeDataString(service)}");
        if (parsed.TryGetValue("scope", out var scope) && !string.IsNullOrEmpty(scope))
            query.Add($"scope={Uri.EscapeDataString(scope)}");
        tokenUrl.Query = string.Join('&', query);

        using var request = new HttpRequestMessage(HttpMethod.Get, tokenUrl.Uri);
        if (credentials is not null)
        {
            var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{credentials.Username}:{credentials.Password}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
        }

        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Registry token endpoint returned {Status}", response.StatusCode);
            return null;
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return ParseTokenPayload(json);
    }

    private static BearerToken? ParseTokenPayload(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string? token = null;
            if (root.TryGetProperty("token", out var t) && t.ValueKind == JsonValueKind.String)
                token = t.GetString();
            if (string.IsNullOrEmpty(token)
                && root.TryGetProperty("access_token", out var at)
                && at.ValueKind == JsonValueKind.String)
                token = at.GetString();
            if (string.IsNullOrEmpty(token)) return null;

            var expires = DefaultTokenLifetimeSeconds;
            if (root.TryGetProperty("expires_in", out var ei) && ei.ValueKind == JsonValueKind.Number
                && ei.TryGetInt32(out var parsedExpires))
                expires = parsedExpires;
            return new BearerToken(token, expires);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Parses a Bearer challenge parameter string into a case-insensitive dictionary.
    /// Tolerates quoted and unquoted values and arbitrary whitespace.
    /// </summary>
    private static Dictionary<string, string> ParseChallenge(string parameter)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var span = parameter.AsSpan();
        var i = 0;
        while (i < span.Length)
        {
            while (i < span.Length && (span[i] == ',' || char.IsWhiteSpace(span[i]))) i++;
            var keyStart = i;
            while (i < span.Length && span[i] != '=') i++;
            if (i >= span.Length) break;
            var key = span[keyStart..i].ToString().Trim();
            i++; // consume '='
            string value;
            if (i < span.Length && span[i] == '"')
            {
                i++; // consume opening quote
                var valStart = i;
                while (i < span.Length && span[i] != '"') i++;
                value = span[valStart..i].ToString();
                if (i < span.Length) i++; // consume closing quote
            }
            else
            {
                var valStart = i;
                while (i < span.Length && span[i] != ',') i++;
                value = span[valStart..i].ToString().Trim();
            }
            if (key.Length > 0) dict[key] = value;
        }
        return dict;
    }

    // ── token cache ──────────────────────────────────────────────────────────

    private string BuildTokenCacheKey(string apiHost, string repository, RegistryCredentials? credentials)
    {
        var fingerprint = credentials is null
            ? "-"
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{credentials.Username}:{credentials.Password}")))[..12];
        return $"oci-token:{apiHost}|{repository}|{fingerprint}";
    }

    private string? TryGetCachedToken(string apiHost, string repository, RegistryCredentials? credentials)
    {
        var key = BuildTokenCacheKey(apiHost, repository, credentials);
        return tokenCache.TryGetValue<string>(key, out var token) ? token : null;
    }

    private void CacheToken(string apiHost, string repository, RegistryCredentials? credentials, BearerToken token)
    {
        var key = BuildTokenCacheKey(apiHost, repository, credentials);
        var ttlSeconds = Math.Max(TokenCacheFloorSeconds, token.ExpiresInSeconds - TokenCacheSafetyMarginSeconds);
        tokenCache.Set(key, token.AccessToken, TimeSpan.FromSeconds(ttlSeconds));
    }

    private sealed record BearerToken(string AccessToken, int ExpiresInSeconds);
}
