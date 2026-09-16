using Microsoft.IdentityModel.Tokens;

namespace Stashboard.Api.Auth.Oidc;

/// <summary>The subset of an OIDC provider's discovery document the login flow needs.</summary>
public sealed record OidcProviderConfiguration(
    string AuthorizationEndpoint,
    string TokenEndpoint,
    string? UserInfoEndpoint,
    string Issuer,
    IReadOnlyCollection<SecurityKey> SigningKeys);

/// <summary>
/// Loads (and caches) an OIDC provider's <c>/.well-known/openid-configuration</c> document and JWKS.
/// Abstracted so the login flow can be tested without a live provider.
/// </summary>
public interface IOidcDiscoveryClient
{
    Task<OidcProviderConfiguration> GetAsync(string issuer, CancellationToken cancellationToken = default);
}
