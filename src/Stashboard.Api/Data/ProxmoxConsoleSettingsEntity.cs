using Stashboard.Core.Entities;

namespace Stashboard.Api.Data;

/// <summary>
/// V6.6 — app-wide master switch for the browser Proxmox LXC console, stored as
/// a single row so it can be toggled at runtime from the Settings page instead
/// of living only in <c>appsettings</c> / env vars. Seeded from the bound
/// <see cref="Stashboard.Core.Options.StashboardOptions.AllowProxmoxConsole"/>
/// on first access, so an operator who set the config flag on first run keeps
/// that value until they change it in the UI.
/// </summary>
/// <remarks>
/// Mirrors <see cref="ContainerExecSettingsEntity"/>. Opening a shell inside an
/// LXC runs arbitrary commands in the guest (via <c>pct exec</c> over SSH), so
/// the toggle defaults to off and is only one of the gates — the per-host
/// <c>AllowConsole</c> opt-in and SSH credentials are also required.
/// </remarks>
public class ProxmoxConsoleSettingsEntity : AuditableEntity
{
    /// <summary>Well-known fixed primary key — there is only ever one proxmox-console-settings row.</summary>
    public static readonly Guid SingletonId = new("c0000000-0000-0000-0000-000000000003");

    /// <summary>When <c>true</c>, the LXC console is enabled server-wide. Default <c>false</c>.</summary>
    public bool Enabled { get; set; }

    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}
