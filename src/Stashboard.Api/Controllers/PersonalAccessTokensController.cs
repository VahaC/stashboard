using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Stashboard.Api.Auth;
using Stashboard.Api.Auth.PersonalAccessTokens;
using Stashboard.Api.Contracts;
using Stashboard.Api.Data;

namespace Stashboard.Api.Controllers;

/// <summary>
/// V10.4 — personal access token management. Mounted under the account surface; JWT-only
/// (<see cref="DenyPersonalAccessTokenAttribute"/>) so a token can never mint or revoke tokens.
/// </summary>
[ApiController]
[Authorize]
[DenyPersonalAccessToken]
[Route("api/account/tokens")]
public class PersonalAccessTokensController(
    IPersonalAccessTokenService tokens,
    IUserService users,
    TimeProvider time) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<PersonalAccessTokenResponse>>> List(CancellationToken cancellationToken)
    {
        var list = await tokens.ListAsync(User.GetUserId(), cancellationToken);
        var now = time.GetUtcNow().UtcDateTime;
        return Ok(list.Select(t => ToResponse(t, now)).ToList());
    }

    [HttpPost]
    public async Task<ActionResult<CreatedPersonalAccessTokenResponse>> Create(
        [FromBody] CreatePersonalAccessTokenRequest req, CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();

        // Re-authenticate: minting a long-lived secret is a security-sensitive action.
        var verified = await users.VerifyPasswordAsync(userId, req.CurrentPassword, cancellationToken);
        if (!verified.Succeeded) return BadRequest(new { error = verified.Failure!.Message });

        var now = time.GetUtcNow().UtcDateTime;
        if (req.ExpiresUtc is { } expiry && expiry <= now)
            return BadRequest(new { error = "Expiry must be in the future." });

        var scope = req.Scope == "read" ? PersonalAccessTokenScope.Read : PersonalAccessTokenScope.Full;
        var result = await tokens.CreateAsync(userId, req.Name, scope, req.ExpiresUtc, cancellationToken);

        return Ok(new CreatedPersonalAccessTokenResponse(ToResponse(result.Token, now), result.PlaintextSecret));
    }

    [HttpPost("{id:guid}/revoke")]
    public async Task<IActionResult> Revoke(Guid id, CancellationToken cancellationToken)
    {
        var ok = await tokens.RevokeAsync(User.GetUserId(), id, cancellationToken);
        return ok ? NoContent() : NotFound();
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var ok = await tokens.DeleteAsync(User.GetUserId(), id, cancellationToken);
        return ok ? NoContent() : NotFound();
    }

    private static PersonalAccessTokenResponse ToResponse(PersonalAccessTokenEntity t, DateTime now)
    {
        var status = t.RevokedUtc is not null ? "revoked"
            : t.ExpiresUtc is not null && t.ExpiresUtc <= now ? "expired"
            : "active";
        var scope = t.Scope == PersonalAccessTokenScope.Read ? "read" : "full";
        return new PersonalAccessTokenResponse(
            t.Id, t.Name, t.DisplayHint, scope, t.ExpiresUtc, t.LastUsedUtc, t.RevokedUtc, t.CreatedUtc, status);
    }
}
