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
    // encrypt-on-import round-trip without a real AES key. Like the real AES
    // service, it throws FormatException on values that were never encrypted
    // (legacy plaintext rows).
    private sealed class FakeEncryption : IEncryptionService
    {
        public string Encrypt(string plaintext) => "enc:" + plaintext;
        public string Decrypt(string ciphertext) => ciphertext.StartsWith("enc:")
            ? ciphertext[4..]
            : throw new FormatException("The input is not a valid Base-64 string.");
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
                    // V10.3 — 2FA state: enabled flag + encrypted secret + replay step.
                    TwoFactorEnabled = true, TwoFactorSecretEncrypted = enc.Encrypt("JBSWY3DPEHPK3PXP"),
                    TwoFactorLastUsedStep = 12345,
                };
                ctx.Users.Add(alice);

                // V10.3 — two recovery codes (hashes only): one already redeemed, one still fresh.
                ctx.TwoFactorRecoveryCodes.Add(new TwoFactorRecoveryCodeEntity
                {
                    UserId = alice.Id, CodeHash = "HASH-USED",
                    UsedUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                });
                ctx.TwoFactorRecoveryCodes.Add(new TwoFactorRecoveryCodeEntity
                {
                    UserId = alice.Id, CodeHash = "HASH-FRESH",
                });

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
                    ComposePathHostPrefix = "/opt/stacks",
                    ComposePathContainerPrefix = "/compose",
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

                var pve = new ProxmoxConnectionEntity
                {
                    UserId = alice.Id, Name = "pve-home", ApiBaseUrl = "https://pve.lan:8006",
                    NodeName = "pve", ServerType = ProxmoxServerType.Pve, ApiTokenId = "root@pam!stash",
                    ApiTokenSecretEncrypted = enc.Encrypt("TOK-SECRET"),
                    SshHost = "pve.lan", SshUsername = "root",
                    SshPrivateKeyEncrypted = enc.Encrypt("PVE-SSH-KEY"),
                    AllowConsole = true, AllowUpdates = true, Enabled = true,
                    ScheduleType = CheckScheduleType.Weekly, CheckOnDayOfWeek = DayOfWeek.Monday,
                    WebhookToken = "pve-webhook-tok",
                };
                ctx.ProxmoxConnections.Add(pve);
                // V7.9 — the service is also associated with a Proxmox host.
                svc.ProxmoxConnectionId = pve.Id;
                // A guest the user opted OUT of monitoring (intent worth backing up)…
                ctx.ProxmoxGuests.Add(new ProxmoxGuestEntity
                {
                    ProxmoxConnectionId = pve.Id, VmId = 101, GuestType = ProxmoxGuestType.Lxc,
                    Name = "vaultwarden", MonitoringEnabled = false, IsRunning = true,
                });
                // …and a default-monitored guest, which must NOT be exported (scan output).
                ctx.ProxmoxGuests.Add(new ProxmoxGuestEntity
                {
                    ProxmoxConnectionId = pve.Id, VmId = 102, GuestType = ProxmoxGuestType.Lxc,
                    Name = "jellyfin", MonitoringEnabled = true, IsRunning = true,
                });

                // V7.8 — a custom container-icon upload (must round-trip)…
                ctx.ContainerIcons.Add(new ContainerIconEntity
                {
                    UserId = alice.Id, DockerConnectionId = connection.Id, ContainerName = "sonarr",
                    IconSource = ContainerIconSource.Custom, LogoBase64 = "data:image/webp;base64,CUSTOMCT==",
                });
                // …and an Auto row, which must NOT be exported (it re-resolves live).
                ctx.ContainerIcons.Add(new ContainerIconEntity
                {
                    UserId = alice.Id, DockerConnectionId = connection.Id, ContainerName = "radarr",
                    IconSource = ContainerIconSource.Auto,
                });
                // V7.8 — a custom guest-icon upload (must round-trip).
                ctx.ProxmoxGuestIcons.Add(new ProxmoxGuestIconEntity
                {
                    UserId = alice.Id, ProxmoxConnectionId = pve.Id, VmId = 101,
                    IconSource = ContainerIconSource.Custom, LogoBase64 = "data:image/webp;base64,CUSTOMVM==",
                });

                // V7.9 — a service↔guest link and a container↔guest cross-link, both
                // keyed by the guest's (connection, vmid) and remapped on import.
                ctx.WebResourceProxmoxGuestLinks.Add(new WebResourceProxmoxGuestLinkEntity
                {
                    WebResourceId = svc.Id, ProxmoxConnectionId = pve.Id, VmId = 101,
                });
                ctx.ContainerProxmoxLinks.Add(new ContainerProxmoxLinkEntity
                {
                    UserId = alice.Id, DockerConnectionId = connection.Id, ContainerName = "sonarr",
                    ProxmoxConnectionId = pve.Id, VmId = 101,
                });

                // V10.2 — a published public status page selecting the service, with a public
                // display-name override. Pages + selections + publish state must round-trip.
                var statusPage = new StatusPageEntity
                {
                    UserId = alice.Id, Title = "Home status", Description = "Public uptime",
                    Slug = "home-status", IsPublished = true,
                };
                statusPage.Items.Add(new StatusPageItemEntity
                {
                    WebResourceId = svc.Id, DisplayName = "Media Server", SortOrder = 0,
                });
                ctx.StatusPages.Add(statusPage);

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

                // V10.3 — 2FA round-trips: enabled flag, re-encrypted secret, replay step, and the
                // recovery-code set (hashes + used-state) replace whatever the target user had.
                Assert.True(bob.TwoFactorEnabled);
                Assert.Equal("JBSWY3DPEHPK3PXP", enc.Decrypt(bob.TwoFactorSecretEncrypted!));
                Assert.Equal(12345, bob.TwoFactorLastUsedStep);
                var recoveryCodes = await ctx.TwoFactorRecoveryCodes.AsNoTracking()
                    .Where(c => c.UserId == userB).ToListAsync();
                Assert.Equal(2, recoveryCodes.Count);
                Assert.Null(recoveryCodes.Single(c => c.CodeHash == "HASH-FRESH").UsedUtc);
                Assert.NotNull(recoveryCodes.Single(c => c.CodeHash == "HASH-USED").UsedUtc);

                var conn = await ctx.DockerConnections.AsNoTracking().SingleAsync(c => c.UserId == userB);
                Assert.Equal("home", conn.Name);
                Assert.Equal(DockerHostType.Ssh, conn.HostType);
                Assert.Equal("CA-CERT", enc.Decrypt(conn.TlsCaCertEncrypted!));
                Assert.Equal("SSH-KEY", enc.Decrypt(conn.SshPrivateKeyEncrypted!));
                Assert.Equal("/opt/stacks", conn.ComposePathHostPrefix);
                Assert.Equal("/compose", conn.ComposePathContainerPrefix);

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

                var pve = await ctx.ProxmoxConnections.AsNoTracking().SingleAsync(c => c.UserId == userB);
                Assert.Equal("pve-home", pve.Name);
                Assert.Equal("pve", pve.NodeName);
                Assert.Equal(ProxmoxServerType.Pve, pve.ServerType);
                Assert.Equal("root@pam!stash", pve.ApiTokenId);
                Assert.Equal("TOK-SECRET", enc.Decrypt(pve.ApiTokenSecretEncrypted!));
                Assert.Equal("PVE-SSH-KEY", enc.Decrypt(pve.SshPrivateKeyEncrypted!));
                Assert.True(pve.AllowConsole);
                Assert.True(pve.AllowUpdates);
                Assert.Equal(CheckScheduleType.Weekly, pve.ScheduleType);
                Assert.Equal(DayOfWeek.Monday, pve.CheckOnDayOfWeek);
                Assert.Equal("pve-webhook-tok", pve.WebhookToken);

                // V7.9 — the service's Proxmox-host association re-maps to the new host.
                Assert.Equal(pve.Id, svc.ProxmoxConnectionId);

                // Only the monitoring-off guest is restored; the default one is left
                // for the next scan to rediscover.
                var guest = await ctx.ProxmoxGuests.AsNoTracking().SingleAsync(g => g.ProxmoxConnectionId == pve.Id);
                Assert.Equal(101, guest.VmId);
                Assert.Equal(ProxmoxGuestType.Lxc, guest.GuestType);
                Assert.Equal("vaultwarden", guest.Name);
                Assert.False(guest.MonitoringEnabled);

                // V7.8 — only the custom icons round-trip (the Auto row is dropped),
                // re-attached to the new connection / guest by name / vmid.
                var ctIcon = await ctx.ContainerIcons.AsNoTracking().SingleAsync(i => i.UserId == userB);
                Assert.Equal(conn.Id, ctIcon.DockerConnectionId);
                Assert.Equal("sonarr", ctIcon.ContainerName);
                Assert.Equal(ContainerIconSource.Custom, ctIcon.IconSource);
                Assert.Equal("data:image/webp;base64,CUSTOMCT==", ctIcon.LogoBase64);

                var gIcon = await ctx.ProxmoxGuestIcons.AsNoTracking().SingleAsync(i => i.UserId == userB);
                Assert.Equal(pve.Id, gIcon.ProxmoxConnectionId);
                Assert.Equal(101, gIcon.VmId);
                Assert.Equal(ContainerIconSource.Custom, gIcon.IconSource);
                Assert.Equal("data:image/webp;base64,CUSTOMVM==", gIcon.LogoBase64);

                // V7.9 — both cross-link kinds round-trip, re-keyed to the new ids.
                var svcLink = await ctx.WebResourceProxmoxGuestLinks.AsNoTracking().SingleAsync(l => l.WebResourceId == svc.Id);
                Assert.Equal(pve.Id, svcLink.ProxmoxConnectionId);
                Assert.Equal(101, svcLink.VmId);

                var ctLink = await ctx.ContainerProxmoxLinks.AsNoTracking().SingleAsync(l => l.UserId == userB);
                Assert.Equal(conn.Id, ctLink.DockerConnectionId);
                Assert.Equal("sonarr", ctLink.ContainerName);
                Assert.Equal(pve.Id, ctLink.ProxmoxConnectionId);
                Assert.Equal(101, ctLink.VmId);

                // V10.2 — the status page round-trips with its publish state, slug and the
                // service selection (remapped to Bob's imported service), display name intact.
                var statusPage = await ctx.StatusPages.AsNoTracking()
                    .Include(p => p.Items)
                    .SingleAsync(p => p.UserId == userB);
                Assert.Equal("Home status", statusPage.Title);
                Assert.Equal("home-status", statusPage.Slug);
                Assert.True(statusPage.IsPublished);
                var pageItem = Assert.Single(statusPage.Items);
                Assert.Equal(svc.Id, pageItem.WebResourceId);
                Assert.Equal("Media Server", pageItem.DisplayName);
            }
        }
        finally
        {
            foreach (var p in new[] { sourceDb, targetDb })
                foreach (var f in new[] { p, p + "-wal", p + "-shm" })
                    if (File.Exists(f)) File.Delete(f);
        }
    }

    [Fact]
    public async Task Export_LegacyPlaintextSecret_ExportsRawValueAndImportReEncrypts()
    {
        // A Telegram bot token saved before the encrypt-at-rest migration sits
        // as plaintext in the *Encrypted column ("123:ABC" is not Base64).
        // Export must not 500 on it; the raw value is exported and the import
        // path re-encrypts it properly.
        var dbPath = Path.Combine(Path.GetTempPath(), $"backup-legacy-{Guid.NewGuid():N}.db");
        var enc = new FakeEncryption();
        const string legacyToken = "123456789:AAE-legacy-plaintext-token";
        try
        {
            Guid userA, userB;
            byte[] export;
            await using (var ctx = NewContext(dbPath))
            {
                await ctx.Database.EnsureCreatedAsync();
                var alice = new UserEntity
                {
                    Email = "alice@x.com", NormalizedEmail = "ALICE@X.COM", PasswordHash = "h",
                    TelegramBotTokenEncrypted = legacyToken, TelegramChatId = "chat",
                };
                var bob = new UserEntity { Email = "bob@x.com", NormalizedEmail = "BOB@X.COM", PasswordHash = "h" };
                ctx.Users.AddRange(alice, bob);
                await ctx.SaveChangesAsync();
                userA = alice.Id;
                userB = bob.Id;

                export = await new BackupService(ctx, enc).ExportAsync(userA);
            }

            await using (var ctx = NewContext(dbPath))
            {
                using var stream = new MemoryStream(export);
                await new BackupService(ctx, enc).ImportAsync(userB, stream);
            }

            await using (var ctx = NewContext(dbPath))
            {
                var bob = await ctx.Users.AsNoTracking().SingleAsync(u => u.Id == userB);
                Assert.Equal(legacyToken, enc.Decrypt(bob.TelegramBotTokenEncrypted!));
            }
        }
        finally
        {
            foreach (var f in new[] { dbPath, dbPath + "-wal", dbPath + "-shm" })
                if (File.Exists(f)) File.Delete(f);
        }
    }

    [Fact]
    public async Task Import_BackupWithoutProxmoxSection_SucceedsForBackCompat()
    {
        // A pre-V6.15 backup has no ProxmoxConnections property at all. It must
        // still import cleanly (the field is optional / defaults to null).
        var dbPath = Path.Combine(Path.GetTempPath(), $"backup-compat-{Guid.NewGuid():N}.db");
        var enc = new FakeEncryption();
        try
        {
            Guid userId;
            await using (var ctx = NewContext(dbPath))
            {
                await ctx.Database.EnsureCreatedAsync();
                var u = new UserEntity { Email = "u@x.com", NormalizedEmail = "U@X.COM", PasswordHash = "h" };
                ctx.Users.Add(u);
                await ctx.SaveChangesAsync();
                userId = u.Id;
            }

            const string legacyJson = """
            {
              "exportedUtc": "2026-01-01T00:00:00Z",
              "categories": [{ "id": "00000000-0000-0000-0000-000000000001", "name": "Media", "color": "#fff" }],
              "tags": [],
              "dockerConnections": [],
              "services": [],
              "dockerWatches": []
            }
            """;

            await using (var ctx = NewContext(dbPath))
            {
                using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(legacyJson));
                var imported = await new BackupService(ctx, enc).ImportAsync(userId, stream);
                Assert.Equal(0, imported);
            }

            await using (var ctx = NewContext(dbPath))
            {
                Assert.Equal(0, await ctx.ProxmoxConnections.CountAsync());
                Assert.Single(await ctx.Categories.Where(c => c.UserId == userId).ToListAsync());
            }
        }
        finally
        {
            foreach (var f in new[] { dbPath, dbPath + "-wal", dbPath + "-shm" })
                if (File.Exists(f)) File.Delete(f);
        }
    }

    [Fact]
    public async Task Import_Services_MergeByNameAndUrl_ReimportDoesNotDuplicate()
    {
        // V6.15.1 — importing a backup into an instance that already holds the
        // same services (e.g. staging → prod) used to create a duplicate of every
        // service. Now a service matching by name + main URL is reused; a service
        // with the same name but a different URL is still treated as new.
        var dbPath = Path.Combine(Path.GetTempPath(), $"backup-svc-merge-{Guid.NewGuid():N}.db");
        var enc = new FakeEncryption();
        try
        {
            Guid userId;
            byte[] export;
            await using (var ctx = NewContext(dbPath))
            {
                await ctx.Database.EnsureCreatedAsync();
                var u = new UserEntity { Email = "u@x.com", NormalizedEmail = "U@X.COM", PasswordHash = "h" };
                ctx.Users.Add(u);
                var connection = new DockerConnectionEntity
                {
                    UserId = u.Id, Name = "DockerOMV", HostType = DockerHostType.LocalSocket,
                };
                ctx.DockerConnections.Add(connection);
                ctx.WebResources.Add(new WebResourceEntity
                {
                    UserId = u.Id, Name = "OMV", MainUrl = "https://omv.local",
                    DockerConnectionId = connection.Id,
                });
                await ctx.SaveChangesAsync();
                userId = u.Id;
                export = await new BackupService(ctx, enc).ExportAsync(userId);
            }

            // Re-import into the same instance — the service must merge, not duplicate.
            await using (var ctx = NewContext(dbPath))
            {
                using var stream = new MemoryStream(export);
                var imported = await new BackupService(ctx, enc).ImportAsync(userId, stream);
                Assert.Equal(0, imported);
            }

            await using (var ctx = NewContext(dbPath))
            {
                var services = await ctx.WebResources.Where(s => s.UserId == userId).ToListAsync();
                var svc = Assert.Single(services);
                Assert.Equal("OMV", svc.Name);

                // The single connection (merged by name) is deletable once that
                // one service is reassigned — no hidden duplicate holds it.
                Assert.Single(await ctx.DockerConnections.Where(c => c.UserId == userId).ToListAsync());
            }

            // Same name but a different URL is a different service — still imported.
            await using (var ctx = NewContext(dbPath))
            {
                var existing = await ctx.WebResources.SingleAsync(s => s.UserId == userId);
                existing.MainUrl = "https://omv.changed.local";
                await ctx.SaveChangesAsync();
            }
            await using (var ctx = NewContext(dbPath))
            {
                using var stream = new MemoryStream(export);
                var imported = await new BackupService(ctx, enc).ImportAsync(userId, stream);
                Assert.Equal(1, imported);
                Assert.Equal(2, await ctx.WebResources.CountAsync(s => s.UserId == userId));
            }
        }
        finally
        {
            foreach (var f in new[] { dbPath, dbPath + "-wal", dbPath + "-shm" })
                if (File.Exists(f)) File.Delete(f);
        }
    }

    [Fact]
    public async Task Import_ProxmoxConnection_MergesByNameWithoutDuplicating()
    {
        // Importing the same backup twice into the same instance must not create a
        // second copy of a host that already exists (merge by name).
        var dbPath = Path.Combine(Path.GetTempPath(), $"backup-merge-{Guid.NewGuid():N}.db");
        var enc = new FakeEncryption();
        try
        {
            Guid userId;
            byte[] export;
            await using (var ctx = NewContext(dbPath))
            {
                await ctx.Database.EnsureCreatedAsync();
                var u = new UserEntity { Email = "u@x.com", NormalizedEmail = "U@X.COM", PasswordHash = "h" };
                ctx.Users.Add(u);
                ctx.ProxmoxConnections.Add(new ProxmoxConnectionEntity
                {
                    UserId = u.Id, Name = "pve-home", ApiBaseUrl = "https://pve.lan:8006",
                    NodeName = "pve", ApiTokenId = "root@pam!stash",
                    ApiTokenSecretEncrypted = enc.Encrypt("TOK"), WebhookToken = "tok-1",
                });
                await ctx.SaveChangesAsync();
                userId = u.Id;
                export = await new BackupService(ctx, enc).ExportAsync(userId);
            }

            // Re-import into the same instance — should be a no-op for the host.
            await using (var ctx = NewContext(dbPath))
            {
                using var stream = new MemoryStream(export);
                await new BackupService(ctx, enc).ImportAsync(userId, stream);
            }

            await using (var ctx = NewContext(dbPath))
            {
                var hosts = await ctx.ProxmoxConnections.Where(c => c.UserId == userId).ToListAsync();
                Assert.Single(hosts);
                Assert.Equal("pve-home", hosts[0].Name);
                // Webhook token kept on the original; the re-import dropped its colliding copy.
                Assert.Equal("tok-1", hosts[0].WebhookToken);
            }
        }
        finally
        {
            foreach (var f in new[] { dbPath, dbPath + "-wal", dbPath + "-shm" })
                if (File.Exists(f)) File.Delete(f);
        }
    }
}


