using System.ComponentModel.DataAnnotations;
using Stashboard.Core.Enums;

namespace Stashboard.Core.Entities;

/// <summary>
/// V6.6 — audit row for a single browser Proxmox LXC console session (an
/// interactive shell inside an LXC, reached by SSHing to the Proxmox host and
/// running <c>pct exec &lt;vmid&gt; -- …</c>). One row is written when the
/// session starts and finalised when it ends, regardless of outcome — the
/// console runs arbitrary commands inside a guest, so every connect/disconnect
/// is recorded (who, when, which host / node / guest, which command, how long,
/// how many bytes, why it ended) in addition to streaming to the application
/// log. Mirrors <see cref="DockerExecSessionEntity"/>; the end-reason enum is
/// shared.
/// </summary>
public class ProxmoxConsoleSessionEntity : AuditableEntity
{
    /// <summary>User who opened the console session. Denormalised so the audit
    /// view never has to walk back through the connection to find the owner
    /// (and so the row survives a connection delete).</summary>
    public Guid InitiatedByUserId { get; set; }

    /// <summary>
    /// The Proxmox host the console targeted. Nullable + <c>SetNull</c> on
    /// delete so deleting a host keeps the historical audit rows around — the
    /// connection name and guest details below are captured at start time for
    /// exactly this case.
    /// </summary>
    public Guid? ProxmoxConnectionId { get; set; }

    /// <summary>Host name captured at start time (survives a rename / delete).</summary>
    [MaxLength(100)]
    public string? ConnectionName { get; set; }

    /// <summary>Proxmox node name captured at start time.</summary>
    [MaxLength(100)]
    public string? NodeName { get; set; }

    /// <summary>VMID of the LXC the shell ran inside.</summary>
    public int VmId { get; set; }

    /// <summary>Guest name captured at start time (best-effort), for the audit view.</summary>
    [MaxLength(255)]
    public string? GuestName { get; set; }

    /// <summary>The command the console session launched inside the LXC (e.g.
    /// <c>/bin/bash</c>). Captured for the audit trail.</summary>
    [MaxLength(500)]
    public string? Command { get; set; }

    public DateTime StartedUtc { get; set; } = DateTime.UtcNow;

    /// <summary><c>null</c> while the session is still open; set when finalised.</summary>
    public DateTime? EndedUtc { get; set; }

    /// <summary>Bytes the browser sent to the LXC (keystrokes / paste).</summary>
    public long BytesFromClient { get; set; }

    /// <summary>Bytes the LXC sent to the browser (terminal output).</summary>
    public long BytesToClient { get; set; }

    public HostShellSessionEndReason EndReason { get; set; } = HostShellSessionEndReason.Active;

    /// <summary>Human-readable detail attached to an <see cref="HostShellSessionEndReason.Error"/>
    /// end; <c>null</c> otherwise.</summary>
    public string? Error { get; set; }
}
