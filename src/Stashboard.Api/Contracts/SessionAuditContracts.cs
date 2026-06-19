using Stashboard.Core.Enums;

namespace Stashboard.Api.Contracts;

// V5.8 — read-only wire contracts for the session audit viewer. These surface
// the rows V5.3 (host terminal) and V5.7 (container exec) already persist; no
// new auditing happens here. The fields are denormalised on the entities
// precisely so the history survives a connection rename / delete.

/// <summary>
/// V5.8 — wire form of a single <see cref="Stashboard.Core.Entities.HostShellSessionEntity"/>
/// row for the Audit page's <em>Host terminal</em> tab.
/// </summary>
public sealed record HostShellSessionResponse(
    Guid Id,
    Guid? DockerConnectionId,
    string? ConnectionName,
    string? SshHost,
    string? SshUsername,
    DateTime StartedUtc,
    DateTime? EndedUtc,
    long BytesFromClient,
    long BytesToClient,
    HostShellSessionEndReason EndReason,
    string? Error)
{
    /// <summary>Convenience flag for the UI: the session is still open.</summary>
    public bool Active => EndedUtc is null;
}

/// <summary>
/// V5.8 — wire form of a single <see cref="Stashboard.Core.Entities.DockerExecSessionEntity"/>
/// row for the Audit page's <em>Container exec</em> tab.
/// </summary>
public sealed record DockerExecSessionResponse(
    Guid Id,
    Guid? DockerConnectionId,
    string? ConnectionName,
    string? ContainerName,
    string? Command,
    DateTime StartedUtc,
    DateTime? EndedUtc,
    long BytesFromClient,
    long BytesToClient,
    HostShellSessionEndReason EndReason,
    string? Error)
{
    /// <summary>Convenience flag for the UI: the session is still open.</summary>
    public bool Active => EndedUtc is null;
}

/// <summary>
/// V6.7.1 — wire form of a single <see cref="Stashboard.Core.Entities.ProxmoxUpdateSessionEntity"/>
/// row for the Audit page's <em>Proxmox updates</em> tab. Records one
/// "Update now" run against the node or one LXC.
/// </summary>
public sealed record ProxmoxUpdateSessionResponse(
    Guid Id,
    Guid? ProxmoxConnectionId,
    string? ConnectionName,
    string? NodeName,
    ProxmoxGuestType TargetType,
    int VmId,
    string? TargetName,
    DateTime StartedUtc,
    DateTime? EndedUtc,
    int? ExitStatus,
    long BytesToClient,
    HostShellSessionEndReason EndReason,
    string? Error)
{
    /// <summary>Convenience flag for the UI: the run is still in flight.</summary>
    public bool Active => EndedUtc is null;
}

/// <summary>
/// V6.11 — wire form of a single <see cref="Stashboard.Core.Entities.ProxmoxMonitoringAuditEntity"/>
/// row for the Audit page's <em>LXC monitoring</em> tab. Records one monitoring
/// change (enable / disable / snooze / unsnooze), per affected guest.
/// </summary>
public sealed record ProxmoxMonitoringAuditResponse(
    Guid Id,
    Guid? ProxmoxConnectionId,
    string? ConnectionName,
    string? NodeName,
    int VmId,
    string? GuestName,
    ProxmoxMonitoringChangeType ChangeType,
    bool MonitoringEnabled,
    DateTime? SnoozedUntil,
    bool Bulk,
    DateTime ChangedUtc);

/// <summary>
/// V6.13 — wire form of a single <see cref="Stashboard.Core.Entities.ProxmoxDestroyAuditEntity"/>
/// row for the Audit page's <em>LXC destroy</em> tab. Records one destroy
/// attempt against an LXC and whether the host accepted it.
/// </summary>
public sealed record ProxmoxDestroyAuditResponse(
    Guid Id,
    Guid? ProxmoxConnectionId,
    string? ConnectionName,
    string? NodeName,
    int VmId,
    string? GuestName,
    bool Success,
    string? Error,
    DateTime DestroyedUtc);

/// <summary>
/// V6.13.1 — wire form of a single <see cref="Stashboard.Core.Entities.ProxmoxCreateAuditEntity"/>
/// row for the Audit page's <em>LXC create</em> tab. Records one create attempt
/// and whether the host accepted it.
/// </summary>
public sealed record ProxmoxCreateAuditResponse(
    Guid Id,
    Guid? ProxmoxConnectionId,
    string? ConnectionName,
    string? NodeName,
    int VmId,
    string? Hostname,
    string? Template,
    bool Success,
    string? Error,
    DateTime CreatedAtUtc);

/// <summary>
/// V7.6 — wire form of a single <see cref="Stashboard.Core.Entities.ComposeChangeAuditEntity"/>
/// row for the Audit page's <em>Compose changes</em> tab. Records one Compose
/// save / restore / apply: who, when, which project + file, which services were
/// touched, and (for apply) whether it succeeded.
/// </summary>
public sealed record ComposeChangeAuditResponse(
    Guid Id,
    Guid? DockerConnectionId,
    string? ConnectionName,
    string ComposeProject,
    string? FileName,
    ComposeChangeType ChangeType,
    IReadOnlyList<string> ChangedServices,
    bool Success,
    string? Error,
    DateTime ChangedUtc);

/// <summary>
/// V6.6 — wire form of a single <see cref="Stashboard.Core.Entities.ProxmoxConsoleSessionEntity"/>
/// row for the Audit page's <em>LXC console</em> tab.
/// </summary>
public sealed record ProxmoxConsoleSessionResponse(
    Guid Id,
    Guid? ProxmoxConnectionId,
    string? ConnectionName,
    string? NodeName,
    int VmId,
    string? GuestName,
    string? Command,
    DateTime StartedUtc,
    DateTime? EndedUtc,
    long BytesFromClient,
    long BytesToClient,
    HostShellSessionEndReason EndReason,
    string? Error)
{
    /// <summary>Convenience flag for the UI: the session is still open.</summary>
    public bool Active => EndedUtc is null;
}
