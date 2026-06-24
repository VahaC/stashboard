using Stashboard.Core.Entities;

namespace Stashboard.Api.Data;

/// <summary>
/// V8.1 — app-wide master switch for <strong>restoring</strong> an LXC from a vzdump
/// backup archive (<c>POST /nodes/{node}/lxc</c> with <c>restore=1</c>). Stored as a
/// single row so it can be toggled at runtime from the Settings page, seeded on first
/// access from the bound
/// <see cref="Stashboard.Core.Options.StashboardOptions.AllowProxmoxRestore"/>
/// config flag.
/// </summary>
/// <remarks>
/// Mirrors <see cref="ProxmoxCreateSettingsEntity"/>. The toggle defaults to off and
/// is only one of the gates — the per-host <c>AllowRestore</c> opt-in is also
/// required, and an overwrite restore additionally needs a stopped target + a UI
/// double confirmation.
/// </remarks>
public class ProxmoxRestoreSettingsEntity : AuditableEntity
{
    /// <summary>Well-known fixed primary key — there is only ever one
    /// proxmox-restore-settings row.</summary>
    public static readonly Guid SingletonId = new("c0000000-0000-0000-0000-000000000008");

    /// <summary>When <c>true</c>, restore-LXC is enabled server-wide. Default <c>false</c>.</summary>
    public bool Enabled { get; set; }

    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}
