using System.ComponentModel.DataAnnotations;
using Stashboard.Core.Entities;

namespace Stashboard.Api.Data;

/// <summary>
/// Coarse capability granted by a personal access token. Stored as an <c>int</c>; values are
/// stable so new scopes (e.g. services-only) can be added later without breaking existing rows.
/// </summary>
public enum PersonalAccessTokenScope
{
    /// <summary>Read-only — the token is accepted on safe HTTP methods (GET/HEAD/OPTIONS) and 403'd on mutations.</summary>
    Read = 1,
    /// <summary>Full data access — every REST method, but never the host-shell / container-exec surface or account-security mutations.</summary>
    Full = 2,
}

/// <summary>
/// V10.4 — a long-lived, scoped, revocable personal access token (PAT). Lets a user script
/// against the REST API without replaying their password/login flow.
/// </summary>
/// <remarks>
/// The raw secret (<c>sb_pat_…</c>) is shown exactly once at creation and never stored — only an
/// HMAC-SHA256 hash is persisted (same principle as <see cref="RefreshTokenEntity"/>). PATs are
/// intentionally <b>independent of</b> <c>User.SecurityStamp</c>: a password change or logout-all
/// does not revoke them, so automation survives session rotation. They are killed only by an
/// explicit revoke or by deleting the owning account (FK cascade).
///
/// Deliberately <b>not</b> exported by the backup/restore feature — a PAT is a bearer secret, so an
/// export would either leak it or be useless. After a restore the user simply mints fresh tokens.
/// </remarks>
public class PersonalAccessTokenEntity : AuditableEntity
{
    public Guid UserId { get; set; }
    public UserEntity? User { get; set; }

    /// <summary>Human-readable label chosen by the user (e.g. "Grafana scraper"). Not unique.</summary>
    [Required, MaxLength(128)]
    public string Name { get; set; } = default!;

    /// <summary>HMAC-SHA256(secret) hex-encoded. Unique. The plaintext is never stored.</summary>
    [Required, MaxLength(64)]
    public string TokenHash { get; set; } = default!;

    /// <summary>
    /// Non-secret display hint shown in the token list so the user can tell tokens apart
    /// (e.g. <c>sb_pat_AB12</c> — the prefix plus the first few characters of the secret).
    /// </summary>
    [Required, MaxLength(32)]
    public string DisplayHint { get; set; } = default!;

    public PersonalAccessTokenScope Scope { get; set; }

    /// <summary>Optional expiry. <c>null</c> means the token never expires.</summary>
    public DateTime? ExpiresUtc { get; set; }

    /// <summary>Last time the token authenticated a request. Throttled to at most one write per minute.</summary>
    public DateTime? LastUsedUtc { get; set; }

    /// <summary>Set when the user revokes the token. A revoked token fails the next request.</summary>
    public DateTime? RevokedUtc { get; set; }

    public bool IsActive(DateTime utcNow) =>
        RevokedUtc is null && (ExpiresUtc is null || utcNow < ExpiresUtc);
}
