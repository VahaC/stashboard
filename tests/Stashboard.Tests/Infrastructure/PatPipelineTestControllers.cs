using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Stashboard.Api.Auth;
using Stashboard.Api.Auth.PersonalAccessTokens;

namespace Stashboard.Tests.Infrastructure;

/// <summary>
/// Minimal endpoints used by the PAT auth-pipeline tests. They exercise the real authentication
/// scheme selector, scope authorization handler, and <see cref="DenyPersonalAccessTokenAttribute"/>
/// without needing the full application's controllers and their dependencies.
/// </summary>
[ApiController]
[Authorize]
[Route("pipeline")]
public sealed class PatPipelineTestController : ControllerBase
{
    /// <summary>Safe method — reachable by any authenticated principal, including a read-only PAT.</summary>
    [HttpGet("read")]
    public IActionResult Read() => Ok(new PipelineProbe(
        User.GetUserId(), User.IsPersonalAccessToken(), User.GetPatScope()));

    /// <summary>Mutating method — a read-only PAT must be rejected here by the scope policy.</summary>
    [HttpPost("write")]
    public IActionResult Write() => Ok(new PipelineProbe(
        User.GetUserId(), User.IsPersonalAccessToken(), User.GetPatScope()));

    /// <summary>JWT-only surface — any PAT (even full scope) must be refused.</summary>
    [HttpPost("deny")]
    [DenyPersonalAccessToken]
    public IActionResult Deny() => Ok();
}

public sealed record PipelineProbe(Guid Uid, bool IsPat, string? Scope);
