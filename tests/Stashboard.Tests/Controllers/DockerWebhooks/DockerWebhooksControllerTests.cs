using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Stashboard.Api.Controllers;
using Stashboard.Api.Data;
using Stashboard.Core.Entities;
using Stashboard.Core.Enums;
using Stashboard.Infrastructure.Docker;
using Stashboard.Tests.Infrastructure;

namespace Stashboard.Tests.Controllers.DockerWebhooks;

/// <summary>
/// V2.6 — integration tests for the public webhook receiver. Uses the same
/// real-Postgres pattern as the rest of the controller tests so the
/// integration is end-to-end through the unique index on
/// <c>WebhookToken</c>.
/// </summary>
public class DockerWebhooksControllerTests : IAsyncLifetime
{
    private const string ValidToken = "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";

    private static bool _schemaReady;
    private static readonly SemaphoreSlim _schemaLock = new(1, 1);

    private readonly ApplicationDbContext _db;
    private readonly DockerWebhookCheckQueue _queue = new();
    private readonly DockerWebhookPayloadParser _parser = new();

    public DockerWebhooksControllerTests()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(BuildTestConnectionString())
            .Options;
        _db = new ApplicationDbContext(options);
    }

    public async ValueTask InitializeAsync()
    {
        await EnsureSchemaAsync();
        await ClearAsync();
    }

    public async ValueTask DisposeAsync() => await _db.DisposeAsync();

    [Fact]
    public async Task Receive_ValidToken_AcceptedEnqueuedAndStampsReceivedAt()
    {
        var watch = await SeedWatchAsync(token: ValidToken, enabled: true);
        var ctrl = BuildController(jsonBody: "{}");

        var result = await ctrl.Receive(ValidToken, CancellationToken.None);

        Assert.IsType<AcceptedResult>(result);
        Assert.Single(_queue.DrainAll(), watch.Id);
        var dbRow = await _db.DockerWatches.AsNoTracking().SingleAsync(w => w.Id == watch.Id);
        Assert.NotNull(dbRow.LastWebhookReceivedUtc);
    }

    [Fact]
    public async Task Receive_DockerHubPayload_Accepted()
    {
        var watch = await SeedWatchAsync(token: ValidToken);
        const string body = """
        { "push_data": { "tag": "v1.0.0", "pushed_at": 1715900400 },
          "repository": { "repo_name": "owner/repo" } }
        """;
        var ctrl = BuildController(jsonBody: body);

        var result = await ctrl.Receive(ValidToken, CancellationToken.None);

        Assert.IsType<AcceptedResult>(result);
        Assert.Single(_queue.DrainAll(), watch.Id);
    }

    [Fact]
    public async Task Receive_UnknownToken_ReturnsNotFoundAndQueueStaysEmpty()
    {
        await SeedWatchAsync(token: ValidToken);
        // Different token, also a valid 64-char hex but doesn't match the
        // seeded watch's token. Must be rejected as 404.
        var bogus = new string('1', 64);
        var ctrl = BuildController(jsonBody: "{}");

        var result = await ctrl.Receive(bogus, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
        Assert.Empty(_queue.DrainAll());
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-hex-or-the-wrong-length")]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")] // 64 chars but contains non-hex
    public async Task Receive_MalformedTokenShape_ReturnsNotFoundBeforeDatabaseLookup(string token)
    {
        await SeedWatchAsync(token: ValidToken);
        var ctrl = BuildController(jsonBody: "{}");

        var result = await ctrl.Receive(token, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
        Assert.Empty(_queue.DrainAll());
    }

    [Fact]
    public async Task Receive_DisabledWatch_StampsButDoesNotEnqueue()
    {
        var watch = await SeedWatchAsync(token: ValidToken, enabled: false);
        var ctrl = BuildController(jsonBody: "{}");

        var result = await ctrl.Receive(ValidToken, CancellationToken.None);

        Assert.IsType<AcceptedResult>(result);
        Assert.Empty(_queue.DrainAll());
        var dbRow = await _db.DockerWatches.AsNoTracking().SingleAsync(w => w.Id == watch.Id);
        Assert.NotNull(dbRow.LastWebhookReceivedUtc);
    }

    [Fact]
    public async Task Receive_MalformedJsonBody_StillAcceptedAndEnqueued()
    {
        // The URL token already authenticated the call; a malformed body
        // is not a 4xx — it just yields the empty payload and proceeds.
        var watch = await SeedWatchAsync(token: ValidToken);
        var ctrl = BuildController(jsonBody: "{ not valid json");

        var result = await ctrl.Receive(ValidToken, CancellationToken.None);

        Assert.IsType<AcceptedResult>(result);
        Assert.Single(_queue.DrainAll(), watch.Id);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private DockerWebhooksController BuildController(string jsonBody)
    {
        var ctrl = new DockerWebhooksController(_db, _queue, _parser, NullLogger<DockerWebhooksController>.Instance);
        var httpContext = new DefaultHttpContext
        {
            Request =
            {
                Body = new MemoryStream(Encoding.UTF8.GetBytes(jsonBody)),
                ContentType = "application/json",
            }
        };
        ctrl.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return ctrl;
    }

    private async Task<DockerWatchEntity> SeedWatchAsync(string token, bool enabled = true)
    {
        var user = new UserEntity
        {
            Id = Guid.NewGuid(),
            Email = $"{Guid.NewGuid():N}@test.local",
            NormalizedEmail = $"{Guid.NewGuid():N}@TEST.LOCAL",
            PasswordHash = "x",
            SecurityStamp = Guid.NewGuid().ToString("N"),
            CreatedUtc = DateTime.UtcNow,
        };
        _db.Users.Add(user);

        var resource = new WebResourceEntity
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            Name = "Svc",
            MainUrl = "https://example.com",
        };
        _db.WebResources.Add(resource);

        var connection = new DockerConnectionEntity
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            Name = $"conn-{Guid.NewGuid():N}",
            HostType = DockerHostType.LocalSocket,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow,
        };
        _db.DockerConnections.Add(connection);

        var watch = new DockerWatchEntity
        {
            Id = Guid.NewGuid(),
            DockerConnectionId = connection.Id,
            UserId = user.Id,
            WebResourceId = resource.Id,
            Label = "app",
            Enabled = enabled,
            ImageReference = "ghcr.io/owner/repo:v1",
            RegistryHost = "ghcr.io",
            Repository = "owner/repo",
            Tag = "v1",
            ContainerName = "svc",
            WebhookToken = token,
        };
        _db.DockerWatches.Add(watch);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        return watch;
    }

    private async Task ClearAsync()
    {
        await _db.DockerWatches.ExecuteDeleteAsync();
        await _db.DockerConnections.ExecuteDeleteAsync();
        await _db.WebResources.ExecuteDeleteAsync();
        await _db.Users.ExecuteDeleteAsync();
    }

    private async Task EnsureSchemaAsync()
    {
        if (_schemaReady) return;
        await _schemaLock.WaitAsync();
        try
        {
            if (!_schemaReady)
            {
                await _db.Database.EnsureDeletedAsync();
                await _db.Database.EnsureCreatedAsync();
                _schemaReady = true;
            }
        }
        finally { _schemaLock.Release(); }
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


