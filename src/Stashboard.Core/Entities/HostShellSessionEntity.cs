using System.ComponentModel.DataAnnotations;
using Stashboard.Core.Enums;

namespace Stashboard.Core.Entities;

/// <summary>
/// V5.3 — audit row for a single browser host-terminal session (an interactive
/// SSH shell on a Docker host). One row is written when the session starts and
/// finalised when it ends, regardless of outcome — the host shell is the most
/// dangerous surface in the product, so every connect/disconnect is recorded
/// (who, when, which host, how long, how many bytes, why it ended) in addition
/// to streaming to the application log.
/// </summary>
public class HostShellSessionEntity : AuditableEntity
{
    /// <summary>User who opened the terminal. Denormalised so the audit view
    /// never has to walk back through the connection to find the owner (and so
    /// the row survives a connection delete).</summary>
    public Guid InitiatedByUserId { get; set; }

    /// <summary>
    /// The Docker connection the shell targeted. Nullable + <c>SetNull</c> on
    /// delete so deleting a connection keeps the historical audit rows around —
    /// the host details below are captured at start time for exactly this case.
    /// </summary>
    public Guid? DockerConnectionId { get; set; }

    /// <summary>Connection name captured at start time (survives a rename / delete).</summary>
    [MaxLength(100)]
    public string? ConnectionName { get; set; }

    /// <summary>SSH host (DNS / IP) the shell connected to. Plaintext, not a secret.</summary>
    [MaxLength(200)]
    public string? SshHost { get; set; }

    /// <summary>SSH login username used for the shell. Plaintext, not a secret.</summary>
    [MaxLength(100)]
    public string? SshUsername { get; set; }

    public DateTime StartedUtc { get; set; } = DateTime.UtcNow;

    /// <summary><c>null</c> while the session is still open; set when finalised.</summary>
    public DateTime? EndedUtc { get; set; }

    /// <summary>Bytes the browser sent to the host (keystrokes / paste).</summary>
    public long BytesFromClient { get; set; }

    /// <summary>Bytes the host sent to the browser (terminal output).</summary>
    public long BytesToClient { get; set; }

    public HostShellSessionEndReason EndReason { get; set; } = HostShellSessionEndReason.Active;

    /// <summary>Human-readable detail attached to an <see cref="HostShellSessionEndReason.Error"/>
    /// end; <c>null</c> otherwise.</summary>
    public string? Error { get; set; }
}
