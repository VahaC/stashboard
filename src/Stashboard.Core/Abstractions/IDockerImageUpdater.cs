using Stashboard.Core.Enums;

namespace Stashboard.Core.Abstractions;

/// <summary>
/// V2.7 — decrypted snapshot of the data the updater needs to perform a
/// one-click "Update now" for a watch. Mirrors the entity-free pattern of
/// <see cref="DockerWatchProfile"/> so the controller can pass a single
/// argument and the updater stays free of EF / encryption concerns.
/// </summary>
public sealed record DockerUpdateProfile(
    string ImageReference,
    string RegistryHost,
    string Repository,
    string Tag,
    string ContainerName,
    DockerHostTransport HostTransport,
    /// <summary>Decrypted registry credentials for private images.
    /// <c>null</c> for anonymous pulls (public Docker Hub / GHCR).</summary>
    RegistryCredentials? RegistryCredentials,
    /// <summary>Mirrors <see cref="DockerWatchProfile.RegistryAuthType"/> —
    /// drives the auth strategy the registry pull request uses.</summary>
    RegistryAuthType RegistryAuthType,
    string? AwsAccessKeyId,
    string? AwsSecretAccessKey,
    string? AwsRegion,
    /// <summary>V5.2 — absolute path inside the Stashboard container to the
    /// connection's Compose project directory. When set (and the host is a
    /// local socket and the container carries the <c>com.docker.compose.service</c>
    /// label and the compose CLI is present) the updater shells out
    /// <c>docker compose</c> instead of doing the raw <c>Docker.DotNet</c>
    /// recreate. <c>null</c> keeps the V2.7 raw recreate.</summary>
    string? ComposeProjectPath = null);

/// <summary>
/// V2.7 — outcome of <see cref="IDockerImageUpdater.UpdateAsync"/>.
/// Captures the full state transition (previous digest → new digest)
/// plus a typed status so the controller can persist a
/// <see cref="Stashboard.Core.Entities.DockerUpdateAttemptEntity"/>
/// without re-interpreting the response.
/// </summary>
/// <remarks>
/// V3.2 — adds <see cref="HealthVerified"/> and <see cref="HealthVerifiedUtc"/>
/// so the audit row can distinguish "started successfully and passed
/// healthcheck" from "started but did not become healthy within the
/// verification window". When a container has no <c>HEALTHCHECK</c>
/// instruction the verification step short-circuits and
/// <see cref="HealthVerified"/> is still set to <c>true</c> — the contract
/// is "we observed the container in a healthy state", and "healthy" for an
/// unmonitored container is taken to mean "still running".
/// </remarks>
public sealed record DockerUpdateResult(
    DockerUpdateAttemptStatus Status,
    string? PreviousDigest,
    string? NewDigest,
    string? Error,
    bool HealthVerified = false,
    DateTime? HealthVerifiedUtc = null)
{
    public bool IsSuccess => Status == DockerUpdateAttemptStatus.Success;
}

/// <summary>
/// V2.7 — one-click "Update now". Pulls the latest image for the
/// configured tag on the target Docker host, then recreates the running
/// container with the new image while preserving every piece of the
/// original launch config (mounts, env, ports, network mode, labels —
/// including the Compose labels, so the container continues to look
/// Compose-managed afterwards).
/// </summary>
/// <remarks>
/// Follows the same "never throw" contract as <see cref="IDockerUpdateChecker"/>:
/// any failure (host unreachable, container missing, pull failure, recreate
/// failure) yields a result with the typed <see cref="DockerUpdateAttemptStatus"/>
/// instead of an exception, so the controller can always persist a row.
/// </remarks>
public interface IDockerImageUpdater
{
    Task<DockerUpdateResult> UpdateAsync(DockerUpdateProfile profile, CancellationToken cancellationToken = default);
}
