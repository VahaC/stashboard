using Microsoft.EntityFrameworkCore;
using Stashboard.Api.Data;
using Stashboard.Api.Services;
using Stashboard.Core.Abstractions;
using Stashboard.Core.Entities;

namespace Stashboard.Tests.Services.Mqtt;

/// <summary>
/// V9.0 — the MQTT integration config (password encrypted) survives a
/// <see cref="BackupService"/> export/import round-trip across instances with
/// different encryption keys, per Definition-of-Done §10.3.
/// </summary>
public class MqttBackupRoundTripTests
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
    public async Task ExportThenImport_PreservesMqttConfig_PasswordReEncrypted()
    {
        var sourceDb = Path.Combine(Path.GetTempPath(), $"mqtt-src-{Guid.NewGuid():N}.db");
        var targetDb = Path.Combine(Path.GetTempPath(), $"mqtt-dst-{Guid.NewGuid():N}.db");
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
                ctx.MqttSettings.Add(new MqttSettingsEntity
                {
                    Id = MqttSettingsEntity.SingletonId,
                    Enabled = true, Host = "mqtt.lan", Port = 8883, UseTls = true, AllowUntrustedTls = true,
                    Username = "ha", PasswordEncrypted = enc.Encrypt("brokerpw"),
                    ClientId = "stashboard", DiscoveryPrefix = "homeassistant", EntityPrefix = "homelab",
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
                var mqtt = await ctx.MqttSettings.AsNoTracking().SingleAsync();
                Assert.True(mqtt.Enabled);
                Assert.Equal("mqtt.lan", mqtt.Host);
                Assert.Equal(8883, mqtt.Port);
                Assert.True(mqtt.UseTls);
                Assert.True(mqtt.AllowUntrustedTls);
                Assert.Equal("ha", mqtt.Username);
                Assert.Equal("homelab", mqtt.EntityPrefix);
                // Password is re-encrypted at rest and decrypts back to the original.
                Assert.StartsWith("enc:", mqtt.PasswordEncrypted);
                Assert.Equal("brokerpw", enc.Decrypt(mqtt.PasswordEncrypted!));
            }
        }
        finally
        {
            foreach (var p in new[] { sourceDb, targetDb })
                try { File.Delete(p); } catch { /* best effort */ }
        }
    }
}



