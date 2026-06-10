using Stashboard.Core.Entities;

namespace Stashboard.Api.Data;

/// <summary>
/// V6.13 — app-wide master switch for <strong>destroying</strong> an LXC
/// (<c>DELETE /nodes/{node}/lxc/{vmid}</c>). Stored as a single row so it can be
/// toggled at runtime from the Settings page, seeded on first access from the
/// bound
/// <see cref="Stashboard.Core.Options.StashboardOptions.AllowProxmoxDestroy"/>
/// config flag.
/// </summary>
/// <remarks>
/// Mirrors <see cref="ProxmoxUpdateApplySettingsEntity"/>. Destroying a container
/// is irreversible (its disk is gone), so the toggle defaults to off and is only
/// one of the gates — the per-host <c>AllowDestroy</c> opt-in, a stopped guest,
/// and a UI double confirmation are also required.
/// </remarks>
public class ProxmoxDestroySettingsEntity : AuditableEntity
{
    /// <summary>Well-known fixed primary key — there is only ever one
    /// proxmox-destroy-settings row.</summary>
    public static readonly Guid SingletonId = new("c0000000-0000-0000-0000-000000000005");

    /// <summary>When <c>true</c>, destroy-LXC is enabled server-wide. Default <c>false</c>.</summary>
    public bool Enabled { get; set; }

    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}
