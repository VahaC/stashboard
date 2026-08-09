using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Stashboard.Api.Auth;
using Stashboard.Api.Data;
using Stashboard.Api.Services.HealthCheck;
using Stashboard.Api.Services.HealthCheckSettings;
using Stashboard.Core.Abstractions;
using Stashboard.Core.Entities;
using Stashboard.Core.Enums;
using Stashboard.Core.Options;
using Stashboard.Tests.Infrastructure;

namespace Stashboard.Tests.Services.HealthCheck;

/// <summary>
/// V10.1 — the retention prune deletes only rows older than the configured window and leaves
/// in-window rows intact, keeping the append-only history table bounded.
/// </summary>
public class HealthCheckHistoryPruneServiceTests : IAsyncLifetime
{
    private readonly ApplicationDbContext _dbContext;
    private readonly IPasswordHasher _passwordHasher = new Pbkdf2PasswordHasher();
    private readonly Mock<IEncryptionService> _encryptionMock = new();
    private Guid _userId;
    private Guid _serviceId;

    private static bool _schemaReady;
    private static readonly SemaphoreSlim _schemaLock = new(1, 1);

    public HealthCheckHistoryPruneServiceTests()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite($"Data Source={Path.Combine(Path.GetTempPath(), "stashboard-hc-prune-tests.db")};Pooling=False")
            .Options;
        _dbContext = new ApplicationDbContext(options);
        _encryptionMock.Setup(e => e.Encrypt(It.IsAny<string>())).Returns<string>(v => $"enc:{v}");
        _encryptionMock.Setup(e => e.Decrypt(It.IsAny<string>())).Returns<string>(v => v.StartsWith("enc:") ? v[4..] : v);
    }

    public async ValueTask InitializeAsync()
    {
        await EnsureSchemaAsync();
        await _dbContext.HealthCheckEvents.ExecuteDeleteAsync();
        await _dbContext.WebResources.ExecuteDeleteAsync();
        await _dbContext.HealthCheckSettings.ExecuteDeleteAsync();
        await _dbContext.Users.ExecuteDeleteAsync();

        var factory = new DataFactory(_dbContext, _encryptionMock.Object, _passwordHasher, Guid.Empty);
        _userId = (await factory.UserAsync()).Id;
        var service = new WebResourceEntity { Id = Guid.NewGuid(), UserId = _userId, Name = "svc", MainUrl = "https://example.com" };
        _dbContext.WebResources.Add(service);
        await _dbContext.SaveChangesAsync();
        _serviceId = service.Id;
        _dbContext.ChangeTracker.Clear();
    }

    public ValueTask DisposeAsync() => _dbContext.DisposeAsync();

    [Fact]
    public async Task Prune_DeletesOnlyRowsOlderThanRetentionWindow()
    {
        // Retention = 30 days.
        _dbContext.HealthCheckSettings.Add(new HealthCheckSettingsEntity
        {
            Id = HealthCheckSettingsEntity.SingletonId,
            HistoryRetentionDays = 30,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow,
        });
        await AddEventAsync(DateTime.UtcNow.AddDays(-40)); // stale → pruned
        await AddEventAsync(DateTime.UtcNow.AddDays(-31)); // stale → pruned
        await AddEventAsync(DateTime.UtcNow.AddDays(-10)); // fresh → kept
        await AddEventAsync(DateTime.UtcNow.AddHours(-1)); // fresh → kept
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();

        var deleted = await BuildService().PruneOnceAsync(CancellationToken.None);

        Assert.Equal(2, deleted);
        var remaining = await _dbContext.HealthCheckEvents.AsNoTracking().ToListAsync();
        Assert.Equal(2, remaining.Count);
        Assert.All(remaining, e => Assert.True(e.TimestampUtc > DateTime.UtcNow.AddDays(-30)));
    }

    private async Task AddEventAsync(DateTime ts)
    {
        _dbContext.HealthCheckEvents.Add(new HealthCheckEventEntity
        {
            WebResourceId = _serviceId,
            Target = HealthCheckTarget.Main,
            Status = ServiceStatus.Up,
            ResponseTimeMs = 100,
            TimestampUtc = ts,
        });
    }

    private HealthCheckHistoryPruneBackgroundService BuildService()
    {
        var sp = new ServiceCollection()
            .AddSingleton(_dbContext)
            .AddSingleton<IHealthCheckSettingsService>(_ =>
                new HealthCheckSettingsService(_dbContext, Options.Create(new HealthCheckOptions()), TimeProvider.System))
            .BuildServiceProvider();

        return new HealthCheckHistoryPruneBackgroundService(
            new TestScopeFactory(sp), NullLogger<HealthCheckHistoryPruneBackgroundService>.Instance);
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
