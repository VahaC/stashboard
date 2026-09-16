using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Stashboard.Api.Auth;
using Stashboard.Api.Auth.Oidc;
using Stashboard.Api.Contracts;
using Stashboard.Api.Data;
using Stashboard.Api.Mapping;

namespace Stashboard.Api.Controllers;

/// <summary>
/// V10.5 — OIDC / SSO login endpoints. All anonymous: they run before the user has a session. The
/// flow is start → (provider) → callback, and the callback returns the same <see cref="AuthResponse"/>
/// the password path issues, so nothing downstream changes.
/// </summary>
[ApiController]
[Route("api/auth/oidc")]
public class OidcController(
    IOidcService oidc,
    IOidcSettingsService settings,
    ITokenService tokens,
    IStashboardMapper mapper) : ControllerBase
{
    /// <summary>Login-page probe: whether the SSO button should show, and its label. No secrets.</summary>
    [HttpGet("info")]
    [AllowAnonymous]
    public async Task<ActionResult<OidcInfoResponse>> Info(CancellationToken cancellationToken)
    {
        var resolved = await settings.GetResolvedAsync(cancellationToken);
        var label = string.IsNullOrWhiteSpace(resolved.DisplayName) ? "SSO" : resolved.DisplayName;
        return Ok(new OidcInfoResponse(resolved.IsUsable, label));
    }

    /// <summary>Begins a sign-in: returns the provider authorize URL the SPA redirects the browser to.</summary>
    [HttpPost("start")]
    [AllowAnonymous]
    public async Task<ActionResult<OidcStartResponse>> Start(CancellationToken cancellationToken)
    {
        var url = await oidc.StartAsync(cancellationToken);
        if (url is null) return BadRequest(new { error = "OIDC sign-in is not configured." });
        return Ok(new OidcStartResponse(url));
    }

    /// <summary>Completes a sign-in by exchanging the code; returns the normal token pair on success.</summary>
    [HttpPost("callback")]
    [AllowAnonymous]
    public async Task<ActionResult<AuthResponse>> Callback([FromBody] OidcCallbackRequest req, CancellationToken cancellationToken)
    {
        var result = await oidc.CompleteAsync(req.Code, req.State, cancellationToken);
        if (!result.Succeeded)
        {
            return result.Failure switch
            {
                OidcFailureReason.AccountConflict => Conflict(new { error = result.Message }),
                OidcFailureReason.NotConfigured => BadRequest(new { error = result.Message }),
                _ => Unauthorized(new { error = result.Message }),
            };
        }

        var pair = await tokens.IssueAsync(result.User!, cancellationToken);
        var response = new AuthResponse(
            pair.AccessToken, pair.AccessTokenExpiresUtc, pair.RefreshToken, pair.RefreshTokenExpiresUtc,
            pair.SessionExpiresUtc, mapper.MapToUserResponse(result.User!));
        return Ok(response);
    }
}
