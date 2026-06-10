using Stashboard.Core.Entities;

namespace Stashboard.Api.Data;

/// <summary>
/// V6.13.1 — app-wide master switch for <strong>creating</strong> an LXC
/// (<c>POST /nodes/{node}/lxc</c>). Stored as a single row so it can be toggled
/// at runtime from the Settings page, seeded on first access from the bound
/// <see cref="Stashboard.Core.Options.StashboardOptions.AllowProxmoxCreate"/>
/// config flag.
/// </summary>
/// <remarks>
/// Mirrors <see cref="ProxmoxDestroySettingsEntity"/>. The toggle defaults to off
/// and is only one of the gates — the per-host <c>AllowCreate</c> opt-in is also
/// required.
/// </remarks>
public class ProxmoxCreateSettingsEntity : AuditableEntity
{
    /// <summary>Well-known fixed primary key — there is only ever one
    /// proxmox-create-settings row.</summary>
    public static readonly Guid SingletonId = new("c0000000-0000-0000-0000-000000000006");

    /// <summary>When <c>true</c>, create-LXC is enabled server-wide. Default <c>false</c>.</summary>
    public bool Enabled { get; set; }

    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}
