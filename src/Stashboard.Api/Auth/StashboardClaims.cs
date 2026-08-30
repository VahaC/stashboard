using System.Security.Claims;

namespace Stashboard.Api.Auth;

/// <summary>
/// Custom claim types used by Stashboard. We intentionally avoid the long XML-namespace
/// strings shipped with <see cref="ClaimTypes"/> to keep tokens compact.
/// </summary>
public static class StashboardClaims
{
    /// <summary>User id (Guid as string). Mirrors <see cref="ClaimTypes.NameIdentifier"/>.</summary>
    public const string UserId = "uid";

    /// <summary>Email of the authenticated user.</summary>
    public const string Email = "email";

    /// <summary>
    /// Snapshot of <c>User.SecurityStamp</c> taken when the access token was issued.
    /// On every authenticated request we compare the claim against the current value
    /// in the database; a mismatch means the token must be rejected (logout-all,
    /// password change, security event).
    /// </summary>
    public const string SecurityStamp = "stmp";

    /// <summary>
    /// Marks a token's intended use. Access tokens carry no purpose claim; the short-lived
    /// 2FA challenge token carries <see cref="TwoFactorPendingPurpose"/> so it can never be
    /// replayed against authenticated endpoints (and vice-versa).
    /// </summary>
    public const string Purpose = "purpose";

    /// <summary>Purpose value for the interim token issued between the two login steps.</summary>
    public const string TwoFactorPendingPurpose = "2fa_pending";

    /// <summary>
    /// How the principal authenticated. Absent for normal JWT access tokens; set to
    /// <see cref="PersonalAccessTokenAuthType"/> when the request was authenticated with a PAT.
    /// Used to keep PATs off high-risk surfaces (host-shell / exec / account-security mutations).
    /// </summary>
    public const string AuthType = "auth_type";

    /// <summary><see cref="AuthType"/> value identifying a personal-access-token principal.</summary>
    public const string PersonalAccessTokenAuthType = "pat";

    /// <summary>Scope carried by a PAT principal: <c>read</c> or <c>full</c>.</summary>
    public const string PatScope = "pat_scope";
}
