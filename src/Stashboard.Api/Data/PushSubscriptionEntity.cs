using System.ComponentModel.DataAnnotations;
using Stashboard.Core.Entities;

namespace Stashboard.Api.Data;

/// <summary>
/// V10.6 — a single browser/device web-push subscription owned by a user. One row
/// per device: a user who installs Stashboard on their phone and desktop has two.
/// </summary>
/// <remarks>
/// The three subscription fields (<see cref="Endpoint"/>, <see cref="P256dh"/>,
/// <see cref="Auth"/>) come straight from the browser's <c>PushManager.subscribe</c>
/// and are <b>not</b> encrypted at rest: they are per-subscription public key
/// material, useless without the server's private VAPID key. They are, however,
/// device-bound bearer material (whoever holds them can push to that device), so —
/// like <see cref="PersonalAccessTokenEntity"/> — they are deliberately <b>not</b>
/// exported by backup/restore; after a restore the user re-subscribes each device.
///
/// The web-push channel has no user-level on/off flag: it is "configured" for a user
/// exactly when they have at least one live subscription. An endpoint that the push
/// service reports as gone (HTTP 404/410) is pruned on the next send.
/// </remarks>
public class PushSubscriptionEntity : AuditableEntity
{
    public Guid UserId { get; set; }
    public UserEntity? User { get; set; }

    /// <summary>The push service endpoint URL the browser handed us. Globally unique —
    /// re-subscribing the same device upserts this row rather than duplicating it.</summary>
    [Required, MaxLength(1000)]
    public string Endpoint { get; set; } = default!;

    /// <summary>The subscription's P-256 ECDH public key (base64url), used to encrypt the payload.</summary>
    [Required, MaxLength(200)]
    public string P256dh { get; set; } = default!;

    /// <summary>The subscription's auth secret (base64url), used to encrypt the payload.</summary>
    [Required, MaxLength(100)]
    public string Auth { get; set; } = default!;

    /// <summary>Optional human-readable label so the user can tell devices apart in the
    /// subscription list (derived from the User-Agent, e.g. "Chrome on Android").</summary>
    [MaxLength(200)]
    public string? Label { get; set; }

    /// <summary>UTC time of the most recent successful push to this subscription.</summary>
    public DateTime? LastSuccessUtc { get; set; }
}
