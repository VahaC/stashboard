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
}
