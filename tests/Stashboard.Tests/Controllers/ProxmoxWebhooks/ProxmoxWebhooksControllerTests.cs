using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Stashboard.Api.Controllers;
using Stashboard.Api.Data;
using Stashboard.Core.Entities;
using Stashboard.Infrastructure.Proxmox;

namespace Stashboard.Tests.Controllers.ProxmoxWebhooks;

/// <summary>
/// V6.11 — integration tests for the public Proxmox update-check webhook
/// receiver. Real SQLite DB so the lookup goes through the unique index on
/// <c>ProxmoxConnections.WebhookToken</c>. Mirrors
/// <see cref="Stashboard.Tests.Controllers.DockerWebhooks"/>.
/// </summary>
public class ProxmoxWebhooksControllerTests : IAsyncLifetime
{
    private const string ValidToken = "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";

    private static bool _schemaReady;
    private static readonly SemaphoreSlim _schemaLock = new(1, 1);

    private readonly ApplicationDbContext _db;
    private readonly ProxmoxScanQueue _queue = new();

    public ProxmoxWebhooksControllerTests()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(BuildTestConnectionString())
            .Options;
        _db = new ApplicationDbContext(options);
    }

    public async Task InitializeAsync()
    {
        await EnsureSchemaAsync();
        await ClearAsync();
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    [Fact]
    public async Task Receive_ValidToken_AcceptedEnqueuedAndStampsReceivedAt()
    {
        var conn = await SeedConnectionAsync(token: ValidToken, enabled: true);
        var ctrl = BuildController();

        var result = await ctrl.Receive(ValidToken, CancellationToken.None);

        Assert.IsType<AcceptedResult>(result);
        Assert.Single(_queue.DrainAll(), conn.Id);
        var row = await _db.ProxmoxConnections.AsNoTracking().SingleAsync(c => c.Id == conn.Id);
        Assert.NotNull(row.LastWebhookReceivedUtc);
    }

    [Fact]
    public async Task Receive_UnknownToken_ReturnsNotFoundAndQueueStaysEmpty()
    {
        await SeedConnectionAsync(token: ValidToken);
        var bogus = new string('1', 64);
        var ctrl = BuildController();

        var result = await ctrl.Receive(bogus, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
        Assert.Empty(_queue.DrainAll());
    }

    [Theory]
    [InlineData("")]
    [InlineData("too-short")]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")] // 64 chars, non-hex
    public async Task Receive_MalformedTokenShape_ReturnsNotFoundBeforeDatabaseLookup(string token)
    {
        await SeedConnectionAsync(token: ValidToken);
        var ctrl = BuildController();

        var result = await ctrl.Receive(token, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
        Assert.Empty(_queue.DrainAll());
    }

    [Fact]
    public async Task Receive_DisabledHost_StampsButDoesNotEnqueue()
    {
        var conn = await SeedConnectionAsync(token: ValidToken, enabled: false);
        var ctrl = BuildController();

        var result = await ctrl.Receive(ValidToken, CancellationToken.None);

        Assert.IsType<AcceptedResult>(result);
        Assert.Empty(_queue.DrainAll());
        var row = await _db.ProxmoxConnections.AsNoTracking().SingleAsync(c => c.Id == conn.Id);
        Assert.NotNull(row.LastWebhookReceivedUtc);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private ProxmoxWebhooksController BuildController()
    {
        var ctrl = new ProxmoxWebhooksController(_db, _queue, NullLogger<ProxmoxWebhooksController>.Instance);
        ctrl.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        return ctrl;
    }

    private async Task<ProxmoxConnectionEntity> SeedConnectionAsync(string token, bool enabled = true)
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

        var conn = new ProxmoxConnectionEntity
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            Name = $"pve-{Guid.NewGuid():N}",
            ApiBaseUrl = "https://pve.lan:8006",
            NodeName = "pve",
            ApiTokenId = "root@pam!stash",
            ApiTokenSecretEncrypted = "enc:secret",
            Enabled = enabled,
            WebhookToken = token,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow,
        };
        _db.ProxmoxConnections.Add(conn);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        return conn;
    }

    private async Task ClearAsync()
    {
        await _db.ProxmoxConnections.ExecuteDeleteAsync();
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

    private static string BuildTestConnectionString() =>
        $"Data Source={Path.Combine(Path.GetTempPath(), "stashboard-tests-proxmox-webhooks.db")};Pooling=False";
}
