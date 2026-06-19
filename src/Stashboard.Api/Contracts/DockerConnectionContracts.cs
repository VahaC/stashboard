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
    /// <summary>V7.1 — host-side prefix of the optional Compose path mapping
    /// (LocalSocket only). Project directories themselves are discovered from
    /// the containers' <c>com.docker.compose.project.working_dir</c> labels.</summary>
    string? ComposePathHostPrefix,
    /// <summary>V7.1 — container-side prefix of the optional Compose path mapping.</summary>
    string? ComposePathContainerPrefix,
    /// <summary>V5.3 — whether this connection has opted in to the browser host
    /// terminal. Only meaningful for SSH hosts; the server also requires the
    /// global <c>Stashboard:AllowHostShell</c> flag before honouring it.</summary>
    bool AllowHostShell,
    /// <summary>V5.7 — whether this connection has opted in to the browser
    /// container-exec terminal. Works for every host type; the server also
    /// requires the global <c>Stashboard:AllowContainerExec</c> switch.</summary>
    bool AllowExec,
    /// <summary>V5.5 — whether this connection participates in the background
    /// image-prune sweep. Default <c>true</c> on new + upgraded connections.</summary>
    bool AllowImagePrune,
    /// <summary>V5.5 — whether the sweep is allowed to remove non-dangling
    /// "unused" images on top of dangling ones. Default <c>false</c>.</summary>
    bool PruneUnusedImages,
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
    [MaxLength(200)] string? SshRemoteSocketPath = null,
    /// <summary>V7.1 — host-side prefix of the optional Compose path mapping
    /// (LocalSocket only; both prefixes must be set together to take effect).
    /// Plaintext (not a secret).</summary>
    [MaxLength(500)] string? ComposePathHostPrefix = null,
    /// <summary>V7.1 — container-side prefix of the optional Compose path
    /// mapping (the bind-mount target of <c>ComposePathHostPrefix</c>).</summary>
    [MaxLength(500)] string? ComposePathContainerPrefix = null,
    /// <summary>V5.3 — opt this connection in to the browser host terminal.
    /// Only honoured for SSH hosts (and only when the global
    /// <c>Stashboard:AllowHostShell</c> flag is on). Defaults to <c>false</c>.</summary>
    bool AllowHostShell = false,
    /// <summary>V5.7 — opt this connection in to the browser container-exec
    /// terminal. Works for every host type (the global
    /// <c>Stashboard:AllowContainerExec</c> switch is also required).
    /// Defaults to <c>false</c>.</summary>
    bool AllowExec = false,
    /// <summary>V5.5 — whether this connection participates in the
    /// background image-prune sweep. Defaults to <c>true</c>.</summary>
    bool AllowImagePrune = true,
    /// <summary>V5.5 — opt this connection in to pruning non-dangling
    /// "unused" images on top of dangling ones. Defaults to <c>false</c>.</summary>
    bool PruneUnusedImages = false);

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
    Guid? WebResourceId,
    /// <summary>V7.8 — the resolved card avatar as a data URI: the user's custom
    /// upload when present, otherwise the official dashboard-icons logo derived
    /// from the image, otherwise <c>null</c> (the UI falls back to a placeholder).</summary>
    string? IconDataUri = null);

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

/// <summary>
/// V7.8 — custom card-icon upload. The image travels as a base64 data URI in a
/// JSON body (the browser reads the file with <c>FileReader.readAsDataURL</c>),
/// deliberately avoiding <c>multipart/form-data</c> / <c>IFormFile</c>. Shared by
/// the Docker container-icon and Proxmox guest-icon endpoints.
/// </summary>
public sealed record ContainerIconUploadRequest(string DataUri);

/// <summary>V7.8 — validation for an uploaded image data URI
/// (<c>data:image/&lt;type&gt;;base64,&lt;data&gt;</c>), capped at 2&#160;MB decoded.</summary>
public static class ImageDataUri
{
    private const int MaxBytes = 2 * 1024 * 1024;
    private const string Marker = ";base64,";

    public static bool TryValidate(string? dataUri, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(dataUri)
            || !dataUri.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
        {
            error = "Not an image.";
            return false;
        }

        var markerIndex = dataUri.IndexOf(Marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            error = "Not a base64 data URI.";
            return false;
        }

        var payload = dataUri[(markerIndex + Marker.Length)..];
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(payload);
        }
        catch (FormatException)
        {
            error = "Invalid base64 image data.";
            return false;
        }

        if (bytes.Length == 0)
        {
            error = "Empty image.";
            return false;
        }
        if (bytes.Length > MaxBytes)
        {
            error = "Image is too large (max 2 MB).";
            return false;
        }
        return true;
    }
}

// ── V5.4 — Compose project bulk update ─────────────────────────────────────

/// <summary>
/// V5.4 — response from the bulk "Update project" endpoint. Carries the
/// aggregate parent audit row plus one child row per service so the
/// frontend can render success / failure inline on each container card.
/// </summary>
public sealed record DockerProjectUpdateResponse(
    DockerUpdateAttemptResponse Parent,
    IReadOnlyList<DockerUpdateAttemptResponse> Services,
    /// <summary>Which dispatch path the updater took ("Compose" =
    /// <c>docker compose</c> shell-out; "Recreate" = per-service raw recreate
    /// fallback). Surfaced so the UI can show the user which mode ran.</summary>
    string Mode);

/// <summary>V3.5 — exposes server feature flags the frontend needs to
/// gate UI affordances (e.g. hiding the Remove button when the flag is
/// off). Read-only — never carries secret material.</summary>
public sealed record StashboardFeaturesResponse(
    bool AllowContainerRemoval,
    /// <summary>V5.3 — global master switch for the browser host terminal.
    /// The UI uses it to decide whether the Terminal tab can ever go live
    /// (a connection's own <c>AllowHostShell</c> opt-in is also required).</summary>
    bool AllowHostShell,
    /// <summary>V5.7 — global master switch for the browser container-exec
    /// terminal. The UI uses it to decide whether the Exec tab can go live
    /// (a connection's own <c>AllowExec</c> opt-in is also required).</summary>
    bool AllowContainerExec,
    /// <summary>V6.6 — global master switch for the browser Proxmox LXC console.
    /// The UI uses it to decide whether the Console tab can go live (a host's
    /// own <c>AllowConsole</c> opt-in + SSH credentials are also required).</summary>
    bool AllowProxmoxConsole,
    /// <summary>V6.7.1 — global master switch for one-click Proxmox "Update now".
    /// The UI uses it to decide whether the Update affordances can go live (a
    /// host's own <c>AllowUpdates</c> opt-in + SSH credentials are also
    /// required).</summary>
    bool AllowProxmoxUpdates,
    /// <summary>V6.13 — global master switch for destroying an LXC. The UI uses it
    /// to decide whether the Destroy affordance can go live (a host's own
    /// <c>AllowDestroy</c> opt-in is also required, and the guest must be
    /// stopped).</summary>
    bool AllowProxmoxDestroy,
    /// <summary>V6.13.1 — global master switch for creating an LXC. The UI uses it
    /// to decide whether the New LXC affordance can go live (a host's own
    /// <c>AllowCreate</c> opt-in is also required).</summary>
    bool AllowProxmoxCreate);
