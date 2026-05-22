using System.ComponentModel.DataAnnotations;
using Stashboard.Core.Entities;

namespace Stashboard.Api.Data;

/// <summary>
/// Reason a refresh token was revoked. Drives token-reuse detection and observability.
/// </summary>
public enum RefreshTokenRevokeReason
{
    /// <summary>Token was rotated as part of a normal refresh call.</summary>
    Rotated = 1,
    /// <summary>User explicitly logged out.</summary>
    LoggedOut = 2,
    /// <summary>An already-revoked token was presented again — entire family invalidated.</summary>
    Reuse = 3,
    /// <summary>Forced logout (e.g. password change, admin action).</summary>
    SecurityStampChanged = 4,
}

/// <summary>
/// Persisted refresh token. Raw token never stored — only an HMAC-SHA256 hash.
/// </summary>
/// <remarks>
/// FamilyId links every token issued in the same logical session (initial login + every rotation).
/// SessionExpiresUtc enforces an absolute window on the family regardless of how often the token is rotated.
/// ExpiresUtc enforces sliding lifetime on the individual token.
/// </remarks>
public class RefreshTokenEntity : AuditableEntity
{
    public Guid UserId { get; set; }
    public UserEntity? User { get; set; }

    /// <summary>HMAC-SHA256(token) hex-encoded. Unique.</summary>
    [Required, MaxLength(64)]
    public string TokenHash { get; set; } = default!;

    /// <summary>Identifier shared by every token rotated from the same login.</summary>
    public Guid FamilyId { get; set; }

    /// <summary>Sliding expiration of this individual token.</summary>
    public DateTime ExpiresUtc { get; set; }

    /// <summary>Absolute expiration of the entire family (set at first login, copied on every rotation).</summary>
    public DateTime SessionExpiresUtc { get; set; }

    public DateTime? RevokedUtc { get; set; }
    public RefreshTokenRevokeReason? RevokedReason { get; set; }

    /// <summary>Successor token id when this one was rotated.</summary>
    public Guid? ReplacedById { get; set; }

    public bool IsActive(DateTime utcNow) =>
        RevokedUtc is null && utcNow < ExpiresUtc && utcNow < SessionExpiresUtc;
}
