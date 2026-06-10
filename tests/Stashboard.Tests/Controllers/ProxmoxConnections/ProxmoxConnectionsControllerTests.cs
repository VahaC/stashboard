using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Stashboard.Api.Auth;
using Stashboard.Api.Contracts;
using Stashboard.Api.Controllers;
using Stashboard.Api.Data;
using Stashboard.Api.Mapping;
using Stashboard.Api.Services.Proxmox;
using Stashboard.Core.Abstractions;
using Stashboard.Core.Entities;
using Stashboard.Core.Enums;

namespace Stashboard.Tests.Controllers.ProxmoxConnections;

/// <summary>
/// V6.7 — tests for the per-LXC monitoring toggle + per-guest "Check now"
/// endpoints on <see cref="ProxmoxConnectionsController"/>. Real SQLite DB;
/// the checker, scan service and API client are mocked since these endpoints
/// only exercise ownership, the toggle persistence, and the enabled/disabled
/// short-circuit — not a live Proxmox host.
/// </summary>
public class ProxmoxConnectionsControllerTests : IAsyncLifetime
{
    private static bool _schemaReady;
    private static readonly SemaphoreSlim _schemaLock = new(1, 1);

    private readonly ApplicationDbContext _db;
    private readonly Mock<IEncryptionService> _encryptionMock = new();
    private readonly Mock<IProxmoxUpdateChecker> _checkerMock = new();
    private readonly Mock<IProxmoxScanService> _scanMock = new();
    private readonly Mock<IProxmoxApiClient> _apiMock = new();
    private readonly Mock<IProxmoxGuestInspector> _inspectorMock = new();
    private readonly Mock<IProxmoxUpdateApplier> _applierMock = new();
    private readonly Mock<IProxmoxUpdateApplySettingsService> _updateSettingsMock = new();
    private readonly Mock<IProxmoxDestroySettingsService> _destroySettingsMock = new();
    private readonly Mock<IProxmoxCreateSettingsService> _createSettingsMock = new();
    private readonly Mock<Stashboard.Api.Services.Proxmox.IProxmoxNodeAlertService> _alertServiceMock = new();
    private readonly Mock<IDockerWebhookTokenGenerator> _webhookTokenGeneratorMock = new();
    private Guid _userId;
    private Guid _otherUserId;

    public ProxmoxConnectionsControllerTests()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(BuildTestConnectionString())
            .Options;
        _db = new ApplicationDbContext(options);
        _encryptionMock.Setup(e => e.Encrypt(It.IsAny<string>())).Returns<string>(v => $"enc:{v}");
        _encryptionMock.Setup(e => e.Decrypt(It.IsAny<string>()))
            .Returns<string>(v => v.StartsWith("enc:") ? v[4..] : v);
        // A deterministic 64-char hex token so the rotate endpoint persists a
        // valid, well-formed value (the unique index + receiver expect that shape).
        _webhookTokenGeneratorMock.Setup(g => g.Generate())
            .Returns(new string('a', 64));
    }

    public async Task InitializeAsync()
    {
        await EnsureSchemaAsync();
        await ClearAllDataAsync();
        _userId = (await SeedUserAsync("owner@example.com")).Id;
        _otherUserId = (await SeedUserAsync("other@example.com")).Id;
    }

    public Task DisposeAsync() => _db.DisposeAsync().AsTask();

    // ── monitoring toggle ─────────────────────────────────────────────────────

    [Fact]
    public async Task SetLxcMonitoring_PersistsFlag_AndClearsPendingWhenDisabled()
    {
        var conn = await SeedConnectionAsync(_userId);
        await SeedGuestAsync(conn.Id, vmId: 105, pendingUpdates: 7, monitoringEnabled: true);
        var ctrl = BuildController();

        var result = await ctrl.SetLxcMonitoring(
            conn.Id, 105, new ProxmoxGuestMonitoringRequest(false), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<ProxmoxConnectionResponse>(ok.Value);
        var card = response.Guests.Single(g => g.VmId == 105);
        Assert.False(card.MonitoringEnabled);
        Assert.Null(card.PendingUpdates);

        var row = await _db.ProxmoxGuests.AsNoTracking()
            .SingleAsync(g => g.ProxmoxConnectionId == conn.Id && g.VmId == 105);
        Assert.False(row.MonitoringEnabled);
        Assert.Null(row.PendingUpdates);
    }

    [Fact]
    public async Task SetLxcMonitoring_ReEnable_PersistsTrue()
    {
        var conn = await SeedConnectionAsync(_userId);
        await SeedGuestAsync(conn.Id, vmId: 105, pendingUpdates: null, monitoringEnabled: false);
        var ctrl = BuildController();

        await ctrl.SetLxcMonitoring(conn.Id, 105, new ProxmoxGuestMonitoringRequest(true), CancellationToken.None);

        var row = await _db.ProxmoxGuests.AsNoTracking()
            .SingleAsync(g => g.ProxmoxConnectionId == conn.Id && g.VmId == 105);
        Assert.True(row.MonitoringEnabled);
    }

    [Fact]
    public async Task SetLxcMonitoring_NodeRow_ReturnsBadRequest()
    {
        var conn = await SeedConnectionAsync(_userId);
        var ctrl = BuildController();

        var result = await ctrl.SetLxcMonitoring(
            conn.Id, 0, new ProxmoxGuestMonitoringRequest(false), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task SetLxcMonitoring_ForeignHost_ReturnsNotFound()
    {
        var conn = await SeedConnectionAsync(_otherUserId);
        await SeedGuestAsync(conn.Id, vmId: 105, pendingUpdates: 1, monitoringEnabled: true);
        var ctrl = BuildController();

        var result = await ctrl.SetLxcMonitoring(
            conn.Id, 105, new ProxmoxGuestMonitoringRequest(false), CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task SetLxcMonitoring_UnknownGuest_ReturnsNotFound()
    {
        var conn = await SeedConnectionAsync(_userId);
        var ctrl = BuildController();

        var result = await ctrl.SetLxcMonitoring(
            conn.Id, 999, new ProxmoxGuestMonitoringRequest(false), CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    // ── per-guest check ───────────────────────────────────────────────────────

    [Fact]
    public async Task CheckLxcNow_DisabledGuest_ReturnsDisabledWithoutScanning()
    {
        var conn = await SeedConnectionAsync(_userId);
        await SeedGuestAsync(conn.Id, vmId: 105, pendingUpdates: null, monitoringEnabled: false);
        var ctrl = BuildController();

        var result = await ctrl.CheckLxcNow(conn.Id, 105, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<ProxmoxConnectionResponse>(ok.Value);
        var card = response.Guests.Single(g => g.VmId == 105);
        Assert.False(card.MonitoringEnabled);
        Assert.NotNull(card.LastCheckedUtc);

        // A disabled guest must never trigger an actual host scan.
        _scanMock.Verify(s => s.CheckConnectionAsync(
            It.IsAny<ProxmoxConnectionEntity>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CheckLxcNow_EnabledGuest_RunsHostScan()
    {
        var conn = await SeedConnectionAsync(_userId);
        await SeedGuestAsync(conn.Id, vmId: 105, pendingUpdates: 0, monitoringEnabled: true);
        var ctrl = BuildController();

        var result = await ctrl.CheckLxcNow(conn.Id, 105, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        _scanMock.Verify(s => s.CheckConnectionAsync(
            It.Is<ProxmoxConnectionEntity>(c => c.Id == conn.Id), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── V6.9 — structured config edit validation ────────────────────────────────

    [Fact]
    public async Task UpdateLxcConfig_RootfsRemoval_Returns400_WithoutTouchingHost()
    {
        var conn = await SeedConnectionAsync(_userId);
        var ctrl = BuildController();

        var result = await ctrl.UpdateLxcConfig(
            conn.Id, 101,
            new ProxmoxLxcConfigUpdateRequest(Mounts: [new ProxmoxLxcMountChange("rootfs", Remove: true)]),
            CancellationToken.None);

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("rootfs cannot be removed", bad.Value!.ToString());
        _apiMock.Verify(a => a.UpdateLxcConfigAsync(
            It.IsAny<ProxmoxConnectionProfile>(), It.IsAny<int>(),
            It.IsAny<ProxmoxLxcConfigUpdate>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UpdateLxcConfig_BadCidr_Returns400()
    {
        var conn = await SeedConnectionAsync(_userId);
        var ctrl = BuildController();

        var result = await ctrl.UpdateLxcConfig(
            conn.Id, 101,
            new ProxmoxLxcConfigUpdateRequest(Networks: [new ProxmoxLxcNetChange("net0", Ip: "192.168.1.5")]),
            CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task UpdateLxcConfig_ValidStructured_PassesUpdateThrough_Returns204()
    {
        var conn = await SeedConnectionAsync(_userId);
        var ctrl = BuildController();

        var result = await ctrl.UpdateLxcConfig(
            conn.Id, 101,
            new ProxmoxLxcConfigUpdateRequest(
                Networks: [new ProxmoxLxcNetChange("net0", Name: "eth0", Bridge: "vmbr0", Ip: "dhcp")]),
            CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        _apiMock.Verify(a => a.UpdateLxcConfigAsync(
            It.IsAny<ProxmoxConnectionProfile>(), 101,
            It.Is<ProxmoxLxcConfigUpdate>(u => u.Networks != null && u.Networks.Count == 1),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UpdateLxcConfig_ForeignHost_ReturnsNotFound()
    {
        var conn = await SeedConnectionAsync(_otherUserId);
        var ctrl = BuildController();

        var result = await ctrl.UpdateLxcConfig(
            conn.Id, 101, new ProxmoxLxcConfigUpdateRequest(Cores: 2), CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
    }

    // ── V6.7.1 apply updates (triple gate + stream + audit) ────────────────────

    [Fact]
    public async Task ApplyLxcUpdates_GlobalDisabled_Returns403()
    {
        _updateSettingsMock.Setup(s => s.IsEnabledAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);
        var conn = await SeedConnectionAsync(_userId, allowUpdates: true, withSsh: true);
        var ctrl = BuildController();

        await ctrl.ApplyLxcUpdates(conn.Id, 105, CancellationToken.None);

        Assert.Equal(StatusCodes.Status403Forbidden, ctrl.Response.StatusCode);
        _applierMock.Verify(a => a.ApplyAsync(
            It.IsAny<ProxmoxConnectionProfile>(), It.IsAny<int>(),
            It.IsAny<Func<string, CancellationToken, Task>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ApplyLxcUpdates_PerHostDisabled_Returns403()
    {
        _updateSettingsMock.Setup(s => s.IsEnabledAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var conn = await SeedConnectionAsync(_userId, allowUpdates: false, withSsh: true);
        var ctrl = BuildController();

        await ctrl.ApplyLxcUpdates(conn.Id, 105, CancellationToken.None);

        Assert.Equal(StatusCodes.Status403Forbidden, ctrl.Response.StatusCode);
    }

    [Fact]
    public async Task ApplyLxcUpdates_NoSsh_Returns409()
    {
        _updateSettingsMock.Setup(s => s.IsEnabledAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var conn = await SeedConnectionAsync(_userId, allowUpdates: true, withSsh: false);
        var ctrl = BuildController();

        await ctrl.ApplyLxcUpdates(conn.Id, 105, CancellationToken.None);

        Assert.Equal(StatusCodes.Status409Conflict, ctrl.Response.StatusCode);
    }

    [Fact]
    public async Task ApplyNodeUpdates_ForeignHost_Returns404()
    {
        _updateSettingsMock.Setup(s => s.IsEnabledAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var conn = await SeedConnectionAsync(_otherUserId, allowUpdates: true, withSsh: true);
        var ctrl = BuildController();

        await ctrl.ApplyNodeUpdates(conn.Id, CancellationToken.None);

        Assert.Equal(StatusCodes.Status404NotFound, ctrl.Response.StatusCode);
    }

    [Fact]
    public async Task ApplyLxcUpdates_AllGatesPass_StreamsOutputAndWritesAuditRow()
    {
        _updateSettingsMock.Setup(s => s.IsEnabledAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var conn = await SeedConnectionAsync(_userId, allowUpdates: true, withSsh: true);
        await SeedGuestAsync(conn.Id, vmId: 105, pendingUpdates: 4, monitoringEnabled: true);

        // The applier streams two lines, then reports a clean exit.
        _applierMock.Setup(a => a.ApplyAsync(
                It.IsAny<ProxmoxConnectionProfile>(), 105,
                It.IsAny<Func<string, CancellationToken, Task>>(), It.IsAny<CancellationToken>()))
            .Returns(async (ProxmoxConnectionProfile _, int _, Func<string, CancellationToken, Task> onLine, CancellationToken ct) =>
            {
                await onLine("Reading package lists...", ct);
                await onLine("0 upgraded, 0 newly installed.", ct);
                return new ProxmoxUpdateApplyResult(0, 48, NonDebian: false, Error: null);
            });
        // After a successful apply the controller re-reads the count; the guest
        // is now up to date.
        _inspectorMock.Setup(i => i.CountPendingUpdatesAsync(
                It.IsAny<ProxmoxConnectionProfile>(), 105, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProxmoxGuestInspection(0, null));

        var ctrl = BuildController();

        await ctrl.ApplyLxcUpdates(conn.Id, 105, CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, ctrl.Response.StatusCode);
        Assert.Equal("application/x-ndjson", ctrl.Response.ContentType);

        ctrl.Response.Body.Position = 0;
        var body = await new StreamReader(ctrl.Response.Body).ReadToEndAsync();
        Assert.Contains("Reading package lists", body);
        Assert.Contains("\"stream\":\"end\"", body);
        Assert.Contains("\"success\":true", body);

        // The guest's pending count was recomputed + persisted to 0, so the
        // dashboard badge clears without a manual Check now.
        var guest = await _db.ProxmoxGuests.AsNoTracking()
            .SingleAsync(g => g.ProxmoxConnectionId == conn.Id && g.VmId == 105);
        Assert.Equal(0, guest.PendingUpdates);
        Assert.NotNull(guest.LastCheckedUtc);

        // A finalised audit row exists for this run.
        var row = await _db.ProxmoxUpdateSessions.AsNoTracking()
            .SingleAsync(r => r.ProxmoxConnectionId == conn.Id);
        Assert.Equal(105, row.VmId);
        Assert.Equal(ProxmoxGuestType.Lxc, row.TargetType);
        Assert.Equal(0, row.ExitStatus);
        Assert.NotNull(row.EndedUtc);
        Assert.Equal(_userId, row.InitiatedByUserId);
    }

    // ── V6.11 — monitoring audit on single toggle ──────────────────────────────

    [Fact]
    public async Task SetLxcMonitoring_WritesAuditRow_OnChange()
    {
        var conn = await SeedConnectionAsync(_userId);
        await SeedGuestAsync(conn.Id, vmId: 105, pendingUpdates: 3, monitoringEnabled: true);
        var ctrl = BuildController();

        await ctrl.SetLxcMonitoring(conn.Id, 105, new ProxmoxGuestMonitoringRequest(false), CancellationToken.None);

        var row = await _db.ProxmoxMonitoringAudits.AsNoTracking()
            .SingleAsync(r => r.ProxmoxConnectionId == conn.Id && r.VmId == 105);
        Assert.Equal(ProxmoxMonitoringChangeType.Disabled, row.ChangeType);
        Assert.False(row.MonitoringEnabled);
        Assert.False(row.Bulk);
        Assert.Equal(_userId, row.InitiatedByUserId);
        Assert.Equal("ct-105", row.GuestName);
    }

    [Fact]
    public async Task SetLxcMonitoring_SameState_WritesNoAuditRow()
    {
        var conn = await SeedConnectionAsync(_userId);
        await SeedGuestAsync(conn.Id, vmId: 105, pendingUpdates: 3, monitoringEnabled: true);
        var ctrl = BuildController();

        await ctrl.SetLxcMonitoring(conn.Id, 105, new ProxmoxGuestMonitoringRequest(true), CancellationToken.None);

        Assert.Empty(await _db.ProxmoxMonitoringAudits.AsNoTracking().ToListAsync());
    }

    // ── V6.11 — bulk monitoring toggle ─────────────────────────────────────────

    [Fact]
    public async Task SetBulkLxcMonitoring_DisablesAllLxcs_PersistsAndAudits()
    {
        var conn = await SeedConnectionAsync(_userId);
        await SeedGuestAsync(conn.Id, vmId: 0, pendingUpdates: 2, monitoringEnabled: true, guestType: ProxmoxGuestType.Node);
        await SeedGuestAsync(conn.Id, vmId: 101, pendingUpdates: 1, monitoringEnabled: true);
        await SeedGuestAsync(conn.Id, vmId: 102, pendingUpdates: 4, monitoringEnabled: true);
        var ctrl = BuildController();

        var result = await ctrl.SetBulkLxcMonitoring(conn.Id, new ProxmoxBulkMonitoringRequest(false), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.IsType<ProxmoxConnectionResponse>(ok.Value);

        var lxcs = await _db.ProxmoxGuests.AsNoTracking()
            .Where(g => g.ProxmoxConnectionId == conn.Id && g.GuestType == ProxmoxGuestType.Lxc)
            .ToListAsync();
        Assert.All(lxcs, g => Assert.False(g.MonitoringEnabled));
        Assert.All(lxcs, g => Assert.Null(g.PendingUpdates));

        // The node row is host-controlled — never touched by the bulk toggle.
        var node = await _db.ProxmoxGuests.AsNoTracking()
            .SingleAsync(g => g.ProxmoxConnectionId == conn.Id && g.VmId == 0);
        Assert.True(node.MonitoringEnabled);

        // One bulk audit row per changed LXC (and none for the node).
        var rows = await _db.ProxmoxMonitoringAudits.AsNoTracking().ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.True(r.Bulk));
        Assert.All(rows, r => Assert.Equal(ProxmoxMonitoringChangeType.Disabled, r.ChangeType));
        Assert.DoesNotContain(rows, r => r.VmId == 0);
    }

    [Fact]
    public async Task SetBulkLxcMonitoring_OnlyAuditsActuallyChangedGuests()
    {
        var conn = await SeedConnectionAsync(_userId);
        await SeedGuestAsync(conn.Id, vmId: 101, pendingUpdates: 1, monitoringEnabled: false); // already off
        await SeedGuestAsync(conn.Id, vmId: 102, pendingUpdates: 4, monitoringEnabled: true);
        var ctrl = BuildController();

        await ctrl.SetBulkLxcMonitoring(conn.Id, new ProxmoxBulkMonitoringRequest(false), CancellationToken.None);

        var rows = await _db.ProxmoxMonitoringAudits.AsNoTracking().ToListAsync();
        var row = Assert.Single(rows);
        Assert.Equal(102, row.VmId);
    }

    [Fact]
    public async Task SetBulkLxcMonitoring_ForeignHost_ReturnsNotFound()
    {
        var conn = await SeedConnectionAsync(_otherUserId);
        var ctrl = BuildController();

        var result = await ctrl.SetBulkLxcMonitoring(conn.Id, new ProxmoxBulkMonitoringRequest(false), CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    // ── V6.11 — snooze ─────────────────────────────────────────────────────────

    [Fact]
    public async Task SnoozeLxcMonitoring_SetsField_ClearsPending_AndAudits()
    {
        var conn = await SeedConnectionAsync(_userId);
        await SeedGuestAsync(conn.Id, vmId: 105, pendingUpdates: 9, monitoringEnabled: true);
        var ctrl = BuildController();
        var until = DateTime.UtcNow.AddHours(3);

        var result = await ctrl.SnoozeLxcMonitoring(conn.Id, 105, new ProxmoxGuestSnoozeRequest(until), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        var guest = await _db.ProxmoxGuests.AsNoTracking()
            .SingleAsync(g => g.ProxmoxConnectionId == conn.Id && g.VmId == 105);
        Assert.NotNull(guest.MonitoringSnoozedUntil);
        Assert.Null(guest.PendingUpdates);
        // Monitoring stays on — snooze is independent of the enable flag.
        Assert.True(guest.MonitoringEnabled);

        var row = await _db.ProxmoxMonitoringAudits.AsNoTracking().SingleAsync();
        Assert.Equal(ProxmoxMonitoringChangeType.Snoozed, row.ChangeType);
        Assert.NotNull(row.SnoozedUntil);
    }

    [Fact]
    public async Task SnoozeLxcMonitoring_NullUntil_ClearsSnooze_AndAuditsCleared()
    {
        var conn = await SeedConnectionAsync(_userId);
        await SeedGuestAsync(conn.Id, vmId: 105, pendingUpdates: null, monitoringEnabled: true,
            snoozedUntil: DateTime.UtcNow.AddHours(2));
        var ctrl = BuildController();

        await ctrl.SnoozeLxcMonitoring(conn.Id, 105, new ProxmoxGuestSnoozeRequest(null), CancellationToken.None);

        var guest = await _db.ProxmoxGuests.AsNoTracking()
            .SingleAsync(g => g.ProxmoxConnectionId == conn.Id && g.VmId == 105);
        Assert.Null(guest.MonitoringSnoozedUntil);

        var row = await _db.ProxmoxMonitoringAudits.AsNoTracking().SingleAsync();
        Assert.Equal(ProxmoxMonitoringChangeType.SnoozeCleared, row.ChangeType);
    }

    [Fact]
    public async Task SnoozeLxcMonitoring_PastInstant_ReturnsBadRequest()
    {
        var conn = await SeedConnectionAsync(_userId);
        await SeedGuestAsync(conn.Id, vmId: 105, pendingUpdates: null, monitoringEnabled: true);
        var ctrl = BuildController();

        var result = await ctrl.SnoozeLxcMonitoring(
            conn.Id, 105, new ProxmoxGuestSnoozeRequest(DateTime.UtcNow.AddMinutes(-1)), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task SnoozeLxcMonitoring_NodeRow_ReturnsBadRequest()
    {
        var conn = await SeedConnectionAsync(_userId);
        var ctrl = BuildController();

        var result = await ctrl.SnoozeLxcMonitoring(
            conn.Id, 0, new ProxmoxGuestSnoozeRequest(DateTime.UtcNow.AddHours(1)), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    // ── V6.11 — bulk update (triple gate + per-guest audit) ─────────────────────

    [Fact]
    public async Task ApplyBulkLxcUpdates_GlobalDisabled_Returns403()
    {
        _updateSettingsMock.Setup(s => s.IsEnabledAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);
        var conn = await SeedConnectionAsync(_userId, allowUpdates: true, withSsh: true);
        var ctrl = BuildController();

        await ctrl.ApplyBulkLxcUpdates(conn.Id, new ProxmoxBulkUpdateRequest([101, 102]), CancellationToken.None);

        Assert.Equal(StatusCodes.Status403Forbidden, ctrl.Response.StatusCode);
        _applierMock.Verify(a => a.ApplyAsync(
            It.IsAny<ProxmoxConnectionProfile>(), It.IsAny<int>(),
            It.IsAny<Func<string, CancellationToken, Task>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ApplyBulkLxcUpdates_PerHostDisabled_Returns403()
    {
        _updateSettingsMock.Setup(s => s.IsEnabledAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var conn = await SeedConnectionAsync(_userId, allowUpdates: false, withSsh: true);
        var ctrl = BuildController();

        await ctrl.ApplyBulkLxcUpdates(conn.Id, new ProxmoxBulkUpdateRequest([101]), CancellationToken.None);

        Assert.Equal(StatusCodes.Status403Forbidden, ctrl.Response.StatusCode);
    }

    [Fact]
    public async Task ApplyBulkLxcUpdates_NoSsh_Returns409()
    {
        _updateSettingsMock.Setup(s => s.IsEnabledAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var conn = await SeedConnectionAsync(_userId, allowUpdates: true, withSsh: false);
        var ctrl = BuildController();

        await ctrl.ApplyBulkLxcUpdates(conn.Id, new ProxmoxBulkUpdateRequest([101]), CancellationToken.None);

        Assert.Equal(StatusCodes.Status409Conflict, ctrl.Response.StatusCode);
    }

    [Fact]
    public async Task ApplyBulkLxcUpdates_AllGatesPass_StreamsEachGuest_AndFinalisesOneAuditPerGuest()
    {
        _updateSettingsMock.Setup(s => s.IsEnabledAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var conn = await SeedConnectionAsync(_userId, allowUpdates: true, withSsh: true);
        await SeedGuestAsync(conn.Id, vmId: 101, pendingUpdates: 2, monitoringEnabled: true);
        await SeedGuestAsync(conn.Id, vmId: 102, pendingUpdates: 5, monitoringEnabled: true);

        _applierMock.Setup(a => a.ApplyAsync(
                It.IsAny<ProxmoxConnectionProfile>(), It.IsAny<int>(),
                It.IsAny<Func<string, CancellationToken, Task>>(), It.IsAny<CancellationToken>()))
            .Returns(async (ProxmoxConnectionProfile _, int vmId, Func<string, CancellationToken, Task> onLine, CancellationToken ct) =>
            {
                await onLine($"Upgrading {vmId}...", ct);
                return new ProxmoxUpdateApplyResult(0, 24, NonDebian: false, Error: null);
            });
        _inspectorMock.Setup(i => i.CountPendingUpdatesAsync(
                It.IsAny<ProxmoxConnectionProfile>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProxmoxGuestInspection(0, null));

        var ctrl = BuildController();

        await ctrl.ApplyBulkLxcUpdates(conn.Id, new ProxmoxBulkUpdateRequest([101, 102]), CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, ctrl.Response.StatusCode);
        Assert.Equal("application/x-ndjson", ctrl.Response.ContentType);

        ctrl.Response.Body.Position = 0;
        var body = await new StreamReader(ctrl.Response.Body).ReadToEndAsync();
        Assert.Contains("\"stream\":\"guest-start\"", body);
        Assert.Contains("\"stream\":\"guest-end\"", body);
        Assert.Contains("\"stream\":\"all-done\"", body);
        Assert.Contains("Upgrading 101", body);
        Assert.Contains("Upgrading 102", body);

        // One finalised audit session per guest.
        var rows = await _db.ProxmoxUpdateSessions.AsNoTracking()
            .Where(r => r.ProxmoxConnectionId == conn.Id)
            .ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.NotNull(r.EndedUtc));
        Assert.All(rows, r => Assert.Equal(ProxmoxGuestType.Lxc, r.TargetType));
        Assert.Contains(rows, r => r.VmId == 101);
        Assert.Contains(rows, r => r.VmId == 102);
    }

    [Fact]
    public async Task ApplyBulkLxcUpdates_IncludesNode_SkipsUnknownVmIds()
    {
        _updateSettingsMock.Setup(s => s.IsEnabledAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var conn = await SeedConnectionAsync(_userId, allowUpdates: true, withSsh: true);
        await SeedGuestAsync(conn.Id, vmId: 0, pendingUpdates: 1, monitoringEnabled: true, guestType: ProxmoxGuestType.Node);
        await SeedGuestAsync(conn.Id, vmId: 101, pendingUpdates: 2, monitoringEnabled: true);

        _applierMock.Setup(a => a.ApplyAsync(
                It.IsAny<ProxmoxConnectionProfile>(), It.IsAny<int>(),
                It.IsAny<Func<string, CancellationToken, Task>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProxmoxUpdateApplyResult(0, 10, NonDebian: false, Error: null));
        _inspectorMock.Setup(i => i.CountPendingUpdatesAsync(
                It.IsAny<ProxmoxConnectionProfile>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProxmoxGuestInspection(0, null));
        _apiMock.Setup(a => a.GetNodePendingUpdatesAsync(
                It.IsAny<ProxmoxConnectionProfile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        var ctrl = BuildController();

        // 0 (node) + 101 (LXC) both run; 999 (unknown) is ignored.
        await ctrl.ApplyBulkLxcUpdates(conn.Id, new ProxmoxBulkUpdateRequest([0, 101, 999]), CancellationToken.None);

        _applierMock.Verify(a => a.ApplyAsync(
            It.IsAny<ProxmoxConnectionProfile>(), 0,
            It.IsAny<Func<string, CancellationToken, Task>>(), It.IsAny<CancellationToken>()), Times.Once);
        _applierMock.Verify(a => a.ApplyAsync(
            It.IsAny<ProxmoxConnectionProfile>(), 101,
            It.IsAny<Func<string, CancellationToken, Task>>(), It.IsAny<CancellationToken>()), Times.Once);
        _applierMock.Verify(a => a.ApplyAsync(
            It.IsAny<ProxmoxConnectionProfile>(), 999,
            It.IsAny<Func<string, CancellationToken, Task>>(), It.IsAny<CancellationToken>()), Times.Never);

        // The node run is audited as a Node target.
        var rows = await _db.ProxmoxUpdateSessions.AsNoTracking()
            .Where(r => r.ProxmoxConnectionId == conn.Id)
            .ToListAsync();
        Assert.Contains(rows, r => r.VmId == 0 && r.TargetType == ProxmoxGuestType.Node);
        Assert.Contains(rows, r => r.VmId == 101 && r.TargetType == ProxmoxGuestType.Lxc);
    }

    // ── V6.11 — webhook rotate / delete ────────────────────────────────────────

    [Fact]
    public async Task RotateWebhookToken_SetsToken()
    {
        var conn = await SeedConnectionAsync(_userId);
        var ctrl = BuildController();

        var result = await ctrl.RotateWebhookToken(conn.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<ProxmoxConnectionResponse>(ok.Value);
        Assert.Equal(new string('a', 64), response.WebhookToken);

        var row = await _db.ProxmoxConnections.AsNoTracking().SingleAsync(c => c.Id == conn.Id);
        Assert.Equal(new string('a', 64), row.WebhookToken);
    }

    [Fact]
    public async Task DeleteWebhookToken_ClearsToken()
    {
        var conn = await SeedConnectionAsync(_userId);
        var ctrl = BuildController();
        await ctrl.RotateWebhookToken(conn.Id, CancellationToken.None);
        _db.ChangeTracker.Clear();

        var result = await ctrl.DeleteWebhookToken(conn.Id, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        var row = await _db.ProxmoxConnections.AsNoTracking().SingleAsync(c => c.Id == conn.Id);
        Assert.Null(row.WebhookToken);
    }

    [Fact]
    public async Task RotateWebhookToken_ForeignHost_ReturnsNotFound()
    {
        var conn = await SeedConnectionAsync(_otherUserId);
        var ctrl = BuildController();

        var result = await ctrl.RotateWebhookToken(conn.Id, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    // ── V6.13 — destroy LXC (triple gate + audit) ──────────────────────────────

    [Fact]
    public async Task DestroyLxc_GlobalDisabled_Returns403_WithoutTouchingHost()
    {
        _destroySettingsMock.Setup(s => s.IsEnabledAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);
        var conn = await SeedConnectionAsync(_userId, allowDestroy: true);
        await SeedGuestAsync(conn.Id, vmId: 105, pendingUpdates: null, monitoringEnabled: true, isRunning: false);
        var ctrl = BuildController();

        var result = await ctrl.DestroyLxc(conn.Id, 105, CancellationToken.None);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, obj.StatusCode);
        _apiMock.Verify(a => a.DeleteLxcAsync(
            It.IsAny<ProxmoxConnectionProfile>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Empty(await _db.ProxmoxDestroyAudits.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task DestroyLxc_PerHostDisabled_Returns403_WithoutTouchingHost()
    {
        _destroySettingsMock.Setup(s => s.IsEnabledAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var conn = await SeedConnectionAsync(_userId, allowDestroy: false);
        await SeedGuestAsync(conn.Id, vmId: 105, pendingUpdates: null, monitoringEnabled: true, isRunning: false);
        var ctrl = BuildController();

        var result = await ctrl.DestroyLxc(conn.Id, 105, CancellationToken.None);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, obj.StatusCode);
        _apiMock.Verify(a => a.DeleteLxcAsync(
            It.IsAny<ProxmoxConnectionProfile>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DestroyLxc_ForeignHost_ReturnsNotFound()
    {
        _destroySettingsMock.Setup(s => s.IsEnabledAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var conn = await SeedConnectionAsync(_otherUserId, allowDestroy: true);
        var ctrl = BuildController();

        var result = await ctrl.DestroyLxc(conn.Id, 105, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task DestroyLxc_NodeRow_ReturnsBadRequest()
    {
        _destroySettingsMock.Setup(s => s.IsEnabledAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var conn = await SeedConnectionAsync(_userId, allowDestroy: true);
        var ctrl = BuildController();

        var result = await ctrl.DestroyLxc(conn.Id, 0, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task DestroyLxc_UnknownGuest_ReturnsNotFound()
    {
        _destroySettingsMock.Setup(s => s.IsEnabledAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var conn = await SeedConnectionAsync(_userId, allowDestroy: true);
        var ctrl = BuildController();

        var result = await ctrl.DestroyLxc(conn.Id, 999, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task DestroyLxc_RunningGuest_Returns409_WithoutTouchingHost()
    {
        _destroySettingsMock.Setup(s => s.IsEnabledAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var conn = await SeedConnectionAsync(_userId, allowDestroy: true);
        await SeedGuestAsync(conn.Id, vmId: 105, pendingUpdates: null, monitoringEnabled: true, isRunning: true);
        var ctrl = BuildController();

        var result = await ctrl.DestroyLxc(conn.Id, 105, CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(result);
        _apiMock.Verify(a => a.DeleteLxcAsync(
            It.IsAny<ProxmoxConnectionProfile>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        // No audit row for a request that never reached the host.
        Assert.Empty(await _db.ProxmoxDestroyAudits.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task DestroyLxc_AllGatesPass_DestroysRemovesGuestAndAudits()
    {
        _destroySettingsMock.Setup(s => s.IsEnabledAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var conn = await SeedConnectionAsync(_userId, allowDestroy: true);
        await SeedGuestAsync(conn.Id, vmId: 105, pendingUpdates: null, monitoringEnabled: true, isRunning: false);
        var ctrl = BuildController();

        var result = await ctrl.DestroyLxc(conn.Id, 105, CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        _apiMock.Verify(a => a.DeleteLxcAsync(
            It.IsAny<ProxmoxConnectionProfile>(), 105, It.IsAny<CancellationToken>()), Times.Once);

        // The guest row is gone, so the card disappears without waiting for a scan.
        Assert.False(await _db.ProxmoxGuests.AsNoTracking()
            .AnyAsync(g => g.ProxmoxConnectionId == conn.Id && g.VmId == 105));

        // A success audit row exists, naming the exact guest.
        var row = await _db.ProxmoxDestroyAudits.AsNoTracking()
            .SingleAsync(r => r.ProxmoxConnectionId == conn.Id);
        Assert.Equal(105, row.VmId);
        Assert.Equal("ct-105", row.GuestName);
        Assert.True(row.Success);
        Assert.Null(row.Error);
        Assert.Equal(_userId, row.InitiatedByUserId);
    }

    [Fact]
    public async Task DestroyLxc_HostRejects_Returns502_KeepsGuest_AndAuditsFailure()
    {
        _destroySettingsMock.Setup(s => s.IsEnabledAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var conn = await SeedConnectionAsync(_userId, allowDestroy: true);
        await SeedGuestAsync(conn.Id, vmId: 105, pendingUpdates: null, monitoringEnabled: true, isRunning: false);
        _apiMock.Setup(a => a.DeleteLxcAsync(It.IsAny<ProxmoxConnectionProfile>(), 105, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Proxmox returned 500: CT is locked"));
        var ctrl = BuildController();

        var result = await ctrl.DestroyLxc(conn.Id, 105, CancellationToken.None);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(502, obj.StatusCode);

        // The guest survives a rejected destroy.
        Assert.True(await _db.ProxmoxGuests.AsNoTracking()
            .AnyAsync(g => g.ProxmoxConnectionId == conn.Id && g.VmId == 105));

        // A failure audit row captures the host's message.
        var row = await _db.ProxmoxDestroyAudits.AsNoTracking()
            .SingleAsync(r => r.ProxmoxConnectionId == conn.Id);
        Assert.False(row.Success);
        Assert.Contains("CT is locked", row.Error);
    }

    // ── V6.14 — destroy QEMU VM (same gates, routed to the qemu endpoint) ────────

    [Fact]
    public async Task DestroyQemu_AllGatesPass_RoutesToQemu_RemovesGuestAndAudits()
    {
        _destroySettingsMock.Setup(s => s.IsEnabledAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var conn = await SeedConnectionAsync(_userId, allowDestroy: true);
        await SeedGuestAsync(conn.Id, vmId: 200, pendingUpdates: null, monitoringEnabled: true,
            guestType: ProxmoxGuestType.Qemu, isRunning: false);
        var ctrl = BuildController();

        var result = await ctrl.DestroyQemu(conn.Id, 200, CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        // It hit the QEMU destroy path, not the LXC one.
        _apiMock.Verify(a => a.DeleteQemuAsync(
            It.IsAny<ProxmoxConnectionProfile>(), 200, It.IsAny<CancellationToken>()), Times.Once);
        _apiMock.Verify(a => a.DeleteLxcAsync(
            It.IsAny<ProxmoxConnectionProfile>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);

        Assert.False(await _db.ProxmoxGuests.AsNoTracking()
            .AnyAsync(g => g.ProxmoxConnectionId == conn.Id && g.VmId == 200));
        var row = await _db.ProxmoxDestroyAudits.AsNoTracking()
            .SingleAsync(r => r.ProxmoxConnectionId == conn.Id);
        Assert.Equal(200, row.VmId);
        Assert.True(row.Success);
    }

    [Fact]
    public async Task DestroyQemu_RunningGuest_Returns409_WithoutTouchingHost()
    {
        _destroySettingsMock.Setup(s => s.IsEnabledAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var conn = await SeedConnectionAsync(_userId, allowDestroy: true);
        await SeedGuestAsync(conn.Id, vmId: 200, pendingUpdates: null, monitoringEnabled: true,
            guestType: ProxmoxGuestType.Qemu, isRunning: true);
        var ctrl = BuildController();

        var result = await ctrl.DestroyQemu(conn.Id, 200, CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(result);
        _apiMock.Verify(a => a.DeleteQemuAsync(
            It.IsAny<ProxmoxConnectionProfile>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DestroyQemu_GlobalDisabled_Returns403_WithoutTouchingHost()
    {
        _destroySettingsMock.Setup(s => s.IsEnabledAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);
        var conn = await SeedConnectionAsync(_userId, allowDestroy: true);
        await SeedGuestAsync(conn.Id, vmId: 200, pendingUpdates: null, monitoringEnabled: true,
            guestType: ProxmoxGuestType.Qemu, isRunning: false);
        var ctrl = BuildController();

        var result = await ctrl.DestroyQemu(conn.Id, 200, CancellationToken.None);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(403, obj.StatusCode);
        _apiMock.Verify(a => a.DeleteQemuAsync(
            It.IsAny<ProxmoxConnectionProfile>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── V6.4 — lifecycle action (start/stop/shutdown/reboot) + optimistic state ──

    [Fact]
    public async Task LxcAction_Stop_MarksGuestStopped_AndReturnsRefreshedHost()
    {
        var conn = await SeedConnectionAsync(_userId);
        await SeedGuestAsync(conn.Id, vmId: 104, pendingUpdates: null, monitoringEnabled: true, isRunning: true);
        _apiMock.Setup(a => a.LxcStatusActionAsync(
                It.IsAny<ProxmoxConnectionProfile>(), 104, "stop", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var ctrl = BuildController();

        // A hard `stop` is immediate, so the persisted guest flips to stopped now.
        var result = await ctrl.LxcAction(conn.Id, 104, "stop", CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        var guest = await _db.ProxmoxGuests.AsNoTracking()
            .SingleAsync(g => g.ProxmoxConnectionId == conn.Id && g.VmId == 104);
        Assert.False(guest.IsRunning);
        Assert.Null(guest.UptimeSeconds);
    }

    [Fact]
    public async Task LxcAction_Shutdown_DoesNotOptimisticallyStop_LeavesStateForSync()
    {
        var conn = await SeedConnectionAsync(_userId);
        await SeedGuestAsync(conn.Id, vmId: 104, pendingUpdates: null, monitoringEnabled: true, isRunning: true);
        _apiMock.Setup(a => a.LxcStatusActionAsync(
                It.IsAny<ProxmoxConnectionProfile>(), 104, "shutdown", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var ctrl = BuildController();

        var result = await ctrl.LxcAction(conn.Id, 104, "shutdown", CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        // A graceful shutdown is async — the card must NOT show "stopped" while the
        // guest is still running on the host; the next live-status sync reconciles it.
        var guest = await _db.ProxmoxGuests.AsNoTracking()
            .SingleAsync(g => g.ProxmoxConnectionId == conn.Id && g.VmId == 104);
        Assert.True(guest.IsRunning);
    }

    [Fact]
    public async Task LxcAction_Start_MarksGuestRunning()
    {
        var conn = await SeedConnectionAsync(_userId);
        await SeedGuestAsync(conn.Id, vmId: 104, pendingUpdates: null, monitoringEnabled: true, isRunning: false);
        _apiMock.Setup(a => a.LxcStatusActionAsync(
                It.IsAny<ProxmoxConnectionProfile>(), 104, "start", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var ctrl = BuildController();

        await ctrl.LxcAction(conn.Id, 104, "start", CancellationToken.None);

        var guest = await _db.ProxmoxGuests.AsNoTracking()
            .SingleAsync(g => g.ProxmoxConnectionId == conn.Id && g.VmId == 104);
        Assert.True(guest.IsRunning);
    }

    [Fact]
    public async Task LxcAction_UnknownVerb_ReturnsBadRequest_WithoutTouchingHost()
    {
        var conn = await SeedConnectionAsync(_userId);
        var ctrl = BuildController();

        var result = await ctrl.LxcAction(conn.Id, 104, "explode", CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        _apiMock.Verify(a => a.LxcStatusActionAsync(
            It.IsAny<ProxmoxConnectionProfile>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task LxcAction_HostUnreachable_Returns502_AndLeavesStateUnchanged()
    {
        var conn = await SeedConnectionAsync(_userId);
        await SeedGuestAsync(conn.Id, vmId: 104, pendingUpdates: null, monitoringEnabled: true, isRunning: true);
        _apiMock.Setup(a => a.LxcStatusActionAsync(
                It.IsAny<ProxmoxConnectionProfile>(), 104, "shutdown", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("connection refused"));
        var ctrl = BuildController();

        var result = await ctrl.LxcAction(conn.Id, 104, "shutdown", CancellationToken.None);

        var obj = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(502, obj.StatusCode);
        // The command never reached the host, so the persisted state is untouched.
        var guest = await _db.ProxmoxGuests.AsNoTracking()
            .SingleAsync(g => g.ProxmoxConnectionId == conn.Id && g.VmId == 104);
        Assert.True(guest.IsRunning);
    }

    // ── V6.14 — QEMU VM lifecycle + reads ───────────────────────────────────────

    [Fact]
    public async Task QemuAction_Stop_RoutesToQemuEndpoint_AndMarksGuestStopped()
    {
        var conn = await SeedConnectionAsync(_userId);
        await SeedGuestAsync(conn.Id, vmId: 200, pendingUpdates: null, monitoringEnabled: true,
            guestType: ProxmoxGuestType.Qemu, isRunning: true);
        _apiMock.Setup(a => a.QemuStatusActionAsync(
                It.IsAny<ProxmoxConnectionProfile>(), 200, "stop", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var ctrl = BuildController();

        var result = await ctrl.QemuAction(conn.Id, 200, "stop", CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        // It hit the QEMU lifecycle path, not the LXC one.
        _apiMock.Verify(a => a.QemuStatusActionAsync(
            It.IsAny<ProxmoxConnectionProfile>(), 200, "stop", It.IsAny<CancellationToken>()), Times.Once);
        _apiMock.Verify(a => a.LxcStatusActionAsync(
            It.IsAny<ProxmoxConnectionProfile>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        // A hard `stop` flips the card to stopped immediately.
        var guest = await _db.ProxmoxGuests.AsNoTracking()
            .SingleAsync(g => g.ProxmoxConnectionId == conn.Id && g.VmId == 200);
        Assert.False(guest.IsRunning);
    }

    [Fact]
    public async Task QemuAction_Shutdown_DoesNotOptimisticallyStop()
    {
        var conn = await SeedConnectionAsync(_userId);
        await SeedGuestAsync(conn.Id, vmId: 200, pendingUpdates: null, monitoringEnabled: true,
            guestType: ProxmoxGuestType.Qemu, isRunning: true);
        _apiMock.Setup(a => a.QemuStatusActionAsync(
                It.IsAny<ProxmoxConnectionProfile>(), 200, "shutdown", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var ctrl = BuildController();

        await ctrl.QemuAction(conn.Id, 200, "shutdown", CancellationToken.None);

        // A VM's graceful shutdown may take a while or be ignored (no guest-agent),
        // so the card stays running until the sync confirms it actually stopped.
        var guest = await _db.ProxmoxGuests.AsNoTracking()
            .SingleAsync(g => g.ProxmoxConnectionId == conn.Id && g.VmId == 200);
        Assert.True(guest.IsRunning);
    }

    [Fact]
    public async Task QemuAction_UnknownVerb_ReturnsBadRequest_WithoutTouchingHost()
    {
        var conn = await SeedConnectionAsync(_userId);
        var ctrl = BuildController();

        var result = await ctrl.QemuAction(conn.Id, 200, "migrate", CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        _apiMock.Verify(a => a.QemuStatusActionAsync(
            It.IsAny<ProxmoxConnectionProfile>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetQemuStatus_ReturnsLiveStatusFromQemuEndpoint()
    {
        var conn = await SeedConnectionAsync(_userId);
        _apiMock.Setup(a => a.GetQemuStatusAsync(It.IsAny<ProxmoxConnectionProfile>(), 200, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProxmoxLxcStatus("running", 0.5, 10, 20, 30, 40, 50, 60, 70, 80, 90));
        var ctrl = BuildController();

        var result = await ctrl.GetQemuStatus(conn.Id, 200, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var status = Assert.IsType<ProxmoxLxcStatus>(ok.Value);
        Assert.Equal("running", status.Status);
    }

    [Fact]
    public async Task GetQemuConfig_ReturnsDetailFromQemuEndpoint()
    {
        var conn = await SeedConnectionAsync(_userId);
        _apiMock.Setup(a => a.GetQemuDetailAsync(It.IsAny<ProxmoxConnectionProfile>(), 200, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProxmoxLxcDetail(
                200, "running", 4, 4096L * 1024 * 1024, null, "win11", "win11", null, true, null, null,
                [], [], 0.1, null, null, null, null, 60));
        var ctrl = BuildController();

        var result = await ctrl.GetQemuConfig(conn.Id, 200, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var detail = Assert.IsType<ProxmoxLxcDetail>(ok.Value);
        Assert.Equal("win11", detail.Hostname);
        Assert.Equal(4, detail.Cores);
    }

    // ── V6.13.1 — light live-status sync ────────────────────────────────────────

    [Fact]
    public async Task SyncLxcStatuses_UpdatesRunningStateFromHost()
    {
        var conn = await SeedConnectionAsync(_userId);
        // DB thinks 104 is running and 105 is stopped…
        await SeedGuestAsync(conn.Id, vmId: 104, pendingUpdates: 3, monitoringEnabled: true, isRunning: true);
        await SeedGuestAsync(conn.Id, vmId: 105, pendingUpdates: null, monitoringEnabled: true, isRunning: false);
        // …but the host reports the opposite (104 stopped externally, 105 started).
        _apiMock.Setup(a => a.ListLxcAsync(It.IsAny<ProxmoxConnectionProfile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProxmoxLxcSummary>
            {
                new(104, "ct-104", IsRunning: false),
                new(105, "ct-105", IsRunning: true, UptimeSeconds: 42),
            });
        _apiMock.Setup(a => a.ListQemuAsync(It.IsAny<ProxmoxConnectionProfile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        var ctrl = BuildController();

        var result = await ctrl.SyncLxcStatuses(conn.Id, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        var g104 = await _db.ProxmoxGuests.AsNoTracking().SingleAsync(g => g.ProxmoxConnectionId == conn.Id && g.VmId == 104);
        var g105 = await _db.ProxmoxGuests.AsNoTracking().SingleAsync(g => g.ProxmoxConnectionId == conn.Id && g.VmId == 105);
        Assert.False(g104.IsRunning);
        Assert.True(g105.IsRunning);
        Assert.Equal(42, g105.UptimeSeconds);
        // The cheap sync must NOT clobber the pending-update count (that's the scan's job).
        Assert.Equal(3, g104.PendingUpdates);
    }

    [Fact]
    public async Task SyncLxcStatuses_AlsoSyncsQemuVms()
    {
        var conn = await SeedConnectionAsync(_userId);
        await SeedGuestAsync(conn.Id, vmId: 104, pendingUpdates: null, monitoringEnabled: true, isRunning: false);
        await SeedGuestAsync(conn.Id, vmId: 200, pendingUpdates: null, monitoringEnabled: true,
            guestType: ProxmoxGuestType.Qemu, isRunning: false);
        _apiMock.Setup(a => a.ListLxcAsync(It.IsAny<ProxmoxConnectionProfile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProxmoxLxcSummary> { new(104, "ct-104", IsRunning: true, UptimeSeconds: 10) });
        _apiMock.Setup(a => a.ListQemuAsync(It.IsAny<ProxmoxConnectionProfile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProxmoxLxcSummary> { new(200, "win11", IsRunning: true, UptimeSeconds: 99) });
        var ctrl = BuildController();

        var result = await ctrl.SyncLxcStatuses(conn.Id, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        var vm = await _db.ProxmoxGuests.AsNoTracking().SingleAsync(g => g.ProxmoxConnectionId == conn.Id && g.VmId == 200);
        Assert.True(vm.IsRunning);
        Assert.Equal(99, vm.UptimeSeconds);
    }

    [Fact]
    public async Task SyncLxcStatuses_ForeignHost_ReturnsNotFound()
    {
        var conn = await SeedConnectionAsync(_otherUserId);
        var ctrl = BuildController();

        var result = await ctrl.SyncLxcStatuses(conn.Id, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task SyncLxcStatuses_HostUnreachable_Returns502()
    {
        var conn = await SeedConnectionAsync(_userId);
        _apiMock.Setup(a => a.ListLxcAsync(It.IsAny<ProxmoxConnectionProfile>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("connection refused"));
        var ctrl = BuildController();

        var result = await ctrl.SyncLxcStatuses(conn.Id, CancellationToken.None);

        var obj = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(502, obj.StatusCode);
    }

    // ── V6.13.1 — create LXC (double gate + validation + audit) ─────────────────

    private static ProxmoxLxcCreateRequest CreateReq(int vmId = 150) =>
        new(vmId, "local:vztmpl/debian-12.tar.zst", "local-lvm", 8,
            Hostname: "newct", Password: "s3cret");

    [Fact]
    public async Task CreateLxc_GlobalDisabled_Returns403_WithoutTouchingHost()
    {
        _createSettingsMock.Setup(s => s.IsEnabledAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);
        var conn = await SeedConnectionAsync(_userId, allowCreate: true);
        var ctrl = BuildController();

        var result = await ctrl.CreateLxc(conn.Id, CreateReq(), CancellationToken.None);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, obj.StatusCode);
        _apiMock.Verify(a => a.CreateLxcAsync(
            It.IsAny<ProxmoxConnectionProfile>(), It.IsAny<ProxmoxLxcCreate>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Empty(await _db.ProxmoxCreateAudits.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task CreateLxc_PerHostDisabled_Returns403_WithoutTouchingHost()
    {
        _createSettingsMock.Setup(s => s.IsEnabledAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var conn = await SeedConnectionAsync(_userId, allowCreate: false);
        var ctrl = BuildController();

        var result = await ctrl.CreateLxc(conn.Id, CreateReq(), CancellationToken.None);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, obj.StatusCode);
        _apiMock.Verify(a => a.CreateLxcAsync(
            It.IsAny<ProxmoxConnectionProfile>(), It.IsAny<ProxmoxLxcCreate>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CreateLxc_ForeignHost_ReturnsNotFound()
    {
        _createSettingsMock.Setup(s => s.IsEnabledAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var conn = await SeedConnectionAsync(_otherUserId, allowCreate: true);
        var ctrl = BuildController();

        var result = await ctrl.CreateLxc(conn.Id, CreateReq(), CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task CreateLxc_VmidAlreadyExists_Returns409_WithoutTouchingHost()
    {
        _createSettingsMock.Setup(s => s.IsEnabledAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var conn = await SeedConnectionAsync(_userId, allowCreate: true);
        await SeedGuestAsync(conn.Id, vmId: 150, pendingUpdates: null, monitoringEnabled: true);
        var ctrl = BuildController();

        var result = await ctrl.CreateLxc(conn.Id, CreateReq(vmId: 150), CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(result);
        _apiMock.Verify(a => a.CreateLxcAsync(
            It.IsAny<ProxmoxConnectionProfile>(), It.IsAny<ProxmoxLxcCreate>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Empty(await _db.ProxmoxCreateAudits.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task CreateLxc_MalformedSpec_Returns400_WithoutTouchingHost()
    {
        _createSettingsMock.Setup(s => s.IsEnabledAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var conn = await SeedConnectionAsync(_userId, allowCreate: true);
        var ctrl = BuildController();

        // A vmid below the allowed range + a malformed gateway are both rejected
        // client-side-equivalently by the server validator before any API call.
        var bad = new ProxmoxLxcCreateRequest(
            42, "local:vztmpl/debian-12.tar.zst", "local-lvm", 8, Password: "x",
            Net0: new ProxmoxLxcNetChange("net0", Gw: "not-an-ip"));

        var result = await ctrl.CreateLxc(conn.Id, bad, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        _apiMock.Verify(a => a.CreateLxcAsync(
            It.IsAny<ProxmoxConnectionProfile>(), It.IsAny<ProxmoxLxcCreate>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CreateLxc_AllGatesPass_CreatesScansAndAudits()
    {
        _createSettingsMock.Setup(s => s.IsEnabledAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var conn = await SeedConnectionAsync(_userId, allowCreate: true);
        var ctrl = BuildController();

        var result = await ctrl.CreateLxc(conn.Id, CreateReq(vmId: 150), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        _apiMock.Verify(a => a.CreateLxcAsync(
            It.IsAny<ProxmoxConnectionProfile>(),
            It.Is<ProxmoxLxcCreate>(s => s.VmId == 150 && s.OsTemplate == "local:vztmpl/debian-12.tar.zst"),
            It.IsAny<CancellationToken>()), Times.Once);
        // The host is re-scanned so the new card appears without waiting for the schedule.
        _scanMock.Verify(s => s.CheckConnectionAsync(
            It.Is<ProxmoxConnectionEntity>(c => c.Id == conn.Id), It.IsAny<CancellationToken>()), Times.Once);

        var row = await _db.ProxmoxCreateAudits.AsNoTracking().SingleAsync(r => r.ProxmoxConnectionId == conn.Id);
        Assert.Equal(150, row.VmId);
        Assert.Equal("newct", row.Hostname);
        Assert.Equal("local:vztmpl/debian-12.tar.zst", row.Template);
        Assert.True(row.Success);
        Assert.Null(row.Error);
        Assert.Equal(_userId, row.InitiatedByUserId);
    }

    [Fact]
    public async Task CreateLxc_AddToHa_RegistersHaResourceAfterCreate()
    {
        _createSettingsMock.Setup(s => s.IsEnabledAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var conn = await SeedConnectionAsync(_userId, allowCreate: true);
        var ctrl = BuildController();

        var req = CreateReq(vmId: 150) with { AddToHa = true };
        var result = await ctrl.CreateLxc(conn.Id, req, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        _apiMock.Verify(a => a.AddHaResourceAsync(
            It.IsAny<ProxmoxConnectionProfile>(), 150, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CreateLxc_AddToHa_HaFailure_StillSucceeds()
    {
        _createSettingsMock.Setup(s => s.IsEnabledAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var conn = await SeedConnectionAsync(_userId, allowCreate: true);
        _apiMock.Setup(a => a.AddHaResourceAsync(It.IsAny<ProxmoxConnectionProfile>(), 150, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("no HA cluster"));
        var ctrl = BuildController();

        var req = CreateReq(vmId: 150) with { AddToHa = true };
        var result = await ctrl.CreateLxc(conn.Id, req, CancellationToken.None);

        // The container was created; a failed HA registration is best-effort and
        // must not turn the create into a failure.
        Assert.IsType<OkObjectResult>(result);
        var row = await _db.ProxmoxCreateAudits.AsNoTracking().SingleAsync(r => r.ProxmoxConnectionId == conn.Id);
        Assert.True(row.Success);
    }

    [Fact]
    public async Task CreateLxc_HostRejects_Returns502_AndAuditsFailure()
    {
        _createSettingsMock.Setup(s => s.IsEnabledAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var conn = await SeedConnectionAsync(_userId, allowCreate: true);
        _apiMock.Setup(a => a.CreateLxcAsync(
                It.IsAny<ProxmoxConnectionProfile>(), It.IsAny<ProxmoxLxcCreate>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Proxmox returned 500: storage 'local-lvm' does not support container directories"));
        var ctrl = BuildController();

        var result = await ctrl.CreateLxc(conn.Id, CreateReq(vmId: 150), CancellationToken.None);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(502, obj.StatusCode);
        // No scan on a failed create.
        _scanMock.Verify(s => s.CheckConnectionAsync(
            It.IsAny<ProxmoxConnectionEntity>(), It.IsAny<CancellationToken>()), Times.Never);

        var row = await _db.ProxmoxCreateAudits.AsNoTracking().SingleAsync(r => r.ProxmoxConnectionId == conn.Id);
        Assert.False(row.Success);
        Assert.Contains("does not support container directories", row.Error);
    }

    // ── V6.8 — node card read endpoints ────────────────────────────────────────

    [Fact]
    public async Task GetNodeStatus_OwnedHost_ReturnsApiSnapshot()
    {
        var conn = await SeedConnectionAsync(_userId);
        var snapshot = new ProxmoxNodeStatus(
            "Intel i5", 1, 6, 6, 3100, true, 0.1, 0.01, 0.5, 0.6, 0.7,
            100, 40, 60, 10, 1, 500, 250, 1000, "Linux", "pve-8", "notfound", "");
        _apiMock.Setup(a => a.GetNodeStatusAsync(It.IsAny<ProxmoxConnectionProfile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(snapshot);
        var ctrl = BuildController();

        var result = await ctrl.GetNodeStatus(conn.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var value = Assert.IsType<ProxmoxNodeStatus>(ok.Value);
        Assert.Equal("Intel i5", value.CpuModel);
        Assert.Equal(6, value.Cpus);
    }

    [Fact]
    public async Task GetNodeStatus_ForeignHost_ReturnsNotFound()
    {
        var conn = await SeedConnectionAsync(_otherUserId);
        var ctrl = BuildController();

        var result = await ctrl.GetNodeStatus(conn.Id, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task GetNodeStatus_UnreachableHost_Returns502()
    {
        var conn = await SeedConnectionAsync(_userId);
        _apiMock.Setup(a => a.GetNodeStatusAsync(It.IsAny<ProxmoxConnectionProfile>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("connection refused"));
        var ctrl = BuildController();

        var result = await ctrl.GetNodeStatus(conn.Id, CancellationToken.None);

        var obj = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(502, obj.StatusCode);
    }

    [Fact]
    public async Task GetNodeDiskSmart_MissingDiskParam_ReturnsBadRequest()
    {
        var conn = await SeedConnectionAsync(_userId);
        var ctrl = BuildController();

        var result = await ctrl.GetNodeDiskSmart(conn.Id, "", CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task GetNodeSensors_OwnedHost_ReturnsInspectorResult()
    {
        var conn = await SeedConnectionAsync(_userId);
        var sensors = new ProxmoxNodeSensors(true, null,
            [new ProxmoxSensorReading("coretemp", "Package id 0", 45, 80, 100, null)]);
        _inspectorMock.Setup(i => i.ReadNodeSensorsAsync(It.IsAny<ProxmoxConnectionProfile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(sensors);
        var ctrl = BuildController();

        var result = await ctrl.GetNodeSensors(conn.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var value = Assert.IsType<ProxmoxNodeSensors>(ok.Value);
        Assert.True(value.Available);
        Assert.Single(value.Readings);
    }

    [Fact]
    public async Task GetNodeSensors_ForeignHost_ReturnsNotFound()
    {
        var conn = await SeedConnectionAsync(_otherUserId);
        var ctrl = BuildController();

        var result = await ctrl.GetNodeSensors(conn.Id, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private ProxmoxConnectionsController BuildController(Guid? userId = null)
    {
        var controller = new ProxmoxConnectionsController(
            _db,
            new ProxmoxConnectionMapper(_encryptionMock.Object),
            _checkerMock.Object,
            _scanMock.Object,
            _apiMock.Object,
            _inspectorMock.Object,
            _applierMock.Object,
            _updateSettingsMock.Object,
            _destroySettingsMock.Object,
            _createSettingsMock.Object,
            _alertServiceMock.Object,
            _webhookTokenGeneratorMock.Object,
            NullLogger<ProxmoxConnectionsController>.Instance);

        var identity = new ClaimsIdentity(
            new[] { new Claim(StashboardClaims.UserId, (userId ?? _userId).ToString()) }, "Test");
        // A real, readable body so streaming endpoints can be inspected.
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) },
        };
        controller.Response.Body = new MemoryStream();
        return controller;
    }

    private async Task<UserEntity> SeedUserAsync(string email)
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

    private async Task<ProxmoxConnectionEntity> SeedConnectionAsync(
        Guid userId, bool allowUpdates = false, bool withSsh = false, bool allowDestroy = false,
        bool allowCreate = false)
    {
        var conn = new ProxmoxConnectionEntity
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Name = $"pve-{Guid.NewGuid():N}",
            ApiBaseUrl = "https://pve.lan:8006",
            NodeName = "pve",
            ApiTokenId = "root@pam!stash",
            ApiTokenSecretEncrypted = "enc:secret",
            AllowUpdates = allowUpdates,
            AllowDestroy = allowDestroy,
            AllowCreate = allowCreate,
            SshHost = withSsh ? "pve.lan" : null,
            SshUsername = withSsh ? "root" : null,
            SshPrivateKeyEncrypted = withSsh ? "enc:PEM" : null,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow,
        };
        _db.ProxmoxConnections.Add(conn);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        return conn;
    }

    private async Task SeedGuestAsync(
        Guid connectionId, int vmId, int? pendingUpdates, bool monitoringEnabled,
        DateTime? snoozedUntil = null, ProxmoxGuestType guestType = ProxmoxGuestType.Lxc,
        bool isRunning = true)
    {
        _db.ProxmoxGuests.Add(new ProxmoxGuestEntity
        {
            Id = Guid.NewGuid(),
            ProxmoxConnectionId = connectionId,
            VmId = vmId,
            Name = guestType == ProxmoxGuestType.Node ? "pve" : $"ct-{vmId}",
            GuestType = guestType,
            IsRunning = isRunning,
            PendingUpdates = pendingUpdates,
            MonitoringEnabled = monitoringEnabled,
            MonitoringSnoozedUntil = snoozedUntil,
            UpdatedUtc = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
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
        await _db.ProxmoxUpdateSessions.ExecuteDeleteAsync();
        await _db.ProxmoxMonitoringAudits.ExecuteDeleteAsync();
        await _db.ProxmoxDestroyAudits.ExecuteDeleteAsync();
        await _db.ProxmoxCreateAudits.ExecuteDeleteAsync();
        await _db.ProxmoxGuests.ExecuteDeleteAsync();
        await _db.ProxmoxConnections.ExecuteDeleteAsync();
        await _db.Users.ExecuteDeleteAsync();
    }

    private static string BuildTestConnectionString() =>
        $"Data Source={Path.Combine(Path.GetTempPath(), "stashboard-tests-proxmox-connections.db")};Pooling=False";
}
