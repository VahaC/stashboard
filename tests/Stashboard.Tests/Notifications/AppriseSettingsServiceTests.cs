using Microsoft.Extensions.Options;
using Stashboard.Api.Contracts;
using Stashboard.Api.Data;
using Stashboard.Api.Notifications;
using Stashboard.Core.Abstractions;
using Stashboard.Tests.Infrastructure;

namespace Stashboard.Tests.Notifications;

/// <summary>
/// V10.0 — the DB-backed Apprise settings: the URLs are encrypted at rest and the API
/// view returns presence flag + non-secret schemes only, the row seeds from the bound
/// options on first access, and the tri-state secret keeps / sets / clears the URLs.
/// </summary>
public class AppriseSettingsServiceTests : DatabaseTestBase
{
    private sealed class FakeEncryption : IEncryptionService
    {
        public string Encrypt(string plaintext) => "enc:" + plaintext;
        public string Decrypt(string ciphertext) => ciphertext["enc:".Length..];
    }

    private readonly FakeEncryption _enc = new();

    private AppriseSettingsService Build(AppriseOptions? seed = null) =>
        new(_dbContext, _enc, Options.Create(seed ?? new AppriseOptions()), TimeProvider.System);

    [Fact]
    public async Task FirstAccess_SeedsFromOptions()
    {
        var svc = Build(new AppriseOptions
        {
            Enabled = true,
            BaseUrl = "http://apprise:8000",
            Urls = "discord://id/token\nntfy://ntfy.sh/topic",
        });

        var resolved = await svc.GetResolvedAsync();

        Assert.True(resolved.Enabled);
        Assert.Equal("http://apprise:8000", resolved.BaseUrl);
        Assert.Equal(["discord://id/token", "ntfy://ntfy.sh/topic"], resolved.Urls);
        Assert.True(resolved.IsConfigured);
    }

    [Fact]
    public async Task Urls_AreEncryptedAtRest_AndApiReturnsPresenceFlagAndSchemesOnly()
    {
        var svc = Build();
        await svc.UpdateAsync(new UpdateAppriseSettingsRequest(
            true, "http://apprise:8000",
            new SecretValueUpsert(SecretValueAction.Set, "discord://id/token\nntfy://ntfy.sh/topic")));

        // API view: presence flag + count + non-secret schemes, never the URLs.
        var view = await svc.GetAsync();
        Assert.True(view.HasUrls);
        Assert.Equal(2, view.UrlCount);
        Assert.Equal(["discord", "ntfy"], view.Targets);

        // Stored ciphertext is encrypted, never the bare URLs.
        var row = await _dbContext.AppriseSettings.FindAsync(AppriseSettingsEntity.SingletonId);
        Assert.NotNull(row!.UrlsEncrypted);
        Assert.StartsWith("enc:", row.UrlsEncrypted);
        Assert.DoesNotContain("discord://id/token", view.Targets);
    }

    [Fact]
    public async Task Urls_Keep_PreservesStored_Clear_DropsThem()
    {
        var svc = Build();
        await svc.UpdateAsync(new UpdateAppriseSettingsRequest(
            true, "http://apprise:8000", new SecretValueUpsert(SecretValueAction.Set, "ntfy://ntfy.sh/a")));

        // Keep (null) leaves them untouched.
        await svc.UpdateAsync(new UpdateAppriseSettingsRequest(true, "http://apprise:8000", null));
        Assert.Equal(["ntfy://ntfy.sh/a"], (await svc.GetResolvedAsync()).Urls);

        // Clear drops them — the channel is no longer configured.
        await svc.UpdateAsync(new UpdateAppriseSettingsRequest(
            true, "http://apprise:8000", new SecretValueUpsert(SecretValueAction.Clear, null)));
        var resolved = await svc.GetResolvedAsync();
        Assert.Empty(resolved.Urls);
        Assert.False(resolved.IsConfigured);
    }

    [Fact]
    public async Task BlankLines_AreStrippedFromTheStoredList()
    {
        var svc = Build();
        await svc.UpdateAsync(new UpdateAppriseSettingsRequest(
            true, "http://apprise:8000",
            new SecretValueUpsert(SecretValueAction.Set, "  ntfy://ntfy.sh/a  \n\n   \ngotify://g.lan/t\n")));

        Assert.Equal(["ntfy://ntfy.sh/a", "gotify://g.lan/t"], (await svc.GetResolvedAsync()).Urls);
    }

    [Fact]
    public async Task NotEnabled_IsNotConfigured_EvenWithUrls()
    {
        var svc = Build();
        await svc.UpdateAsync(new UpdateAppriseSettingsRequest(
            false, "http://apprise:8000", new SecretValueUpsert(SecretValueAction.Set, "ntfy://ntfy.sh/a")));

        Assert.False((await svc.GetResolvedAsync()).IsConfigured);
    }
}
