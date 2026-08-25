namespace Stashboard.Api.Auth;

/// <summary>
/// Data the user needs to add the account to an authenticator app: the otpauth URI (rendered
/// as a QR code by the client) and the same secret formatted for manual entry. The raw secret
/// only ever leaves the server here, during enrollment.
/// </summary>
public sealed record TwoFactorEnrollment(string OtpauthUri, string ManualKey);

/// <summary>
/// Result of an operation that produces a fresh set of recovery codes (enable / regenerate).
/// The plaintext codes are returned ONLY here so the UI can show them once; only hashes persist.
/// </summary>
public sealed record RecoveryCodesResult(IReadOnlyList<string>? Codes, AuthFailure? Failure)
{
    public bool Succeeded => Codes is not null && Failure is null;
    public static RecoveryCodesResult Ok(IReadOnlyList<string> codes) => new(codes, null);
    public static RecoveryCodesResult Fail(AuthFailureReason reason, string message) =>
        new(null, new AuthFailure(reason, message));
}

/// <summary>
/// Two-factor authentication (TOTP) for a single account: enrollment, enable/disable, recovery
/// codes, and the login-time code check. The TOTP secret is encrypted at rest and never returned
/// after enrollment. Disable and recovery-code regeneration are security-sensitive mutations that
/// rotate the SecurityStamp and revoke all sessions, matching the password/email-change behaviour.
/// </summary>
public interface ITwoFactorService
{
    /// <summary>
    /// Begins enrollment: generates a fresh secret, stores it encrypted (still disabled), and
    /// returns the otpauth URI + manual key. Overwrites any abandoned, unconfirmed enrollment.
    /// Returns null only when the user does not exist.
    /// </summary>
    Task<TwoFactorEnrollment?> BeginEnrollAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Confirms the first code and enables 2FA, returning a fresh set of one-time recovery codes.
    /// Fails when no enrollment is pending or the code is wrong.
    /// </summary>
    Task<RecoveryCodesResult> EnableAsync(Guid userId, string code, CancellationToken cancellationToken = default);

    /// <summary>
    /// Disables 2FA after verifying the current password: clears the secret + recovery codes,
    /// rotates the SecurityStamp and revokes all sessions.
    /// </summary>
    Task<OperationResult> DisableAsync(Guid userId, string currentPassword, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the recovery-code set after verifying the current password, invalidating the old
    /// codes; rotates the SecurityStamp and revokes all sessions. Returns the new plaintext codes.
    /// </summary>
    Task<RecoveryCodesResult> RegenerateRecoveryCodesAsync(Guid userId, string currentPassword, CancellationToken cancellationToken = default);

    /// <summary>
    /// Completes the second login step: verifies a TOTP code (or, failing that, consumes a
    /// recovery code) for the pending user. Shares the account-lockout counter with the password
    /// step — wrong codes increment it and trigger the same lockout. Returns the user on success.
    /// </summary>
    Task<AuthResult> CompleteLoginAsync(Guid userId, string code, CancellationToken cancellationToken = default);
}
