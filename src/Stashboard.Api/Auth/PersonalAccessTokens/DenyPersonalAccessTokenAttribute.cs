using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Stashboard.Api.Auth.PersonalAccessTokens;

/// <summary>
/// Refuses requests authenticated with a personal access token, returning <c>403</c> regardless of
/// the token's scope. Applied to high-risk surfaces a PAT must never reach by construction:
/// the host-shell / container-exec ticket endpoints and account-security mutations (password,
/// email, 2FA, account deletion, the token-management endpoints themselves).
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class DenyPersonalAccessTokenAttribute : Attribute, IAuthorizationFilter
{
    public void OnAuthorization(AuthorizationFilterContext context)
    {
        if (context.HttpContext.User.IsPersonalAccessToken())
        {
            context.Result = new ObjectResult(new { error = "Personal access tokens are not allowed on this endpoint." })
            {
                StatusCode = StatusCodes.Status403Forbidden,
            };
        }
    }
}
