using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Stashboard.Api.Auth;
using Stashboard.Api.Contracts;
using Stashboard.Api.Data;
using Stashboard.Api.Mapping;
using Stashboard.Api.Services;
using Stashboard.Api.Services.ImagePrune;
using Stashboard.Core.Abstractions;
using Stashboard.Core.Entities;
using Stashboard.Core.Enums;
using Stashboard.Core.Options;
using Stashboard.Tests.Infrastructure;

namespace Stashboard.Tests.Services;

/// <summary>
/// V5.5 — unit tests for <see cref="DockerImagePruneBackgroundService"/>.
/// Drives <c>SweepOnceAsync</c> directly against a real SQLite test
/// database with a mocked <see cref="IDockerPruneRunner"/> so the behaviour
/// that matters (master-switch gate, due-by-interval filter,
/// <c>AllowImagePrune</c> opt-out, audit-row persistence, <c>LastImagePruneUtc</c>
/// advancement on success but not failure) can be pinned down without a
/// real Docker daemon.
/// </summary>
public class DockerImagePruneBackgroundServiceTests : IAsyncLifetime
{
    private readonly ApplicationDbContext _dbContext;
    private readonly IPasswordHasher _passwordHasher = new Pbkdf2PasswordHasher();
    private readonly Mock<IEncryptionService> _encryptionMock = new();
    private readonly Mock<IDockerPruneRunner> _runnerMock = new();
    private readonly ServiceCollection _services = new();
    private Guid _userId;

    private static bool _schemaReady;
    private static readonly SemaphoreSlim _schemaLock = new(1, 1);

    public DockerImagePruneBackgroundServiceTests()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(BuildTestConnectionString())
            .Options;
        _dbContext = new ApplicationDbContext(options);

        _encryptionMock.Setup(e => e.Encrypt(It.IsAny<string>())).Returns<string>(v => $"enc:{v}");
        _encryptionMock.Setup(e => e.Decrypt(It.IsAny<string>()))
            .Returns<string>(v => v.StartsWith("enc:") ? v[4..] : v);
    }

    public async Task InitializeAsync()
    {
        await EnsureSchemaAsync();
        await ClearAsync();
        var factory = new DataFactory(_dbContext, _encryptionMock.Object, _passwordHasher, Guid.Empty);
        var user = await factory.UserAsync();
        _userId = user.Id;
    }

    public Task DisposeAsync() => _dbContext.DisposeAsync().AsTask();

    [Fact]
    public async Task Sweep_DoesNothing_WhenMasterSwitchIsOff()
    {
        await SeedSettingsAsync(enabled: false, intervalHours: 1);
        await SeedConnectionAsync(allowPrune: true);

        var service = BuildService();
        await service.SweepOnceAsync(CancellationToken.None);

        Assert.Empty(await _dbContext.DockerPruneRuns.AsNoTracking().ToListAsync());
        _runnerMock.Verify(r => r.RunAsync(It.IsAny<DockerPruneRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Sweep_SkipsConnections_WithAllowImagePruneFalse()
    {
        await SeedSettingsAsync(enabled: true, intervalHours: 1);
        await SeedConnectionAsync(allowPrune: false);

        var service = BuildService();
        await service.SweepOnceAsync(CancellationToken.None);

        Assert.Empty(await _dbContext.DockerPruneRuns.AsNoTracking().ToListAsync());
        _runnerMock.Verify(r => r.RunAsync(It.IsAny<DockerPruneRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Sweep_SkipsConnections_WhereIntervalHasNotElapsed()
    {
        await SeedSettingsAsync(enabled: true, intervalHours: 24);
        // Last prune was 1 hour ago — well inside the 24h interval.
        await SeedConnectionAsync(allowPrune: true, lastPruneUtc: DateTime.UtcNow.AddHours(-1));

        var service = BuildService();
        await service.SweepOnceAsync(CancellationToken.None);

        Assert.Empty(await _dbContext.DockerPruneRuns.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Sweep_SkipsFreshlyCreatedConnection_NeverPruned_WithinFirstInterval()
    {
        await SeedSettingsAsync(enabled: true, intervalHours: 24);
        // Created 1 hour ago, never pruned: inside the 24h interval measured
        // from creation, so the first scheduled prune shouldn't fire yet.
        await SeedConnectionAsync(allowPrune: true, createdUtc: DateTime.UtcNow.AddHours(-1));

        var service = BuildService();
        await service.SweepOnceAsync(CancellationToken.None);

        Assert.Empty(await _dbContext.DockerPruneRuns.AsNoTracking().ToListAsync());
        _runnerMock.Verify(r => r.RunAsync(It.IsAny<DockerPruneRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Sweep_RunsDueConnection_PersistsAuditRow_AdvancesLastPrune()
    {
        await SeedSettingsAsync(enabled: true, intervalHours: 1);
        var conn = await SeedConnectionAsync(allowPrune: true);
        var completedUtc = DateTime.UtcNow;
        _runnerMock
            .Setup(r => r.RunAsync(It.IsAny<DockerPruneRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DockerPruneRunResult(
                DockerPruneStatus.Success, 3, 1_500_000,
                StartedUtc: completedUtc.AddSeconds(-2), CompletedUtc: completedUtc,
                IncludedUnused: false, Error: null));

        var service = BuildService();
        await service.SweepOnceAsync(CancellationToken.None);

        var row = await _dbContext.DockerPruneRuns.AsNoTracking().SingleAsync();
        Assert.Equal(conn.Id, row.DockerConnectionId);
        Assert.Equal(DockerPruneTrigger.Scheduled, row.Trigger);
        Assert.Equal(DockerPruneStatus.Success, row.Status);
        Assert.Null(row.InitiatedByUserId);
        Assert.Equal(3, row.ImagesDeleted);

        var refreshed = await _dbContext.DockerConnections.AsNoTracking().SingleAsync(c => c.Id == conn.Id);
        Assert.Equal(completedUtc, refreshed.LastImagePruneUtc);
    }

    [Fact]
    public async Task Sweep_HostUnreachable_StillWritesAuditRow_ButDoesNotAdvanceLastPrune()
    {
        await SeedSettingsAsync(enabled: true, intervalHours: 1);
        var conn = await SeedConnectionAsync(allowPrune: true);
        _runnerMock
            .Setup(r => r.RunAsync(It.IsAny<DockerPruneRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DockerPruneRunResult(
                DockerPruneStatus.HostUnreachable, 0, 0,
                StartedUtc: DateTime.UtcNow, CompletedUtc: DateTime.UtcNow,
                IncludedUnused: false, Error: "timeout"));

        var service = BuildService();
        await service.SweepOnceAsync(CancellationToken.None);

        Assert.Single(await _dbContext.DockerPruneRuns.AsNoTracking().ToListAsync());
        var refreshed = await _dbContext.DockerConnections.AsNoTracking().SingleAsync(c => c.Id == conn.Id);
        Assert.Null(refreshed.LastImagePruneUtc);
    }

    [Fact]
    public async Task Sweep_HonoursPruneUnusedFlag_OnTheConnection()
    {
        await SeedSettingsAsync(enabled: true, intervalHours: 1);
        await SeedConnectionAsync(allowPrune: true, pruneUnused: true);
        DockerPruneRequest? captured = null;
        _runnerMock
            .Setup(r => r.RunAsync(It.IsAny<DockerPruneRequest>(), It.IsAny<CancellationToken>()))
            .Callback<DockerPruneRequest, CancellationToken>((req, _) => captured = req)
            .ReturnsAsync(new DockerPruneRunResult(
                DockerPruneStatus.Success, 1, 100,
                DateTime.UtcNow, DateTime.UtcNow, IncludedUnused: true, Error: null));

        var service = BuildService();
        await service.SweepOnceAsync(CancellationToken.None);

        Assert.NotNull(captured);
        Assert.True(captured!.IncludeUnused);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private DockerImagePruneBackgroundService BuildService()
    {
        // The background service resolves a scope on every sweep, so the test
        // wires the same _dbContext as the scoped service. A tiny custom
        // factory returns a scope that hands out the test's context + a real
        // ImagePruneSettingsService backed by the same DB.
        var sp = new ServiceCollection()
            .AddSingleton(_dbContext)
            .AddSingleton<IDockerConnectionMapper>(new DockerConnectionMapper(_encryptionMock.Object))
            .AddSingleton(_runnerMock.Object)
            .AddSingleton<IImagePruneSettingsService>(_ =>
                new ImagePruneSettingsService(
                    _dbContext,
                    Options.Create(new ImagePruneOptions { Enabled = true, IntervalHours = 168 }),
                    TimeProvider.System))
            .BuildServiceProvider();

        var scopeFactory = new TestScopeFactory(sp);
        return new DockerImagePruneBackgroundService(
            scopeFactory, TimeProvider.System, NullLogger<DockerImagePruneBackgroundService>.Instance);
    }

    private async Task SeedSettingsAsync(bool enabled, int intervalHours)
    {
        _dbContext.ImagePruneSettings.Add(new ImagePruneSettingsEntity
        {
            Id = ImagePruneSettingsEntity.SingletonId,
            Enabled = enabled,
            IntervalHours = intervalHours,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow,
        });
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();
    }

    private async Task<DockerConnectionEntity> SeedConnectionAsync(
        bool allowPrune, bool pruneUnused = false, DateTime? lastPruneUtc = null, DateTime? createdUtc = null)
    {
        var conn = new DockerConnectionEntity
        {
            Id = Guid.NewGuid(),
            UserId = _userId,
            Name = $"conn-{Guid.NewGuid():N}",
            HostType = DockerHostType.LocalSocket,
            AllowImagePrune = allowPrune,
            PruneUnusedImages = pruneUnused,
            LastImagePruneUtc = lastPruneUtc,
            // Default to an old creation date so a "never pruned" connection
            // is due by default. The first-run-not-eager behaviour is covered
            // by the dedicated test that passes a recent createdUtc.
            CreatedUtc = createdUtc ?? DateTime.UtcNow.AddDays(-30),
            UpdatedUtc = DateTime.UtcNow,
        };
        _dbContext.DockerConnections.Add(conn);
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();
        return conn;
    }

    private async Task ClearAsync()
    {
        await _dbContext.DockerPruneRuns.ExecuteDeleteAsync();
        await _dbContext.ImagePruneSettings.ExecuteDeleteAsync();
        await _dbContext.DockerConnections.ExecuteDeleteAsync();
        await _dbContext.Users.ExecuteDeleteAsync();
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

    private static string BuildTestConnectionString()
    {
        var path = Path.Combine(Path.GetTempPath(), "stashboard-prune-bg-tests.db");
        return $"Data Source={path};Pooling=False";
    }

    /// <summary>
    /// Minimal scope factory that hands out scopes resolving from a single
    /// <see cref="ServiceProvider"/>. Sufficient because every resolution in
    /// the background service is a singleton-shaped registration above.
    /// </summary>
    private sealed class TestScopeFactory(IServiceProvider provider) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new TestScope(provider);

        private sealed class TestScope(IServiceProvider provider) : IServiceScope
        {
            public IServiceProvider ServiceProvider => provider;
            public void Dispose() { }
        }
    }
}
