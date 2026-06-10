using Stashboard.Core.Entities;

namespace Stashboard.Api.Data;

/// <summary>
/// V6.7.1 — app-wide master switch for one-click <strong>Update now</strong> on
/// Proxmox (applying pending package updates on the node and inside its LXCs via
/// <c>apt-get dist-upgrade</c> over SSH). Stored as a single row so it can be
/// toggled at runtime from the Settings page, seeded on first access from the
/// bound
/// <see cref="Stashboard.Core.Options.StashboardOptions.AllowProxmoxUpdates"/>
/// config flag.
/// </summary>
/// <remarks>
/// Mirrors <see cref="ProxmoxConsoleSettingsEntity"/>. Applying updates runs
/// privileged package operations in the guest / node, so the toggle defaults to
/// off and is only one of the gates — the per-host <c>AllowUpdates</c> opt-in
/// and SSH credentials are also required.
/// </remarks>
public class ProxmoxUpdateApplySettingsEntity : AuditableEntity
{
    /// <summary>Well-known fixed primary key — there is only ever one
    /// proxmox-updates-settings row.</summary>
    public static readonly Guid SingletonId = new("c0000000-0000-0000-0000-000000000004");

    /// <summary>When <c>true</c>, Update now is enabled server-wide. Default <c>false</c>.</summary>
    public bool Enabled { get; set; }

    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}
