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
