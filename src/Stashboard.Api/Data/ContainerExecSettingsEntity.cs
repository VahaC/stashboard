using Stashboard.Core.Entities;

namespace Stashboard.Api.Data;

/// <summary>
/// V5.7 — app-wide master switch for the browser container-exec terminal,
/// stored as a single row so it can be toggled at runtime from the Settings
/// page instead of living only in <c>appsettings</c> / env vars. Seeded from the
/// bound <see cref="Stashboard.Core.Options.StashboardOptions.AllowContainerExec"/>
/// on first access, so an operator who set the config flag on first run keeps
/// that value until they change it in the UI.
/// </summary>
/// <remarks>
/// Mirrors <see cref="HostShellSettingsEntity"/>. Opening a shell inside a
/// container runs arbitrary commands in the workload, so the toggle defaults to
/// off and is only one of two gates — the per-connection <c>AllowExec</c>
/// opt-in is also required.
/// </remarks>
public class ContainerExecSettingsEntity : AuditableEntity
{
    /// <summary>Well-known fixed primary key — there is only ever one container-exec-settings row.</summary>
    public static readonly Guid SingletonId = new("c0000000-0000-0000-0000-000000000002");

    /// <summary>When <c>true</c>, container exec is enabled server-wide. Default <c>false</c>.</summary>
    public bool Enabled { get; set; }

    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}
