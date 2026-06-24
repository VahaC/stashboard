using Stashboard.Core.Entities;

namespace Stashboard.Api.Data;

/// <summary>
/// V8.0 — app-wide master switch for <strong>cloning</strong> a guest and managing
/// <strong>snapshots</strong> (<c>POST …/lxc/{vmid}/clone</c> and the
/// <c>…/snapshot</c> endpoints). Stored as a single row so it can be toggled at
/// runtime from the Settings page, seeded on first access from the bound
/// <see cref="Stashboard.Core.Options.StashboardOptions.AllowProxmoxClone"/>
/// config flag.
/// </summary>
/// <remarks>
/// Mirrors <see cref="ProxmoxCreateSettingsEntity"/>. The toggle defaults to off
/// and is only one of the gates — the per-host <c>AllowClone</c> opt-in is also
/// required.
/// </remarks>
public class ProxmoxCloneSettingsEntity : AuditableEntity
{
    /// <summary>Well-known fixed primary key — there is only ever one
    /// proxmox-clone-settings row.</summary>
    public static readonly Guid SingletonId = new("c0000000-0000-0000-0000-000000000007");

    /// <summary>When <c>true</c>, clone/snapshot is enabled server-wide. Default <c>false</c>.</summary>
    public bool Enabled { get; set; }

    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}
