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
    [RegularExpression("^(system|light|dark)$", ErrorMessage = "Theme must be 'system', 'light', or 'dark'.")]
    string? Theme
);

public sealed record UpdateThemeRequest(
    [Required, RegularExpression("^(system|light|dark)$", ErrorMessage = "Theme must be 'system', 'light', or 'dark'.")]
    string Theme
);

public sealed record DashboardPreferencesResponse(
    string SortMode,
    bool GroupByCategory
);

public sealed record UpdateDashboardPreferencesRequest(
    [Required, RegularExpression("^(name|category)$", ErrorMessage = "Sort mode must be 'name' or 'category'.")]
    string SortMode,
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

// ── Email server (SMTP) settings ────────────────────────────────────────────────

/// <summary>
/// Masked view of the app-wide email settings. The SMTP password is never returned —
/// only <see cref="HasPassword"/> tells the UI whether one is stored.
/// </summary>
public sealed record EmailSettingsResponse(
    string Provider,
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
    [Required, RegularExpression("^(Smtp|LogOnly)$", ErrorMessage = "Provider must be 'Smtp' or 'LogOnly'.")]
    string Provider,
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
    string Theme,
    DateTime CreatedUtc,
    DateTime? LastLoginUtc
);

