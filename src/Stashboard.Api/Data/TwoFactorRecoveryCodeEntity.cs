using System.ComponentModel.DataAnnotations;
using Stashboard.Core.Entities;

namespace Stashboard.Api.Data;

/// <summary>
/// A single one-time recovery code for two-factor authentication. The plaintext code is
/// shown to the user exactly once at generation time; only its hash is stored here (same
/// principle as a password hash). A code is consumable once — <see cref="UsedUtc"/> is set
/// the moment it is redeemed, after which it can never be used again.
/// </summary>
public class TwoFactorRecoveryCodeEntity : AuditableEntity
{
    public Guid UserId { get; set; }
    public UserEntity? User { get; set; }

    /// <summary>PBKDF2 hash of the recovery code (via <c>IPasswordHasher</c>). Never the plaintext.</summary>
    [Required]
    public string CodeHash { get; set; } = default!;

    /// <summary>UTC time the code was redeemed, or null while still available.</summary>
    public DateTime? UsedUtc { get; set; }
}
