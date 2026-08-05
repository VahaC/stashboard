using Stashboard.Api.Contracts;

namespace Stashboard.Api.Notifications;

/// <summary>
/// Fully-resolved, decrypted Apprise settings used by the notification services and
/// the test endpoint at send-time. <see cref="Urls"/> are plaintext — never log them.
/// </summary>
public sealed record ResolvedAppriseSettings(
    bool Enabled,
    string BaseUrl,
    IReadOnlyList<string> Urls)
{
    /// <summary>True when the channel is on and has somewhere to send to.</summary>
    public bool IsConfigured => Enabled && !string.IsNullOrWhiteSpace(BaseUrl) && Urls.Count > 0;
}

/// <summary>
/// Reads and writes the single app-wide Apprise-settings row. The row is created on
/// first access from the bound <see cref="AppriseOptions"/> so an existing deployment
/// keeps its configured values until an operator edits them in the UI.
/// </summary>
public interface IAppriseSettingsService
{
    /// <summary>Loads (creating if needed) the settings and decrypts the URLs for sending.</summary>
    Task<ResolvedAppriseSettings> GetResolvedAsync(CancellationToken cancellationToken = default);

    /// <summary>Masked view for the API — the URLs are never returned, only a presence flag,
    /// a count, and the non-secret target schemes.</summary>
    Task<AppriseSettingsResponse> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>Persists edited settings, encrypting the URLs (tri-state: keep / set / clear).</summary>
    Task UpdateAsync(UpdateAppriseSettingsRequest request, CancellationToken cancellationToken = default);
}
