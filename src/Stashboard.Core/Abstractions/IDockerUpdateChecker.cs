using Stashboard.Core.Enums;

namespace Stashboard.Core.Abstractions;



/// <summary>
/// Decrypted snapshot of a Docker watch used by <see cref="IDockerUpdateChecker"/>
/// to perform a check or connection test. Keeping the orchestrator entity-free
/// lets the test-connection endpoint build a transient profile from the request
/// body without persisting anything.
/// </summary>
/// <param name="ImageReference">Raw user input, kept for diagnostics.</param>
/// <param name="RegistryHost">Parsed canonical registry host (e.g. <c>docker.io</c>).</param>
/// <param name="Repository">Parsed repository path (e.g. <c>library/nginx</c>).</param>
/// <param name="Tag">Tag to check (e.g. <c>latest</c>).</param>
/// <param name="HostType">Docker host transport.</param>
/// <param name="HostUrl">Docker host URL when <see cref="HostType"/> is <c>TcpTls</c>.</param>
/// <param name="ContainerName">Container name (or id) to inspect on the host.</param>
/// <param name="Tls">Decrypted TLS material when applicable.</param>
/// <param name="RegistryCredentials">Decrypted registry credentials when applicable.</param>
/// <param name="TagPatternFilter">Optional .NET regex applied to the registry's
/// tag list when resolving "latest" (V2.1). <c>null</c> means "use the pinned
/// <see cref="Tag"/> directly". When set, the orchestrator lists tags, filters
/// by the pattern, picks the highest semver / lexicographic candidate, and
/// compares its digest against the running container.</param>
public sealed record DockerWatchProfile(
    string ImageReference,
    string RegistryHost,
    string Repository,
    string Tag,
    DockerHostType HostType,
    string? HostUrl,
    string ContainerName,
    DockerTlsMaterial? Tls,
    RegistryCredentials? RegistryCredentials,
    string? TagPatternFilter = null,
    /// <summary>V2.3 — optional GitHub PAT, used by the release-notes
    /// enrichment when <see cref="RegistryHost"/> is <c>ghcr.io</c>. <c>null</c>
    /// for public repositories.</summary>
    string? GitHubPat = null,
    /// <summary>V2.4 — registry authentication strategy. Defaults to
    /// <see cref="RegistryAuthType.Auto"/> to preserve V1/V2.x behaviour.</summary>
    RegistryAuthType RegistryAuthType = RegistryAuthType.Auto,
    /// <summary>V2.4 — AWS access key id, only used when
    /// <see cref="RegistryAuthType"/> is <see cref="RegistryAuthType.AwsEcr"/>.</summary>
    string? AwsAccessKeyId = null,
    /// <summary>V2.4 — AWS secret access key, only used when
    /// <see cref="RegistryAuthType"/> is <see cref="RegistryAuthType.AwsEcr"/>.</summary>
    string? AwsSecretAccessKey = null,
    /// <summary>V2.4 — AWS region for ECR (e.g. <c>eu-central-1</c>).</summary>
    string? AwsRegion = null,
    /// <summary>V2.5 — decrypted SSH credentials, populated when
    /// <see cref="HostType"/> is <see cref="DockerHostType.Ssh"/>.</summary>
    DockerSshCredentials? Ssh = null)
{
    /// <summary>V2.5 — convenience accessor that bundles the host transport
    /// fields so checker and test paths talk to <see cref="IDockerHostClient"/>
    /// through a single argument.</summary>
    public DockerHostTransport HostTransport => new(HostType, HostUrl, Tls, Ssh);
}

/// <summary>
/// Outcome of <see cref="IDockerUpdateChecker.CheckAsync"/>. <see cref="Status"/>
/// is what the dashboard renders; everything else is detail surfaced in the
/// modal/tooltip.
/// </summary>
public sealed record DockerCheckResult(
    DockerUpdateStatus Status,
    string? CurrentDigest,
    string? LatestDigest,
    string? CurrentVersionTag,
    string? LatestVersionTag,
    string? Error,
    DateTime CheckedAtUtc,
    /// <summary>V2.3 — GitHub release URL for <c>LatestVersionTag</c> when the
    /// registry is GHCR and a matching release exists; <c>null</c> otherwise.
    /// Enrichment is best-effort — a missing release or a network blip never
    /// degrades the parent <see cref="Status"/>.</summary>
    string? LatestReleaseUrl = null,
    /// <summary>V2.3 — release notes body (markdown) for the same tag,
    /// truncated to 2 000 chars by the GitHub client so the persisted column
    /// stays bounded.</summary>
    string? LatestReleaseBody = null);

/// <summary>Outcome of <see cref="IDockerUpdateChecker.TestConnectionAsync"/>.</summary>
public sealed record DockerConnectionTestResult(
    bool DockerHostReachable,
    bool ContainerFound,
    bool RegistryReachable,
    string? Error);

/// <summary>
/// Compares the digest of the image currently running in a container against
/// the digest published in its registry. Composes the host client (Phase 3)
/// and the registry client (Phase 2) — it does not talk to either directly,
/// so it remains pure orchestration with no transport concerns.
/// </summary>
public interface IDockerUpdateChecker
{
    Task<DockerCheckResult> CheckAsync(DockerWatchProfile profile, CancellationToken cancellationToken = default);
    Task<DockerConnectionTestResult> TestConnectionAsync(DockerWatchProfile profile, CancellationToken cancellationToken = default);
}
