namespace Stashboard.Api.Notifications.Push;

/// <summary>
/// V10.6 — VAPID key material for web push, bound from the <c>Vapid</c> config
/// section. The key pair is auto-generated once and persisted next to the SQLite
/// database on first run (see <see cref="SecretProvisioning"/>), exactly like the
/// encryption key and JWT secret; an operator may instead supply both keys
/// explicitly via env / appsettings.
/// </summary>
public sealed class VapidOptions
{
    public const string SectionName = "Vapid";

    /// <summary>Base64url application-server public key. Handed to the browser so it can
    /// subscribe against this server; not a secret.</summary>
    public string PublicKey { get; set; } = "";

    /// <summary>Base64url application-server private key. Signs the VAPID JWT on every send.</summary>
    public string PrivateKey { get; set; } = "";

    /// <summary>VAPID <c>sub</c> claim — a <c>mailto:</c> or URL the push service can contact.
    /// Not a secret.</summary>
    public string Subject { get; set; } = "mailto:admin@stashboard.local";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(PublicKey) && !string.IsNullOrWhiteSpace(PrivateKey);
}
