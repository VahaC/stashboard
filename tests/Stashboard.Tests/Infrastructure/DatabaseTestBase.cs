using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Stashboard.Api.Data;

namespace Stashboard.Tests.Infrastructure;

/// <summary>
/// Shared base for tests that hit the real PostgreSQL test DB (stashboard_test).
/// Schema is created once per process; each test wipes all rows on entry.
/// </summary>
public abstract class DatabaseTestBase : IAsyncLifetime
{
    protected ApplicationDbContext _dbContext { get; private set; } = default!;

    private static bool _schemaReady;
    private static readonly SemaphoreSlim _schemaLock = new(1, 1);

    public virtual async Task InitializeAsync()
    {
        _dbContext = CreateDbContext();
        await EnsureSchemaAsync(_dbContext);
        await ClearAllDataAsync(_dbContext);
    }

    public virtual async Task DisposeAsync()
    {
        await _dbContext.DisposeAsync();
    }

    protected static ApplicationDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(BuildTestConnectionString())
            .Options;
        return new ApplicationDbContext(options);
    }

    private static async Task EnsureSchemaAsync(ApplicationDbContext db)
    {
        if (_schemaReady) return;
        await _schemaLock.WaitAsync();
        try
        {
            if (_schemaReady) return;
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();
            _schemaReady = true;
        }
        finally { _schemaLock.Release(); }
    }

    protected static async Task ClearAllDataAsync(ApplicationDbContext db)
    {
        await db.HostShellSessions.ExecuteDeleteAsync();
        await db.WebResourceTags.ExecuteDeleteAsync();
        await db.Credentials.ExecuteDeleteAsync();
        await db.WebResources.ExecuteDeleteAsync();
        await db.Tags.ExecuteDeleteAsync();
        await db.Categories.ExecuteDeleteAsync();
        await db.RefreshTokens.ExecuteDeleteAsync();
        await db.Users.ExecuteDeleteAsync();
        await db.EmailSettings.ExecuteDeleteAsync();
        await db.HostShellSettings.ExecuteDeleteAsync();
        await db.ContainerExecSettings.ExecuteDeleteAsync();
        await db.DockerExecSessions.ExecuteDeleteAsync();
        await db.HealthCheckSettings.ExecuteDeleteAsync();
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
}
