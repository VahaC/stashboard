using Microsoft.EntityFrameworkCore;
using Stashboard.Api.Data;
using Stashboard.Api.Services;
using Stashboard.Core.Abstractions;
using Stashboard.Core.Entities;
using Stashboard.Core.Enums;

namespace Stashboard.Tests.Notifications;

/// <summary>
/// V10.0 — the Apprise integration config (URLs encrypted) and the per-target Apprise
/// toggles survive a <see cref="BackupService"/> export/import round-trip across
/// instances with different encryption keys, per Definition-of-Done §10.3.
/// </summary>
public class AppriseBackupRoundTripTests
{
    private sealed class FakeEncryption : IEncryptionService
    {
        public string Encrypt(string plaintext) => "enc:" + plaintext;
        public string Decrypt(string ciphertext) => ciphertext.StartsWith("enc:")
            ? ciphertext[4..]
            : throw new FormatException("not encrypted");
    }

    private static ApplicationDbContext NewContext(string path) =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False").Options);

    [Fact]
    public async Task ExportThenImport_PreservesAppriseConfigAndPerTargetToggles()
    {
        var sourceDb = Path.Combine(Path.GetTempPath(), $"apprise-src-{Guid.NewGuid():N}.db");
        var targetDb = Path.Combine(Path.GetTempPath(), $"apprise-dst-{Guid.NewGuid():N}.db");
        var enc = new FakeEncryption();
        try
        {
            Guid userA, userB;
            byte[] export;

            await using (var ctx = NewContext(sourceDb))
            {
                await ctx.Database.EnsureCreatedAsync();
                var alice = new UserEntity { Email = "a@x.com", NormalizedEmail = "A@X.COM", PasswordHash = "h" };
                ctx.Users.Add(alice);

                ctx.AppriseSettings.Add(new AppriseSettingsEntity
                {
                    Id = AppriseSettingsEntity.SingletonId,
                    Enabled = true,
                    BaseUrl = "http://apprise:8000",
                    UrlsEncrypted = enc.Encrypt("discord://id/token\nntfy://ntfy.sh/topic"),
                });

                var dockerConn = new DockerConnectionEntity
                {
                    UserId = alice.Id, Name = "home", HostType = DockerHostType.Ssh,
                    SshHost = "1.2.3.4", SshUsername = "root",
                };
                ctx.DockerConnections.Add(dockerConn);
                ctx.DockerWatches.Add(new DockerWatchEntity
                {
                    DockerConnectionId = dockerConn.Id, UserId = alice.Id, Label = "app",
                    ImageReference = "ghcr.io/o/r:latest", RegistryHost = "ghcr.io", Repository = "o/r",
                    Tag = "latest", ContainerName = "sonarr", AppriseNotificationsEnabled = true,
                });
                ctx.ProxmoxConnections.Add(new ProxmoxConnectionEntity
                {
                    UserId = alice.Id, Name = "pve-home", ApiBaseUrl = "https://pve.lan:8006",
                    NodeName = "pve", ServerType = ProxmoxServerType.Pve, ApiTokenId = "root@pam!stash",
                    AppriseNotificationsEnabled = true,
                });

                await ctx.SaveChangesAsync();
                userA = alice.Id;
                export = await new BackupService(ctx, enc).ExportAsync(userA);
            }

            await using (var ctx = NewContext(targetDb))
            {
                await ctx.Database.EnsureCreatedAsync();
                var bob = new UserEntity { Email = "b@x.com", NormalizedEmail = "B@X.COM", PasswordHash = "h" };
                ctx.Users.Add(bob);
                await ctx.SaveChangesAsync();
                userB = bob.Id;
            }

            await using (var ctx = NewContext(targetDb))
            {
                using var stream = new MemoryStream(export);
                await new BackupService(ctx, enc).ImportAsync(userB, stream);
            }

            await using (var ctx = NewContext(targetDb))
            {
                var apprise = await ctx.AppriseSettings.AsNoTracking().SingleAsync();
                Assert.True(apprise.Enabled);
                Assert.Equal("http://apprise:8000", apprise.BaseUrl);
                // URLs re-encrypted at rest and decrypt back to the original list.
                Assert.StartsWith("enc:", apprise.UrlsEncrypted);
                Assert.Equal("discord://id/token\nntfy://ntfy.sh/topic", enc.Decrypt(apprise.UrlsEncrypted!));

                var watch = await ctx.DockerWatches.AsNoTracking().SingleAsync();
                Assert.True(watch.AppriseNotificationsEnabled);

                var pve = await ctx.ProxmoxConnections.AsNoTracking().SingleAsync();
                Assert.True(pve.AppriseNotificationsEnabled);
            }
        }
        finally
        {
            foreach (var p in new[] { sourceDb, targetDb })
                try { File.Delete(p); } catch { /* best effort */ }
        }
    }
}



