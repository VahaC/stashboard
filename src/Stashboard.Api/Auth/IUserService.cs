using Stashboard.Api.Data;

namespace Stashboard.Api.Auth;

/// <summary>
/// Result of a credential operation: either the authenticated/registered user, or a typed failure.
/// </summary>
public sealed record AuthResult(UserEntity? User, AuthFailure? Failure)
{
    public bool Succeeded => User is not null && Failure is null;
}

/// <summary>
/// Result of an account-management operation that may fail with a typed reason but does not
/// necessarily return a user (e.g. password reset, email confirmation).
/// </summary>
public sealed record OperationResult(bool Succeeded, AuthFailure? Failure)
{
    public static OperationResult Ok() => new(true, null);
    public static OperationResult Fail(AuthFailureReason reason, string message) =>
        new(false, new AuthFailure(reason, message));
}

/// <summary>
/// Result of an operation that issues a one-time token (email confirmation, password reset).
/// The plaintext token is returned ONLY here so the caller can email it; never stored.
/// </summary>
public sealed record TokenIssueResult(UserEntity? User, string? Token, AuthFailure? Failure)
{
    public bool Succeeded => User is not null && Token is not null && Failure is null;
}

public interface IUserService
{
    Task<AuthResult> RegisterAsync(string email, string password, CancellationToken cancellationToken = default);
    Task<AuthResult> ValidatePasswordAsync(string email, string password, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a user with Telegram settings materialized for application use
    /// (bot token is decrypted in-memory when present).
    /// </summary>
    Task<UserEntity?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a user with Telegram settings materialized for application use
    /// (bot token is decrypted in-memory when present).
    /// </summary>
    Task<UserEntity?> FindByEmailAsync(string email, CancellationToken cancellationToken = default);

    /// <summary>Rotates SecurityStamp — all existing access tokens become invalid on the next request.</summary>
    Task RotateSecurityStampAsync(Guid userId, CancellationToken cancellationToken = default);

    // ── Account management ───────────────────────────────────────────────────
    Task<TokenIssueResult> CreateEmailConfirmationTokenAsync(Guid userId, CancellationToken cancellationToken = default);
    Task<OperationResult> ConfirmEmailAsync(string email, string token, CancellationToken cancellationToken = default);

    Task<TokenIssueResult> CreatePasswordResetTokenAsync(string email, CancellationToken cancellationToken = default);
    Task<OperationResult> ResetPasswordAsync(string email, string token, string newPassword, CancellationToken cancellationToken = default);

    Task<OperationResult> ChangePasswordAsync(Guid userId, string currentPassword, string newPassword, CancellationToken cancellationToken = default);

    /// <summary>Re-authenticates an already-signed-in user by verifying their current password (no side effects).</summary>
    Task<OperationResult> VerifyPasswordAsync(Guid userId, string currentPassword, CancellationToken cancellationToken = default);

    Task<TokenIssueResult> RequestEmailChangeAsync(Guid userId, string newEmail, string currentPassword, CancellationToken cancellationToken = default);
    Task<OperationResult> ConfirmEmailChangeAsync(Guid userId, string token, CancellationToken cancellationToken = default);

    /// <summary>
    /// Partial update — only fields with non-null values are written. Pass <c>null</c> for any
    /// field you don't want to change. Empty/whitespace <paramref name="displayName"/> clears it.
    /// </summary>
    Task<OperationResult> UpdateProfileAsync(Guid userId, string? displayName, string? theme, CancellationToken cancellationToken = default);

    /// <summary>Sets only the user's UI theme preference. Cheap path for the in-app theme switcher.</summary>
    Task<OperationResult> SetThemeAsync(Guid userId, string theme, CancellationToken cancellationToken = default);

    /// <summary>Sets dashboard sorting/grouping preferences for the current user.</summary>
    Task<OperationResult> SetDashboardPreferencesAsync(Guid userId, string sortMode, bool groupByCategory, CancellationToken cancellationToken = default);

    Task<OperationResult> UpdateTelegramSettingsAsync(
        Guid userId,
        string? botToken,
        string? chatId,
        bool notificationsEnabled,
        CancellationToken cancellationToken = default);

    Task<OperationResult> DeleteAccountAsync(Guid userId, string currentPassword, CancellationToken cancellationToken = default);

    static string Normalize(string email) => (email ?? string.Empty).Trim().ToUpperInvariant();
}
