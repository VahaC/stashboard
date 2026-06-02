namespace Stashboard.Core.Options;

/// <summary>
/// V5.5 — first-run seed for the image-prune background sweep. The live
/// value lives on <c>ImagePruneSettingsEntity</c> in the database and is
/// managed from the Settings → Image cleanup page; this config block only
/// seeds the row the first time it's read.
/// </summary>
public sealed class ImagePruneOptions
{
    public const string SectionName = "Stashboard:ImagePrune";

    /// <summary>Master switch for the background sweep. Default <c>true</c>.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How often the sweep runs per connection, in hours. Default 168 (weekly).</summary>
    public int IntervalHours { get; set; } = 168;
}
