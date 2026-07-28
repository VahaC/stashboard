using Stashboard.Core.Enums;

namespace Stashboard.Core.Entities;

/// <summary>
/// V6.8.1 — per-connection node-health alerting preferences (one row per
/// Proxmox host, 1:1 with <see cref="ProxmoxConnectionEntity"/>). The node
/// analogue of a Docker watch's <c>Enabled</c> flag: until the user opts a node
/// in, <see cref="Enabled"/> is <c>false</c> and the evaluation loop skips it
/// entirely, so the additive migration leaves every node muted by default.
/// <para>Threshold overrides are nullable — a <c>null</c> means "use the global
/// default" (the V6.8 card baseline), so a deliberately hot node can be tuned
/// without touching the fleet. The two <c>LastNotified…Signature</c> columns
/// give the alert digest the same per-channel throttle the update notifier uses
/// (a flaky SMTP server doesn't suppress Telegram, and vice-versa).</para>
/// </summary>
public class ProxmoxNodeAlertSettingsEntity : AuditableEntity
{
    /// <summary>Owning connection. Unique (1:1) — one settings row per host.
    /// Deleting the host cascades this row away.</summary>
    public Guid ProxmoxConnectionId { get; set; }
    public ProxmoxConnectionEntity ProxmoxConnection { get; set; } = default!;

    /// <summary>Master opt-in. <c>false</c> by default — the node is muted until
    /// the user flips the toggle on the node modal's Alerts tab.</summary>
    public bool Enabled { get; set; }

    /// <summary>Which categories may fire, as a <see cref="ProxmoxAlertCategory"/>
    /// bit mask. Defaults to <see cref="ProxmoxAlertCategory.All"/> so enabling a
    /// node watches everything; the user can opt individual categories out.</summary>
    public ProxmoxAlertCategory CategoryMask { get; set; } = ProxmoxAlertCategory.All;

    // ── optional per-node threshold overrides (null ⇒ global default) ──────────

    public int? CpuWarn { get; set; }
    public int? CpuCrit { get; set; }
    public int? MemWarn { get; set; }
    public int? MemCrit { get; set; }
    public int? StorageWarn { get; set; }
    public int? StorageCrit { get; set; }

    /// <summary>Fallback thermal thresholds (°C) used only when a chip exposes no
    /// high/crit of its own. <c>null</c> ⇒ global default.</summary>
    public int? TempWarn { get; set; }
    public int? TempCrit { get; set; }

    // ── notification throttle (one signature per channel) ─────────────────────

    /// <summary>Signature of the active-alert set already emailed about. The
    /// digest is re-sent only when this changes, so a steady deviation never
    /// re-pings. Stamped after a successful send.</summary>
    public string? LastNotifiedSignature { get; set; }

    /// <summary>Same as <see cref="LastNotifiedSignature"/> for the Telegram
    /// channel — independent so a transient failure on one channel doesn't
    /// suppress the other.</summary>
    public string? LastTelegramNotifiedSignature { get; set; }

    /// <summary>UTC timestamp of the most recent alert notification (either channel).</summary>
    public DateTime? LastNotificationSentUtc { get; set; }

    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}
