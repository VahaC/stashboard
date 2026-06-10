using Stashboard.Core.Enums;

namespace Stashboard.Core.Abstractions;

/// <summary>
/// V6.0 — decrypted snapshot of a Proxmox connection used by the checker for a
/// scan or a connection test. Entity-free so the test-connection endpoint can
/// build a transient profile from the request body without persisting anything
/// (mirrors <see cref="DockerWatchProfile"/>).
/// </summary>
/// <param name="ApiBaseUrl">Base URL, e.g. <c>https://pve.lan:8006</c>.</param>
/// <param name="NodeName">Proxmox node name (e.g. <c>pve</c>).</param>
/// <param name="ApiTokenId">Token id, <c>USER@REALM!TOKENID</c>.</param>
/// <param name="ApiTokenSecret">Decrypted token secret.</param>
/// <param name="SkipTlsVerify">Skip TLS validation (self-signed certs).</param>
/// <param name="Ssh">Decrypted SSH credentials for <c>pct exec</c>, or
/// <c>null</c> when SSH isn't configured (node-only checks still work).</param>
public sealed record ProxmoxConnectionProfile(
    string ApiBaseUrl,
    string NodeName,
    string ApiTokenId,
    string ApiTokenSecret,
    bool SkipTlsVerify,
    DockerSshCredentials? Ssh,
    /// <summary>V6.8.3 — PVE vs PBS, so the client picks the right auth header
    /// scheme and node/storage endpoints. Defaults to PVE for existing callers.</summary>
    ProxmoxServerType ServerType = ProxmoxServerType.Pve);

/// <summary>Per-guest outcome of a scan — one entry per node + LXC.</summary>
/// <param name="VmId"><c>0</c> for the node, the real vmid for an LXC.</param>
/// <param name="Name">Display name (LXC hostname / node name).</param>
/// <param name="GuestType">Node or LXC.</param>
/// <param name="IsRunning">Whether the guest was running at scan time.</param>
/// <param name="PendingUpdates">Pending update count, or <c>null</c> when it
/// couldn't be determined.</param>
/// <param name="Error">Per-guest error, or <c>null</c> on success.</param>
public sealed record ProxmoxGuestResult(
    int VmId,
    string Name,
    ProxmoxGuestType GuestType,
    bool IsRunning,
    int? PendingUpdates,
    string? Error,
    /// <summary>Primary IPv4 of a running LXC, or <c>null</c>.</summary>
    string? IpAddress = null,
    /// <summary>Seconds since start (running guests), else <c>null</c>.</summary>
    long? UptimeSeconds = null,
    int? CpuCores = null,
    /// <summary>Memory limit in bytes.</summary>
    long? MemoryBytes = null,
    /// <summary>Root disk size in bytes.</summary>
    long? DiskBytes = null,
    /// <summary>Raw semicolon-separated Proxmox tags.</summary>
    string? Tags = null);

/// <summary>
/// Outcome of <see cref="IProxmoxUpdateChecker.CheckAsync"/>. <see cref="Error"/>
/// is a connection-level failure (API unreachable / auth) that means
/// <see cref="Guests"/> couldn't be enumerated at all; per-guest problems are
/// reported on the individual <see cref="ProxmoxGuestResult"/> entries instead.
/// </summary>
public sealed record ProxmoxCheckResult(
    IReadOnlyList<ProxmoxGuestResult> Guests,
    DateTime CheckedAtUtc,
    string? Error);

/// <summary>Outcome of <see cref="IProxmoxUpdateChecker.TestConnectionAsync"/>.</summary>
/// <param name="ApiReachable">API responded to an authenticated probe.</param>
/// <param name="SshReachable">SSH connected + a trivial command ran. <c>null</c>
/// when SSH isn't configured (not tested).</param>
/// <param name="Error">First failure encountered, or <c>null</c>.</param>
public sealed record ProxmoxConnectionTestResult(
    bool ApiReachable,
    bool? SshReachable,
    string? Error);

/// <summary>
/// Orchestrates a Proxmox update scan: lists LXCs and reads the node's update
/// count over the REST API, then reads per-LXC counts over SSH. Pure
/// orchestration — it composes <see cref="IProxmoxApiClient"/> and
/// <see cref="IProxmoxGuestInspector"/> rather than talking to either transport
/// directly, so it stays unit-testable with mocked transports.
/// </summary>
public interface IProxmoxUpdateChecker
{
    /// <param name="disabledVmIds">V6.7 — vmids of LXCs whose update monitoring
    /// is turned off. They're still discovered (resource details come free from
    /// the listing) but the per-guest update count — the expensive IP + SSH
    /// step — is skipped, mirroring how a disabled Docker watch is left
    /// unchecked. The node row is never disabled.</param>
    Task<ProxmoxCheckResult> CheckAsync(
        ProxmoxConnectionProfile profile,
        IReadOnlySet<int>? disabledVmIds = null,
        CancellationToken cancellationToken = default);
    Task<ProxmoxConnectionTestResult> TestConnectionAsync(ProxmoxConnectionProfile profile, CancellationToken cancellationToken = default);
}
