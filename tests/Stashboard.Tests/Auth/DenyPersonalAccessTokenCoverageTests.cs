using System.Reflection;
using Stashboard.Api.Auth.PersonalAccessTokens;
using Stashboard.Api.Controllers;

namespace Stashboard.Tests.Auth;

/// <summary>
/// Guards the JWT-only surface: every security / account-lifecycle endpoint a PAT must never reach
/// has to carry <see cref="DenyPersonalAccessTokenAttribute"/>. A pipeline test proves the filter's
/// runtime behaviour once; this proves it's actually wired to the right endpoints, so a newly-added
/// sensitive endpoint that forgets the annotation fails here instead of silently accepting PATs.
/// </summary>
public class DenyPersonalAccessTokenCoverageTests
{
    public static IEnumerable<object[]> JwtOnlyEndpoints()
    {
        yield return [typeof(AccountController), "ChangePassword"];
        yield return [typeof(AccountController), "ChangeEmail"];
        yield return [typeof(AccountController), "ConfirmEmailChange"];
        yield return [typeof(AccountController), "EnrollTwoFactor"];
        yield return [typeof(AccountController), "EnableTwoFactor"];
        yield return [typeof(AccountController), "DisableTwoFactor"];
        yield return [typeof(AccountController), "RegenerateRecoveryCodes"];
        yield return [typeof(AccountController), "Delete"];
        yield return [typeof(AccountController), "UpdateEmailSettings"];
        yield return [typeof(AccountController), "GetTelegramSettings"];
        yield return [typeof(AccountController), "UpdateTelegramSettings"];
        yield return [typeof(AuthController), "LogoutAll"];
        yield return [typeof(HostTerminalController), "CreateTicket"];
        yield return [typeof(ContainerExecController), "CreateTicket"];
    }

    [Theory]
    [MemberData(nameof(JwtOnlyEndpoints))]
    public void Endpoint_DeniesPersonalAccessTokens(Type controller, string method)
    {
        var info = controller.GetMethod(method, BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(info);
        Assert.True(HasDeny(info!), $"{controller.Name}.{method} must carry [DenyPersonalAccessToken].");
    }

    [Fact]
    public void TokenManagementController_IsEntirelyJwtOnly()
    {
        // Class-level attribute covers create / list / revoke / delete — a PAT can never manage tokens.
        Assert.True(typeof(PersonalAccessTokensController)
            .GetCustomAttributes(typeof(DenyPersonalAccessTokenAttribute), inherit: true).Length > 0);
    }

    private static bool HasDeny(MethodInfo method) =>
        method.GetCustomAttributes(typeof(DenyPersonalAccessTokenAttribute), inherit: true).Length > 0
        || method.DeclaringType!.GetCustomAttributes(typeof(DenyPersonalAccessTokenAttribute), inherit: true).Length > 0;
}
