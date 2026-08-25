using Stashboard.Api.Data;

namespace Stashboard.Api.Auth;

public sealed record TokenPair(
    string AccessToken,
    DateTime AccessTokenExpiresUtc,
    string RefreshToken,
    DateTime RefreshTokenExpiresUtc,
    DateTime SessionExpiresUtc);

/// <summary>
/// Outcome of a refresh-token rotation attempt. Either a fresh pair (and the owning user)
/// or a typed failure that the caller maps to an HTTP response.
/// </summary>
public sealed record RotateResult(TokenPair? Pair, UserEntity? User, RotateFailureReason? Failure)
{
    public bool Succeeded => Pair is not null && User is not null && Failure is null;

    public static RotateResult Fail(RotateFailureReason reason) => new(null, null, reason);
    public static RotateResult Success(TokenPair pair, UserEntity user) => new(pair, user, null);
}

public enum RotateFailureReason
{
    NotFound,
    Reused,
    Expired,
    SessionExpired,
    UserMissing,
}

/// <summary>
/// The decoded contents of a valid 2FA challenge token: the pending user and the
/// security-stamp snapshot taken when the password step succeeded.
/// </summary>
public sealed record TwoFactorChallenge(Guid UserId, string SecurityStamp);

public interface ITokenService
{
    /// <summary>Issues a fresh access+refresh pair and starts a new family.</summary>
    Task<TokenPair> IssueAsync(UserEntity user, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically rotates a refresh token. On success returns a new pair and revokes the
    /// presented one. On reuse (revoked token presented) revokes the entire family.
    /// </summary>
    Task<RotateResult> RotateAsync(string refreshToken, CancellationToken cancellationToken = default);

    /// <summary>Revokes the entire family containing the supplied token (logout).</summary>
    Task RevokeAsync(string refreshToken, CancellationToken cancellationToken = default);

    /// <summary>Revokes every active token belonging to the user (logout-all).</summary>
    Task RevokeAllForUserAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Removes refresh-token rows that have been expired or revoked beyond the retention window.</summary>
    Task<int> CleanupAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Issues the short-lived, signed challenge token returned by the password step when the
    /// account has 2FA enabled. It carries the user id, a security-stamp snapshot, and a purpose
    /// claim so it can never stand in for an access token. No DB state — it expires on its own.
    /// </summary>
    string IssueTwoFactorChallenge(UserEntity user);

    /// <summary>
    /// Validates a 2FA challenge token (signature, issuer/audience, expiry, purpose claim) and
    /// returns its embedded user id + security-stamp snapshot, or null when invalid/expired.
    /// </summary>
    TwoFactorChallenge? ValidateTwoFactorChallenge(string challengeToken);
}
