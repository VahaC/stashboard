using Stashboard.Api.Contracts;

namespace Stashboard.Api.Auth.Oidc;

/// <summary>
/// Fully-resolved, decrypted OIDC settings used by the login flow. <see cref="ClientSecret"/> is
/// plaintext (null for a public / PKCE-only client) — never log it.
/// </summary>
public sealed record ResolvedOidcSettings(
    bool Enabled,
    string DisplayName,
    string Issuer,
    string ClientId,
    string? ClientSecret,
    string Scopes,
    string RedirectBaseUrl,
    bool AllowOidcRegistration)
{
    /// <summary>The redirect URI registered at the provider — <c>{RedirectBaseUrl}/oidc/callback</c>.</summary>
    public string RedirectUri => OidcConstants.BuildRedirectUri(RedirectBaseUrl);

    /// <summary>True when the row holds enough to actually run a sign-in (issuer + client id present and enabled).</summary>
    public bool IsUsable =>
        Enabled
        && !string.IsNullOrWhiteSpace(Issuer)
        && !string.IsNullOrWhiteSpace(ClientId)
        && !string.IsNullOrWhiteSpace(RedirectBaseUrl);
}

/// <summary>
/// Reads and writes the single app-wide OIDC-settings row. The row is created (disabled) on first
/// access; there is no env seed — the provider is configured entirely from the UI.
/// </summary>
public interface IOidcSettingsService
{
    /// <summary>Loads (creating if needed) the settings and decrypts the client secret for the exchange.</summary>
    Task<ResolvedOidcSettings> GetResolvedAsync(CancellationToken cancellationToken = default);

    /// <summary>Masked view for the API — the client secret is never returned, only a presence flag.</summary>
    Task<OidcSettingsResponse> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>Cheap presence check for the feature flag / login button (true only when usable).</summary>
    Task<bool> IsEnabledAsync(CancellationToken cancellationToken = default);

    /// <summary>Persists edited settings, encrypting the client secret (tri-state: keep / set / clear).</summary>
    Task UpdateAsync(UpdateOidcSettingsRequest request, CancellationToken cancellationToken = default);
}
