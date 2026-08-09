using Microsoft.EntityFrameworkCore;
using Stashboard.Api.Data;
using Stashboard.Core.Entities;
using Stashboard.Core.Enums;
using Stashboard.Tests.Infrastructure;

namespace Stashboard.Tests.Persistence;

/// <summary>
/// Persistence tests for <see cref="DockerWatchEntity"/>. The schema is 1:N
/// with <see cref="WebResourceEntity"/> (a composite service like WordPress
/// can track both the app container and the MariaDB container); uniqueness
/// is enforced per-label within a service. Cascade deletes from both
/// <see cref="UserEntity"/> and <see cref="WebResourceEntity"/> still apply.
/// </summary>
public class DockerWatchEntityPersistenceTests : DatabaseTestBase
{
    [Fact]
    public async Task SaveAndRead_RoundTripsAllFields()
    {
        var user = await SeedUserAsync();
        var resource = await SeedWebResourceAsync(user.Id);

        var connection = new DockerConnectionEntity
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            Name = "home-server",
            HostType = DockerHostType.TcpTls,
            HostUrl = "tcp://docker.local:2376",
            TlsCaCertEncrypted = "encrypted-ca",
            TlsClientCertEncrypted = "encrypted-cert",
            TlsClientKeyEncrypted = "encrypted-key",
        };
        _dbContext.DockerConnections.Add(connection);
        // Assign the service to this connection so the round-trip is
        // representative of the real flow.
        resource.DockerConnectionId = connection.Id;
        _dbContext.WebResources.Update(resource);

        var watch = new DockerWatchEntity
        {
            Id = Guid.NewGuid(),
            DockerConnectionId = connection.Id,
            WebResourceId = resource.Id,
            UserId = user.Id,
            Label = "app",
            Enabled = true,
            ImageReference = "ghcr.io/owner/repo:v1.2.3",
            RegistryHost = "ghcr.io",
            Repository = "owner/repo",
            Tag = "v1.2.3",
            ContainerName = "my-service",
            RegistryUsernameEncrypted = "encrypted-user",
            RegistryPasswordEncrypted = "encrypted-pass",
            GitHubPatEncrypted = "encrypted-pat",
            RegistryAuthType = RegistryAuthType.AwsEcr,
            AwsAccessKeyIdEncrypted = "encrypted-aws-key",
            AwsSecretAccessKeyEncrypted = "encrypted-aws-secret",
            AwsRegion = "eu-central-1",
            UpdateNotificationsEnabled = true,
            TelegramNotificationsEnabled = true,
            ScheduleType = CheckScheduleType.Weekly,
            CheckEveryHours = 24,
            CheckAtTime = new TimeOnly(8, 30),
            CheckOnDayOfWeek = DayOfWeek.Monday,
            UpdateStatus = DockerUpdateStatus.UpdateAvailable,
            CurrentDigest = "sha256:aaa",
            LatestDigest = "sha256:bbb",
            CurrentVersionTag = "v1.2.3",
            LatestVersionTag = "v1.2.4",
            LatestReleaseUrl = "https://github.com/owner/repo/releases/tag/v1.2.4",
            LatestReleaseBody = "## v1.2.4\n- shipped",
            LastCheckedUtc = DateTime.UtcNow,
            LastUpdateDetectedUtc = DateTime.UtcNow,
            LastNotificationSentUtc = DateTime.UtcNow,
            LastNotifiedDigest = "sha256:bbb",
            LastTelegramNotifiedDigest = "sha256:bbb",
            LastError = null,
            WebhookToken = new string('f', 64),
            LastWebhookReceivedUtc = DateTime.UtcNow,
        };
        _dbContext.DockerWatches.Add(watch);
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();

        var persistedConn = await _dbContext.DockerConnections.AsNoTracking()
            .SingleAsync(c => c.Id == connection.Id);
        Assert.Equal("home-server", persistedConn.Name);
        Assert.Equal(DockerHostType.TcpTls, persistedConn.HostType);
        Assert.Equal("tcp://docker.local:2376", persistedConn.HostUrl);

        var persistedResource = await _dbContext.WebResources.AsNoTracking()
            .SingleAsync(r => r.Id == resource.Id);
        Assert.Equal(connection.Id, persistedResource.DockerConnectionId);
        Assert.Equal("encrypted-ca", persistedConn.TlsCaCertEncrypted);
        Assert.Equal("encrypted-cert", persistedConn.TlsClientCertEncrypted);
        Assert.Equal("encrypted-key", persistedConn.TlsClientKeyEncrypted);

        var persisted = await _dbContext.DockerWatches.AsNoTracking()
            .SingleAsync(w => w.Id == watch.Id);

        Assert.Equal(resource.Id, persisted.WebResourceId);
        Assert.Equal(user.Id, persisted.UserId);
        Assert.Equal("ghcr.io/owner/repo:v1.2.3", persisted.ImageReference);
        Assert.Equal("ghcr.io", persisted.RegistryHost);
        Assert.Equal("owner/repo", persisted.Repository);
        Assert.Equal("v1.2.3", persisted.Tag);
        Assert.Equal("my-service", persisted.ContainerName);
        Assert.Equal("app", persisted.Label);
        Assert.Equal("encrypted-user", persisted.RegistryUsernameEncrypted);
        Assert.Equal("encrypted-pass", persisted.RegistryPasswordEncrypted);
        Assert.Equal("encrypted-pat", persisted.GitHubPatEncrypted);
        Assert.Equal(RegistryAuthType.AwsEcr, persisted.RegistryAuthType);
        Assert.Equal("encrypted-aws-key", persisted.AwsAccessKeyIdEncrypted);
        Assert.Equal("encrypted-aws-secret", persisted.AwsSecretAccessKeyEncrypted);
        Assert.Equal("eu-central-1", persisted.AwsRegion);
        Assert.True(persisted.UpdateNotificationsEnabled);
        Assert.True(persisted.TelegramNotificationsEnabled);
        Assert.Equal(CheckScheduleType.Weekly, persisted.ScheduleType);
        Assert.Equal(24, persisted.CheckEveryHours);
        Assert.Equal(new TimeOnly(8, 30), persisted.CheckAtTime);
        Assert.Equal(DayOfWeek.Monday, persisted.CheckOnDayOfWeek);
        Assert.Equal(DockerUpdateStatus.UpdateAvailable, persisted.UpdateStatus);
        Assert.Equal("sha256:aaa", persisted.CurrentDigest);
        Assert.Equal("sha256:bbb", persisted.LatestDigest);
        Assert.Equal("v1.2.3", persisted.CurrentVersionTag);
        Assert.Equal("v1.2.4", persisted.LatestVersionTag);
        Assert.Equal("https://github.com/owner/repo/releases/tag/v1.2.4", persisted.LatestReleaseUrl);
        Assert.Contains("shipped", persisted.LatestReleaseBody);
        Assert.NotNull(persisted.LastCheckedUtc);
        Assert.NotNull(persisted.LastUpdateDetectedUtc);
        Assert.NotNull(persisted.LastNotificationSentUtc);
        Assert.Equal("sha256:bbb", persisted.LastNotifiedDigest);
        Assert.Equal("sha256:bbb", persisted.LastTelegramNotifiedDigest);
        Assert.Null(persisted.LastError);
        Assert.Equal(new string('f', 64), persisted.WebhookToken);
        Assert.NotNull(persisted.LastWebhookReceivedUtc);
    }

    [Fact]
    public async Task UniqueIndex_OnWebhookToken_RejectsDuplicateTokenAcrossWatches()
    {
        var user = await SeedUserAsync();
        var conn = await SeedConnectionAsync(user.Id, "c1");
        var token = new string('a', 64);

        // Distinct container names so the (connection, container) unique index
        // doesn't fire first — we're isolating the webhook-token index here.
        var watchA = BuildMinimalWatch(conn.Id, webResourceId: null, user.Id, containerName: "a");
        watchA.WebhookToken = token;
        _dbContext.DockerWatches.Add(watchA);
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();

        var watchB = BuildMinimalWatch(conn.Id, webResourceId: null, user.Id, containerName: "b");
        watchB.WebhookToken = token;
        _dbContext.DockerWatches.Add(watchB);

        await Assert.ThrowsAsync<DbUpdateException>(() => _dbContext.SaveChangesAsync());
    }

    [Fact]
    public async Task UniqueIndex_OnWebhookToken_AllowsMultipleNullValues()
    {
        var user = await SeedUserAsync();
        var conn = await SeedConnectionAsync(user.Id, "c1");

        // Default state: WebhookToken is null on both. The unique index must
        // treat multiple NULLs as distinct (Postgres + SQLite both do).
        _dbContext.DockerWatches.Add(BuildMinimalWatch(conn.Id, webResourceId: null, user.Id, containerName: "a"));
        _dbContext.DockerWatches.Add(BuildMinimalWatch(conn.Id, webResourceId: null, user.Id, containerName: "b"));

        await _dbContext.SaveChangesAsync();

        var rows = await _dbContext.DockerWatches.AsNoTracking().ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, w => Assert.Null(w.WebhookToken));
    }

    [Fact]
    public async Task MultipleWatchesPerConnection_AllowedWhenContainerNamesDiffer()
    {
        // Composite service: WordPress = app container + database container,
        // both on the same host but distinct container names.
        var user = await SeedUserAsync();
        var conn = await SeedConnectionAsync(user.Id, "c1");
        _dbContext.DockerWatches.Add(BuildMinimalWatch(conn.Id, webResourceId: null, user.Id, label: "app", containerName: "wp-app"));
        _dbContext.DockerWatches.Add(BuildMinimalWatch(conn.Id, webResourceId: null, user.Id, label: "db", containerName: "wp-db"));

        await _dbContext.SaveChangesAsync();

        var rows = await _dbContext.DockerWatches.AsNoTracking()
            .Where(w => w.DockerConnectionId == conn.Id)
            .ToListAsync();
        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public async Task UniqueIndex_OnConnectionContainerName_RejectsDuplicateContainerOnSameConnection()
    {
        var user = await SeedUserAsync();
        var conn = await SeedConnectionAsync(user.Id, "c1");
        _dbContext.DockerWatches.Add(BuildMinimalWatch(conn.Id, webResourceId: null, user.Id, containerName: "nginx"));
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();

        _dbContext.DockerWatches.Add(BuildMinimalWatch(conn.Id, webResourceId: null, user.Id, containerName: "nginx"));

        await Assert.ThrowsAsync<DbUpdateException>(() => _dbContext.SaveChangesAsync());
    }

    [Fact]
    public async Task DeletingWebResource_DetachesWatchButKeepsIt()
    {
        // V3.6 — a container outlives its service; deleting the service only
        // nulls the optional link, it doesn't remove the watch.
        var user = await SeedUserAsync();
        var conn = await SeedConnectionAsync(user.Id, "c1");
        var resource = await SeedWebResourceAsync(user.Id);
        var watch = BuildMinimalWatch(conn.Id, resource.Id, user.Id);
        _dbContext.DockerWatches.Add(watch);
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();

        await _dbContext.WebResources.Where(r => r.Id == resource.Id).ExecuteDeleteAsync();

        var persisted = await _dbContext.DockerWatches.AsNoTracking()
            .FirstOrDefaultAsync(w => w.Id == watch.Id);
        Assert.NotNull(persisted);
        Assert.Null(persisted!.WebResourceId);
    }

    [Fact]
    public async Task DeletingConnection_CascadesAndRemovesTheWatch()
    {
        // V3.6 — the watch is owned by its host connection; removing the
        // connection removes the tracked container with it.
        var user = await SeedUserAsync();
        var conn = await SeedConnectionAsync(user.Id, "c1");
        var watch = BuildMinimalWatch(conn.Id, webResourceId: null, user.Id);
        _dbContext.DockerWatches.Add(watch);
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();

        await _dbContext.DockerConnections.Where(c => c.Id == conn.Id).ExecuteDeleteAsync();

        var remaining = await _dbContext.DockerWatches.AsNoTracking()
            .AnyAsync(w => w.Id == watch.Id);
        Assert.False(remaining);
    }

    [Fact]
    public async Task DeletingUser_CascadesAndRemovesTheWatch()
    {
        var user = await SeedUserAsync();
        var conn = await SeedConnectionAsync(user.Id, "c1");
        var watch = BuildMinimalWatch(conn.Id, webResourceId: null, user.Id);
        _dbContext.DockerWatches.Add(watch);
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();

        await _dbContext.Users.Where(u => u.Id == user.Id).ExecuteDeleteAsync();

        var remaining = await _dbContext.DockerWatches.AsNoTracking()
            .AnyAsync(w => w.Id == watch.Id);
        Assert.False(remaining);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static DockerWatchEntity BuildMinimalWatch(
        Guid connectionId, Guid? webResourceId, Guid userId,
        string label = "app", string containerName = "nginx") => new()
    {
        Id = Guid.NewGuid(),
        DockerConnectionId = connectionId,
        WebResourceId = webResourceId,
        UserId = userId,
        Label = label,
        Enabled = true,
        ImageReference = "nginx:latest",
        RegistryHost = "docker.io",
        Repository = "library/nginx",
        Tag = "latest",
        ContainerName = containerName,
    };

    private async Task<DockerConnectionEntity> SeedConnectionAsync(Guid userId, string name)
    {
        var conn = new DockerConnectionEntity
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Name = name,
            HostType = DockerHostType.LocalSocket,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow,
        };
        _dbContext.DockerConnections.Add(conn);
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();
        return conn;
    }

    private async Task<UserEntity> SeedUserAsync()
    {
        var user = new UserEntity
        {
            Id = Guid.NewGuid(),
            Email = $"watch-{Guid.NewGuid():N}@test.local",
            NormalizedEmail = $"WATCH-{Guid.NewGuid():N}@TEST.LOCAL",
            PasswordHash = "x",
            SecurityStamp = Guid.NewGuid().ToString("N"),
            CreatedUtc = DateTime.UtcNow,
        };
        _dbContext.Users.Add(user);
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();
        return user;
    }

    private async Task<WebResourceEntity> SeedWebResourceAsync(Guid userId)
    {
        var resource = new WebResourceEntity
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Name = "Service",
            MainUrl = "https://example.com",
            CurrentStatus = ServiceStatus.Unknown,
        };
        _dbContext.WebResources.Add(resource);
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();
        return resource;
    }
}


