using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Stashboard.Api.Data;

namespace Stashboard.Api.Auth.Oidc;

/// <summary>
/// Custom OIDC Authorization-Code + PKCE client. It deliberately does not use the ASP.NET cookie
/// middleware: Stashboard's session is its own JWT access + refresh pair, so the flow finishes by
/// resolving a <see cref="UserEntity"/> and the controller issues the usual token pair. Discovery /
/// JWKS validation reuse the Microsoft.IdentityModel stack; the token + userinfo HTTP calls go
/// through the "oidc" <see cref="IHttpClientFactory"/> client so they're mockable in tests.
/// </summary>
public sealed class OidcService(
    IOidcSettingsService settings,
    IOidcDiscoveryClient discovery,
    IOidcStateStore stateStore,
    IHttpClientFactory httpClientFactory,
    ApplicationDbContext db,
    TimeProvider time,
    ILogger<OidcService> logger) : IOidcService
{
    public async Task<string?> StartAsync(CancellationToken cancellationToken = default)
    {
        var resolved = await settings.GetResolvedAsync(cancellationToken);
        if (!resolved.IsUsable) return null;

        var config = await discovery.GetAsync(resolved.Issuer, cancellationToken);

        var state = RandomToken();
        var nonce = RandomToken();
        var codeVerifier = RandomToken();
        var codeChallenge = Base64UrlSha256(codeVerifier);

        stateStore.Save(state, new OidcAuthState(codeVerifier, nonce), OidcConstants.StateLifetime);

        var query = new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = resolved.ClientId,
            ["redirect_uri"] = resolved.RedirectUri,
            ["scope"] = resolved.Scopes,
            ["state"] = state,
            ["nonce"] = nonce,
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256",
        };
        var qs = string.Join('&', query.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
        var separator = config.AuthorizationEndpoint.Contains('?') ? '&' : '?';
        return $"{config.AuthorizationEndpoint}{separator}{qs}";
    }

    public async Task<OidcLoginResult> CompleteAsync(string code, string state, CancellationToken cancellationToken = default)
    {
        var resolved = await settings.GetResolvedAsync(cancellationToken);
        if (!resolved.IsUsable)
            return OidcLoginResult.Fail(OidcFailureReason.NotConfigured, "OIDC sign-in is not configured.");

        var authState = stateStore.Consume(state);
        if (authState is null)
            return OidcLoginResult.Fail(OidcFailureReason.InvalidState, "The sign-in request expired or was already used. Please try again.");

        var config = await discovery.GetAsync(resolved.Issuer, cancellationToken);

        var tokens = await ExchangeCodeAsync(code, authState.CodeVerifier, resolved, config, cancellationToken);
        if (tokens is null)
            return OidcLoginResult.Fail(OidcFailureReason.TokenExchangeFailed, "Could not exchange the authorization code with the provider.");

        var claims = ValidateIdToken(tokens.Value.IdToken, authState.Nonce, resolved, config);
        if (claims is null)
            return OidcLoginResult.Fail(OidcFailureReason.InvalidIdToken, "The provider's identity token failed validation.");

        var (subject, email, emailVerified, displayName) =
            await ResolveIdentityAsync(claims, tokens.Value.AccessToken, config, cancellationToken);

        if (string.IsNullOrWhiteSpace(subject))
            return OidcLoginResult.Fail(OidcFailureReason.InvalidIdToken, "The provider did not return a subject identifier.");
        if (string.IsNullOrWhiteSpace(email) || !emailVerified)
            return OidcLoginResult.Fail(OidcFailureReason.EmailNotVerified,
                "Your OIDC provider did not supply a verified email address, which Stashboard requires to link your account.");

        return await LinkOrProvisionAsync(subject!, email!, displayName, resolved.AllowOidcRegistration, cancellationToken);
    }

    // ── Token exchange ─────────────────────────────────────────────────────────

    private async Task<(string IdToken, string? AccessToken)?> ExchangeCodeAsync(
        string code, string codeVerifier, ResolvedOidcSettings resolved, OidcProviderConfiguration config,
        CancellationToken cancellationToken)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = resolved.RedirectUri,
            ["client_id"] = resolved.ClientId,
            ["code_verifier"] = codeVerifier,
        };
        // Confidential client — send the secret. A public (PKCE-only) client omits it.
        if (!string.IsNullOrEmpty(resolved.ClientSecret))
            form["client_secret"] = resolved.ClientSecret;

        try
        {
            var client = httpClientFactory.CreateClient("oidc");
            using var response = await client.PostAsync(config.TokenEndpoint, new FormUrlEncodedContent(form), cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("OIDC token exchange failed ({Status}): {Body}", (int)response.StatusCode, Truncate(body));
                return null;
            }

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (!root.TryGetProperty("id_token", out var idTokenEl) || idTokenEl.ValueKind != JsonValueKind.String)
            {
                logger.LogWarning("OIDC token response did not contain an id_token.");
                return null;
            }
            var accessToken = root.TryGetProperty("access_token", out var atEl) && atEl.ValueKind == JsonValueKind.String
                ? atEl.GetString()
                : null;
            return (idTokenEl.GetString()!, accessToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            logger.LogWarning(ex, "OIDC token exchange threw.");
            return null;
        }
    }

    // ── id_token validation ────────────────────────────────────────────────────

    private System.Security.Claims.ClaimsPrincipal? ValidateIdToken(
        string idToken, string expectedNonce, ResolvedOidcSettings resolved, OidcProviderConfiguration config)
    {
        var parameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = config.Issuer,
            ValidateAudience = true,
            ValidAudience = resolved.ClientId,
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = config.SigningKeys,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
        };

        var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
        try
        {
            var principal = handler.ValidateToken(idToken, parameters, out _);
            // Replay / interleaving guard: the nonce minted at /start must match the id_token.
            var nonce = principal.FindFirst("nonce")?.Value;
            if (!string.Equals(nonce, expectedNonce, StringComparison.Ordinal))
            {
                logger.LogWarning("OIDC id_token nonce mismatch.");
                return null;
            }
            return principal;
        }
        catch (Exception ex) when (ex is SecurityTokenException or ArgumentException)
        {
            logger.LogWarning(ex, "OIDC id_token validation failed.");
            return null;
        }
    }

    // ── Claim resolution (id_token first, userinfo as a fallback) ───────────────

    private async Task<(string? Subject, string? Email, bool EmailVerified, string? DisplayName)> ResolveIdentityAsync(
        System.Security.Claims.ClaimsPrincipal claims, string? accessToken, OidcProviderConfiguration config,
        CancellationToken cancellationToken)
    {
        var subject = claims.FindFirst("sub")?.Value;
        var email = claims.FindFirst("email")?.Value;
        var emailVerified = ParseBool(claims.FindFirst("email_verified")?.Value);
        var displayName = claims.FindFirst("name")?.Value ?? claims.FindFirst("preferred_username")?.Value;

        // Some providers only expose email / email_verified from the userinfo endpoint. Call it once,
        // and only when we actually need it (email missing) and can (access token + endpoint present).
        if (string.IsNullOrWhiteSpace(email) && !string.IsNullOrEmpty(accessToken) && !string.IsNullOrEmpty(config.UserInfoEndpoint))
        {
            var info = await FetchUserInfoAsync(config.UserInfoEndpoint!, accessToken!, cancellationToken);
            if (info is not null)
            {
                email = info.Value.Email ?? email;
                emailVerified = emailVerified || info.Value.EmailVerified;
                displayName ??= info.Value.DisplayName;
            }
        }

        return (subject, email, emailVerified, displayName);
    }

    private async Task<(string? Email, bool EmailVerified, string? DisplayName)?> FetchUserInfoAsync(
        string userInfoEndpoint, string accessToken, CancellationToken cancellationToken)
    {
        try
        {
            var client = httpClientFactory.CreateClient("oidc");
            using var request = new HttpRequestMessage(HttpMethod.Get, userInfoEndpoint);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode) return null;

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var root = doc.RootElement;
            var email = root.TryGetProperty("email", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
            var verified = root.TryGetProperty("email_verified", out var v)
                && (v.ValueKind == JsonValueKind.True || (v.ValueKind == JsonValueKind.String && ParseBool(v.GetString())));
            var name = root.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
            return (email, verified, name);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            logger.LogWarning(ex, "OIDC userinfo request threw.");
            return null;
        }
    }

    // ── Linking / provisioning ──────────────────────────────────────────────────

    private async Task<OidcLoginResult> LinkOrProvisionAsync(
        string subject, string email, string? displayName, bool allowRegistration, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow().UtcDateTime;

        // 1) Stable link by subject — survives a provider-side email change.
        var bySubject = await db.Users.FirstOrDefaultAsync(u => u.OidcSubject == subject, cancellationToken);
        if (bySubject is not null)
        {
            bySubject.LastLoginUtc = now;
            await db.SaveChangesAsync(cancellationToken);
            return OidcLoginResult.Ok(bySubject);
        }

        // 2) First OIDC login — match an existing account by verified email.
        var normalized = IUserService.Normalize(email);
        var byEmail = await db.Users.FirstOrDefaultAsync(u => u.NormalizedEmail == normalized, cancellationToken);
        if (byEmail is not null)
        {
            // The email already belongs to an account bound to a different subject — refuse rather
            // than silently re-bind (a hijack vector). The owner must resolve it deliberately.
            if (!string.IsNullOrEmpty(byEmail.OidcSubject) && byEmail.OidcSubject != subject)
                return OidcLoginResult.Fail(OidcFailureReason.AccountConflict,
                    "This email is already linked to a different OIDC identity.");

            byEmail.OidcSubject = subject;
            byEmail.LastLoginUtc = now;
            await db.SaveChangesAsync(cancellationToken);
            return OidcLoginResult.Ok(byEmail);
        }

        // 3) Unknown account — provision only when the operator opted in.
        if (!allowRegistration)
            return OidcLoginResult.Fail(OidcFailureReason.RegistrationDisabled,
                "No Stashboard account exists for this identity, and OIDC registration is disabled.");

        var user = new UserEntity
        {
            Email = email.Trim(),
            NormalizedEmail = normalized,
            // Passwordless: a sentinel hash no password can satisfy (see OidcConstants).
            PasswordHash = OidcConstants.NoPasswordSentinel,
            OidcSubject = subject,
            // The provider already verified the address (checked above), so skip the email-confirm gate.
            EmailConfirmed = true,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim(),
            LastLoginUtc = now,
            CreatedUtc = now,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync(cancellationToken);
        return OidcLoginResult.Ok(user);
    }

    // ── helpers ─────────────────────────────────────────────────────────────────

    private static bool ParseBool(string? value) => bool.TryParse(value, out var b) && b;

    private static string RandomToken()
    {
        // 256 bits, URL-safe — used for state, nonce and the PKCE verifier (43-char minimum satisfied).
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Base64Url(bytes);
    }

    private static string Base64UrlSha256(string value) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(value)));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Truncate(string value) => value.Length <= 512 ? value : value[..512];
}
