using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Stashboard.Api.Auth;

namespace Stashboard.Api.Auth.PersonalAccessTokens;

/// <summary>
/// Central wiring for Stashboard's dual-credential authentication: JWT access tokens and personal
/// access tokens. Lives in one place so the API host and the integration test host exercise the
/// exact same registration code (no drift between "what runs" and "what's tested").
/// </summary>
public static class StashboardAuthenticationExtensions
{
    /// <summary>Name of the policy scheme that routes each request to JWT or PAT by bearer prefix.</summary>
    public const string PolicyScheme = "Stashboard";

    public static IServiceCollection AddStashboardAuthentication(this IServiceCollection services, JwtOptions jwt)
    {
        services
            .AddAuthentication(options =>
            {
                options.DefaultScheme = PolicyScheme;
                options.DefaultAuthenticateScheme = PolicyScheme;
                options.DefaultChallengeScheme = PolicyScheme;
            })
            // The selector inspects the raw Authorization header and forwards PAT-prefixed bearers to
            // the PAT handler, everything else to JwtBearer — so existing [Authorize] works unchanged.
            .AddPolicyScheme(PolicyScheme, "JWT or PAT", options =>
            {
                options.ForwardDefaultSelector = context =>
                {
                    var header = context.Request.Headers.Authorization.ToString();
                    return header.StartsWith("Bearer " + PersonalAccessTokenService.TokenPrefix, StringComparison.Ordinal)
                        ? PatAuthenticationDefaults.Scheme
                        : JwtBearerDefaults.AuthenticationScheme;
                };
            })
            .AddJwtBearer(opts =>
            {
                opts.MapInboundClaims = false;
                opts.TokenValidationParameters = TokenService.BuildValidationParameters(jwt);
                opts.Events = JwtBearerEventHandlers.Build();
            })
            .AddScheme<AuthenticationSchemeOptions, PatAuthenticationHandler>(PatAuthenticationDefaults.Scheme, _ => { });

        services.AddSingleton<IAuthorizationHandler, PatScopeAuthorizationHandler>();
        services.AddAuthorization(options =>
        {
            // Fold the PAT read/write rule into the default policy so every [Authorize] enforces it.
            options.DefaultPolicy = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .AddRequirements(new PatScopeRequirement())
                .Build();
        });

        return services;
    }
}
