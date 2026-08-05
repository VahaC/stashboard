namespace Stashboard.Api.Notifications;

/// <summary>
/// Apprise notification configuration bound from the "Apprise" section in
/// appsettings. Used only to seed the runtime-editable
/// <see cref="Data.AppriseSettingsEntity"/> row on first access; live settings are
/// read from the DB via <see cref="IAppriseSettingsService"/>.
/// </summary>
public sealed class AppriseOptions
{
    public const string SectionName = "Apprise";

    /// <summary>Master switch. Off by default — nothing is sent until configured.</summary>
    public bool Enabled { get; set; }

    /// <summary>Apprise API base URL (root or full <c>/notify</c> endpoint).</summary>
    public string BaseUrl { get; set; } = "";

    /// <summary>Newline-separated Apprise URLs to seed the encrypted list with.</summary>
    public string Urls { get; set; } = "";
}
