using Microsoft.AspNetCore.Authorization;

namespace Stashboard.Api.Auth.PersonalAccessTokens;

/// <summary>
/// Added to the default authorization policy so it is enforced on every <c>[Authorize]</c> endpoint.
/// A read-scoped PAT may only use safe (non-mutating) HTTP methods; JWT principals and full-scope
/// PATs are unaffected. Method-based rather than per-endpoint, so a new mutating endpoint is locked
/// down by default with no annotation to forget.
/// </summary>
public sealed class PatScopeRequirement : IAuthorizationRequirement;

public sealed class PatScopeAuthorizationHandler : AuthorizationHandler<PatScopeRequirement>
{
    private static readonly HashSet<string> SafeMethods =
        new(StringComparer.OrdinalIgnoreCase) { "GET", "HEAD", "OPTIONS" };

    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, PatScopeRequirement requirement)
    {
        // Only constrains read-scoped PATs; everything else satisfies the requirement.
        if (context.User.IsPersonalAccessToken() && context.User.GetPatScope() == "read")
        {
            if (context.Resource is HttpContext http && !SafeMethods.Contains(http.Request.Method))
            {
                context.Fail(new AuthorizationFailureReason(this, "Read-only token cannot perform mutating requests."));
                return Task.CompletedTask;
            }
        }

        context.Succeed(requirement);
        return Task.CompletedTask;
    }
}
