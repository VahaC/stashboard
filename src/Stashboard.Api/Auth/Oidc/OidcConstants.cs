namespace Stashboard.Api.Auth.Oidc;

/// <summary>V10.5 — shared constants for the OIDC / SSO login flow.</summary>
public static class OidcConstants
{
    /// <summary>
    /// Sentinel value written to <c>UserEntity.PasswordHash</c> for an OIDC-provisioned (passwordless)
    /// account. It is deliberately not a valid <c>pbkdf2-sha256$…</c> string, so
    /// <see cref="Pbkdf2PasswordHasher.Verify"/> rejects every password against it — local login is
    /// impossible until the owner sets a real password (e.g. via "forgot password"). The column stays
    /// non-null, so the schema invariant is preserved without a migration around the required field.
    /// </summary>
    public const string NoPasswordSentinel = "oidc-no-password";

    /// <summary>Frontend route the provider redirects back to; the SPA posts the code here to complete the flow.</summary>
    public const string CallbackPath = "/oidc/callback";

    /// <summary>How long an in-flight authorization request (state + PKCE verifier + nonce) stays valid.</summary>
    public static readonly TimeSpan StateLifetime = TimeSpan.FromMinutes(5);

    /// <summary>Builds the redirect URI registered at the provider from the configured base URL.</summary>
    public static string BuildRedirectUri(string redirectBaseUrl) =>
        $"{redirectBaseUrl.TrimEnd('/')}{CallbackPath}";
}
