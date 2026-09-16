using System.Collections.Concurrent;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Stashboard.Api.Auth.Oidc;

/// <summary>
/// Real discovery client backed by <see cref="ConfigurationManager{T}"/>, which fetches the
/// well-known document + JWKS over HTTP and caches them with automatic refresh. One manager is
/// kept per issuer so changing the configured provider picks up a fresh document.
/// </summary>
public sealed class OidcDiscoveryClient(IHttpClientFactory httpClientFactory) : IOidcDiscoveryClient
{
    private readonly ConcurrentDictionary<string, ConfigurationManager<OpenIdConnectConfiguration>> _managers = new();

    public async Task<OidcProviderConfiguration> GetAsync(string issuer, CancellationToken cancellationToken = default)
    {
        var manager = _managers.GetOrAdd(issuer, iss =>
        {
            var metadataAddress = iss.TrimEnd('/') + "/.well-known/openid-configuration";
            // Homelab providers are frequently reached over plain HTTP on the LAN (e.g.
            // http://authentik.lan), so we don't force HTTPS here — the issuer URL is
            // operator-supplied trusted config, mirroring the existing SkipTlsVerify posture.
            var retriever = new HttpDocumentRetriever(httpClientFactory.CreateClient("oidc")) { RequireHttps = false };
            return new ConfigurationManager<OpenIdConnectConfiguration>(
                metadataAddress, new OpenIdConnectConfigurationRetriever(), retriever);
        });

        var config = await manager.GetConfigurationAsync(cancellationToken);
        return new OidcProviderConfiguration(
            config.AuthorizationEndpoint,
            config.TokenEndpoint,
            string.IsNullOrEmpty(config.UserInfoEndpoint) ? null : config.UserInfoEndpoint,
            string.IsNullOrEmpty(config.Issuer) ? issuer : config.Issuer,
            config.SigningKeys.ToList());
    }
}
