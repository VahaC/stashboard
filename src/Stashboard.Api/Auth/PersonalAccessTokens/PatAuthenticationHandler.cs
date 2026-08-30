using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Stashboard.Api.Data;

namespace Stashboard.Api.Auth.PersonalAccessTokens;

/// <summary>
/// Authenticates requests carrying a personal access token in <c>Authorization: Bearer sb_pat_…</c>.
/// The policy scheme (see <c>AddStashboardAuthentication</c>) forwards only PAT-prefixed bearers here,
/// so a JWT never reaches this handler and vice-versa.
/// </summary>
/// <remarks>
/// The resulting principal carries <c>uid</c> (so <c>User.GetUserId()</c> works unchanged), the
/// owner's email, an <c>auth_type=pat</c> marker, and the token's scope. It deliberately carries
/// <b>no</b> security-stamp claim and is <b>not</b> validated against <c>User.SecurityStamp</c> —
/// PATs survive password changes / logout-all by design and are killed only by revoke or account
/// deletion.
/// </remarks>
public sealed class PatAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    private const string BearerPrefix = "Bearer ";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(header) || !header.StartsWith(BearerPrefix, StringComparison.Ordinal))
            return AuthenticateResult.NoResult();

        var token = header[BearerPrefix.Length..].Trim();
        if (!token.StartsWith(PersonalAccessTokenService.TokenPrefix, StringComparison.Ordinal))
            return AuthenticateResult.NoResult();

        var service = Context.RequestServices.GetRequiredService<IPersonalAccessTokenService>();
        var principalInfo = await service.ValidateAsync(token, Context.RequestAborted);
        if (principalInfo is null)
            return AuthenticateResult.Fail("Invalid, expired, or revoked personal access token.");

        var db = Context.RequestServices.GetRequiredService<ApplicationDbContext>();
        var email = await db.Users.AsNoTracking()
            .Where(u => u.Id == principalInfo.UserId)
            .Select(u => u.Email)
            .FirstOrDefaultAsync(Context.RequestAborted);
        if (email is null)
            return AuthenticateResult.Fail("Token owner no longer exists.");

        var scope = principalInfo.Scope == PersonalAccessTokenScope.Read ? "read" : "full";
        var identity = new ClaimsIdentity(
            new[]
            {
                new Claim(StashboardClaims.UserId, principalInfo.UserId.ToString()),
                new Claim(StashboardClaims.Email, email),
                new Claim(StashboardClaims.AuthType, StashboardClaims.PersonalAccessTokenAuthType),
                new Claim(StashboardClaims.PatScope, scope),
            },
            authenticationType: PatAuthenticationDefaults.Scheme,
            nameType: StashboardClaims.UserId,
            roleType: null);

        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), PatAuthenticationDefaults.Scheme);
        return AuthenticateResult.Success(ticket);
    }
}
