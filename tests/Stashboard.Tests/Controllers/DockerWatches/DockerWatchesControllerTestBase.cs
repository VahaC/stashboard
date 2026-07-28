using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Stashboard.Api.Auth;
using Stashboard.Api.Controllers;
using Stashboard.Api.Data;
using Stashboard.Api.Mapping;
using Stashboard.Core.Abstractions;
using Stashboard.Core.Entities;
using Stashboard.Core.Enums;
using Stashboard.Infrastructure.Docker;
using Stashboard.Tests.Infrastructure;

namespace Stashboard.Tests.Controllers.DockerWatches;

/// <summary>
/// Test base for <see cref="DockerWatchesController"/> endpoints. Mirrors the
/// <c>WebResourcesControllerTestBase</c> pattern — real PostgreSQL DB
/// (<c>stashboard_test</c>), schema created once per process, all rows wiped
/// per test. Provides seed helpers plus a controller factory with a mocked
/// orchestrator that tests can program per-case.
/// </summary>
public abstract class DockerWatchesControllerTestBase : IAsyncLifetime
{
    protected Guid _userId { get; private set; }
    protected Guid _otherUserId { get; private set; }

    protected readonly ApplicationDbContext _dbContext;
    protected readonly IPasswordHasher _passwordHasher = new Pbkdf2PasswordHasher();
    protected readonly Mock<IEncryptionService> _encryptionMock = new();
    protected readonly Mock<IDockerUpdateChecker> _updateCheckerMock = new();
    protected readonly Mock<IDockerWebhookTokenGenerator> _webhookTokenGeneratorMock = new();
    protected readonly Mock<IDockerImageUpdater> _imageUpdaterMock = new();
    // Default (unconfigured) IsSelfTargetAsync returns false, so every existing
    // test keeps exercising the normal in-process recreate path. Self-update
    // tests override the setup.
    protected readonly Mock<ISelfUpdateLauncher> _selfUpdateLauncherMock = new();
    protected readonly Mock<IDockerHostClient> _hostClientMock = new();
    protected readonly Mock<IDockerLogStreamer> _logStreamerMock = new();
    protected readonly Mock<IDockerStatsStreamer> _statsStreamerMock = new();

    protected IDataFactory _dataFactory { get; private set; } = default!;

    private static bool _schemaReady;
    private static readonly SemaphoreSlim _schemaLock = new(1, 1);

    protected DockerWatchesControllerTestBase()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(BuildTestConnectionString())
            .Options;
        _dbContext = new ApplicationDbContext(options);

        _encryptionMock.Setup(e => e.Encrypt(It.IsAny<string>())).Returns<string>(v => $"enc:{v}");
        _encryptionMock.Setup(e => e.Decrypt(It.IsAny<string>()))
            .Returns<string>(v => v.StartsWith("enc:") ? v[4..] : v);

        // Default deterministic token — individual tests override via Setup as needed.
        _webhookTokenGeneratorMock.Setup(g => g.Generate()).Returns(new string('a', 64));
    }

    public async Task InitializeAsync()
    {
        await EnsureSchemaAsync();
        await ClearAllDataAsync();
        _dataFactory = new DataFactory(_dbContext, _encryptionMock.Object, _passwordHasher, Guid.Empty);
        var seeder = new UserSeeder(_dataFactory);
        await seeder.SeedAsync();
        _userId = seeder.Owner.Id;
        _otherUserId = seeder.Other.Id;
        _dataFactory = new DataFactory(_dbContext, _encryptionMock.Object, _passwordHasher, _userId);
    }

    public async Task DisposeAsync()
    {
        await _dbContext.DisposeAsync();
    }

    private async Task EnsureSchemaAsync()
    {
        if (_schemaReady) return;
        await _schemaLock.WaitAsync();
        try
        {
            if (!_schemaReady)
            {
                await _dbContext.Database.EnsureDeletedAsync();
                await _dbContext.Database.EnsureCreatedAsync();
                _schemaReady = true;
            }
        }
        finally { _schemaLock.Release(); }
    }

    private async Task ClearAllDataAsync()
    {
        // Order matters: child rows before parent rows (WebResources, Users).
        await _dbContext.DockerUpdateAttempts.ExecuteDeleteAsync();
        await _dbContext.DockerWatches.ExecuteDeleteAsync();
        await _dbContext.DockerConnections.ExecuteDeleteAsync();
        await _dbContext.WebResourceTags.ExecuteDeleteAsync();
        await _dbContext.Credentials.ExecuteDeleteAsync();
        await _dbContext.WebResources.ExecuteDeleteAsync();
        await _dbContext.Tags.ExecuteDeleteAsync();
        await _dbContext.Categories.ExecuteDeleteAsync();
        await _dbContext.Users.ExecuteDeleteAsync();
    }

    private static string BuildTestConnectionString()
    {
        var apiDir = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Stashboard.Api"));
        var config = new ConfigurationBuilder()
            .SetBasePath(apiDir)
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile("appsettings.Development.json", optional: true)
            .AddEnvironmentVariables(prefix: "STASHBOARD_")
            .Build();
        var devCs = config.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is not configured.");
        _ = devCs; // dev connection string unused; tests use a local SQLite file
        return $"Data Source={Path.Combine(Path.GetTempPath(), "stashboard-tests.db")};Pooling=False";
    }

    // ── Controller factory ───────────────────────────────────────────────────

    protected DockerWatchesController BuildController(Guid? userId = null)
    {
        var mapper = new DockerWatchMapper(_encryptionMock.Object, new ImageReferenceParser());
        var connectionMapper = new DockerConnectionMapper(_encryptionMock.Object);
        var controller = new DockerWatchesController(
            _dbContext, mapper, connectionMapper,
            _updateCheckerMock.Object, _webhookTokenGeneratorMock.Object,
            _imageUpdaterMock.Object, _selfUpdateLauncherMock.Object, _hostClientMock.Object,
            _logStreamerMock.Object, _statsStreamerMock.Object);

        var identity = new ClaimsIdentity(
            new[] { new Claim(StashboardClaims.UserId, (userId ?? _userId).ToString()) }, "Test");
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };
        return controller;
    }

    /// <summary>V3.6 — builds the connection-scoped watch controller used by
    /// the Docker page, wired to the same mocks/db as the service-scoped one.</summary>
    protected DockerConnectionWatchesController BuildConnectionWatchesController(Guid? userId = null)
    {
        var mapper = new DockerWatchMapper(_encryptionMock.Object, new ImageReferenceParser());
        var controller = new DockerConnectionWatchesController(
            _dbContext, mapper,
            _updateCheckerMock.Object, _webhookTokenGeneratorMock.Object, _imageUpdaterMock.Object,
            _selfUpdateLauncherMock.Object);

        var identity = new ClaimsIdentity(
            new[] { new Claim(StashboardClaims.UserId, (userId ?? _userId).ToString()) }, "Test");
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };
        return controller;
    }

    /// <summary>V3.6 — seed a standalone Docker connection (not attached to any
    /// service) for the connection-scoped watch tests.</summary>
    protected async Task<DockerConnectionEntity> SeedConnectionAsync(Guid userId, string? name = null)
    {
        var conn = new DockerConnectionEntity
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Name = name ?? $"conn-{Guid.NewGuid():N}",
            HostType = DockerHostType.LocalSocket,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow,
        };
        _dbContext.DockerConnections.Add(conn);
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();
        return conn;
    }

    // ── Seed helpers ─────────────────────────────────────────────────────────

    protected Task<DockerWatchEntity> SeedWatchAsync(
        Guid webResourceId,
        Guid userId,
        string label = "app",
        bool enabled = true,
        string imageReference = "ghcr.io/owner/repo:v1",
        string registryHost = "ghcr.io",
        string repository = "owner/repo",
        string tag = "v1",
        string containerName = "svc",
        DockerHostType hostType = DockerHostType.LocalSocket,
        DockerUpdateStatus status = DockerUpdateStatus.Unknown,
        bool withRegistryCreds = false)
        => SeedWatchInternalAsync(webResourceId, userId, label, enabled, imageReference, registryHost, repository,
            tag, containerName, hostType, status, withRegistryCreds);

    private async Task<DockerWatchEntity> SeedWatchInternalAsync(
        Guid webResourceId, Guid userId, string label, bool enabled, string imageReference,
        string registryHost, string repository, string tag, string containerName,
        DockerHostType hostType, DockerUpdateStatus status, bool withRegistryCreds)
    {
        // V3.6 — a watch is owned by its Docker connection (required FK) and
        // only optionally linked to a service. Ensure the service has a
        // connection assigned and stamp the watch with it.
        var connection = await EnsureConnectionAsync(webResourceId, userId, hostType);

        var watch = new DockerWatchEntity
        {
            Id = Guid.NewGuid(),
            DockerConnectionId = connection.Id,
            WebResourceId = webResourceId,
            UserId = userId,
            Label = label,
            Enabled = enabled,
            ImageReference = imageReference,
            RegistryHost = registryHost,
            Repository = repository,
            Tag = tag,
            ContainerName = containerName,
            UpdateStatus = status,
            RegistryUsernameEncrypted = withRegistryCreds ? "enc:user" : null,
            RegistryPasswordEncrypted = withRegistryCreds ? "enc:pass" : null,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow,
        };
        _dbContext.DockerWatches.Add(watch);
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();
        return watch;
    }

    /// <summary>Seed a Docker connection and assign it to the service. Idempotent
    /// per service — if the service already has a connection assigned, returns
    /// the existing one. Connections are now user-scoped, so the service-side
    /// FK is what makes the link.</summary>
    protected async Task<DockerConnectionEntity> EnsureConnectionAsync(
        Guid webResourceId,
        Guid userId,
        DockerHostType hostType = DockerHostType.LocalSocket,
        string? hostUrl = null,
        bool withTls = false)
    {
        var assignedId = await _dbContext.WebResources.AsNoTracking()
            .Where(s => s.Id == webResourceId)
            .Select(s => s.DockerConnectionId)
            .FirstOrDefaultAsync();
        if (assignedId is not null)
        {
            return await _dbContext.DockerConnections.AsNoTracking()
                .SingleAsync(c => c.Id == assignedId.Value);
        }

        var conn = new DockerConnectionEntity
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            // Names must be unique per (UserId, Name); use the service id to
            // disambiguate when tests seed many services for the same user.
            Name = $"test-conn-{webResourceId:N}",
            HostType = hostType,
            HostUrl = hostUrl,
            TlsCaCertEncrypted = withTls ? "enc:ca" : null,
            TlsClientCertEncrypted = withTls ? "enc:cert" : null,
            TlsClientKeyEncrypted = withTls ? "enc:key" : null,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow,
        };
        _dbContext.DockerConnections.Add(conn);
        await _dbContext.SaveChangesAsync();

        // Wire the assignment on the WebResource — the controllers and the
        // background scan both resolve the connection via this FK now.
        await _dbContext.WebResources
            .Where(s => s.Id == webResourceId)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.DockerConnectionId, conn.Id));

        _dbContext.ChangeTracker.Clear();
        return conn;
    }

    protected Task<DockerWatchEntity?> ReloadWatchAsync(Guid id) =>
        _dbContext.DockerWatches.AsNoTracking().FirstOrDefaultAsync(w => w.Id == id);

    /// <summary>Reload the (single) watch attached to a service. Convenience helper
    /// for tests that only ever create one watch per service; for multi-watch
    /// scenarios use the watch-by-id query directly.</summary>
    protected Task<DockerWatchEntity?> ReloadWatchByServiceAsync(Guid webResourceId) =>
        _dbContext.DockerWatches.AsNoTracking()
            .FirstOrDefaultAsync(w => w.WebResourceId == webResourceId);

    protected Task<List<DockerWatchEntity>> ReloadWatchesByServiceAsync(Guid webResourceId) =>
        _dbContext.DockerWatches.AsNoTracking()
            .Where(w => w.WebResourceId == webResourceId)
            .ToListAsync();
}
