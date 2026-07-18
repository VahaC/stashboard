using Microsoft.Extensions.Options;
using Stashboard.Api.Contracts;
using Stashboard.Api.Services.Mqtt;
using Stashboard.Core.Abstractions;
using Stashboard.Tests.Infrastructure;

namespace Stashboard.Tests.Services.Mqtt;

/// <summary>
/// V9.0 — the DB-backed MQTT settings: the broker password is encrypted at rest and
/// the API view returns a presence flag only, the row seeds from the bound options
/// on first access, and the tri-state secret keeps / sets / clears the password.
/// </summary>
public class MqttSettingsServiceTests : DatabaseTestBase
{
    private sealed class FakeEncryption : IEncryptionService
    {
        public string Encrypt(string plaintext) => "enc:" + plaintext;
        public string Decrypt(string ciphertext) => ciphertext["enc:".Length..];
    }

    private readonly FakeEncryption _enc = new();

    private MqttSettingsService Build(MqttOptions? seed = null) =>
        new(_dbContext, _enc, Options.Create(seed ?? new MqttOptions()), TimeProvider.System);

    private static UpdateMqttSettingsRequest Update(bool enabled = true, string host = "mqtt.lan",
        SecretValueUpsert? password = null, string entityPrefix = "stashboard") =>
        new(enabled, host, 1883, false, false, "user", password, "stashboard", "homeassistant", entityPrefix);

    [Fact]
    public async Task FirstAccess_SeedsFromOptions()
    {
        var svc = Build(new MqttOptions { Enabled = true, Host = "seed.lan", Port = 8883, EntityPrefix = "lab" });
        var resolved = await svc.GetResolvedAsync();

        Assert.True(resolved.Enabled);
        Assert.Equal("seed.lan", resolved.Host);
        Assert.Equal(8883, resolved.Port);
        Assert.Equal("lab", resolved.EntityPrefix);
    }

    [Fact]
    public async Task Password_IsEncryptedAtRest_AndApiReturnsPresenceFlagOnly()
    {
        var svc = Build();
        await svc.UpdateAsync(Update(password: new SecretValueUpsert(SecretValueAction.Set, "s3cret")));

        // API view: presence flag only, never the password.
        var view = await svc.GetAsync();
        Assert.True(view.HasPassword);

        // Stored ciphertext is encrypted, decryptable back to plaintext.
        var resolved = await svc.GetResolvedAsync();
        Assert.Equal("s3cret", resolved.Password);

        var row = await _dbContext.MqttSettings.FindAsync(Stashboard.Api.Data.MqttSettingsEntity.SingletonId);
        Assert.NotNull(row!.PasswordEncrypted);
        // Stored as ciphertext (the fake prefixes "enc:"; real AES emits opaque base64),
        // never the bare plaintext the API consumer typed.
        Assert.NotEqual("s3cret", row.PasswordEncrypted);
        Assert.StartsWith("enc:", row.PasswordEncrypted);
    }

    [Fact]
    public async Task Password_Keep_PreservesStored_Clear_DropsIt()
    {
        var svc = Build();
        await svc.UpdateAsync(Update(password: new SecretValueUpsert(SecretValueAction.Set, "first")));

        // Keep (null / Keep action) leaves it untouched.
        await svc.UpdateAsync(Update(password: null));
        Assert.Equal("first", (await svc.GetResolvedAsync()).Password);

        // Clear drops it.
        await svc.UpdateAsync(Update(password: new SecretValueUpsert(SecretValueAction.Clear, null)));
        Assert.False((await svc.GetAsync()).HasPassword);
        Assert.Equal("", (await svc.GetResolvedAsync()).Password);
    }

    [Fact]
    public async Task BlankPrefixes_FallBackToDefaults()
    {
        var svc = Build();
        await svc.UpdateAsync(new UpdateMqttSettingsRequest(true, "h", 1883, false, false, "", null, "", "", ""));

        var resolved = await svc.GetResolvedAsync();
        Assert.Equal("stashboard", resolved.ClientId);
        Assert.Equal("homeassistant", resolved.DiscoveryPrefix);
        Assert.Equal("stashboard", resolved.EntityPrefix);
    }
}
