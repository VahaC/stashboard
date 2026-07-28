namespace Stashboard.Api.Contracts;

/// <summary>
/// V10.0 — masked view of the app-wide Apprise notification settings. The Apprise
/// URLs are never returned — only <see cref="HasUrls"/> / <see cref="UrlCount"/>
/// and the non-secret <see cref="Targets"/> schemes (e.g. <c>discord</c>, <c>ntfy</c>).
/// </summary>
public sealed record AppriseSettingsResponse(
    bool Enabled,
    string BaseUrl,
    bool HasUrls,
    int UrlCount,
    IReadOnlyList<string> Targets);

/// <summary>V10.0 — update payload for the Apprise settings.</summary>
public sealed record UpdateAppriseSettingsRequest(
    bool Enabled,
    [property: System.ComponentModel.DataAnnotations.MaxLength(512)] string? BaseUrl,
    // Tri-state secret: omit / Keep to preserve the stored URLs, Set to replace
    // (Value = the URLs, one per line), Clear to drop them all.
    SecretValueUpsert? Urls);

/// <summary>V10.0 — per-target outcome of the "Send test" button.</summary>
public sealed record AppriseTargetResult(string Target, bool Success, string? Error);

/// <summary>
/// V10.0 — outcome of the Apprise "Send test" button: an overall reachability flag,
/// an optional top-level error (e.g. nothing configured), and a per-target breakdown.
/// </summary>
public sealed record AppriseTestResponse(bool Ok, string? Error, IReadOnlyList<AppriseTargetResult> Results);
