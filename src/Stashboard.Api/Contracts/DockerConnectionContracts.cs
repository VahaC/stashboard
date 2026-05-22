using System.ComponentModel.DataAnnotations;
using Stashboard.Core.Enums;

namespace Stashboard.Api.Contracts;

/// <summary>
/// Read model for a user-level Docker daemon connection. Never carries
/// decrypted TLS / SSH material — only presence flags so the UI can render
/// "configured / not configured" without leaking values.
/// <see cref="UsageCount"/> tells the UI how many services currently
/// reference this connection (powers the delete-blocked warning).
/// </summary>
/// <remarks>
/// V2.5 — adds SSH fields. <see cref="SshHost"/> / <see cref="SshPort"/> /
/// <see cref="SshUsername"/> / <see cref="SshRemoteSocketPath"/> are plaintext
/// (not secrets); <see cref="HasSshPrivateKey"/> and
/// <see cref="HasSshPrivateKeyPassphrase"/> are presence flags so the UI knows
/// when the private key / passphrase is configured server-side without ever
/// returning the PEM blob.
/// </remarks>
public sealed record DockerConnectionResponse(
    Guid Id,
    string Name,
    DockerHostType HostType,
    string? HostUrl,
    bool HasTlsConfigured,
    /// <summary>V2.5 — SSH host name / IP for Ssh connections.</summary>
    string? SshHost,
    /// <summary>V2.5 — SSH port (default 22).</summary>
    int? SshPort,
    /// <summary>V2.5 — SSH login username.</summary>
    string? SshUsername,
    /// <summary>V2.5 — whether an SSH private key is configured server-side.
    /// The PEM blob itself is never returned.</summary>
    bool HasSshPrivateKey,
    /// <summary>V2.5 — whether a private-key passphrase is configured.</summary>
    bool HasSshPrivateKeyPassphrase,
    /// <summary>V2.5 — remote socket path (default <c>/var/run/docker.sock</c>).</summary>
    string? SshRemoteSocketPath,
    int UsageCount,
    DateTime CreatedUtc,
    DateTime UpdatedUtc);

/// <summary>
/// Create-or-update payload for a Docker connection. TLS and SSH secret
/// fields honour the tri-state <see cref="SecretValueAction"/> semantics so
/// an edit can preserve the persisted encrypted value without round-tripping
/// it. Non-secret SSH fields (host / port / username / remote socket path)
/// travel in plaintext.
/// </summary>
public sealed record DockerConnectionUpsertRequest(
    [Required, MaxLength(100)] string Name,
    DockerHostType HostType,
    [MaxLength(500)] string? HostUrl,
    SecretValueUpsert? TlsCaCert,
    SecretValueUpsert? TlsClientCert,
    SecretValueUpsert? TlsClientKey,
    /// <summary>V2.5 — SSH host name / IP. Required when
    /// <see cref="HostType"/> is <see cref="DockerHostType.Ssh"/>.</summary>
    [MaxLength(200)] string? SshHost = null,
    /// <summary>V2.5 — SSH port (default 22).</summary>
    int? SshPort = null,
    /// <summary>V2.5 — SSH login username. Required for SSH connections.</summary>
    [MaxLength(100)] string? SshUsername = null,
    /// <summary>V2.5 — PEM-encoded private key (tri-state secret).</summary>
    SecretValueUpsert? SshPrivateKey = null,
    /// <summary>V2.5 — optional passphrase for the private key (tri-state secret).</summary>
    SecretValueUpsert? SshPrivateKeyPassphrase = null,
    /// <summary>V2.5 — remote socket path override (default
    /// <c>/var/run/docker.sock</c>; useful for rootless Docker).</summary>
    [MaxLength(200)] string? SshRemoteSocketPath = null);

/// <summary>
/// Test-connection request — same shape as the upsert but never persisted.
/// The <c>Name</c> field is omitted because the ping doesn't touch the row.
/// </summary>
public sealed record DockerConnectionPingRequest(
    DockerHostType HostType,
    [MaxLength(500)] string? HostUrl,
    SecretValueUpsert? TlsCaCert,
    SecretValueUpsert? TlsClientCert,
    SecretValueUpsert? TlsClientKey,
    /// <summary>V2.5 — SSH host name / IP for Ssh connections.</summary>
    [MaxLength(200)] string? SshHost = null,
    /// <summary>V2.5 — SSH port (default 22).</summary>
    int? SshPort = null,
    /// <summary>V2.5 — SSH login username.</summary>
    [MaxLength(100)] string? SshUsername = null,
    /// <summary>V2.5 — PEM private key (tri-state secret).</summary>
    SecretValueUpsert? SshPrivateKey = null,
    /// <summary>V2.5 — passphrase for the private key (tri-state secret).</summary>
    SecretValueUpsert? SshPrivateKeyPassphrase = null,
    /// <summary>V2.5 — remote socket path override.</summary>
    [MaxLength(200)] string? SshRemoteSocketPath = null);

public sealed record DockerConnectionPingResponse(
    bool DockerHostReachable,
    string? Error);

/// <summary>
/// A container running on the connected Docker daemon. Surfaced to the UI so the
/// user can pick which one to track instead of typing the name by hand.
/// <see cref="ComposeService"/> / <see cref="ComposeProject"/> / <see cref="ComposeConfigFiles"/>
/// come from the standard <c>com.docker.compose.*</c> labels and let the UI hint
/// at the right update command.
/// </summary>
public sealed record DockerContainerInfo(
    string Name,
    string Image,
    string ImageId,
    string State,
    string Status,
    string? ComposeProject,
    string? ComposeService,
    string? ComposeConfigFiles);

/// <summary>
/// Manual update command(s) generated from <c>ContainerInspect</c> of a live
/// container. Returns the compose-style command when the container is managed
/// by docker compose; otherwise falls back to a <c>docker pull</c>/<c>run</c>
/// approximation. <see cref="Warning"/> carries any caveats the user should see.
/// </summary>
public sealed record DockerUpdateCommandResponse(
    string Command,
    string Source,
    string? Warning);

// ── V3.5 — Docker instances page ──────────────────────────────────────────

/// <summary>
/// V3.5 — wire contract for a single container card on the Docker
/// instances page. Fields beyond <see cref="DockerContainerInfo"/>:
/// container id, created timestamp, port mappings, and — when one exists
/// — a back-pointer to the user's <c>DockerWatch</c> that tracks this
/// container, so the card can offer a "Open in service" deep link
/// instead of duplicating the watch modal here.
/// </summary>
public sealed record DockerContainerCard(
    string Id,
    string Name,
    string Image,
    string ImageId,
    string State,
    string Status,
    DateTime? CreatedUtc,
    IReadOnlyList<DockerContainerPortMapping> Ports,
    string? ComposeProject,
    string? ComposeService,
    /// <summary>The user's <c>DockerWatch.Id</c> tracking this container, if
    /// one exists. The page uses this to render a "Open in service" link.</summary>
    Guid? WatchId,
    /// <summary>The user's <c>WebResource.Id</c> tracking this container, if
    /// a watch exists. Paired with <see cref="WatchId"/> so the link can route
    /// to <c>/?service={id}&amp;watch={watchId}</c>.</summary>
    Guid? WebResourceId);

public sealed record DockerContainerPortMapping(
    int PrivatePort,
    int? PublicPort,
    string Type,
    string? Ip);

/// <summary>
/// V3.5 — response from a lifecycle action endpoint. <see cref="Attempt"/>
/// is the new audit row written for the action.
/// </summary>
public sealed record DockerContainerActionResponse(
    DockerUpdateAttemptResponse Attempt);

/// <summary>V3.5 — exposes server feature flags the frontend needs to
/// gate UI affordances (e.g. hiding the Remove button when the flag is
/// off). Read-only — never carries secret material.</summary>
public sealed record StashboardFeaturesResponse(
    bool AllowContainerRemoval);
