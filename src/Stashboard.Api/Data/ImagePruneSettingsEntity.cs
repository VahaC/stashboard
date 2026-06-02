using Stashboard.Core.Entities;

namespace Stashboard.Api.Data;

/// <summary>
/// V5.5 — app-wide settings for the background image-prune sweep. Single
/// row keyed by <see cref="SingletonId"/>. Seeded from the bound
/// <see cref="Stashboard.Core.Options.ImagePruneOptions"/> on first access,
/// so an operator who set the config on first run keeps that value until
/// they change it in the UI. Mirrors <see cref="HostShellSettingsEntity"/>.
/// </summary>
public class ImagePruneSettingsEntity : AuditableEntity
{
    public static readonly Guid SingletonId = new("c0000000-0000-0000-0000-000000000002");

    /// <summary>Master switch for the background sweep. Default <c>true</c> —
    /// dangling images are safe to clean up, so the feature ships on by
    /// default to prevent disks filling up over months of auto-updates.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How often the background sweep runs per connection. Default
    /// 168 hours (weekly). Minimum enforced as 1 hour at the service layer.</summary>
    public int IntervalHours { get; set; } = 168;

    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}
