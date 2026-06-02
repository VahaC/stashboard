using System.ComponentModel.DataAnnotations;
using Stashboard.Core.Enums;

namespace Stashboard.Core.Entities;

/// <summary>
/// V5.7 — audit row for a single browser container-exec session (an interactive
/// shell inside a Docker container). One row is written when the session starts
/// and finalised when it ends, regardless of outcome — exec runs arbitrary
/// commands in a workload, so every connect/disconnect is recorded (who, when,
/// which container, which command, how long, how many bytes, why it ended) in
/// addition to streaming to the application log. Mirrors
/// <see cref="HostShellSessionEntity"/>; the end-reason enum is shared.
/// </summary>
public class DockerExecSessionEntity : AuditableEntity
{
    /// <summary>User who opened the exec session. Denormalised so the audit
    /// view never has to walk back through the connection to find the owner
    /// (and so the row survives a connection delete).</summary>
    public Guid InitiatedByUserId { get; set; }

    /// <summary>
    /// The Docker connection the exec targeted. Nullable + <c>SetNull</c> on
    /// delete so deleting a connection keeps the historical audit rows around —
    /// the connection name and container details below are captured at start
    /// time for exactly this case.
    /// </summary>
    public Guid? DockerConnectionId { get; set; }

    /// <summary>Connection name captured at start time (survives a rename / delete).</summary>
    [MaxLength(100)]
    public string? ConnectionName { get; set; }

    /// <summary>Name of the container the shell ran inside.</summary>
    [MaxLength(255)]
    public string? ContainerName { get; set; }

    /// <summary>The command the exec session launched (e.g. <c>/bin/sh</c>).
    /// Captured for the audit trail.</summary>
    [MaxLength(500)]
    public string? Command { get; set; }

    public DateTime StartedUtc { get; set; } = DateTime.UtcNow;

    /// <summary><c>null</c> while the session is still open; set when finalised.</summary>
    public DateTime? EndedUtc { get; set; }

    /// <summary>Bytes the browser sent to the container (keystrokes / paste).</summary>
    public long BytesFromClient { get; set; }

    /// <summary>Bytes the container sent to the browser (terminal output).</summary>
    public long BytesToClient { get; set; }

    public HostShellSessionEndReason EndReason { get; set; } = HostShellSessionEndReason.Active;

    /// <summary>Human-readable detail attached to an <see cref="HostShellSessionEndReason.Error"/>
    /// end; <c>null</c> otherwise.</summary>
    public string? Error { get; set; }
}
