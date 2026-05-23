using System.Security.Claims;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Moq;
using Stashboard.Api.Auth;
using Stashboard.Api.Contracts;
using Stashboard.Api.Controllers;
using Stashboard.Api.Data;
using Stashboard.Api.Notifications;
using Stashboard.Core.Abstractions;
using Stashboard.Core.Entities;
using Stashboard.Core.Enums;
using Stashboard.Tests.Infrastructure;

namespace Stashboard.Tests.Controllers.WebResources;

/// <summary>
/// Base for all WebResourcesController tests.
/// Uses a real PostgreSQL database (stashboard_test) so tests run against actual SQL behaviour.
///
/// Schema is created once per process via <see cref="EnsureSchemaAsync"/>.
/// All application-level rows are deleted before every single test so no state leaks
/// between test methods, while keeping schema-creation overhead minimal.
/// </summary>
public abstract class WebResourcesControllerTestBase : IAsyncLifetime
{
    protected Guid _userId { get; private set; }
    protected Guid _otherUserId { get; private set; }

    protected readonly ApplicationDbContext _dbContext;
    protected readonly IPasswordHasher _passwordHasher = new Pbkdf2PasswordHasher();
    protected readonly Mock<IEncryptionService> _encryptionMock = new();
    protected readonly Mock<IFaviconService> _faviconMock = new();
    protected readonly Mock<IServiceHealthChecker> _healthCheckerMock = new();
    protected readonly Mock<IServiceStatusNotificationService> _statusNotificationsMock = new();
    protected readonly Mock<IWebHostEnvironment> _envMock = new();

    protected IDataFactory _dataFactory { get; private set; } = default!;

    // Schema is only created once across the whole test run.
    private static bool _schemaReady;
    private static readonly SemaphoreSlim _schemaLock = new(1, 1);

    protected WebResourcesControllerTestBase()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(BuildTestConnectionString())
            .Options;

        _dbContext = new ApplicationDbContext(options);

        _encryptionMock.Setup(e => e.Encrypt(It.IsAny<string>())).Returns<string>(v => $"enc:{v}");
        _encryptionMock.Setup(e => e.Decrypt(It.IsAny<string>())).Returns<string>(v => v.StartsWith("enc:") ? v[4..] : v);

        _faviconMock
            .Setup(f => f.ResolveFaviconUrlAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string url, CancellationToken _) => $"https://favicon.example.com?url={url}");

        _faviconMock
            .Setup(f => f.DownloadAsBase64Async(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string url, CancellationToken _) => $"data:image/png;base64,AAAA=={url}");

        _healthCheckerMock
            .Setup(h => h.CheckAsync(It.IsAny<WebResourceEntity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((WebResourceEntity service, CancellationToken _) => new ServiceCheckResult(
                service.MainUrlHealthCheckEnabled
                    ? new HealthCheckResult(Stashboard.Core.Enums.ServiceStatus.Up, 100, null)
                    : new HealthCheckResult(Stashboard.Core.Enums.ServiceStatus.Unknown, null, null),
                !string.IsNullOrWhiteSpace(service.AdditionalUrl)
                    ? service.AdditionalUrlHealthCheckEnabled
                        ? new HealthCheckResult(Stashboard.Core.Enums.ServiceStatus.Up, 100, null)
                        : new HealthCheckResult(Stashboard.Core.Enums.ServiceStatus.Unknown, null, null)
                    : null));

        _envMock.SetupGet(e => e.ContentRootPath).Returns(Path.GetTempPath());
        _envMock.SetupGet(e => e.WebRootPath).Returns((string)null!);
    }

    // ── xUnit lifecycle ───────────────────────────────────────────────────────

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

    // ── Schema helpers ────────────────────────────────────────────────────────

    /// <summary>Creates tables once per test process. Subsequent calls are no-ops.</summary>
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
        finally
        {
            _schemaLock.Release();
        }
    }

    /// <summary>Wipes all application rows from the test database. Called once per process run.</summary>
    private async Task ClearAllDataAsync()
    {
        await _dbContext.WebResourceTags.ExecuteDeleteAsync();
        await _dbContext.Credentials.ExecuteDeleteAsync();
        await _dbContext.WebResources.ExecuteDeleteAsync();
        await _dbContext.Tags.ExecuteDeleteAsync();
        await _dbContext.Categories.ExecuteDeleteAsync();
        await _dbContext.Users.ExecuteDeleteAsync();
    }

    // ── Connection string

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

    // ── Controller factory ────────────────────────────────────────────────────

    protected WebResourcesController BuildController(Guid? userId = null)
    {
        var mapper = new Stashboard.Api.Mapping.WebResourceMapper(
            _encryptionMock.Object,
            _faviconMock.Object);

        var userService = new UserService(
            _dbContext,
            _passwordHasher,
            _encryptionMock.Object,
            Options.Create(new JwtOptions
            {
                Secret = "test-secret-test-secret-test-secret-test-secret",
                EmailConfirmationTokenLifetimeHours = 24,
                PasswordResetTokenLifetimeHours = 2,
            }),
            new TestTimeProvider());

        var controller = new WebResourcesController(
            _dbContext,
            userService,
            _encryptionMock.Object,
            _healthCheckerMock.Object,
            mapper,
            _statusNotificationsMock.Object,
            _envMock.Object,
            _faviconMock.Object);

        var identity = new ClaimsIdentity(
            new[] { new Claim(StashboardClaims.UserId, (userId ?? _userId).ToString()) }, "Test");

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };

        return controller;
    }

    // ── DB helpers to reload entities (bypasses EF tracking cache) ───────────

    protected Task<WebResourceEntity?> ReloadServiceAsync(Guid id) =>
        _dbContext.WebResources
            .AsNoTracking()
            .Include(s => s.Credentials)
            .Include(s => s.WebResourceTags).ThenInclude(st => st.Tag)
            .Include(s => s.Category)
            .FirstOrDefaultAsync(s => s.Id == id);

    protected Task<List<CredentialEntity>> ReloadCredentialsAsync(Guid serviceId) =>
        _dbContext.Credentials.AsNoTracking().Where(c => c.WebResourceId == serviceId).ToListAsync();

    protected Task<List<WebResourceTagEntity>> ReloadServiceTagsAsync(Guid serviceId) =>
        _dbContext.WebResourceTags.AsNoTracking()
            .Include(st => st.Tag)
            .Where(st => st.WebResourceId == serviceId)
            .ToListAsync();

    protected static WebResourceUpsertRequest DefaultRequest(
        string name = "My Service",
        string mainUrl = "https://example.com") =>
        new(name, mainUrl, true, null, true, null, HealthCheckMethod.Get, null, null, null,
            LogoSource.AutoFavicon, null, [], []);
}
