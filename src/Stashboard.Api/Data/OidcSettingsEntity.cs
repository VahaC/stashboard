using System.ComponentModel.DataAnnotations;
using Stashboard.Core.Entities;

namespace Stashboard.Api.Data;

/// <summary>
/// V10.5 — app-wide OIDC / SSO provider configuration, stored as a single row so it can be
/// edited at runtime from the UI instead of living in env vars (mirrors the editable-SMTP /
/// MQTT / Apprise model). The client secret is encrypted at rest and never returned to the
/// client. Off by default; there is no env seed — the row is created disabled on first access.
/// </summary>
public class OidcSettingsEntity : AuditableEntity
{
    /// <summary>Well-known fixed primary key — there is only ever one OIDC-settings row.</summary>
    public static readonly Guid SingletonId = new("0c1dc000-0000-0000-0000-000000000001");

    /// <summary>Master switch. When false the login button is hidden and OIDC login is refused.</summary>
    public bool Enabled { get; set; }

    /// <summary>Label shown on the login button, e.g. "Authentik". Rendered as "Sign in with {DisplayName}".</summary>
    [MaxLength(64)]
    public string DisplayName { get; set; } = "";

    /// <summary>Provider issuer / authority URL; the <c>/.well-known/openid-configuration</c> document is read from it.</summary>
    [MaxLength(512)]
    public string Issuer { get; set; } = "";

    [MaxLength(256)]
    public string ClientId { get; set; } = "";

    /// <summary>AES ciphertext of the client secret, or null for a public (PKCE-only) client. Never returned.</summary>
    public string? ClientSecretEncrypted { get; set; }

    /// <summary>Space-separated scopes requested at authorize time. Must include <c>openid</c>.</summary>
    [MaxLength(256)]
    public string Scopes { get; set; } = "openid profile email";

    /// <summary>Public base URL of this Stashboard used to build the redirect URI (<c>{base}/oidc/callback</c>).</summary>
    [MaxLength(512)]
    public string RedirectBaseUrl { get; set; } = "";

    /// <summary>When true, a first OIDC login for an unknown verified email provisions a new account.</summary>
    public bool AllowOidcRegistration { get; set; }

    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}
