using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Stashboard.Api.Data;

namespace Stashboard.Api.Auth;

/// <summary>
/// JwtBearer events that enforce server-side invalidation of access tokens by comparing the
/// <see cref="StashboardClaims.SecurityStamp"/> claim with the user's current value in the DB.
/// A mismatch (or missing user / missing stamp) fails the authentication.
/// </summary>
public static class JwtBearerEventHandlers
{
    public static JwtBearerEvents Build() => new()
    {
        OnTokenValidated = async ctx =>
        {
            var principal = ctx.Principal;
            if (principal is null) { ctx.Fail("No principal."); return; }

            var stamp = principal.GetSecurityStamp();
            if (string.IsNullOrEmpty(stamp)) { ctx.Fail("Missing security stamp claim."); return; }

            Guid userId;
            try { userId = principal.GetUserId(); }
            catch (UnauthorizedAccessException) { ctx.Fail("Missing user id claim."); return; }

            var db = ctx.HttpContext.RequestServices.GetRequiredService<ApplicationDbContext>();
            var current = await db.Users
                .AsNoTracking()
                .Where(u => u.Id == userId)
                .Select(u => u.SecurityStamp)
                .FirstOrDefaultAsync(ctx.HttpContext.RequestAborted);

            if (current is null) { ctx.Fail("User no longer exists."); return; }
            if (!string.Equals(current, stamp, StringComparison.Ordinal))
            {
                ctx.Fail("Security stamp mismatch.");
            }
        }
    };
}
