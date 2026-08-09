using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Stashboard.Api.Auth;
using Stashboard.Api.Data;
using Stashboard.Api.Notifications;
using Stashboard.Api.Services;
using Stashboard.Api.Services.HealthCheck;
using Stashboard.Api.Services.HealthCheckSettings;
using Stashboard.Core.Abstractions;
using Stashboard.Core.Entities;
using Stashboard.Core.Enums;
using Stashboard.Core.Options;
using Stashboard.Tests.Infrastructure;

namespace Stashboard.Tests.Services;

/// <summary>
/// V5.6 — unit tests for <see cref="HealthCheckBackgroundService"/>. Drives
/// <c>ScanOnceAsync</c> directly against a real SQLite test database with a
/// mocked <see cref="IServiceHealthChecker"/>, pinning down the wiring that
/// V5.6 introduced: the scan sources the interval, failure threshold and
/// in-probe retry knobs from the DB-backed <see cref="HealthCheckSettingsEntity"/>
/// (not config), returns the live interval so the loop reschedules on it, and
/// feeds the retry settings into the probe.
/// </summary>
public class HealthCheckBackgroundServiceTests : IAsyncLifetime
{
    private readonly ApplicationDbContext _dbContext;
    private readonly IPasswordHasher _passwordHasher = new Pbkdf2PasswordHasher();
    private readonly Mock<IEncryptionService> _encryptionMock = new();
    private readonly Mock<IServiceHealthChecker> _checkerMock = new();
    private readonly Mock<IUserService> _userServiceMock = new();
    private readonly Mock<IServiceStatusNotificationService> _notificationsMock = new();
    private Guid _userId;

    private static bool _schemaReady;
    private static readonly SemaphoreSlim _schemaLock = new(1, 1);

    public HealthCheckBackgroundServiceTests()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(BuildTestConnectionString())
            .Options;
        _dbContext = new ApplicationDbContext(options);

        _encryptionMock.Setup(e => e.Encrypt(It.IsAny<string>())).Returns<string>(v => $"enc:{v}");
        _encryptionMock.Setup(e => e.Decrypt(It.IsAny<string>())).Returns<string>(v => v.StartsWith("enc:") ? v[4..] : v);
        // FindByIdAsync → null keeps the scan from branching into notifications;
        // the status-write behaviour under test happens before that branch.
        _userServiceMock
            .Setup(u => u.FindByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((UserEntity?)null);
    }

    public async ValueTask InitializeAsync()
    {
        await EnsureSchemaAsync();
        await ClearAsync();
        var factory = new DataFactory(_dbContext, _encryptionMock.Object, _passwordHasher, Guid.Empty);
        _userId = (await factory.UserAsync()).Id;
    }

    public async ValueTask DisposeAsync() => _dbContext.DisposeAsync().AsTask();

    [Fact]
    public async Task Scan_ReturnsDbInterval_AndPassesDbRetrySettingsToChecker()
    {
        await SeedSettingsAsync(intervalSeconds: 120, failureThreshold: 3, retryCount: 5, retryDelayMs: 250);
        await SeedServiceAsync();
        HealthCheckRetrySettings? capturedRetry = null;
        SetupChecker(ServiceStatus.Up, captureRetry: r => capturedRetry = r);

        var returnedInterval = await BuildService().ScanOnceAsync(CancellationToken.None);

        Assert.Equal(120, returnedInterval); // the loop reschedules on the live DB value
        Assert.NotNull(capturedRetry);
        Assert.Equal(5, capturedRetry!.RetryCount);
        Assert.Equal(250, capturedRetry.RetryDelayMs);
    }

    [Fact]
    public async Task Scan_AppliesDbFailureThreshold_OnlyMarksDownAfterThresholdConsecutiveFailures()
    {
        await SeedSettingsAsync(intervalSeconds: 60, failureThreshold: 2, retryCount: 0, retryDelayMs: 0);
        await SeedServiceAsync();
        SetupChecker(ServiceStatus.Down);
        var service = BuildService();

        // First failed scan: below the threshold of 2 — status stays Up, counter at 1.
        await service.ScanOnceAsync(CancellationToken.None);
        var afterFirst = await LoadServiceAsync();
        Assert.Equal(ServiceStatus.Up, afterFirst.CurrentStatus);
        Assert.Equal(1, afterFirst.MainUrlConsecutiveFailures);

        // Second consecutive failure reaches the threshold — now it flips to Down.
        await service.ScanOnceAsync(CancellationToken.None);
        var afterSecond = await LoadServiceAsync();
        Assert.Equal(ServiceStatus.Down, afterSecond.CurrentStatus);
        Assert.Equal(2, afterSecond.MainUrlConsecutiveFailures);
    }

    [Fact]
    public async Task Scan_WritesHistoryEvent_OnStatusTransition()
    {
        // Seeded service starts Up; the checker returns Up too on the first scan (no transition,
        // but the first sample seeds the timeline), then a transition to Down records a row.
        await SeedSettingsAsync(intervalSeconds: 60, failureThreshold: 1, retryCount: 0, retryDelayMs: 0);
        await SeedServiceAsync();
        SetupChecker(ServiceStatus.Down);

        await BuildService().ScanOnceAsync(CancellationToken.None);

        var events = await _dbContext.HealthCheckEvents.AsNoTracking().ToListAsync();
        var row = Assert.Single(events);
        Assert.Equal(HealthCheckTarget.Main, row.Target);
        Assert.Equal(ServiceStatus.Down, row.Status);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private void SetupChecker(ServiceStatus mainStatus, Action<HealthCheckRetrySettings?>? captureRetry = null)
    {
        _checkerMock
            .Setup(c => c.CheckAsync(It.IsAny<WebResourceEntity>(), It.IsAny<HealthCheckRetrySettings?>(), It.IsAny<CancellationToken>()))
            .Callback<WebResourceEntity, HealthCheckRetrySettings?, CancellationToken>((_, retry, _) => captureRetry?.Invoke(retry))
            .ReturnsAsync(new ServiceCheckResult(
                new HealthCheckResult(mainStatus, 100, mainStatus == ServiceStatus.Down ? "boom" : null),
                Additional: null));
    }

    private HealthCheckBackgroundService BuildService()
    {
        var sp = new ServiceCollection()
            .AddSingleton(_dbContext)
            .AddSingleton(_checkerMock.Object)
            .AddSingleton(_userServiceMock.Object)
            .AddSingleton(_notificationsMock.Object)
            .AddSingleton<IHealthCheckSettingsService>(_ =>
                new HealthCheckSettingsService(_dbContext, Options.Create(new HealthCheckOptions()), TimeProvider.System))
            .AddSingleton<IHealthCheckEventRecorder>(new HealthCheckEventRecorder())
            .BuildServiceProvider();

        var optionsMonitor = new Mock<IOptionsMonitor<HealthCheckOptions>>();
        optionsMonitor.Setup(m => m.CurrentValue).Returns(new HealthCheckOptions());

        return new HealthCheckBackgroundService(
            new TestScopeFactory(sp), optionsMonitor.Object, NullLogger<HealthCheckBackgroundService>.Instance);
    }

    private async Task SeedSettingsAsync(int intervalSeconds, int failureThreshold, int retryCount, int retryDelayMs)
    {
        _dbContext.HealthCheckSettings.Add(new HealthCheckSettingsEntity
        {
            Id = HealthCheckSettingsEntity.SingletonId,
            IntervalSeconds = intervalSeconds,
            FailureThreshold = failureThreshold,
            RetryCount = retryCount,
            RetryDelayMs = retryDelayMs,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow,
        });
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();
    }

    private async Task SeedServiceAsync()
    {
        _dbContext.WebResources.Add(new WebResourceEntity
        {
            Id = Guid.NewGuid(),
            UserId = _userId,
            Name = "svc",
            MainUrl = "https://example.com",
            CurrentStatus = ServiceStatus.Up,
        });
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();
    }

    private async Task<WebResourceEntity> LoadServiceAsync()
    {
        _dbContext.ChangeTracker.Clear();
        return await _dbContext.WebResources.AsNoTracking().SingleAsync();
    }

    private async Task ClearAsync()
    {
        await _dbContext.HealthCheckEvents.ExecuteDeleteAsync();
        await _dbContext.WebResources.ExecuteDeleteAsync();
        await _dbContext.HealthCheckSettings.ExecuteDeleteAsync();
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
        var path = Path.Combine(Path.GetTempPath(), "stashboard-healthcheck-bg-tests.db");
        return $"Data Source={path};Pooling=False";
    }

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


