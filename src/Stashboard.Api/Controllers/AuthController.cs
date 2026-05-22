using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Stashboard.Api.Auth;
using Stashboard.Api.Contracts;
using Stashboard.Api.Data;
using Stashboard.Api.Mapping;

namespace Stashboard.Api.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController(IUserService users, ITokenService tokens, IStashboardMapper mapper, IOptions<JwtOptions> jwtOptions) : ControllerBase
{
    private readonly JwtOptions _jwt = jwtOptions.Value;
    [HttpPost("register")]
    [AllowAnonymous]
    public async Task<ActionResult<AuthResponse>> Register([FromBody] RegisterRequest req, CancellationToken сancellationToken)
    {
        var result = await users.RegisterAsync(req.Email, req.Password, сancellationToken);
        if (!result.Succeeded)
        {
            return result.Failure!.Reason switch
            {
                AuthFailureReason.EmailAlreadyTaken => Conflict(new { error = result.Failure.Message }),
                _ => BadRequest(new { error = result.Failure.Message }),
            };
        }
        var pair = await tokens.IssueAsync(result.User!, сancellationToken);
        return Ok(BuildResponse(pair, result.User!));
    }

    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<ActionResult<AuthResponse>> Login([FromBody] LoginRequest req, CancellationToken сancellationToken)
    {
        var result = await users.ValidatePasswordAsync(req.Email, req.Password, сancellationToken);
        if (!result.Succeeded)
        {
            return result.Failure!.Reason switch
            {
                AuthFailureReason.AccountLocked => StatusCode(StatusCodes.Status423Locked, new { error = result.Failure.Message }),
                _ => Unauthorized(new { error = result.Failure.Message }),
            };
        }
        if (_jwt.RequireConfirmedEmail && !result.User!.EmailConfirmed)
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "Email not confirmed.", reason = nameof(AuthFailureReason.EmailNotConfirmed) });
        var pair = await tokens.IssueAsync(result.User!, сancellationToken);
        return Ok(BuildResponse(pair, result.User!));
    }

    [HttpPost("refresh")]
    [AllowAnonymous]
    public async Task<ActionResult<AuthResponse>> Refresh([FromBody] RefreshRequest req, CancellationToken сancellationToken)
    {
        var rotation = await tokens.RotateAsync(req.RefreshToken, сancellationToken);
        if (!rotation.Succeeded) return Unauthorized(new { error = "Invalid refresh token." });
        return Ok(BuildResponse(rotation.Pair!, rotation.User!));
    }

    [HttpPost("logout")]
    [Authorize]
    public async Task<IActionResult> Logout([FromBody] RefreshRequest req, CancellationToken сancellationToken)
    {
        await tokens.RevokeAsync(req.RefreshToken, сancellationToken);
        return NoContent();
    }

    [HttpPost("logout-all")]
    [Authorize]
    public async Task<IActionResult> LogoutAll(CancellationToken сancellationToken)
    {
        var userId = User.GetUserId();
        await users.RotateSecurityStampAsync(userId, сancellationToken);
        await tokens.RevokeAllForUserAsync(userId, сancellationToken);
        return NoContent();
    }

    [HttpGet("me")]
    [Authorize]
    public async Task<ActionResult<UserResponse>> Me(CancellationToken сancellationToken)
    {
        var user = await users.FindByIdAsync(User.GetUserId(), сancellationToken);
        if (user is null) return Unauthorized();
        return Ok(mapper.MapToUserResponse(user));
    }

    private AuthResponse BuildResponse(TokenPair pair, UserEntity user) =>
        new(pair.AccessToken, pair.AccessTokenExpiresUtc, pair.RefreshToken, pair.RefreshTokenExpiresUtc,
            pair.SessionExpiresUtc, mapper.MapToUserResponse(user));
}

