namespace Stashboard.Core.Abstractions;

/// <summary>
/// Outcome categories surfaced by <see cref="IRegistryClient.GetManifestDigestAsync"/>.
/// Maps directly onto the upstream HTTP failure modes plus our own envelope failures
/// (no <c>Docker-Content-Digest</c> header, broken JSON, etc.).
/// </summary>
public enum RegistryManifestStatus
{
    Ok = 0,
    NotFound = 1,
    Unauthorized = 2,
    RateLimited = 3,
    NetworkError = 4,
    InvalidResponse = 5,
}

/// <summary>
/// Result returned by <see cref="IRegistryClient.GetManifestDigestAsync"/>. On success the
/// <see cref="Digest"/> is the registry's reported <c>Docker-Content-Digest</c> for the
/// requested reference; for multi-arch images this is the digest of the manifest <i>list</i>,
/// which is what the local Docker daemon stores against the running container.
/// </summary>
/// <param name="Status">Categorical outcome — always set.</param>
/// <param name="Digest">Resolved digest (<c>sha256:…</c>) on success, otherwise <c>null</c>.</param>
/// <param name="ContentType">Manifest media type that backed the digest (helps distinguish
/// single-arch from multi-arch manifests in logs).</param>
/// <param name="Error">Human-readable error description on failure.</param>
/// <param name="FetchedAtUtc">When the call was issued.</param>
public sealed record RegistryManifestResult(
    RegistryManifestStatus Status,
    string? Digest,
    string? ContentType,
    string? Error,
    DateTime FetchedAtUtc)
{
    public bool IsSuccess => Status == RegistryManifestStatus.Ok && !string.IsNullOrEmpty(Digest);
}

/// <summary>Credentials for a private registry. Pre-decrypted at the call site.</summary>
public sealed record RegistryCredentials(string Username, string Password);

/// <summary>
/// V2.4 — pre-resolved authentication context handed to <see cref="IRegistryClient"/>.
/// Bundles the static credentials (if any) with the dynamic provider the client
/// should use to obtain an HTTP Basic pair at call time when the auth strategy
/// requires it (currently: AWS ECR via <see cref="IAwsEcrTokenProvider"/>).
/// </summary>
/// <param name="AuthType">Chosen strategy.</param>
/// <param name="Credentials">Pre-decrypted username/password for the
/// <see cref="Stashboard.Core.Enums.RegistryAuthType.Auto"/> and
/// <see cref="Stashboard.Core.Enums.RegistryAuthType.Basic"/> strategies.
/// Null for fully anonymous calls.</param>
/// <param name="AwsAccessKeyId">AWS access key id for ECR.</param>
/// <param name="AwsSecretAccessKey">AWS secret access key for ECR.</param>
/// <param name="AwsRegion">AWS region for ECR (e.g. <c>eu-central-1</c>).</param>
public sealed record RegistryAuthContext(
    Stashboard.Core.Enums.RegistryAuthType AuthType,
    RegistryCredentials? Credentials,
    string? AwsAccessKeyId = null,
    string? AwsSecretAccessKey = null,
    string? AwsRegion = null)
{
    /// <summary>Convenience: anonymous, Auto strategy.</summary>
    public static RegistryAuthContext Anonymous { get; } =
        new(Stashboard.Core.Enums.RegistryAuthType.Auto, null);

    /// <summary>Convenience: bundles legacy username/password credentials into
    /// an Auto-strategy context — the historical default.</summary>
    public static RegistryAuthContext FromCredentials(RegistryCredentials? credentials) =>
        new(Stashboard.Core.Enums.RegistryAuthType.Auto, credentials);
}

/// <summary>
/// Result returned by <see cref="IRegistryClient.ListTagsAsync"/>. <see cref="Tags"/>
/// is the raw, registry-provided list (no client-side sorting — the OCI spec does
/// not guarantee order, so callers that care about ordering must sort themselves).
/// </summary>
/// <param name="Status">Categorical outcome — always set; reuses
/// <see cref="RegistryManifestStatus"/> so the failure taxonomy is consistent.</param>
/// <param name="Tags">Tag names returned by the registry (empty on failure or
/// when the repository has no tags).</param>
/// <param name="Error">Human-readable error description on failure.</param>
/// <param name="FetchedAtUtc">When the call was issued.</param>
public sealed record RegistryTagListResult(
    RegistryManifestStatus Status,
    IReadOnlyList<string> Tags,
    string? Error,
    DateTime FetchedAtUtc)
{
    public bool IsSuccess => Status == RegistryManifestStatus.Ok;
}

/// <summary>
/// Talks to OCI Distribution v2 registries (Docker Hub + GHCR in V1, generic OCI as the
/// substrate) to resolve the current digest of an image reference.
/// </summary>
public interface IRegistryClient
{
    /// <summary>
    /// Resolves the registry's current digest for <paramref name="repository"/>:<paramref name="reference"/>.
    /// Handles anonymous and Bearer-token flows transparently; tokens are cached so repeated
    /// checks for the same image don't re-hit the auth endpoint.
    /// </summary>
    /// <param name="registryHost">Canonical registry host (e.g. <c>docker.io</c>, <c>ghcr.io</c>).
    /// Implementations transparently route Docker Hub traffic to the real API host.</param>
    /// <param name="repository">Full repository path (e.g. <c>library/nginx</c>, <c>owner/repo</c>).</param>
    /// <param name="reference">Tag or digest (e.g. <c>latest</c>, <c>v1.2.3</c>, <c>sha256:…</c>).</param>
    /// <param name="auth">Pre-resolved auth context (strategy + credentials or AWS keys).</param>
    Task<RegistryManifestResult> GetManifestDigestAsync(
        string registryHost,
        string repository,
        string reference,
        RegistryAuthContext auth,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists tags published for <paramref name="repository"/> via the OCI Distribution
    /// <c>GET /v2/{name}/tags/list</c> endpoint. Powers the V2.1 tag-pattern filter
    /// (skip pre-release / non-matching tags when deciding what "latest" means).
    /// </summary>
    /// <remarks>
    /// V2.1 fetches a single page bounded by <paramref name="pageSize"/> (the
    /// registry may return fewer). Pagination via the <c>Link</c> header is not
    /// implemented yet — sufficient for current use cases since typical
    /// repositories return a few hundred tags per page and the pattern filter
    /// narrows that to a handful of candidates client-side.
    /// </remarks>
    Task<RegistryTagListResult> ListTagsAsync(
        string registryHost,
        string repository,
        RegistryAuthContext auth,
        int pageSize = 200,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Backwards-compatible overloads of <see cref="IRegistryClient"/> that take the
/// V1 <see cref="RegistryCredentials"/> directly. Pre-V2.4 call sites and tests
/// keep working through these.
/// </summary>
public static class RegistryClientExtensions
{
    public static Task<RegistryManifestResult> GetManifestDigestAsync(
        this IRegistryClient client,
        string registryHost,
        string repository,
        string reference,
        RegistryCredentials? credentials,
        CancellationToken cancellationToken = default) =>
        client.GetManifestDigestAsync(
            registryHost, repository, reference,
            RegistryAuthContext.FromCredentials(credentials), cancellationToken);

    public static Task<RegistryTagListResult> ListTagsAsync(
        this IRegistryClient client,
        string registryHost,
        string repository,
        RegistryCredentials? credentials,
        int pageSize = 200,
        CancellationToken cancellationToken = default) =>
        client.ListTagsAsync(
            registryHost, repository,
            RegistryAuthContext.FromCredentials(credentials), pageSize, cancellationToken);
}
