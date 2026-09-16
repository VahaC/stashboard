using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Tokens;
using Moq;
using Moq.Protected;
using Stashboard.Api.Auth;
using Stashboard.Api.Auth.Oidc;
using Stashboard.Api.Contracts;
using Stashboard.Api.Data;
using Stashboard.Core.Abstractions;
using Stashboard.Tests.Infrastructure;

namespace Stashboard.Tests.Auth.Oidc;

/// <summary>
/// V10.5 — the Authorization-Code + PKCE login: a valid exchange yields a linked/provisioned user,
/// linking is by verified email then by stable subject, provisioning is gated by the toggle, and
/// the state / nonce guards reject replay and tampering. The provider's HTTP surface (token +
/// userinfo) is mocked; the id_token is signed with a test RSA key the stub discovery client serves.
/// </summary>
public class OidcServiceTests : DatabaseTestBase
{
    private const string Issuer = "https://idp.test";
    private const string ClientId = "stashboard";
    private const string TokenEndpoint = "https://idp.test/token";
    private const string UserInfoEndpoint = "https://idp.test/userinfo";

    private static readonly RSA Rsa = RSA.Create(2048);
    private static readonly RsaSecurityKey SigningKey = new(Rsa) { KeyId = "test-key" };

    private sealed class FakeEncryption : IEncryptionService
    {
        public string Encrypt(string plaintext) => "enc:" + plaintext;
        public string Decrypt(string ciphertext) => ciphertext["enc:".Length..];
    }

    /// <summary>Stub discovery returning fixed endpoints + the test signing key.</summary>
    private sealed class StubDiscovery(bool withUserInfo = true) : IOidcDiscoveryClient
    {
        public Task<OidcProviderConfiguration> GetAsync(string issuer, CancellationToken ct = default) =>
            Task.FromResult(new OidcProviderConfiguration(
                "https://idp.test/authorize", TokenEndpoint, withUserInfo ? UserInfoEndpoint : null,
                Issuer, [SigningKey]));
    }

    private readonly OidcStateStore _stateStore = new(TimeProvider.System);

    private async Task<OidcSettingsService> SeedSettingsAsync(bool allowRegistration = false, string? secret = null)
    {
        var svc = new OidcSettingsService(_dbContext, new FakeEncryption(), TimeProvider.System);
        await svc.UpdateAsync(new UpdateOidcSettingsRequest(
            true, "Authentik", Issuer, ClientId,
            secret is null ? null : new SecretValueUpsert(SecretValueAction.Set, secret),
            "openid profile email", "https://app.test", allowRegistration));
        return svc;
    }

    private OidcService BuildService(OidcSettingsService settings, HttpClient httpClient, bool withUserInfo = true)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient("oidc")).Returns(httpClient);
        return new OidcService(
            settings, new StubDiscovery(withUserInfo), _stateStore, factory.Object,
            _dbContext, TimeProvider.System, NullLogger<OidcService>.Instance);
    }

    private static HttpClient HttpReturning(Func<HttpRequestMessage, HttpResponseMessage> factory)
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync((HttpRequestMessage req, CancellationToken _) => factory(req));
        return new HttpClient(handler.Object);
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static string BuildIdToken(string sub, string? email, bool emailVerified, string nonce, string? name = null)
    {
        var claims = new List<Claim>
        {
            new("sub", sub),
            new("nonce", nonce),
            new("email_verified", emailVerified ? "true" : "false"),
        };
        if (email is not null) claims.Add(new Claim("email", email));
        if (name is not null) claims.Add(new Claim("name", name));

        var creds = new SigningCredentials(SigningKey, SecurityAlgorithms.RsaSha256);
        var now = DateTime.UtcNow;
        var token = new JwtSecurityToken(Issuer, ClientId, claims, now.AddMinutes(-1), now.AddMinutes(5), creds);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    /// <summary>Token-endpoint response carrying the given id_token (+ a dummy access token).</summary>
    private HttpClient TokenReturning(string idToken, string? userInfoBody = null) =>
        HttpReturning(req =>
        {
            if (req.RequestUri!.AbsoluteUri == UserInfoEndpoint)
                return userInfoBody is null ? new HttpResponseMessage(HttpStatusCode.NotFound) : Json(userInfoBody);
            return Json($$"""{"id_token":"{{idToken}}","access_token":"at-123","token_type":"Bearer"}""");
        });

    private void SeedState(string state = "state-1", string nonce = "nonce-1") =>
        _stateStore.Save(state, new OidcAuthState("verifier-xyz", nonce), TimeSpan.FromMinutes(5));

    // ── Linking / provisioning ──────────────────────────────────────────────────

    [Fact]
    public async Task Complete_ValidExchange_LinksExistingUserByVerifiedEmail()
    {
        _dbContext.Users.Add(new UserEntity { Email = "a@x.com", NormalizedEmail = "A@X.COM", PasswordHash = "h" });
        await _dbContext.SaveChangesAsync();

        var settings = await SeedSettingsAsync();
        SeedState();
        var svc = BuildService(settings, TokenReturning(BuildIdToken("sub-1", "a@x.com", true, "nonce-1")));

        var result = await svc.CompleteAsync("code", "state-1");

        Assert.True(result.Succeeded);
        Assert.Equal("a@x.com", result.User!.Email);
        var stored = await _dbContext.Users.AsNoTracking().FirstAsync(u => u.NormalizedEmail == "A@X.COM");
        Assert.Equal("sub-1", stored.OidcSubject);
    }

    [Fact]
    public async Task Complete_EmailNotVerified_Fails()
    {
        _dbContext.Users.Add(new UserEntity { Email = "a@x.com", NormalizedEmail = "A@X.COM", PasswordHash = "h" });
        await _dbContext.SaveChangesAsync();

        var settings = await SeedSettingsAsync();
        SeedState();
        var svc = BuildService(settings, TokenReturning(BuildIdToken("sub-1", "a@x.com", false, "nonce-1")), withUserInfo: false);

        var result = await svc.CompleteAsync("code", "state-1");

        Assert.False(result.Succeeded);
        Assert.Equal(OidcFailureReason.EmailNotVerified, result.Failure);
    }

    [Fact]
    public async Task Complete_UnknownEmail_RegistrationDisabled_Fails()
    {
        var settings = await SeedSettingsAsync(allowRegistration: false);
        SeedState();
        var svc = BuildService(settings, TokenReturning(BuildIdToken("sub-1", "new@x.com", true, "nonce-1")));

        var result = await svc.CompleteAsync("code", "state-1");

        Assert.False(result.Succeeded);
        Assert.Equal(OidcFailureReason.RegistrationDisabled, result.Failure);
        Assert.Empty(_dbContext.Users);
    }

    [Fact]
    public async Task Complete_UnknownEmail_RegistrationEnabled_ProvisionsPasswordlessUser()
    {
        var settings = await SeedSettingsAsync(allowRegistration: true);
        SeedState();
        var svc = BuildService(settings, TokenReturning(BuildIdToken("sub-1", "new@x.com", true, "nonce-1", name: "New Person")));

        var result = await svc.CompleteAsync("code", "state-1");

        Assert.True(result.Succeeded);
        var user = await _dbContext.Users.AsNoTracking().FirstAsync();
        Assert.Equal("new@x.com", user.Email);
        Assert.Equal("sub-1", user.OidcSubject);
        Assert.True(user.EmailConfirmed);
        Assert.Equal("New Person", user.DisplayName);
        // Passwordless sentinel — no password can satisfy it.
        Assert.Equal(OidcConstants.NoPasswordSentinel, user.PasswordHash);
        Assert.False(new Pbkdf2PasswordHasher().Verify("anything", user.PasswordHash));
    }

    [Fact]
    public async Task Complete_LinksBySubject_EvenWhenProviderEmailChanged()
    {
        _dbContext.Users.Add(new UserEntity
        {
            Email = "old@x.com", NormalizedEmail = "OLD@X.COM", PasswordHash = "h", OidcSubject = "sub-1",
        });
        await _dbContext.SaveChangesAsync();

        var settings = await SeedSettingsAsync();
        SeedState();
        // Same subject, different email — should resolve to the existing user by subject.
        var svc = BuildService(settings, TokenReturning(BuildIdToken("sub-1", "changed@x.com", true, "nonce-1")));

        var result = await svc.CompleteAsync("code", "state-1");

        Assert.True(result.Succeeded);
        Assert.Equal("old@x.com", result.User!.Email); // email not overwritten
        Assert.Single(_dbContext.Users.AsNoTracking());
    }

    [Fact]
    public async Task Complete_SubjectEmailConflict_Fails()
    {
        // The email belongs to an account already bound to a *different* subject.
        _dbContext.Users.Add(new UserEntity
        {
            Email = "a@x.com", NormalizedEmail = "A@X.COM", PasswordHash = "h", OidcSubject = "other-sub",
        });
        await _dbContext.SaveChangesAsync();

        var settings = await SeedSettingsAsync();
        SeedState();
        var svc = BuildService(settings, TokenReturning(BuildIdToken("sub-1", "a@x.com", true, "nonce-1")));

        var result = await svc.CompleteAsync("code", "state-1");

        Assert.False(result.Succeeded);
        Assert.Equal(OidcFailureReason.AccountConflict, result.Failure);
    }

    [Fact]
    public async Task Complete_EmailFromUserInfo_WhenAbsentInIdToken()
    {
        _dbContext.Users.Add(new UserEntity { Email = "a@x.com", NormalizedEmail = "A@X.COM", PasswordHash = "h" });
        await _dbContext.SaveChangesAsync();

        var settings = await SeedSettingsAsync();
        SeedState();
        // id_token has no email; userinfo supplies the verified address.
        var idToken = BuildIdToken("sub-1", email: null, emailVerified: false, nonce: "nonce-1");
        var svc = BuildService(settings, TokenReturning(idToken, userInfoBody: """{"email":"a@x.com","email_verified":true}"""));

        var result = await svc.CompleteAsync("code", "state-1");

        Assert.True(result.Succeeded);
        Assert.Equal("a@x.com", result.User!.Email);
    }

    // ── State / token / nonce guards ─────────────────────────────────────────────

    [Fact]
    public async Task Complete_UnknownState_Fails()
    {
        var settings = await SeedSettingsAsync();
        var svc = BuildService(settings, TokenReturning(BuildIdToken("sub-1", "a@x.com", true, "nonce-1")));

        var result = await svc.CompleteAsync("code", "never-seen");

        Assert.False(result.Succeeded);
        Assert.Equal(OidcFailureReason.InvalidState, result.Failure);
    }

    [Fact]
    public async Task Complete_ReplayedState_Fails_OnSecondUse()
    {
        _dbContext.Users.Add(new UserEntity { Email = "a@x.com", NormalizedEmail = "A@X.COM", PasswordHash = "h" });
        await _dbContext.SaveChangesAsync();

        var settings = await SeedSettingsAsync();
        SeedState();
        var svc = BuildService(settings, TokenReturning(BuildIdToken("sub-1", "a@x.com", true, "nonce-1")));

        Assert.True((await svc.CompleteAsync("code", "state-1")).Succeeded);
        var second = await svc.CompleteAsync("code", "state-1");

        Assert.False(second.Succeeded);
        Assert.Equal(OidcFailureReason.InvalidState, second.Failure);
    }

    [Fact]
    public async Task Complete_NonceMismatch_Fails()
    {
        var settings = await SeedSettingsAsync();
        SeedState(nonce: "stored-nonce");
        // id_token carries a different nonce than the one minted at /start.
        var svc = BuildService(settings, TokenReturning(BuildIdToken("sub-1", "a@x.com", true, "WRONG-nonce")));

        var result = await svc.CompleteAsync("code", "state-1");

        Assert.False(result.Succeeded);
        Assert.Equal(OidcFailureReason.InvalidIdToken, result.Failure);
    }

    [Fact]
    public async Task Complete_TokenExchangeHttpError_Fails()
    {
        var settings = await SeedSettingsAsync();
        SeedState();
        var http = HttpReturning(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("""{"error":"invalid_grant"}""", Encoding.UTF8, "application/json"),
        });
        var svc = BuildService(settings, http);

        var result = await svc.CompleteAsync("code", "state-1");

        Assert.False(result.Succeeded);
        Assert.Equal(OidcFailureReason.TokenExchangeFailed, result.Failure);
    }

    // ── Start ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Start_BuildsAuthorizeUrlWithPkce_AndStoresConsumableState()
    {
        var settings = await SeedSettingsAsync();
        var svc = BuildService(settings, TokenReturning("unused"));

        var url = await svc.StartAsync();

        Assert.NotNull(url);
        Assert.Contains("response_type=code", url);
        Assert.Contains("code_challenge=", url);
        Assert.Contains("code_challenge_method=S256", url);
        Assert.Contains($"client_id={ClientId}", url);
        Assert.Contains(Uri.EscapeDataString("https://app.test/oidc/callback"), url);

        // The state minted into the URL must be consumable exactly once.
        var query = new Uri(url).Query.TrimStart('?');
        var pair = query.Split('&').Select(p => p.Split('=', 2)).First(p => p[0] == "state");
        var state = Uri.UnescapeDataString(pair[1]);
        Assert.NotNull(_stateStore.Consume(state));
        Assert.Null(_stateStore.Consume(state));
    }

    [Fact]
    public async Task Start_ReturnsNull_WhenNotConfigured()
    {
        var svc = new OidcSettingsService(_dbContext, new FakeEncryption(), TimeProvider.System);
        // Row left at its disabled default.
        var service = BuildService(svc, TokenReturning("unused"));

        Assert.Null(await service.StartAsync());
    }
}
