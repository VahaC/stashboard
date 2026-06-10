using System.ComponentModel.DataAnnotations;
using Stashboard.Core.Enums;

namespace Stashboard.Core.Entities;

/// <summary>
/// V6.7.1 — audit row for a single one-click <strong>Update now</strong> on
/// Proxmox: applying pending package updates either on the node itself or inside
/// one LXC, by SSHing to the host and running <c>apt-get dist-upgrade</c>
/// (directly, or via <c>pct exec &lt;vmid&gt; -- …</c>). One row is written when
/// the run starts and finalised when it ends, regardless of outcome — applying
/// updates runs privileged package operations, so every attempt is recorded
/// (who, when, which host / node / guest, the exit status, how many bytes of
/// output, why it ended). Mirrors <see cref="ProxmoxConsoleSessionEntity"/>; the
/// end-reason enum is shared. Output is one-way (host → browser), so there is no
/// "bytes from client".
/// </summary>
public class ProxmoxUpdateSessionEntity : AuditableEntity
{
    /// <summary>User who triggered the update. Denormalised so the audit view
    /// never has to walk back through the connection to find the owner (and so
    /// the row survives a connection delete).</summary>
    public Guid InitiatedByUserId { get; set; }

    /// <summary>
    /// The Proxmox host the update targeted. Nullable + <c>SetNull</c> on delete
    /// so deleting a host keeps the historical audit rows around — the
    /// connection name and target details below are captured at start time.
    /// </summary>
    public Guid? ProxmoxConnectionId { get; set; }

    /// <summary>Host name captured at start time (survives a rename / delete).</summary>
    [MaxLength(100)]
    public string? ConnectionName { get; set; }

    /// <summary>Proxmox node name captured at start time.</summary>
    [MaxLength(100)]
    public string? NodeName { get; set; }

    /// <summary>What the update ran against — the node itself or one LXC.</summary>
    public ProxmoxGuestType TargetType { get; set; }

    /// <summary>VMID of the LXC the update ran inside, or <c>0</c> for the node.</summary>
    public int VmId { get; set; }

    /// <summary>Guest name captured at start time (best-effort), for the audit view.
    /// Node name for a node-level run.</summary>
    [MaxLength(255)]
    public string? TargetName { get; set; }

    public DateTime StartedUtc { get; set; } = DateTime.UtcNow;

    /// <summary><c>null</c> while the run is still in flight; set when finalised.</summary>
    public DateTime? EndedUtc { get; set; }

    /// <summary>Exit status of the remote <c>apt-get</c> command, or <c>null</c>
    /// when the run never produced one (SSH connect failed / cancelled).</summary>
    public int? ExitStatus { get; set; }

    /// <summary>Bytes of apt output streamed to the browser.</summary>
    public long BytesToClient { get; set; }

    public HostShellSessionEndReason EndReason { get; set; } = HostShellSessionEndReason.Active;

    /// <summary>Human-readable detail attached to an
    /// <see cref="HostShellSessionEndReason.Error"/> end; <c>null</c> otherwise.</summary>
    public string? Error { get; set; }
}
