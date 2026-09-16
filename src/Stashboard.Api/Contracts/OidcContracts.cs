using System.ComponentModel.DataAnnotations;

namespace Stashboard.Api.Contracts;

// ── V10.5 — OIDC / SSO single sign-on ──────────────────────────────────────────

/// <summary>
/// Masked view of the app-wide OIDC provider settings. The client secret is never returned —
/// only <see cref="HasClientSecret"/> tells the UI whether one is stored.
/// </summary>
public sealed record OidcSettingsResponse(
    bool Enabled,
    string DisplayName,
    string Issuer,
    string ClientId,
    bool HasClientSecret,
    string Scopes,
    string RedirectBaseUrl,
    bool AllowOidcRegistration,
    /// <summary>The exact redirect URI to register at the provider — <c>{RedirectBaseUrl}/oidc/callback</c>.</summary>
    string RedirectUri
);

public sealed record UpdateOidcSettingsRequest(
    bool Enabled,
    [MaxLength(64)] string? DisplayName,
    [MaxLength(512)] string? Issuer,
    [MaxLength(256)] string? ClientId,
    // Tri-state secret: omit / Keep to preserve the stored secret, Set to replace, Clear to drop (public client).
    SecretValueUpsert? ClientSecret,
    [MaxLength(256)] string? Scopes,
    [MaxLength(512)] string? RedirectBaseUrl,
    bool AllowOidcRegistration
);

/// <summary>Outcome of the "Test discovery" button — whether the issuer's well-known document loaded.</summary>
public sealed record OidcDiscoveryTestResponse(bool Ok, string? AuthorizationEndpoint, string? TokenEndpoint, string? Error);

/// <summary>Anonymous, login-page-safe view: whether OIDC is configured and the button label. No secrets, no endpoints.</summary>
public sealed record OidcInfoResponse(bool Enabled, string ButtonLabel);

/// <summary>Response to <c>POST /api/auth/oidc/start</c> — the provider authorize URL the SPA redirects to.</summary>
public sealed record OidcStartResponse(string AuthorizeUrl);

/// <summary>The code + state the SPA reads off the redirect and posts back to complete the exchange.</summary>
public sealed record OidcCallbackRequest(
    [Required] string Code,
    [Required] string State
);
