using System.ComponentModel.DataAnnotations;

namespace Stashboard.Api.Contracts;

public sealed record RegisterRequest(
    [Required, EmailAddress, StringLength(256)] string Email,
    [Required, StringLength(128, MinimumLength = 8)] string Password
);

public sealed record LoginRequest(
    [Required, EmailAddress, StringLength(256)] string Email,
    [Required, StringLength(128, MinimumLength = 1)] string Password
);

public sealed record RefreshRequest(
    [Required] string RefreshToken
);

public sealed record AuthResponse(
    string AccessToken,
    DateTime AccessTokenExpiresUtc,
    string RefreshToken,
    DateTime RefreshTokenExpiresUtc,
    DateTime SessionExpiresUtc,
    UserResponse User
);

public sealed record UserResponse(
    Guid Id, string Email
);

// ── Account management ────────────────────────────────────────────────────────

public sealed record ChangePasswordRequest(
    [Required, StringLength(128, MinimumLength = 1)] string CurrentPassword,
    [Required, StringLength(128, MinimumLength = 8)] string NewPassword
);

public sealed record ForgotPasswordRequest(
    [Required, EmailAddress, StringLength(256)] string Email
);

public sealed record ResetPasswordRequest(
    [Required, EmailAddress, StringLength(256)] string Email,
    [Required] string Token,
    [Required, StringLength(128, MinimumLength = 8)] string NewPassword
);

public sealed record ConfirmEmailRequest(
    [Required, EmailAddress, StringLength(256)] string Email,
    [Required] string Token
);

public sealed record ResendConfirmationRequest(
    [Required, EmailAddress, StringLength(256)] string Email
);

public sealed record ChangeEmailRequest(
    [Required, EmailAddress, StringLength(256)] string NewEmail,
    [Required, StringLength(128, MinimumLength = 1)] string CurrentPassword
);

public sealed record ConfirmEmailChangeRequest(
    [Required] string Token
);

public sealed record UpdateProfileRequest(
    [StringLength(128)] string? DisplayName,
    Theme? Theme
);

public sealed record UpdateThemeRequest(
    Theme Theme
);

public sealed record DashboardPreferencesResponse(
    DashboardSortMode SortMode,
    bool GroupByCategory
);

public sealed record UpdateDashboardPreferencesRequest(
    DashboardSortMode SortMode,
    bool GroupByCategory
);

public sealed record TelegramSettingsResponse(
    string? BotToken,
    string? ChatId,
    bool NotificationsEnabled
);

public sealed record UpdateTelegramSettingsRequest(
    [MaxLength(256)] string? BotToken,
    [MaxLength(128)] string? ChatId,
    bool NotificationsEnabled
);

public sealed record DeleteAccountRequest(
    [Required, StringLength(128, MinimumLength = 1)] string CurrentPassword
);

/// <summary>V10.5 — toggles whether the current account may sign in with its local password.</summary>
public sealed record SetLocalLoginRequest(bool LocalLoginDisabled);

// ── Email server (SMTP) settings ────────────────────────────────────────────────

/// <summary>
/// Masked view of the app-wide email settings. The SMTP password is never returned —
/// only <see cref="HasPassword"/> tells the UI whether one is stored.
/// </summary>
public sealed record EmailSettingsResponse(
    EmailProvider Provider,
    string Host,
    int Port,
    bool UseStartTls,
    string Username,
    bool HasPassword,
    string FromAddress,
    string FromName,
    string AppBaseUrl
);

public sealed record UpdateEmailSettingsRequest(
    EmailProvider Provider,
    [MaxLength(256)] string? Host,
    [Range(1, 65535)] int Port,
    bool UseStartTls,
    [MaxLength(256)] string? Username,
    // Tri-state secret: omit / Keep to preserve the stored password, Set to replace, Clear to drop.
    SecretValueUpsert? Password,
    [MaxLength(256)] string? FromAddress,
    [MaxLength(128)] string? FromName,
    [MaxLength(512)] string? AppBaseUrl
);

public sealed record ProfileResponse(
    Guid Id,
    string Email,
    string? DisplayName,
    bool EmailConfirmed,
    string? PendingEmail,
    Theme Theme,
    DateTime CreatedUtc,
    DateTime? LastLoginUtc,
    bool TwoFactorEnabled,
    /// <summary>V10.5 — true once this account has been linked to an OIDC identity (has a stored subject).</summary>
    bool OidcLinked,
    /// <summary>V10.5 — whether the owner has turned off local password sign-in for this account.</summary>
    bool LocalLoginDisabled
);

// ── Two-factor authentication (TOTP) ──────────────────────────────────────────

/// <summary>Enrollment payload: the otpauth URI (rendered as a QR by the client) + manual key.</summary>
public sealed record TwoFactorEnrollResponse(string OtpauthUri, string ManualKey);

public sealed record EnableTwoFactorRequest(
    [Required, StringLength(16, MinimumLength = 6)] string Code
);

/// <summary>The one-time recovery codes, shown exactly once after enable / regenerate.</summary>
public sealed record RecoveryCodesResponse(IReadOnlyList<string> RecoveryCodes);

/// <summary>Confirms a security-sensitive 2FA mutation (disable / regenerate) with the password.</summary>
public sealed record TwoFactorPasswordRequest(
    [Required, StringLength(128, MinimumLength = 1)] string CurrentPassword
);

/// <summary>Step-1 login response when the account has 2FA enabled — no tokens, only a challenge.</summary>
public sealed record TwoFactorChallengeResponse(bool RequiresTwoFactor, string ChallengeToken);

public sealed record TwoFactorLoginRequest(
    [Required] string ChallengeToken,
    [Required, StringLength(16, MinimumLength = 6)] string Code
);

