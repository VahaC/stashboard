using Microsoft.EntityFrameworkCore;
using Stashboard.Api.Data;
using Stashboard.Api.Services;
using Stashboard.Core.Abstractions;
using Stashboard.Core.Entities;
using Stashboard.Core.Enums;

namespace Stashboard.Tests.Services;

/// <summary>
/// End-to-end round-trip for <see cref="BackupService"/> on SQLite: export one
/// user's full configuration, import it into a different user, and assert that
/// Docker connections, watches, services (with flags + links), credentials and
/// user settings all survive — including encrypted material, which must be
/// decrypted on export and re-encrypted on import.
/// </summary>
public class BackupServiceTests
{
    // Reversible fake so the test can assert the decrypt-on-export /
    // encrypt-on-import round-trip without a real AES key.
    private sealed class FakeEncryption : IEncryptionService
    {
        public string Encrypt(string plaintext) => "enc:" + plaintext;
        public string Decrypt(string ciphertext) => ciphertext.StartsWith("enc:") ? ciphertext[4..] : ciphertext;
    }

    private static ApplicationDbContext NewContext(string path) =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False").Options);

    [Fact]
    public async Task ExportThenImport_RestoresFullConfigurationAcrossUsers()
    {
        // Two separate databases model the realistic case: export from one
        // instance, import into a fresh one (no global webhook-token collision).
        var sourceDb = Path.Combine(Path.GetTempPath(), $"backup-src-{Guid.NewGuid():N}.db");
        var targetDb = Path.Combine(Path.GetTempPath(), $"backup-dst-{Guid.NewGuid():N}.db");
        var enc = new FakeEncryption();
        try
        {
            Guid userA, userB;
            byte[] export;

            await using (var ctx = NewContext(sourceDb))
            {
                await ctx.Database.EnsureCreatedAsync();

                var alice = new UserEntity
                {
                    Email = "alice@x.com", NormalizedEmail = "ALICE@X.COM", PasswordHash = "h",
                    DisplayName = "Alice", Theme = "dark", DashboardSortMode = "category",
                    DashboardGroupByCategory = true, TelegramBotTokenEncrypted = enc.Encrypt("bot123"),
                    TelegramChatId = "chat456", TelegramNotificationsEnabled = true,
                };
                ctx.Users.Add(alice);

                var category = new CategoryEntity { UserId = alice.Id, Name = "Media", Color = "#ffffff" };
                var tag = new TagEntity { UserId = alice.Id, Name = "prod" };
                ctx.Categories.Add(category);
                ctx.Tags.Add(tag);

                var connection = new DockerConnectionEntity
                {
                    UserId = alice.Id, Name = "home", HostType = DockerHostType.Ssh,
                    SshHost = "1.2.3.4", SshUsername = "root",
                    TlsCaCertEncrypted = enc.Encrypt("CA-CERT"),
                    SshPrivateKeyEncrypted = enc.Encrypt("SSH-KEY"),
                };
                ctx.DockerConnections.Add(connection);

                var svc = new WebResourceEntity
                {
                    UserId = alice.Id, Name = "Sonarr", MainUrl = "https://sonarr",
                    MainUrlHealthCheckEnabled = false, OfflineNotificationsEnabled = false,
                    CategoryId = category.Id, DockerConnectionId = connection.Id,
                    LogoBase64 = "data:image/png;base64,STOREDICON==",
                    LogoSource = LogoSource.AutoFavicon,
                };
                svc.Credentials.Add(new CredentialEntity { Key = "api", EncryptedValue = enc.Encrypt("SECRET"), IsSecret = true });
                svc.WebResourceTags.Add(new WebResourceTagEntity { TagId = tag.Id });
                ctx.WebResources.Add(svc);

                ctx.DockerWatches.Add(new DockerWatchEntity
                {
                    DockerConnectionId = connection.Id, WebResourceId = svc.Id, UserId = alice.Id,
                    Label = "app", ImageReference = "ghcr.io/o/r:latest", RegistryHost = "ghcr.io",
                    Repository = "o/r", Tag = "latest", ContainerName = "sonarr",
                    RegistryPasswordEncrypted = enc.Encrypt("REG-PW"),
                    ScheduleType = CheckScheduleType.Daily, CheckAtTime = new TimeOnly(12, 0),
                    WebhookToken = "webhook-tok",
                });

                await ctx.SaveChangesAsync();
                userA = alice.Id;

                export = await new BackupService(ctx, enc).ExportAsync(userA);
            }

            // Fresh target instance with a different user.
            await using (var ctx = NewContext(targetDb))
            {
                await ctx.Database.EnsureCreatedAsync();
                var bob = new UserEntity { Email = "bob@x.com", NormalizedEmail = "BOB@X.COM", PasswordHash = "h" };
                ctx.Users.Add(bob);
                await ctx.SaveChangesAsync();
                userB = bob.Id;
            }

            // Import Alice's backup into Bob.
            await using (var ctx = NewContext(targetDb))
            {
                using var stream = new MemoryStream(export);
                var imported = await new BackupService(ctx, enc).ImportAsync(userB, stream);
                Assert.Equal(1, imported); // one service
            }

            // Verify Bob's restored configuration.
            await using (var ctx = NewContext(targetDb))
            {
                var bob = await ctx.Users.AsNoTracking().SingleAsync(u => u.Id == userB);
                Assert.Equal("dark", bob.Theme);
                Assert.Equal("Alice", bob.DisplayName);
                Assert.True(bob.DashboardGroupByCategory);
                Assert.Equal("bot123", enc.Decrypt(bob.TelegramBotTokenEncrypted!));

                var conn = await ctx.DockerConnections.AsNoTracking().SingleAsync(c => c.UserId == userB);
                Assert.Equal("home", conn.Name);
                Assert.Equal(DockerHostType.Ssh, conn.HostType);
                Assert.Equal("CA-CERT", enc.Decrypt(conn.TlsCaCertEncrypted!));
                Assert.Equal("SSH-KEY", enc.Decrypt(conn.SshPrivateKeyEncrypted!));

                var svc = await ctx.WebResources.AsNoTracking()
                    .Include(s => s.Credentials).Include(s => s.WebResourceTags)
                    .SingleAsync(s => s.UserId == userB);
                Assert.False(svc.MainUrlHealthCheckEnabled);
                Assert.False(svc.OfflineNotificationsEnabled);
                Assert.Equal(conn.Id, svc.DockerConnectionId);
                Assert.Equal("SECRET", enc.Decrypt(svc.Credentials.Single().EncryptedValue));
                Assert.Single(svc.WebResourceTags);
                Assert.Equal("data:image/png;base64,STOREDICON==", svc.LogoBase64);

                var watch = await ctx.DockerWatches.AsNoTracking().SingleAsync(w => w.UserId == userB);
                Assert.Equal("sonarr", watch.ContainerName);
                Assert.Equal(conn.Id, watch.DockerConnectionId);
                Assert.Equal(svc.Id, watch.WebResourceId);
                Assert.Equal("REG-PW", enc.Decrypt(watch.RegistryPasswordEncrypted!));
                Assert.Equal(CheckScheduleType.Daily, watch.ScheduleType);
                Assert.Equal(new TimeOnly(12, 0), watch.CheckAtTime);
                Assert.Equal("webhook-tok", watch.WebhookToken);
            }
        }
        finally
        {
            foreach (var p in new[] { sourceDb, targetDb })
                foreach (var f in new[] { p, p + "-wal", p + "-shm" })
                    if (File.Exists(f)) File.Delete(f);
        }
    }
}
