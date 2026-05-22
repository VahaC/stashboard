using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Stashboard.Core.Abstractions;

namespace Stashboard.Infrastructure.Aws;

/// <summary>
/// V2.4 — talks to AWS ECR <c>GetAuthorizationToken</c> directly using a
/// hand-rolled SigV4 signer. Caches the returned token until ~30 minutes
/// before the ECR-reported expiry, keyed on access key id + region.
/// </summary>
/// <remarks>
/// The dependency-free signer is intentional — pulling the AWS SDK would
/// add ~5 MB of transitive deps just for one POST. SigV4 is mechanical:
/// canonical request → string-to-sign → derived signing key → HMAC chain.
/// See https://docs.aws.amazon.com/IAM/latest/UserGuide/create-signed-request.html
/// for the reference algorithm.
/// </remarks>
public sealed class AwsEcrTokenProvider(
    IHttpClientFactory httpClientFactory,
    IMemoryCache tokenCache,
    ILogger<AwsEcrTokenProvider> logger,
    TimeProvider? timeProvider = null) : IAwsEcrTokenProvider
{
    public const string HttpClientName = "aws-ecr";

    private const string Service = "ecr";
    private const string AmzTarget = "AmazonEC2ContainerRegistry_V20150921.GetAuthorizationToken";
    private const string AmzContentType = "application/x-amz-json-1.1";
    private const int CacheSafetyMarginSeconds = 1800; // 30 min before ECR expiry.
    private const int CacheFloorSeconds = 60;
    private const int CacheCeilingSeconds = 12 * 60 * 60;

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public async Task<AwsEcrTokenResult> GetAuthorizationTokenAsync(
        string accessKeyId,
        string secretAccessKey,
        string region,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accessKeyId) || string.IsNullOrWhiteSpace(secretAccessKey) || string.IsNullOrWhiteSpace(region))
        {
            return new AwsEcrTokenResult(AwsEcrTokenStatus.InvalidResponse, null, null, null,
                "accessKeyId, secretAccessKey and region are all required.");
        }

        var cacheKey = BuildCacheKey(accessKeyId, region);
        if (tokenCache.TryGetValue<AwsEcrTokenResult>(cacheKey, out var cached) && cached is not null)
            return cached;

        try
        {
            var fetched = await CallGetAuthorizationTokenAsync(
                accessKeyId, secretAccessKey, region, cancellationToken);
            if (fetched.IsSuccess && fetched.ExpiresAtUtc is not null)
            {
                var ttlSeconds = ComputeTtlSeconds(fetched.ExpiresAtUtc.Value);
                tokenCache.Set(cacheKey, fetched, TimeSpan.FromSeconds(ttlSeconds));
            }
            return fetched;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new AwsEcrTokenResult(AwsEcrTokenStatus.NetworkError, null, null, null,
                "AWS ECR request timed out.");
        }
        catch (HttpRequestException ex)
        {
            logger.LogDebug(ex, "AWS ECR GetAuthorizationToken request failed");
            return new AwsEcrTokenResult(AwsEcrTokenStatus.NetworkError, null, null, null, ex.Message);
        }
    }

    private async Task<AwsEcrTokenResult> CallGetAuthorizationTokenAsync(
        string accessKeyId, string secretAccessKey, string region, CancellationToken cancellationToken)
    {
        var host = $"ecr.{region}.amazonaws.com";
        var endpoint = $"https://{host}/";
        const string payload = "{}"; // No registry-ids = "the caller's account".
        var payloadBytes = Encoding.UTF8.GetBytes(payload);

        var now = _time.GetUtcNow().UtcDateTime;
        var amzDate = now.ToString("yyyyMMddTHHmmssZ");
        var dateStamp = now.ToString("yyyyMMdd");
        var payloadHash = Hex(SHA256.HashData(payloadBytes));

        // SigV4 canonical request.
        var canonicalHeaders =
            $"content-type:{AmzContentType}\n" +
            $"host:{host}\n" +
            $"x-amz-date:{amzDate}\n" +
            $"x-amz-target:{AmzTarget}\n";
        const string signedHeaders = "content-type;host;x-amz-date;x-amz-target";
        var canonicalRequest = $"POST\n/\n\n{canonicalHeaders}\n{signedHeaders}\n{payloadHash}";

        var credentialScope = $"{dateStamp}/{region}/{Service}/aws4_request";
        var stringToSign =
            $"AWS4-HMAC-SHA256\n{amzDate}\n{credentialScope}\n" +
            Hex(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest)));

        var signingKey = DeriveSigningKey(secretAccessKey, dateStamp, region, Service);
        var signature = Hex(HmacSha256(signingKey, stringToSign));

        var authorization =
            $"AWS4-HMAC-SHA256 Credential={accessKeyId}/{credentialScope}, " +
            $"SignedHeaders={signedHeaders}, Signature={signature}";

        var client = httpClientFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new ByteArrayContent(payloadBytes),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(AmzContentType);
        request.Headers.TryAddWithoutValidation("X-Amz-Date", amzDate);
        request.Headers.TryAddWithoutValidation("X-Amz-Target", AmzTarget);
        request.Headers.TryAddWithoutValidation("Authorization", authorization);

        using var response = await client.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (response.StatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.Unauthorized)
        {
            return new AwsEcrTokenResult(AwsEcrTokenStatus.Unauthorized, null, null, null,
                $"AWS ECR rejected the request ({(int)response.StatusCode}): {Truncate(body)}");
        }
        if (!response.IsSuccessStatusCode)
        {
            // ECR returns 400 with an error code in the body for things like
            // InvalidSignatureException / UnrecognizedClientException.
            return new AwsEcrTokenResult(AwsEcrTokenStatus.Unauthorized, null, null, null,
                $"AWS ECR returned HTTP {(int)response.StatusCode}: {Truncate(body)}");
        }

        return ParseTokenResponse(body);
    }

    /// <summary>Parses a successful <c>GetAuthorizationToken</c> body into a
    /// <see cref="RegistryCredentials"/> pair. ECR returns the token as
    /// Base64(<c>AWS:&lt;password&gt;</c>) so we split on the first colon.
    /// Public for direct unit-testing of the parsing logic — the production
    /// path always reaches it via <see cref="GetAuthorizationTokenAsync"/>.</summary>
    public static AwsEcrTokenResult ParseTokenResponse(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("authorizationData", out var arr)
                || arr.ValueKind != JsonValueKind.Array
                || arr.GetArrayLength() == 0)
            {
                return new AwsEcrTokenResult(AwsEcrTokenStatus.InvalidResponse, null, null, null,
                    "AWS ECR response missing authorizationData[0].");
            }

            var first = arr[0];
            var encoded = first.TryGetProperty("authorizationToken", out var tokenEl)
                && tokenEl.ValueKind == JsonValueKind.String
                ? tokenEl.GetString()
                : null;
            if (string.IsNullOrEmpty(encoded))
            {
                return new AwsEcrTokenResult(AwsEcrTokenStatus.InvalidResponse, null, null, null,
                    "AWS ECR response missing authorizationToken.");
            }

            string decoded;
            try { decoded = Encoding.UTF8.GetString(Convert.FromBase64String(encoded)); }
            catch (FormatException)
            {
                return new AwsEcrTokenResult(AwsEcrTokenStatus.InvalidResponse, null, null, null,
                    "AWS ECR authorizationToken was not valid Base64.");
            }

            var colon = decoded.IndexOf(':');
            if (colon <= 0 || colon == decoded.Length - 1)
            {
                return new AwsEcrTokenResult(AwsEcrTokenStatus.InvalidResponse, null, null, null,
                    "AWS ECR authorizationToken did not decode to 'username:password'.");
            }

            var username = decoded[..colon];
            var password = decoded[(colon + 1)..];

            var proxyEndpoint = first.TryGetProperty("proxyEndpoint", out var pe) && pe.ValueKind == JsonValueKind.String
                ? pe.GetString()
                : null;
            DateTime? expiresAtUtc = null;
            if (first.TryGetProperty("expiresAt", out var exp))
            {
                if (exp.ValueKind == JsonValueKind.Number && exp.TryGetDouble(out var seconds))
                    expiresAtUtc = DateTime.UnixEpoch.AddSeconds(seconds);
                else if (exp.ValueKind == JsonValueKind.String
                    && DateTime.TryParse(exp.GetString(), null,
                        System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                        out var parsedDate))
                    expiresAtUtc = parsedDate;
            }

            return new AwsEcrTokenResult(AwsEcrTokenStatus.Ok,
                new RegistryCredentials(username, password),
                proxyEndpoint, expiresAtUtc, null);
        }
        catch (JsonException ex)
        {
            return new AwsEcrTokenResult(AwsEcrTokenStatus.InvalidResponse, null, null, null,
                $"AWS ECR response could not be parsed: {ex.Message}");
        }
    }

    private int ComputeTtlSeconds(DateTime expiresAtUtc)
    {
        var seconds = (int)Math.Floor((expiresAtUtc - _time.GetUtcNow().UtcDateTime).TotalSeconds) - CacheSafetyMarginSeconds;
        return Math.Clamp(seconds, CacheFloorSeconds, CacheCeilingSeconds);
    }

    private static string BuildCacheKey(string accessKeyId, string region) =>
        $"aws-ecr:{region}|{Hex(SHA256.HashData(Encoding.UTF8.GetBytes(accessKeyId)))[..16]}";

    private static byte[] DeriveSigningKey(string secret, string dateStamp, string region, string service)
    {
        var kDate = HmacSha256(Encoding.UTF8.GetBytes("AWS4" + secret), dateStamp);
        var kRegion = HmacSha256(kDate, region);
        var kService = HmacSha256(kRegion, service);
        return HmacSha256(kService, "aws4_request");
    }

    private static byte[] HmacSha256(byte[] key, string data)
    {
        using var hmac = new HMACSHA256(key);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
    }

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    private static string Truncate(string body) =>
        string.IsNullOrEmpty(body) ? string.Empty : body.Length <= 240 ? body : body[..240] + "…";
}
