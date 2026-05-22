using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Stashboard.Api.Data;
using Stashboard.Api.Mapping;
using Stashboard.Api.Notifications;
using Stashboard.Api.Services;
using Stashboard.Core.Abstractions;
using Stashboard.Core.Entities;
using Stashboard.Core.Enums;
using Stashboard.Core.Options;
using Stashboard.Infrastructure.Docker;
using Stashboard.Tests.Infrastructure;

namespace Stashboard.Tests.Services;

/// <summary>
/// Integration-style tests for <see cref="DockerUpdateBackgroundService"/>.
/// Real PostgreSQL test DB, mocked orchestrator (so we control digests
/// without hitting Docker/registries) and mocked email sender.
///
/// The DoD for Phase 5 in ROADMAP.md is "running the
/// background loop against a seeded DockerWatchEntity flips its UpdateStatus
/// correctly without manual intervention". <see cref="ScanOnce_DueWatch_UpdatesStatusAndStampsTimestamps"/>
/// is the test that proves it.
/// </summary>
public class DockerUpdateBackgroundServiceTests : IAsyncLifetime
{
    private const string DigestRunning = "sha256:111100000000000000000000000000000000000000000000000000000000aaaa";
    private const string DigestLatestV1 = "sha256:111100000000000000000000000000000000000000000000000000000000aaaa";
    private const string DigestLatestV2 = "sha256:222200000000000000000000000000000000000000000000000000000000bbbb";

    private static bool _schemaReady;
    private static readonly SemaphoreSlim _schemaLock = new(1, 1);

    private ApplicationDbContext _db = default!;
    private readonly Mock<IDockerUpdateChecker> _checkerMock = new();
    private readonly Mock<IEmailSender> _emailMock = new();
    private readonly Mock<ITelegramSender> _telegramMock = new();
    private readonly DockerWebhookCheckQueue _webhookQueue = new();

    public async Task InitializeAsync()
    {
        _db = CreateDbContext();
        await EnsureSchemaAsync();
        await ClearAllDataAsync();
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    // ── DoD test ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task ScanOnce_DueWatch_UpdatesStatusAndStampsTimestamps()
    {
        var user = await SeedUserAsync();
        var service = await SeedServiceAsync(user.Id);
        var watch = await SeedWatchAsync(service.Id, user.Id, lastCheckedUtc: null);

        var checkedAt = new DateTime(2026, 5, 16, 12, 0, 0, DateTimeKind.Utc);
        SetupCheck(new DockerCheckResult(
            DockerUpdateStatus.UpdateAvailable, DigestRunning, DigestLatestV2,
            "v1", "v2", null, checkedAt));

        var sut = BuildBackgroundService();

        await sut.ScanOnceAsync(CancellationToken.None);

        var dbRow = await ReloadWatchAsync(watch.Id);
        Assert.NotNull(dbRow);
        Assert.Equal(DockerUpdateStatus.UpdateAvailable, dbRow!.UpdateStatus);
        Assert.Equal(DigestRunning, dbRow.CurrentDigest);
        Assert.Equal(DigestLatestV2, dbRow.LatestDigest);
        Assert.Equal(checkedAt, dbRow.LastCheckedUtc);
        Assert.Equal(checkedAt, dbRow.LastUpdateDetectedUtc);
    }

    // ── Due-ness filter ──────────────────────────────────────────────────────

    [Fact]
    public async Task ScanOnce_WatchCheckedRecently_IsSkipped()
    {
        var user = await SeedUserAsync();
        var service = await SeedServiceAsync(user.Id);
        // Hourly every 6 h; LastCheckedUtc = 10 min ago -> NOT due.
        var watch = await SeedWatchAsync(service.Id, user.Id,
            lastCheckedUtc: DateTime.UtcNow.AddMinutes(-10),
            checkEveryHours: 6);

        var sut = BuildBackgroundService();
        await sut.ScanOnceAsync(CancellationToken.None);

        _checkerMock.Verify(c => c.CheckAsync(It.IsAny<DockerWatchProfile>(), It.IsAny<CancellationToken>()),
            Times.Never);
        var dbRow = await ReloadWatchAsync(watch.Id);
        // The skipped watch's LastCheckedUtc must NOT have been bumped to "now" by the scan.
        // (Postgres truncates DateTime to microseconds so we don't compare ticks exactly.)
        Assert.NotNull(dbRow!.LastCheckedUtc);
        Assert.True((DateTime.UtcNow - dbRow.LastCheckedUtc!.Value).TotalMinutes > 5,
            $"Expected LastCheckedUtc to remain ~10 min old, but it was {dbRow.LastCheckedUtc:o}");
    }

    [Fact]
    public async Task ScanOnce_DisabledWatch_IsSkipped()
    {
        var user = await SeedUserAsync();
        var service = await SeedServiceAsync(user.Id);
        await SeedWatchAsync(service.Id, user.Id, enabled: false, lastCheckedUtc: null);

        var sut = BuildBackgroundService();
        await sut.ScanOnceAsync(CancellationToken.None);

        _checkerMock.Verify(c => c.CheckAsync(It.IsAny<DockerWatchProfile>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ScanOnce_OnlyDueWatchesGetChecked_OthersUntouched()
    {
        var user = await SeedUserAsync();
        var svc1 = await SeedServiceAsync(user.Id, "Svc 1");
        var svc2 = await SeedServiceAsync(user.Id, "Svc 2");
        var dueWatch = await SeedWatchAsync(svc1.Id, user.Id, lastCheckedUtc: null, containerName: "due");
        var skippedWatch = await SeedWatchAsync(svc2.Id, user.Id,
            lastCheckedUtc: DateTime.UtcNow.AddMinutes(-5),
            checkEveryHours: 1,
            containerName: "skipped");

        SetupCheck(new DockerCheckResult(
            DockerUpdateStatus.UpToDate, DigestRunning, DigestLatestV1,
            "v1", "v1", null, DateTime.UtcNow));

        var sut = BuildBackgroundService();
        await sut.ScanOnceAsync(CancellationToken.None);

        // Orchestrator was called exactly once, against the due watch's container.
        _checkerMock.Verify(c => c.CheckAsync(
            It.Is<DockerWatchProfile>(p => p.ContainerName == "due"),
            It.IsAny<CancellationToken>()), Times.Once);
        _checkerMock.Verify(c => c.CheckAsync(
            It.Is<DockerWatchProfile>(p => p.ContainerName == "skipped"),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── V2.2 schedule modes ─────────────────────────────────────────────────

    [Fact]
    public async Task ScanOnce_DailySchedule_BeforeTargetTime_IsSkipped()
    {
        var user = await SeedUserAsync();
        var service = await SeedServiceAsync(user.Id);
        var now = DateTime.UtcNow;
        // Target time = 1 h in the future today; last check = 2 h ago. Most
        // recent occurrence of the target is yesterday, which is BEFORE the
        // last check → not due.
        var oneHourAhead = TimeOnly.FromDateTime(now.AddHours(1));
        await SeedWatchAsync(service.Id, user.Id,
            lastCheckedUtc: now.AddHours(-2),
            scheduleType: CheckScheduleType.Daily,
            checkAtTime: oneHourAhead);

        var sut = BuildBackgroundService();
        await sut.ScanOnceAsync(CancellationToken.None);

        _checkerMock.Verify(c => c.CheckAsync(It.IsAny<DockerWatchProfile>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ScanOnce_DailySchedule_AfterTargetTime_IsDue()
    {
        var user = await SeedUserAsync();
        var service = await SeedServiceAsync(user.Id);
        var now = DateTime.UtcNow;
        // Target time = 30 min in the past; last check = 2 days ago → due now.
        var thirtyMinAgo = TimeOnly.FromDateTime(now.AddMinutes(-30));
        await SeedWatchAsync(service.Id, user.Id,
            lastCheckedUtc: now.AddDays(-2),
            scheduleType: CheckScheduleType.Daily,
            checkAtTime: thirtyMinAgo);

        SetupCheck(new DockerCheckResult(
            DockerUpdateStatus.UpToDate, DigestRunning, DigestLatestV1,
            "v1", "v1", null, now));

        var sut = BuildBackgroundService();
        await sut.ScanOnceAsync(CancellationToken.None);

        _checkerMock.Verify(c => c.CheckAsync(It.IsAny<DockerWatchProfile>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ScanOnce_WeeklySchedule_WrongDay_IsSkipped()
    {
        var user = await SeedUserAsync();
        var service = await SeedServiceAsync(user.Id);
        var now = DateTime.UtcNow;
        // Target day-of-week is tomorrow; last check was 2 days ago. The most
        // recent occurrence of (tomorrow's day-of-week, 08:00) is 5 days back
        // — which IS after last check → due. Flip to "yesterday" to make it
        // not due: most recent occurrence is yesterday's date at the same time,
        // which is AFTER last check (also 2 days ago).
        // To get a deterministically-skipped weekly, set day-of-week to TODAY
        // but use a target time that's in the FUTURE today, with last check
        // being just an hour ago.
        var oneHourAhead = TimeOnly.FromDateTime(now.AddHours(1));
        await SeedWatchAsync(service.Id, user.Id,
            lastCheckedUtc: now.AddHours(-1),
            scheduleType: CheckScheduleType.Weekly,
            checkAtTime: oneHourAhead,
            checkOnDayOfWeek: now.DayOfWeek);

        var sut = BuildBackgroundService();
        await sut.ScanOnceAsync(CancellationToken.None);

        _checkerMock.Verify(c => c.CheckAsync(It.IsAny<DockerWatchProfile>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ScanOnce_WeeklySchedule_TargetDayJustPassed_IsDue()
    {
        var user = await SeedUserAsync();
        var service = await SeedServiceAsync(user.Id);
        var now = DateTime.UtcNow;
        // Today is the target day, target time = 30 min ago. Last check was
        // a week ago → due now.
        var thirtyMinAgo = TimeOnly.FromDateTime(now.AddMinutes(-30));
        await SeedWatchAsync(service.Id, user.Id,
            lastCheckedUtc: now.AddDays(-8),
            scheduleType: CheckScheduleType.Weekly,
            checkAtTime: thirtyMinAgo,
            checkOnDayOfWeek: now.DayOfWeek);

        SetupCheck(new DockerCheckResult(
            DockerUpdateStatus.UpToDate, DigestRunning, DigestLatestV1,
            "v1", "v1", null, now));

        var sut = BuildBackgroundService();
        await sut.ScanOnceAsync(CancellationToken.None);

        _checkerMock.Verify(c => c.CheckAsync(It.IsAny<DockerWatchProfile>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── Notification dispatch ────────────────────────────────────────────────

    [Fact]
    public async Task ScanOnce_NewLatestDigest_SendsEmailAndStampsThrottleKey()
    {
        var user = await SeedUserAsync("alice@example.com");
        var service = await SeedServiceAsync(user.Id, "Sonarr");
        var watch = await SeedWatchAsync(service.Id, user.Id, lastCheckedUtc: null);

        SetupCheck(new DockerCheckResult(
            DockerUpdateStatus.UpdateAvailable, DigestRunning, DigestLatestV2,
            "v1", "v2", null, DateTime.UtcNow));

        EmailMessage? captured = null;
        _emailMock.Setup(e => e.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()))
            .Callback<EmailMessage, CancellationToken>((m, _) => captured = m)
            .Returns(Task.CompletedTask);

        var sut = BuildBackgroundService();
        await sut.ScanOnceAsync(CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Equal("alice@example.com", captured!.To);
        Assert.Contains("Sonarr", captured.Subject);

        var dbRow = await ReloadWatchAsync(watch.Id);
        Assert.Equal(DigestLatestV2, dbRow!.LastNotifiedDigest);
        Assert.NotNull(dbRow.LastNotificationSentUtc);
    }

    [Fact]
    public async Task ScanOnce_SameLatestDigestAcrossTwoScans_OnlyEmailsOnce()
    {
        var user = await SeedUserAsync();
        var service = await SeedServiceAsync(user.Id);
        await SeedWatchAsync(service.Id, user.Id, lastCheckedUtc: null, checkEveryHours: 1);

        SetupCheck(new DockerCheckResult(
            DockerUpdateStatus.UpdateAvailable, DigestRunning, DigestLatestV2,
            "v1", "v2", null, DateTime.UtcNow));

        _emailMock.Setup(e => e.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var sut = BuildBackgroundService();

        // First scan -> due, sends email.
        await sut.ScanOnceAsync(CancellationToken.None);
        // Bump LastCheckedUtc far enough in the past for the second pass to qualify
        // again. (Simulates the user's CheckEveryHours window elapsing.)
        await MakeWatchDueAgainAsync(service.Id);
        // Second scan -> same LatestDigest -> no email.
        await sut.ScanOnceAsync(CancellationToken.None);

        _emailMock.Verify(e => e.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ScanOnce_ErrorStatus_DoesNotSendEmail()
    {
        var user = await SeedUserAsync();
        var service = await SeedServiceAsync(user.Id);
        await SeedWatchAsync(service.Id, user.Id, lastCheckedUtc: null);

        SetupCheck(new DockerCheckResult(
            DockerUpdateStatus.Error, null, null, "v1", null, "registry unreachable", DateTime.UtcNow));

        var sut = BuildBackgroundService();
        await sut.ScanOnceAsync(CancellationToken.None);

        _emailMock.Verify(e => e.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── Resilience ───────────────────────────────────────────────────────────

    [Fact]
    public async Task ScanOnce_OneWatchThrows_OthersStillGetProcessed()
    {
        var user = await SeedUserAsync();
        var svcA = await SeedServiceAsync(user.Id, "A");
        var svcB = await SeedServiceAsync(user.Id, "B");
        var watchA = await SeedWatchAsync(svcA.Id, user.Id, lastCheckedUtc: null, containerName: "boom");
        var watchB = await SeedWatchAsync(svcB.Id, user.Id, lastCheckedUtc: null, containerName: "ok");

        _checkerMock.Setup(c => c.CheckAsync(
                It.Is<DockerWatchProfile>(p => p.ContainerName == "boom"),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("kaboom"));
        _checkerMock.Setup(c => c.CheckAsync(
                It.Is<DockerWatchProfile>(p => p.ContainerName == "ok"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DockerCheckResult(
                DockerUpdateStatus.UpToDate, DigestRunning, DigestLatestV1,
                "v1", "v1", null, DateTime.UtcNow));

        var sut = BuildBackgroundService();
        await sut.ScanOnceAsync(CancellationToken.None);

        var rowA = await ReloadWatchAsync(watchA.Id);
        var rowB = await ReloadWatchAsync(watchB.Id);

        Assert.Equal(DockerUpdateStatus.Error, rowA!.UpdateStatus);
        Assert.Contains("kaboom", rowA.LastError);
        Assert.Equal(DockerUpdateStatus.UpToDate, rowB!.UpdateStatus);
    }

    [Fact]
    public async Task ScanOnce_NoDueWatches_NoDbWritesNoOrchestratorCalls()
    {
        var user = await SeedUserAsync();
        var service = await SeedServiceAsync(user.Id);
        await SeedWatchAsync(service.Id, user.Id,
            lastCheckedUtc: DateTime.UtcNow.AddMinutes(-1),
            checkEveryHours: 6);

        var sut = BuildBackgroundService();
        await sut.ScanOnceAsync(CancellationToken.None);

        _checkerMock.Verify(c => c.CheckAsync(It.IsAny<DockerWatchProfile>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _emailMock.Verify(e => e.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── V2.6 webhook drain ──────────────────────────────────────────────────

    [Fact]
    public async Task DrainWebhookQueue_QueuedWatch_RunsCheckImmediatelyBypassingSchedule()
    {
        var user = await SeedUserAsync();
        var service = await SeedServiceAsync(user.Id);
        // Schedule is not due — last check 1 min ago, every 24 h — but the
        // webhook drain must still pick it up.
        var watch = await SeedWatchAsync(service.Id, user.Id,
            lastCheckedUtc: DateTime.UtcNow.AddMinutes(-1),
            checkEveryHours: 24);

        SetupCheck(new DockerCheckResult(
            DockerUpdateStatus.UpdateAvailable, DigestRunning, DigestLatestV2,
            "v1", "v2", null, DateTime.UtcNow));

        Assert.True(_webhookQueue.TryEnqueue(watch.Id));

        var sut = BuildBackgroundService();
        await sut.DrainWebhookQueueOnceAsync(CancellationToken.None);

        _checkerMock.Verify(c => c.CheckAsync(
            It.IsAny<DockerWatchProfile>(), It.IsAny<CancellationToken>()), Times.Once);
        var dbRow = await ReloadWatchAsync(watch.Id);
        Assert.Equal(DockerUpdateStatus.UpdateAvailable, dbRow!.UpdateStatus);
        Assert.Equal(DigestLatestV2, dbRow.LatestDigest);
    }

    [Fact]
    public async Task DrainWebhookQueue_DisabledWatch_IsSkippedSilently()
    {
        var user = await SeedUserAsync();
        var service = await SeedServiceAsync(user.Id);
        var watch = await SeedWatchAsync(service.Id, user.Id,
            lastCheckedUtc: null, enabled: false);

        Assert.True(_webhookQueue.TryEnqueue(watch.Id));

        var sut = BuildBackgroundService();
        await sut.DrainWebhookQueueOnceAsync(CancellationToken.None);

        _checkerMock.Verify(c => c.CheckAsync(
            It.IsAny<DockerWatchProfile>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DrainWebhookQueue_NoQueuedIds_DoesNothing()
    {
        var sut = BuildBackgroundService();

        // Should be a no-op — no DB query, no orchestrator call.
        await sut.DrainWebhookQueueOnceAsync(CancellationToken.None);

        _checkerMock.Verify(c => c.CheckAsync(
            It.IsAny<DockerWatchProfile>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private void SetupCheck(DockerCheckResult result)
    {
        _checkerMock.Setup(c => c.CheckAsync(It.IsAny<DockerWatchProfile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);
    }

    private DockerUpdateBackgroundService BuildBackgroundService()
    {
        var services = new ServiceCollection();
        services.AddDbContext<ApplicationDbContext>(opts => opts.UseSqlite(BuildTestConnectionString()));

        // Stash mocked orchestrator + email sender so the scan picks them up.
        services.AddSingleton(_checkerMock.Object);
        services.AddSingleton(_emailMock.Object);
        services.AddSingleton(_telegramMock.Object);

        // Real mapper (depends on encryption + parser).
        var encryption = new Mock<IEncryptionService>();
        encryption.Setup(e => e.Encrypt(It.IsAny<string>())).Returns<string>(v => $"enc:{v}");
        encryption.Setup(e => e.Decrypt(It.IsAny<string>()))
            .Returns<string>(v => v.StartsWith("enc:") ? v[4..] : v);
        services.AddSingleton(encryption.Object);
        services.AddSingleton<IImageReferenceParser, ImageReferenceParser>();
        services.AddScoped<IDockerWatchMapper, DockerWatchMapper>();

        // Real notification service (depends on the mocked IEmailSender).
        services.AddScoped<IDockerUpdateNotificationService, DockerUpdateNotificationService>();
        services.AddLogging();

        var sp = services.BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
        var monitor = new TestOptionsMonitor(new DockerUpdateOptions { TickIntervalSeconds = 300 });
        return new DockerUpdateBackgroundService(scopeFactory, monitor, _webhookQueue,
            NullLogger<DockerUpdateBackgroundService>.Instance);
    }

    private async Task MakeWatchDueAgainAsync(Guid serviceId)
    {
        var watch = await _db.DockerWatches.AsTracking().SingleAsync(w => w.WebResourceId == serviceId);
        watch.LastCheckedUtc = DateTime.UtcNow.AddHours(-24);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
    }

    private async Task<DockerWatchEntity> SeedWatchAsync(
        Guid serviceId, Guid userId,
        DateTime? lastCheckedUtc,
        int checkEveryHours = 24,
        bool enabled = true,
        string containerName = "svc",
        string label = "app",
        CheckScheduleType scheduleType = CheckScheduleType.Hourly,
        TimeOnly? checkAtTime = null,
        DayOfWeek? checkOnDayOfWeek = null)
    {
        // Each watch is owned by a DockerConnection — the background scan reads
        // the host transport straight off it. V3.6: the watch stores the
        // connection id directly.
        var connectionId = await EnsureConnectionAsync(serviceId, userId);

        var watch = new DockerWatchEntity
        {
            Id = Guid.NewGuid(),
            DockerConnectionId = connectionId,
            WebResourceId = serviceId,
            UserId = userId,
            Label = label,
            Enabled = enabled,
            ImageReference = "ghcr.io/owner/repo:v1",
            RegistryHost = "ghcr.io",
            Repository = "owner/repo",
            Tag = "v1",
            ContainerName = containerName,
            UpdateNotificationsEnabled = true,
            ScheduleType = scheduleType,
            CheckEveryHours = checkEveryHours,
            CheckAtTime = checkAtTime,
            CheckOnDayOfWeek = checkOnDayOfWeek,
            LastCheckedUtc = lastCheckedUtc,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow,
        };
        _db.DockerWatches.Add(watch);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        return watch;
    }

    private async Task<Guid> EnsureConnectionAsync(Guid serviceId, Guid userId)
    {
        var assignedId = await _db.WebResources.AsNoTracking()
            .Where(s => s.Id == serviceId)
            .Select(s => s.DockerConnectionId)
            .FirstOrDefaultAsync();
        if (assignedId is not null) return assignedId.Value;

        var connection = new DockerConnectionEntity
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Name = $"test-conn-{serviceId:N}",
            HostType = DockerHostType.LocalSocket,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow,
        };
        _db.DockerConnections.Add(connection);
        await _db.SaveChangesAsync();
        await _db.WebResources
            .Where(s => s.Id == serviceId)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.DockerConnectionId, connection.Id));
        _db.ChangeTracker.Clear();
        return connection.Id;
    }

    private async Task<UserEntity> SeedUserAsync(string email = "owner@example.com")
    {
        var user = new UserEntity
        {
            Id = Guid.NewGuid(),
            Email = email,
            NormalizedEmail = $"{email.ToUpperInvariant()}-{Guid.NewGuid():N}",
            PasswordHash = "x",
            SecurityStamp = Guid.NewGuid().ToString("N"),
            CreatedUtc = DateTime.UtcNow,
        };
        _db.Users.Add(user);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        return user;
    }

    private async Task<WebResourceEntity> SeedServiceAsync(Guid userId, string name = "Service")
    {
        var service = new WebResourceEntity
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Name = name,
            MainUrl = "https://example.com",
        };
        _db.WebResources.Add(service);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        return service;
    }

    private Task<DockerWatchEntity?> ReloadWatchAsync(Guid id) =>
        _db.DockerWatches.AsNoTracking().FirstOrDefaultAsync(w => w.Id == id);

    // ── DB plumbing ──────────────────────────────────────────────────────────

    private static ApplicationDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(BuildTestConnectionString())
            .Options;
        return new ApplicationDbContext(options);
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

    private async Task ClearAllDataAsync()
    {
        await _db.DockerWatches.ExecuteDeleteAsync();
        await _db.DockerConnections.ExecuteDeleteAsync();
        await _db.WebResourceTags.ExecuteDeleteAsync();
        await _db.Credentials.ExecuteDeleteAsync();
        await _db.WebResources.ExecuteDeleteAsync();
        await _db.Tags.ExecuteDeleteAsync();
        await _db.Categories.ExecuteDeleteAsync();
        await _db.Users.ExecuteDeleteAsync();
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

    private sealed class TestOptionsMonitor(DockerUpdateOptions value) : IOptionsMonitor<DockerUpdateOptions>
    {
        public DockerUpdateOptions CurrentValue { get; } = value;
        public DockerUpdateOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<DockerUpdateOptions, string?> listener) => null;
    }
}
