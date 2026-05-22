using System.Security.Claims;

namespace Stashboard.Api.Auth;

/// <summary>
/// Convenience accessors for Stashboard-specific claims on a <see cref="ClaimsPrincipal"/>.
/// </summary>
public static class ClaimsPrincipalExtensions
{
    /// <summary>
    /// Returns the authenticated user id, or throws <see cref="UnauthorizedAccessException"/>
    /// when the principal carries no usable id claim.
    /// </summary>
    public static Guid GetUserId(this ClaimsPrincipal principal)
    {
        var raw = principal.FindFirstValue(StashboardClaims.UserId)
            ?? principal.FindFirstValue(ClaimTypes.NameIdentifier);

        if (Guid.TryParse(raw, out var id)) return id;
        throw new UnauthorizedAccessException("Authenticated principal has no valid user id claim.");
    }

    /// <summary>Returns the email claim, or null when absent.</summary>
    public static string? GetEmail(this ClaimsPrincipal principal)
        => principal.FindFirstValue(StashboardClaims.Email)
           ?? principal.FindFirstValue(ClaimTypes.Email);

    /// <summary>Returns the security-stamp snapshot embedded in the access token, or null.</summary>
    public static string? GetSecurityStamp(this ClaimsPrincipal principal)
        => principal.FindFirstValue(StashboardClaims.SecurityStamp);
}
