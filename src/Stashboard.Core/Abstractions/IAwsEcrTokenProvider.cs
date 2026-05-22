namespace Stashboard.Core.Abstractions;

/// <summary>
/// Outcome categories returned by <see cref="IAwsEcrTokenProvider.GetAuthorizationTokenAsync"/>.
/// Mirrors the shape of <see cref="RegistryManifestStatus"/> so the registry client
/// can re-emit the failure with consistent semantics.
/// </summary>
public enum AwsEcrTokenStatus
{
    /// <summary>Token decoded successfully and <see cref="AwsEcrTokenResult.Credentials"/> is populated.</summary>
    Ok = 0,

    /// <summary>ECR rejected the IAM credentials (HTTP 400 with an
    /// <c>InvalidSignatureException</c> / <c>UnrecognizedClientException</c>,
    /// or 403).</summary>
    Unauthorized = 1,

    /// <summary>Network / DNS / timeout.</summary>
    NetworkError = 2,

    /// <summary>Response body could not be parsed into the expected shape.</summary>
    InvalidResponse = 3,
}

/// <summary>
/// Result returned by <see cref="IAwsEcrTokenProvider.GetAuthorizationTokenAsync"/>.
/// </summary>
/// <param name="Status">Categorical outcome — always set.</param>
/// <param name="Credentials">Decoded <c>AWS:&lt;auth-token&gt;</c> pair the
/// registry client uses as HTTP Basic credentials. <c>null</c> on failure.</param>
/// <param name="ProxyEndpoint">Registry endpoint reported by ECR (e.g.
/// <c>https://123456789012.dkr.ecr.eu-central-1.amazonaws.com</c>).
/// Currently informational — the registry client uses the host parsed from
/// the image reference.</param>
/// <param name="ExpiresAtUtc">ECR-reported absolute expiry. The provider
/// caches the token until ~30 minutes before this, but we also surface it
/// so callers can log it.</param>
/// <param name="Error">Human-readable error description on failure.</param>
public sealed record AwsEcrTokenResult(
    AwsEcrTokenStatus Status,
    RegistryCredentials? Credentials,
    string? ProxyEndpoint,
    DateTime? ExpiresAtUtc,
    string? Error)
{
    public bool IsSuccess => Status == AwsEcrTokenStatus.Ok && Credentials is not null;
}

/// <summary>
/// V2.4 — resolves AWS ECR Basic-auth credentials for the registry client.
/// Wraps the ECR <c>GetAuthorizationToken</c> API behind a 12-hour
/// <see cref="Microsoft.Extensions.Caching.Memory.IMemoryCache"/>-backed
/// token cache, keyed on the AWS access key id + region.
/// </summary>
/// <remarks>
/// We deliberately do not depend on the AWS SDK — the call is a single
/// signed POST against <c>ecr.{region}.amazonaws.com</c>, and dragging in
/// the SDK + its transitive graph for one endpoint is not worth the
/// binary-size tax. The implementation hand-rolls SigV4 the same way the
/// official CLI does internally.
/// </remarks>
public interface IAwsEcrTokenProvider
{
    /// <summary>Resolves an authorization token for <paramref name="accessKeyId"/> /
    /// <paramref name="secretAccessKey"/> in <paramref name="region"/>. Returns
    /// the cached token when one is still valid; otherwise calls ECR and caches
    /// the new pair.</summary>
    Task<AwsEcrTokenResult> GetAuthorizationTokenAsync(
        string accessKeyId,
        string secretAccessKey,
        string region,
        CancellationToken cancellationToken = default);
}
