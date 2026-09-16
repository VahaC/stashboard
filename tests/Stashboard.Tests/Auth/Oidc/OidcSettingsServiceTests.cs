using Stashboard.Api.Auth.Oidc;
using Stashboard.Api.Contracts;
using Stashboard.Api.Data;
using Stashboard.Core.Abstractions;
using Stashboard.Tests.Infrastructure;

namespace Stashboard.Tests.Auth.Oidc;

/// <summary>
/// V10.5 — the DB-backed OIDC settings: the client secret is encrypted at rest and never returned
/// (presence flag only), the row is created disabled on first access (no env seed), the tri-state
/// secret keeps / sets / clears, and the feature flag reflects whether the provider is usable.
/// </summary>
public class OidcSettingsServiceTests : DatabaseTestBase
{
    private sealed class FakeEncryption : IEncryptionService
    {
        public string Encrypt(string plaintext) => "enc:" + plaintext;
        public string Decrypt(string ciphertext) => ciphertext["enc:".Length..];
    }

    private OidcSettingsService Build() => new(_dbContext, new FakeEncryption(), TimeProvider.System);

    private static UpdateOidcSettingsRequest Req(
        bool enabled = true, string issuer = "https://idp.test", string clientId = "stashboard",
        SecretValueUpsert? secret = null, string redirectBaseUrl = "https://app.test") =>
        new(enabled, "Authentik", issuer, clientId, secret, "openid profile email", redirectBaseUrl, false);

    [Fact]
    public async Task FirstAccess_CreatesDisabledRow_WithNoSeed()
    {
        var resolved = await Build().GetResolvedAsync();

        Assert.False(resolved.Enabled);
        Assert.False(resolved.IsUsable);
        Assert.Equal("openid profile email", resolved.Scopes);
        Assert.Null(resolved.ClientSecret);
    }

    [Fact]
    public async Task Secret_IsEncryptedAtRest_AndApiReturnsPresenceFlagOnly()
    {
        var svc = Build();
        await svc.UpdateAsync(Req(secret: new SecretValueUpsert(SecretValueAction.Set, "s3cr3t")));

        // API view: presence flag, never the secret.
        var view = await svc.GetAsync();
        Assert.True(view.HasClientSecret);
        Assert.Equal("https://app.test/oidc/callback", view.RedirectUri);

        // Stored ciphertext is encrypted, never the bare secret.
        var row = await _dbContext.OidcSettings.FindAsync(OidcSettingsEntity.SingletonId);
        Assert.NotNull(row!.ClientSecretEncrypted);
        Assert.StartsWith("enc:", row.ClientSecretEncrypted);

        // Resolved view (login flow) decrypts it.
        Assert.Equal("s3cr3t", (await svc.GetResolvedAsync()).ClientSecret);
    }

    [Fact]
    public async Task Secret_Keep_PreservesStored_Clear_DropsIt()
    {
        var svc = Build();
        await svc.UpdateAsync(Req(secret: new SecretValueUpsert(SecretValueAction.Set, "s3cr3t")));

        // Keep (null) leaves it untouched.
        await svc.UpdateAsync(Req(secret: null));
        Assert.Equal("s3cr3t", (await svc.GetResolvedAsync()).ClientSecret);

        // Clear drops it — now a public / PKCE-only client.
        await svc.UpdateAsync(Req(secret: new SecretValueUpsert(SecretValueAction.Clear, null)));
        Assert.Null((await svc.GetResolvedAsync()).ClientSecret);
        Assert.False((await svc.GetAsync()).HasClientSecret);
    }

    [Fact]
    public async Task IsEnabled_OnlyWhenEnabledAndConfigured()
    {
        var svc = Build();
        Assert.False(await svc.IsEnabledAsync());

        // Enabled but missing issuer / client id is not usable.
        await svc.UpdateAsync(Req(enabled: true, issuer: "", clientId: ""));
        Assert.False(await svc.IsEnabledAsync());

        // Configured but disabled is not usable.
        await svc.UpdateAsync(Req(enabled: false));
        Assert.False(await svc.IsEnabledAsync());

        // Enabled + configured + redirect base → usable.
        await svc.UpdateAsync(Req(enabled: true));
        Assert.True(await svc.IsEnabledAsync());
    }
}
