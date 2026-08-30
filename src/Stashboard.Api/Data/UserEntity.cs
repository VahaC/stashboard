using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Stashboard.Core.Entities;

namespace Stashboard.Api.Data;

/// <summary>
/// Application user. Replaces the previous ASP.NET Core Identity ApplicationUser.
/// All authentication state (password hash, lockout, security stamp) is stored here.
/// </summary>
public class UserEntity : AuditableEntity
{
    /// <summary>Original email as supplied by the user.</summary>
    [Required, MaxLength(256)]
    public string Email { get; set; } = default!;

    /// <summary>Upper-invariant email used for case-insensitive uniqueness checks.</summary>
    [Required, MaxLength(256)]
    public string NormalizedEmail { get; set; } = default!;

    /// <summary>Password hash in the format <c>pbkdf2-sha256$iter$saltB64$hashB64</c>.</summary>
    [Required]
    public string PasswordHash { get; set; } = default!;

    /// <summary>
    /// Random value rotated on logout-all / password change. Embedded in every JWT as the
    /// "stmp" claim so we can invalidate already-issued access tokens server-side.
    /// </summary>
    [Required, MaxLength(64)]
    public string SecurityStamp { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>UTC time until which the account is locked, or null when not locked.</summary>
    public DateTime? LockoutEndUtc { get; set; }

    /// <summary>Consecutive failed login attempts. Reset on successful login.</summary>
    public int FailedAccessCount { get; set; }

    public DateTime? LastLoginUtc { get; set; }

    /// <summary>Optional human-readable name shown in the UI.</summary>
    [MaxLength(128)]
    public string? DisplayName { get; set; }

    /// <summary>UI theme preference: <c>system</c> (default — follow OS), <c>light</c>, or <c>dark</c>.</summary>
    [Required, MaxLength(16)]
    public string Theme { get; set; } = "system";

    /// <summary>Dashboard sort mode: <c>name</c> (default) or <c>category</c>.</summary>
    [Required, MaxLength(16)]
    public string DashboardSortMode { get; set; } = "name";

    /// <summary>Dashboard grouping preference.</summary>
    public bool DashboardGroupByCategory { get; set; }

    [MaxLength(2048)]
    public string? TelegramBotTokenEncrypted { get; set; }

    [NotMapped]
    public string? TelegramBotToken { get; set; }

    [MaxLength(128)]
    public string? TelegramChatId { get; set; }

    public bool TelegramNotificationsEnabled { get; set; }

    /// <summary>True once the user clicks the link sent to their inbox.</summary>
    public bool EmailConfirmed { get; set; }

    /// <summary>SHA-256 hash of the email-confirmation token. Plaintext token is sent by email only.</summary>
    [MaxLength(128)]
    public string? EmailConfirmationTokenHash { get; set; }
    public DateTime? EmailConfirmationTokenExpiresUtc { get; set; }

    /// <summary>SHA-256 hash of the password-reset token. Cleared after successful reset.</summary>
    [MaxLength(128)]
    public string? PasswordResetTokenHash { get; set; }
    public DateTime? PasswordResetTokenExpiresUtc { get; set; }

    /// <summary>New email address awaiting confirmation. <see cref="Email"/> stays unchanged until confirmation.</summary>
    [MaxLength(256)]
    public string? PendingEmail { get; set; }

    [MaxLength(128)]
    public string? PendingEmailTokenHash { get; set; }
    public DateTime? PendingEmailTokenExpiresUtc { get; set; }

    // ── Two-factor authentication (TOTP) ───────────────────────────────────────

    /// <summary>True once the user has enrolled an authenticator app and confirmed a first code.</summary>
    public bool TwoFactorEnabled { get; set; }

    /// <summary>
    /// Encrypted-at-rest TOTP shared secret (Base32, via <c>IEncryptionService</c>). Written at
    /// enrollment with <see cref="TwoFactorEnabled"/> still false, so an abandoned enrollment never
    /// gates login. Never returned to the client except as the otpauth URI shown once during enroll.
    /// </summary>
    [MaxLength(512)]
    public string? TwoFactorSecretEncrypted { get; set; }

    /// <summary>
    /// The last TOTP time-step that was successfully consumed. A presented code whose step is
    /// less than or equal to this is rejected as a replay, closing the 30–90s reuse window.
    /// </summary>
    public long? TwoFactorLastUsedStep { get; set; }

    public ICollection<RefreshTokenEntity> RefreshTokens { get; set; } = new List<RefreshTokenEntity>();

    public ICollection<TwoFactorRecoveryCodeEntity> TwoFactorRecoveryCodes { get; set; } = new List<TwoFactorRecoveryCodeEntity>();

    /// <summary>V10.4 — personal access tokens minted by the user for scripting the REST API.</summary>
    public ICollection<PersonalAccessTokenEntity> PersonalAccessTokens { get; set; } = new List<PersonalAccessTokenEntity>();
}
