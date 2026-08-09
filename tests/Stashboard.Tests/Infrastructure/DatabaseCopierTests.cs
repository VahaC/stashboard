using Microsoft.EntityFrameworkCore;
using Stashboard.Api.Data;
using Stashboard.Core.Entities;

namespace Stashboard.Tests.Infrastructure;

/// <summary>
/// Exercises <see cref="DatabaseCopier"/> with SQLite on both ends. The copier
/// is provider-agnostic, so this validates the row-for-row copy logic (key
/// preservation, FK ordering, verbatim ciphertext) that the real PostgreSQL ->
/// SQLite migration relies on, without needing a Postgres instance.
/// </summary>
public class DatabaseCopierTests
{
    private static ApplicationDbContext NewContext(string path) =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False").Options);

    [Fact]
    public async Task CopyAllAsync_PreservesKeysAndCiphertext()
    {
        var sourcePath = Path.Combine(Path.GetTempPath(), $"copier-src-{Guid.NewGuid():N}.db");
        var targetPath = Path.Combine(Path.GetTempPath(), $"copier-dst-{Guid.NewGuid():N}.db");
        try
        {
            Guid userId, connectionId, webId, watchId;

            await using (var src = NewContext(sourcePath))
            {
                await src.Database.EnsureCreatedAsync();

                var user = new UserEntity
                {
                    Email = "a@b.com",
                    NormalizedEmail = "A@B.COM",
                    PasswordHash = "hash",
                    Theme = "dark",
                };
                src.Users.Add(user);

                var connection = new DockerConnectionEntity
                {
                    UserId = user.Id,
                    Name = "home-server",
                    TlsCaCertEncrypted = "enc:ca-cert",
                };
                src.DockerConnections.Add(connection);

                var web = new WebResourceEntity
                {
                    UserId = user.Id,
                    Name = "Sonarr",
                    MainUrl = "https://sonarr.example.com",
                    DockerConnectionId = connection.Id,
                };
                web.Credentials.Add(new CredentialEntity { Key = "api", EncryptedValue = "enc:secret", IsSecret = true });
                src.WebResources.Add(web);

                var watch = new DockerWatchEntity
                {
                    DockerConnectionId = connection.Id,
                    UserId = user.Id,
                    WebResourceId = web.Id,
                    Label = "app",
                    ImageReference = "ghcr.io/owner/repo:latest",
                    RegistryHost = "ghcr.io",
                    Repository = "owner/repo",
                    Tag = "latest",
                    ContainerName = "sonarr",
                    RegistryPasswordEncrypted = "enc:registry-pw",
                };
                src.DockerWatches.Add(watch);

                await src.SaveChangesAsync();
                userId = user.Id; connectionId = connection.Id; webId = web.Id; watchId = watch.Id;
            }

            await using (var target = NewContext(targetPath))
            {
                await target.Database.EnsureCreatedAsync();
                await using var src = NewContext(sourcePath);
                var report = await DatabaseCopier.CopyAllAsync(src, target);
                Assert.Equal(1, report.Single(r => r.Table == nameof(UserEntity)).Rows);
                Assert.Equal(1, report.Single(r => r.Table == nameof(DockerWatchEntity)).Rows);
            }

            await using (var verify = NewContext(targetPath))
            {
                // Primary keys preserved verbatim.
                Assert.True(await verify.Users.AnyAsync(u => u.Id == userId));
                Assert.True(await verify.DockerConnections.AnyAsync(c => c.Id == connectionId));
                Assert.True(await verify.WebResources.AnyAsync(w => w.Id == webId));

                // Foreign keys preserved.
                var watch = await verify.DockerWatches.AsNoTracking().SingleAsync(w => w.Id == watchId);
                Assert.Equal(connectionId, watch.DockerConnectionId);
                Assert.Equal(webId, watch.WebResourceId);

                // Encrypted columns copied as-is (no decrypt/re-encrypt).
                Assert.Equal("enc:registry-pw", watch.RegistryPasswordEncrypted);
                Assert.Equal("enc:ca-cert", (await verify.DockerConnections.AsNoTracking().SingleAsync()).TlsCaCertEncrypted);
                Assert.Equal("enc:secret", (await verify.Credentials.AsNoTracking().SingleAsync()).EncryptedValue);
            }
        }
        finally
        {
            foreach (var p in new[] { sourcePath, targetPath })
                foreach (var f in new[] { p, p + "-wal", p + "-shm" })
                    if (File.Exists(f)) File.Delete(f);
        }
    }

    [Fact]
    public async Task CopyAllAsync_RefusesNonEmptyTarget()
    {
        var sourcePath = Path.Combine(Path.GetTempPath(), $"copier-src-{Guid.NewGuid():N}.db");
        var targetPath = Path.Combine(Path.GetTempPath(), $"copier-dst-{Guid.NewGuid():N}.db");
        try
        {
            await using (var src = NewContext(sourcePath))
                await src.Database.EnsureCreatedAsync();

            await using var target = NewContext(targetPath);
            await target.Database.EnsureCreatedAsync();
            target.Users.Add(new UserEntity { Email = "x@y.com", NormalizedEmail = "X@Y.COM", PasswordHash = "h" });
            await target.SaveChangesAsync();

            await using var src2 = NewContext(sourcePath);
            await Assert.ThrowsAsync<InvalidOperationException>(() => DatabaseCopier.CopyAllAsync(src2, target));
        }
        finally
        {
            foreach (var p in new[] { sourcePath, targetPath })
                foreach (var f in new[] { p, p + "-wal", p + "-shm" })
                    if (File.Exists(f)) File.Delete(f);
        }
    }
}


