using Stashboard.Core.Entities;

namespace Stashboard.Api.Data;

/// <summary>
/// V5.3 — app-wide master switch for the browser host terminal, stored as a
/// single row so it can be toggled at runtime from the Settings page instead of
/// living only in <c>appsettings</c> / env vars. Seeded from the bound
/// <see cref="Stashboard.Core.Options.StashboardOptions.AllowHostShell"/> on
/// first access, so an operator who set the config flag on first run keeps that
/// value until they change it in the UI.
/// </summary>
/// <remarks>
/// Mirrors <see cref="EmailSettingsEntity"/>. The host terminal is the most
/// dangerous surface in the product (host-level RCE), so the toggle defaults to
/// off and is only one of three gates — the per-connection <c>AllowHostShell</c>
/// opt-in and an SSH connection are also required.
/// </remarks>
public class HostShellSettingsEntity : AuditableEntity
{
    /// <summary>Well-known fixed primary key — there is only ever one host-shell-settings row.</summary>
    public static readonly Guid SingletonId = new("c0000000-0000-0000-0000-000000000001");

    /// <summary>When <c>true</c>, the host terminal is enabled server-wide. Default <c>false</c>.</summary>
    public bool Enabled { get; set; }

    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}
