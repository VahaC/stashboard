using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Stashboard.Api.Auth;
using Stashboard.Api.Contracts;
using Stashboard.Api.Data;
using Stashboard.Api.Mapping;
using Stashboard.Api.Services.Proxmox;
using Stashboard.Core.Abstractions;
using Stashboard.Core.Entities;
using Stashboard.Core.Enums;
using Stashboard.Core.Proxmox;

namespace Stashboard.Api.Controllers;

/// <summary>
/// V6.0 — user-scoped Proxmox hosts. A user can own many named hosts; each is
/// polled for pending package updates on its own schedule. The discovered node
/// + LXC cards are returned inline on the connection. Path:
/// <c>/api/proxmox/connections[/{id}]</c>.
/// </summary>
[ApiController]
[Authorize]
[Route("api/proxmox/connections")]
public class ProxmoxConnectionsController(
    ApplicationDbContext db,
    IProxmoxConnectionMapper mapper,
    IProxmoxUpdateChecker checker,
    IProxmoxScanService scanService,
    IProxmoxApiClient apiClient,
    IProxmoxGuestInspector guestInspector,
    IProxmoxUpdateApplier updateApplier,
    IProxmoxUpdateApplySettingsService updateSettings,
    IProxmoxDestroySettingsService destroySettings,
    IProxmoxCreateSettingsService createSettings,
    IProxmoxCloneSettingsService cloneSettings,
    IProxmoxNodeAlertService alertService,
    IDockerWebhookTokenGenerator webhookTokenGenerator,
    IContainerIconResolver iconResolver,
    ILogger<ProxmoxConnectionsController> logger) : ControllerBase
{
    private Guid UserId => User.GetUserId();

    [HttpGet]
    public async Task<ActionResult<List<ProxmoxConnectionResponse>>> List(CancellationToken cancellationToken)
    {
        var userId = UserId;
        var connections = await db.ProxmoxConnections.AsNoTracking()
            .Where(c => c.UserId == userId)
            .OrderBy(c => c.Name)
            .ToListAsync(cancellationToken);

        var ids = connections.Select(c => c.Id).ToList();
        var guests = await db.ProxmoxGuests.AsNoTracking()
            .Where(g => ids.Contains(g.ProxmoxConnectionId))
            .ToListAsync(cancellationToken);
        var byConnection = guests.GroupBy(g => g.ProxmoxConnectionId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<ProxmoxGuestEntity>)g.ToList());

        // V7.9 — per-guest linked service, across all the user's hosts at once.
        var allLinks = await db.WebResourceProxmoxGuestLinks.AsNoTracking()
            .Where(l => ids.Contains(l.ProxmoxConnectionId))
            .Join(db.WebResources.Where(s => s.UserId == userId),
                l => l.WebResourceId, s => s.Id,
                (l, s) => new { l.ProxmoxConnectionId, l.VmId, ServiceId = s.Id, ServiceName = s.Name })
            .ToListAsync(cancellationToken);
        var linksByConnection = allLinks
            .GroupBy(l => l.ProxmoxConnectionId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyDictionary<int, (Guid, string)>)g
                    .GroupBy(x => x.VmId)
                    .ToDictionary(x => x.Key, x => (x.First().ServiceId, x.First().ServiceName)));

        return Ok(connections
            .Select(c => mapper.ToResponse(
                c, byConnection.GetValueOrDefault(c.Id, []), linksByConnection.GetValueOrDefault(c.Id)))
            .ToList());
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ProxmoxConnectionResponse>> Get(Guid id, CancellationToken cancellationToken)
    {
        var connection = await LoadOwnedAsync(id, tracking: false, cancellationToken);
        if (connection is null) return NotFound();
        return Ok(await ToResponseAsync(connection, await LoadGuestsAsync(id, cancellationToken), cancellationToken));
    }

    [HttpPost]
    public async Task<ActionResult<ProxmoxConnectionResponse>> Create(
        [FromBody] ProxmoxConnectionUpsertRequest request, CancellationToken cancellationToken)
    {
        var connection = new ProxmoxConnectionEntity
        {
            Id = Guid.NewGuid(),
            UserId = UserId,
            CreatedUtc = DateTime.UtcNow,
        };
        mapper.ApplyUpsert(connection, request);
        db.ProxmoxConnections.Add(connection);

        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException ex) when (IsUniqueNameViolation(ex))
        {
            return Conflict(new { error = $"A Proxmox host with name '{connection.Name}' already exists." });
        }

        return CreatedAtAction(nameof(Get), new { id = connection.Id }, mapper.ToResponse(connection, []));
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<ProxmoxConnectionResponse>> Update(
        Guid id, [FromBody] ProxmoxConnectionUpsertRequest request, CancellationToken cancellationToken)
    {
        var connection = await LoadOwnedAsync(id, tracking: true, cancellationToken);
        if (connection is null) return NotFound();

        mapper.ApplyUpsert(connection, request);

        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException ex) when (IsUniqueNameViolation(ex))
        {
            return Conflict(new { error = $"A Proxmox host with name '{connection.Name}' already exists." });
        }

        return Ok(await ToResponseAsync(connection, await LoadGuestsAsync(id, cancellationToken), cancellationToken));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var connection = await LoadOwnedAsync(id, tracking: false, cancellationToken);
        if (connection is null) return NotFound();

        await db.ProxmoxConnections.Where(c => c.Id == id).ExecuteDeleteAsync(cancellationToken);
        return NoContent();
    }

    [HttpPost("test")]
    public async Task<ActionResult<ProxmoxConnectionPingResponse>> Test(
        [FromBody] ProxmoxConnectionPingRequest request,
        [FromQuery] Guid? connectionId,
        CancellationToken cancellationToken)
    {
        var existing = connectionId is null
            ? null
            : await LoadOwnedAsync(connectionId.Value, tracking: false, cancellationToken);

        var profile = mapper.BuildProfile(request, existing);
        var result = await checker.TestConnectionAsync(profile, cancellationToken);
        return Ok(new ProxmoxConnectionPingResponse(result.ApiReachable, result.SshReachable, result.Error));
    }

    /// <summary>Runs an immediate scan of this host, bypassing the schedule.</summary>
    [HttpPost("{id:guid}/check")]
    public async Task<ActionResult<ProxmoxConnectionResponse>> CheckNow(Guid id, CancellationToken cancellationToken)
    {
        var connection = await LoadOwnedAsync(id, tracking: true, cancellationToken);
        if (connection is null) return NotFound();

        await scanService.CheckConnectionAsync(connection, cancellationToken);
        return Ok(await ToResponseAsync(connection, await LoadGuestsAsync(id, cancellationToken), cancellationToken));
    }

    /// <summary>
    /// V6.13.1 — light live-status sync. Re-reads the host's guest lists (cheap
    /// <c>GET /nodes/{node}/lxc</c> + <c>/qemu</c>, no SSH; V6.14 added the VMs) and
    /// updates each known guest's running state / uptime / resources, so a guest
    /// started or stopped <em>outside</em> Stashboard (the Proxmox UI, CLI, a crash)
    /// is reflected within the page's poll interval instead of waiting for the next
    /// scheduled — and much heavier — scan.
    /// Deliberately does NOT touch pending-update counts or <c>LastError</c> (those
    /// need the SSH apt probe the full scan runs) and never adds/removes guest rows
    /// (discovery stays with the scan). The page polls this every ~20s.
    /// </summary>
    [HttpPost("{id:guid}/lxc/sync")]
    public async Task<ActionResult<ProxmoxConnectionResponse>> SyncLxcStatuses(Guid id, CancellationToken cancellationToken)
    {
        var connection = await LoadOwnedAsync(id, tracking: false, cancellationToken);
        if (connection is null) return NotFound();

        var profile = mapper.BuildProfile(connection);
        // V6.14 — sync both LXC containers and QEMU VMs (vmids are unique
        // cluster-wide, so the two summary lists merge into one dictionary).
        Dictionary<int, ProxmoxLxcSummary> byVmId;
        try
        {
            var lxc = await apiClient.ListLxcAsync(profile, cancellationToken);
            var qemu = await apiClient.ListQemuAsync(profile, cancellationToken);
            byVmId = lxc.Concat(qemu).ToDictionary(s => s.VmId);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return StatusCode(502, new { error = $"Proxmox host unreachable: {ex.Message}" });
        }

        var guests = await db.ProxmoxGuests.AsTracking()
            .Where(g => g.ProxmoxConnectionId == id && g.GuestType != ProxmoxGuestType.Node)
            .ToListAsync(cancellationToken);

        var now = DateTime.UtcNow;
        foreach (var guest in guests)
        {
            if (!byVmId.TryGetValue(guest.VmId, out var s)) continue;   // gone from the host ⇒ leave to the scan
            guest.IsRunning = s.IsRunning;
            guest.UptimeSeconds = s.IsRunning ? s.UptimeSeconds : null;
            if (s.CpuCores is not null) guest.CpuCores = s.CpuCores;
            if (s.MemoryBytes is not null) guest.MemoryBytes = s.MemoryBytes;
            if (s.DiskBytes is not null) guest.DiskBytes = s.DiskBytes;
            guest.Tags = s.Tags;
            guest.UpdatedUtc = now;
        }
        await db.SaveChangesAsync(cancellationToken);   // no-op SQL when nothing actually changed

        return Ok(await ToResponseAsync(connection, await LoadGuestsAsync(id, cancellationToken), cancellationToken));
    }

    // ── V7.8 — guest card icons ──────────────────────────────────────────────

    /// <summary>
    /// V7.8 — resolved card avatars for every guest on the host, as a
    /// <c>vmId → data:URI</c> map. Per guest: the user's custom upload wins,
    /// else the official OS icon derived from the guest's <c>ostype</c>, else the
    /// entry is omitted (the card shows a placeholder). Kept off the connection
    /// response so the shared mapper stays sync + icon-free; the page overlays
    /// this map onto its cards.
    /// </summary>
    [HttpGet("{id:guid}/guest-icons")]
    public async Task<ActionResult<Dictionary<string, string>>> GuestIcons(
        Guid id, CancellationToken cancellationToken)
    {
        var connection = await LoadOwnedAsync(id, tracking: false, cancellationToken);
        if (connection is null) return NotFound();

        var userId = UserId;
        var custom = await db.ProxmoxGuestIcons.AsNoTracking()
            .Where(i => i.UserId == userId && i.ProxmoxConnectionId == id
                && i.IconSource == ContainerIconSource.Custom && i.LogoBase64 != null)
            .ToDictionaryAsync(i => i.VmId, i => i.LogoBase64!, cancellationToken);

        var guests = await db.ProxmoxGuests.AsNoTracking()
            .Where(g => g.ProxmoxConnectionId == id && g.GuestType != ProxmoxGuestType.Node)
            .Select(g => new { g.VmId, g.OsType })
            .ToListAsync(cancellationToken);

        var result = new Dictionary<string, string>();
        foreach (var guest in guests)
        {
            string? dataUri = custom.GetValueOrDefault(guest.VmId)
                ?? await iconResolver.ResolveIconForOsAsync(guest.OsType, cancellationToken);
            if (dataUri is not null)
                result[guest.VmId.ToString()] = dataUri;
        }

        return Ok(result);
    }

    /// <summary>
    /// V7.8 — set a custom icon for a guest. The image arrives as a base64 data URI
    /// in a JSON body (no <c>multipart/form-data</c> / <c>IFormFile</c>) and is
    /// stored straight on the override row; no file is written to disk. Upserts the
    /// row to <see cref="ContainerIconSource.Custom"/>.
    /// </summary>
    [HttpPost("{id:guid}/guests/{vmId:int}/icon")]
    public async Task<ActionResult<object>> UploadGuestIcon(
        Guid id, int vmId, [FromBody] ContainerIconUploadRequest request, CancellationToken cancellationToken)
    {
        var connection = await LoadOwnedAsync(id, tracking: false, cancellationToken);
        if (connection is null) return NotFound();
        if (!ImageDataUri.TryValidate(request?.DataUri, out var error))
            return BadRequest(new { error });

        var icon = await LoadGuestIconAsync(id, vmId, cancellationToken);
        if (icon is null)
        {
            icon = new ProxmoxGuestIconEntity { UserId = UserId, ProxmoxConnectionId = id, VmId = vmId };
            db.ProxmoxGuestIcons.Add(icon);
        }

        icon.IconSource = ContainerIconSource.Custom;
        icon.LogoBase64 = request!.DataUri;
        await db.SaveChangesAsync(cancellationToken);
        return Ok(new { stored = true });
    }

    /// <summary>V7.8 — reset a guest's icon back to Auto: drops the override row so
    /// the OS icon / placeholder takes over again.</summary>
    [HttpDelete("{id:guid}/guests/{vmId:int}/icon")]
    public async Task<IActionResult> ResetGuestIcon(Guid id, int vmId, CancellationToken cancellationToken)
    {
        var connection = await LoadOwnedAsync(id, tracking: false, cancellationToken);
        if (connection is null) return NotFound();

        var icon = await LoadGuestIconAsync(id, vmId, cancellationToken);
        if (icon is not null)
        {
            db.ProxmoxGuestIcons.Remove(icon);
            await db.SaveChangesAsync(cancellationToken);
        }
        return NoContent();
    }

    private Task<ProxmoxGuestIconEntity?> LoadGuestIconAsync(Guid connectionId, int vmId, CancellationToken cancellationToken)
    {
        var userId = UserId;
        return db.ProxmoxGuestIcons.FirstOrDefaultAsync(
            i => i.UserId == userId && i.ProxmoxConnectionId == connectionId && i.VmId == vmId, cancellationToken);
    }

    /// <summary>
    /// V6.7 — turn update monitoring on/off for a single LXC (the Proxmox
    /// analogue of enabling/disabling a Docker watch). Disabled guests are
    /// skipped by scheduled and manual checks and excluded from notifications.
    /// The node row (<c>vmId == 0</c>) is host-controlled and not toggleable.
    /// Returns the whole host so the client's cached card list refreshes.
    /// </summary>
    [HttpPut("{id:guid}/lxc/{vmId:int}/monitoring")]
    public async Task<ActionResult<ProxmoxConnectionResponse>> SetLxcMonitoring(
        Guid id, int vmId, [FromBody] ProxmoxGuestMonitoringRequest request, CancellationToken cancellationToken)
    {
        if (vmId == 0)
            return BadRequest(new { error = "The node row is host-controlled and can't be toggled per-guest." });

        var connection = await LoadOwnedAsync(id, tracking: false, cancellationToken);
        if (connection is null) return NotFound();

        var guest = await db.ProxmoxGuests.AsTracking()
            .FirstOrDefaultAsync(g => g.ProxmoxConnectionId == id && g.VmId == vmId, cancellationToken);
        if (guest is null) return NotFound();

        var changed = guest.MonitoringEnabled != request.Enabled;
        guest.MonitoringEnabled = request.Enabled;
        // Turning monitoring off clears the stale count so the card drops its
        // "updates pending" emphasis immediately (re-enabling re-populates it on
        // the next check).
        if (!request.Enabled) guest.PendingUpdates = null;
        guest.UpdatedUtc = DateTime.UtcNow;

        // V6.11 — record the change for the audit trail (only when it actually
        // flips, so re-asserting the same state isn't noise).
        if (changed)
            AddMonitoringAudit(connection, guest,
                request.Enabled ? ProxmoxMonitoringChangeType.Enabled : ProxmoxMonitoringChangeType.Disabled,
                bulk: false);

        await db.SaveChangesAsync(cancellationToken);

        return Ok(await ToResponseAsync(connection, await LoadGuestsAsync(id, cancellationToken), cancellationToken));
    }

    /// <summary>
    /// V7.9 — link this guest to a single service (or unlink with a null
    /// <c>webResourceId</c>), the Proxmox analogue of a Docker watch's "Linked
    /// service" dropdown. Replaces any existing service link for the guest, so a
    /// guest belongs to at most one service. Returns the whole host so the client's
    /// cached card list refreshes with the new link.
    /// </summary>
    [HttpPut("{id:guid}/guests/{vmId:int}/service")]
    public async Task<ActionResult<ProxmoxConnectionResponse>> SetGuestService(
        Guid id, int vmId, [FromBody] ProxmoxGuestServiceLinkRequest request, CancellationToken cancellationToken)
    {
        if (vmId == 0)
            return BadRequest(new { error = "The Proxmox node cannot be linked — link an LXC or VM guest." });

        var connection = await LoadOwnedAsync(id, tracking: false, cancellationToken);
        if (connection is null) return NotFound();

        var guestExists = await db.ProxmoxGuests.AsNoTracking().AnyAsync(
            g => g.ProxmoxConnectionId == id && g.VmId == vmId && g.GuestType != ProxmoxGuestType.Node,
            cancellationToken);
        if (!guestExists) return NotFound();

        var userId = UserId;
        if (request.WebResourceId is { } serviceId
            && !await db.WebResources.AnyAsync(s => s.Id == serviceId && s.UserId == userId, cancellationToken))
            return BadRequest(new { error = "Service does not exist." });

        // Single-service invariant: drop any existing link(s) for this guest, then
        // add the chosen one (a null request just clears).
        var existing = await db.WebResourceProxmoxGuestLinks
            .Where(l => l.ProxmoxConnectionId == id && l.VmId == vmId)
            .ToListAsync(cancellationToken);
        if (existing.Count > 0) db.WebResourceProxmoxGuestLinks.RemoveRange(existing);
        if (request.WebResourceId is { } svc)
            db.WebResourceProxmoxGuestLinks.Add(new WebResourceProxmoxGuestLinkEntity
            {
                WebResourceId = svc,
                ProxmoxConnectionId = id,
                VmId = vmId,
            });
        await db.SaveChangesAsync(cancellationToken);

        return Ok(await ToResponseAsync(connection, await LoadGuestsAsync(id, cancellationToken), cancellationToken));
    }

    /// <summary>
    /// V6.11 — host-wide bulk monitoring toggle: enable or disable update
    /// monitoring for <strong>every</strong> LXC on the host in one call (the
    /// operator-facing "Enable all / Disable all"). Implemented server-side so the
    /// whole flip is one transaction and one audit row per actually-changed guest,
    /// rather than a chatty per-guest loop from the client. The node row is never
    /// touched (it's host-controlled). Returns the whole host so the client's
    /// cached cards refresh.
    /// </summary>
    [HttpPut("{id:guid}/lxc/monitoring/bulk")]
    public async Task<ActionResult<ProxmoxConnectionResponse>> SetBulkLxcMonitoring(
        Guid id, [FromBody] ProxmoxBulkMonitoringRequest request, CancellationToken cancellationToken)
    {
        var connection = await LoadOwnedAsync(id, tracking: false, cancellationToken);
        if (connection is null) return NotFound();

        var guests = await db.ProxmoxGuests.AsTracking()
            .Where(g => g.ProxmoxConnectionId == id && g.GuestType == ProxmoxGuestType.Lxc)
            .ToListAsync(cancellationToken);

        var now = DateTime.UtcNow;
        foreach (var guest in guests)
        {
            if (guest.MonitoringEnabled == request.Enabled) continue;   // already in target state
            guest.MonitoringEnabled = request.Enabled;
            if (!request.Enabled) guest.PendingUpdates = null;
            guest.UpdatedUtc = now;
            AddMonitoringAudit(connection, guest,
                request.Enabled ? ProxmoxMonitoringChangeType.Enabled : ProxmoxMonitoringChangeType.Disabled,
                bulk: true);
        }

        await db.SaveChangesAsync(cancellationToken);
        return Ok(await ToResponseAsync(connection, await LoadGuestsAsync(id, cancellationToken), cancellationToken));
    }

    /// <summary>
    /// V6.11 — snooze one LXC's update monitoring for a maintenance window. While
    /// the (future) instant in <see cref="ProxmoxGuestSnoozeRequest.Until"/> hasn't
    /// passed the guest is skipped by scheduled and manual checks exactly like
    /// monitoring-off, then the scan service auto-re-includes it once it elapses. A
    /// <c>null</c> <c>Until</c> clears an active snooze immediately. The node row is
    /// never snoozeable.
    /// </summary>
    [HttpPut("{id:guid}/lxc/{vmId:int}/snooze")]
    public async Task<ActionResult<ProxmoxConnectionResponse>> SnoozeLxcMonitoring(
        Guid id, int vmId, [FromBody] ProxmoxGuestSnoozeRequest request, CancellationToken cancellationToken)
    {
        if (vmId == 0)
            return BadRequest(new { error = "The node row is host-controlled and can't be snoozed." });

        if (request.Until is { } until && until <= DateTime.UtcNow)
            return BadRequest(new { error = "The snooze-until time must be in the future." });

        var connection = await LoadOwnedAsync(id, tracking: false, cancellationToken);
        if (connection is null) return NotFound();

        var guest = await db.ProxmoxGuests.AsTracking()
            .FirstOrDefaultAsync(g => g.ProxmoxConnectionId == id && g.VmId == vmId, cancellationToken);
        if (guest is null) return NotFound();

        var wasSnoozed = guest.MonitoringSnoozedUntil is not null;
        guest.MonitoringSnoozedUntil = request.Until;
        // Drop the "updates pending" emphasis while snoozed (same as disable);
        // the next scan after the window re-populates it.
        if (request.Until is not null) guest.PendingUpdates = null;
        guest.UpdatedUtc = DateTime.UtcNow;

        if (request.Until is not null)
            AddMonitoringAudit(connection, guest, ProxmoxMonitoringChangeType.Snoozed, bulk: false);
        else if (wasSnoozed)
            AddMonitoringAudit(connection, guest, ProxmoxMonitoringChangeType.SnoozeCleared, bulk: false);

        await db.SaveChangesAsync(cancellationToken);
        return Ok(await ToResponseAsync(connection, await LoadGuestsAsync(id, cancellationToken), cancellationToken));
    }

    /// <summary>Writes one monitoring-change audit row (host / guest details
    /// denormalised at write time so the history survives a rename / delete). The
    /// row is added to the change tracker; the caller owns the <c>SaveChanges</c>
    /// so it lands in the same transaction as the toggle itself.</summary>
    private void AddMonitoringAudit(
        ProxmoxConnectionEntity connection, ProxmoxGuestEntity guest,
        ProxmoxMonitoringChangeType changeType, bool bulk)
    {
        var nowUtc = DateTime.UtcNow;
        db.ProxmoxMonitoringAudits.Add(new ProxmoxMonitoringAuditEntity
        {
            Id = Guid.NewGuid(),
            InitiatedByUserId = UserId,
            ProxmoxConnectionId = connection.Id,
            ConnectionName = connection.Name,
            NodeName = connection.NodeName,
            VmId = guest.VmId,
            GuestName = guest.Name,
            ChangeType = changeType,
            MonitoringEnabled = guest.MonitoringEnabled,
            SnoozedUntil = changeType == ProxmoxMonitoringChangeType.Snoozed ? guest.MonitoringSnoozedUntil : null,
            Bulk = bulk,
            ChangedUtc = nowUtc,
            CreatedUtc = nowUtc,
        });
    }

    /// <summary>
    /// V6.7 — "Check now" from an LXC's Watch tab. Proxmox has no per-container
    /// probe — a check is a single SSH/API sweep of the whole node — so an
    /// enabled guest triggers the same full-node scan as the host-level Check
    /// now (every card refreshes together). A disabled guest returns a
    /// deterministic "disabled" outcome without scanning, mirroring how a
    /// disabled Docker watch reports <c>Disabled</c> rather than an error.
    /// </summary>
    [HttpPost("{id:guid}/lxc/{vmId:int}/check")]
    public async Task<ActionResult<ProxmoxConnectionResponse>> CheckLxcNow(
        Guid id, int vmId, CancellationToken cancellationToken)
    {
        var connection = await LoadOwnedAsync(id, tracking: true, cancellationToken);
        if (connection is null) return NotFound();

        var guest = await db.ProxmoxGuests.AsTracking()
            .FirstOrDefaultAsync(g => g.ProxmoxConnectionId == id && g.VmId == vmId, cancellationToken);
        if (guest is null) return NotFound();

        if (!guest.MonitoringEnabled)
        {
            // Deterministic disabled response — stamp the timestamp but don't
            // scan. The card reads MonitoringEnabled to show "Disabled".
            guest.LastCheckedUtc = DateTime.UtcNow;
            guest.PendingUpdates = null;
            guest.UpdatedUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            return Ok(await ToResponseAsync(connection, await LoadGuestsAsync(id, cancellationToken), cancellationToken));
        }

        await scanService.CheckConnectionAsync(connection, cancellationToken);
        return Ok(await ToResponseAsync(connection, await LoadGuestsAsync(id, cancellationToken), cancellationToken));
    }

    /// <summary>V6.2 — read one LXC's full configuration + live status for the
    /// container modal's Config tab. Reads straight from the Proxmox API (no
    /// persisted snapshot), so it works for any discovered LXC.</summary>
    [HttpGet("{id:guid}/lxc/{vmId:int}/config")]
    public async Task<ActionResult<ProxmoxLxcDetail>> GetLxcConfig(
        Guid id, int vmId, CancellationToken cancellationToken)
    {
        var connection = await LoadOwnedAsync(id, tracking: false, cancellationToken);
        if (connection is null) return NotFound();

        var profile = mapper.BuildProfile(connection);
        try
        {
            var detail = await apiClient.GetLxcDetailAsync(profile, vmId, cancellationToken);
            return Ok(detail);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return StatusCode(502, new { error = $"Proxmox host unreachable: {ex.Message}" });
        }
    }

    /// <summary>V6.3 — RRD time-series for an LXC, for the Stats tab sparklines.</summary>
    [HttpGet("{id:guid}/lxc/{vmId:int}/rrddata")]
    public async Task<ActionResult<IReadOnlyList<ProxmoxLxcRrdPoint>>> GetLxcRrdData(
        Guid id, int vmId, [FromQuery] string timeframe = "hour", CancellationToken cancellationToken = default)
    {
        var connection = await LoadOwnedAsync(id, tracking: false, cancellationToken);
        if (connection is null) return NotFound();

        var profile = mapper.BuildProfile(connection);
        try
        {
            return Ok(await apiClient.GetLxcRrdDataAsync(profile, vmId, timeframe, cancellationToken));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return StatusCode(502, new { error = $"Proxmox host unreachable: {ex.Message}" });
        }
    }

    /// <summary>V6.3 — recent tasks scoped to one LXC, newest first.</summary>
    [HttpGet("{id:guid}/lxc/{vmId:int}/tasks")]
    public async Task<ActionResult<IReadOnlyList<ProxmoxTask>>> GetLxcTasks(
        Guid id, int vmId, CancellationToken cancellationToken)
    {
        var connection = await LoadOwnedAsync(id, tracking: false, cancellationToken);
        if (connection is null) return NotFound();

        var profile = mapper.BuildProfile(connection);
        try
        {
            return Ok(await apiClient.GetLxcTasksAsync(profile, vmId, limit: 25, cancellationToken));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return StatusCode(502, new { error = $"Proxmox host unreachable: {ex.Message}" });
        }
    }

    /// <summary>V6.3 — one task's log, addressed by UPID (query param — UPIDs
    /// contain colons that don't sit cleanly in a route segment).</summary>
    [HttpGet("{id:guid}/tasks/log")]
    public async Task<ActionResult<object>> GetTaskLog(
        Guid id, [FromQuery] string upid, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(upid)) return BadRequest(new { error = "upid is required." });
        var connection = await LoadOwnedAsync(id, tracking: false, cancellationToken);
        if (connection is null) return NotFound();

        var profile = mapper.BuildProfile(connection);
        try
        {
            var log = await apiClient.GetTaskLogAsync(profile, upid, limit: 512, cancellationToken);
            return Ok(new { log });
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return StatusCode(502, new { error = $"Proxmox host unreachable: {ex.Message}" });
        }
    }

    /// <summary>V6.4 — the LXC's current runtime status; polled by the modal's
    /// real-time Stats view (Proxmox has no stats stream for LXC).</summary>
    [HttpGet("{id:guid}/lxc/{vmId:int}/status")]
    public async Task<ActionResult<ProxmoxLxcStatus>> GetLxcStatus(
        Guid id, int vmId, CancellationToken cancellationToken)
    {
        var connection = await LoadOwnedAsync(id, tracking: false, cancellationToken);
        if (connection is null) return NotFound();

        var profile = mapper.BuildProfile(connection);
        try
        {
            return Ok(await apiClient.GetLxcStatusAsync(profile, vmId, cancellationToken));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return StatusCode(502, new { error = $"Proxmox host unreachable: {ex.Message}" });
        }
    }

    /// <summary>V6.4 — start / stop / shutdown / reboot an LXC. Needs the API
    /// token to hold <c>VM.PowerMgmt</c> on the host.
    /// <para>The route token is <c>{verb}</c>, not <c>{action}</c>:
    /// <c>action</c> is a <strong>reserved</strong> MVC routing token (it selects
    /// the action <em>method</em> by name), so <c>…/status/{action}</c> only ever
    /// matched a method literally named after the URL value and otherwise fell
    /// through to the SPA fallback as a 405. The URL is unchanged
    /// (<c>…/status/shutdown</c>).</para>
    /// <para>On success the persisted running state is updated optimistically only
    /// for the deterministic, near-instant verbs (<c>start</c>/<c>reboot</c> ⇒
    /// running, hard <c>stop</c> ⇒ stopped) and the refreshed host is returned, so
    /// the card flips immediately. A graceful <c>shutdown</c> is <em>asynchronous</em>
    /// — the guest OS / agent may take a while or ignore it (a QEMU VM with no
    /// guest-agent in particular) — so it is deliberately <strong>not</strong>
    /// flipped here; the next live-status sync reconciles the card against the host
    /// once the guest actually stops, instead of showing "stopped" while it's still
    /// running.</para></summary>
    [HttpPost("{id:guid}/lxc/{vmId:int}/status/{verb}")]
    public Task<ActionResult<ProxmoxConnectionResponse>> LxcAction(
        Guid id, int vmId, string verb, CancellationToken cancellationToken) =>
        GuestActionAsync(id, vmId, verb, qemu: false, cancellationToken);

    /// <summary>V6.14 — start / stop / shutdown / reboot a QEMU VM. The VM analogue
    /// of <see cref="LxcAction"/> — same verb set, same optimistic card update —
    /// routed to <c>POST …/qemu/{vmid}/status/{verb}</c>. Needs the API token to
    /// hold <c>VM.PowerMgmt</c> on the host.</summary>
    [HttpPost("{id:guid}/qemu/{vmId:int}/status/{verb}")]
    public Task<ActionResult<ProxmoxConnectionResponse>> QemuAction(
        Guid id, int vmId, string verb, CancellationToken cancellationToken) =>
        GuestActionAsync(id, vmId, verb, qemu: true, cancellationToken);

    /// <summary>Shared lifecycle handler for both guest kinds: validate the verb,
    /// run the action through the matching API client method, then optimistically
    /// flip the persisted running state so the card updates immediately (the next
    /// scan reconciles).</summary>
    private async Task<ActionResult<ProxmoxConnectionResponse>> GuestActionAsync(
        Guid id, int vmId, string verb, bool qemu, CancellationToken cancellationToken)
    {
        if (verb is not ("start" or "stop" or "shutdown" or "reboot"))
            return BadRequest(new { error = $"Unsupported action '{verb}'." });

        var connection = await LoadOwnedAsync(id, tracking: false, cancellationToken);
        if (connection is null) return NotFound();

        var profile = mapper.BuildProfile(connection);
        try
        {
            if (qemu) await apiClient.QemuStatusActionAsync(profile, vmId, verb, cancellationToken);
            else await apiClient.LxcStatusActionAsync(profile, vmId, verb, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return StatusCode(502, new { error = $"Proxmox host unreachable: {ex.Message}" });
        }

        // Optimistically reflect the intent so the card updates now — but only for
        // the verbs whose outcome is immediate and certain. A graceful `shutdown`
        // is async (the guest may take time or ignore it — a QEMU VM with no
        // guest-agent especially), so we leave its state untouched and let the next
        // live-status sync reconcile it, rather than show "stopped" while the guest
        // is still running on the host. The next scan re-reads the real state.
        if (verb is "start" or "reboot" or "stop")
        {
            var guest = await db.ProxmoxGuests.AsTracking()
                .FirstOrDefaultAsync(g => g.ProxmoxConnectionId == id && g.VmId == vmId, cancellationToken);
            if (guest is not null)
            {
                var nowRunning = verb is "start" or "reboot";
                guest.IsRunning = nowRunning;
                if (!nowRunning) guest.UptimeSeconds = null;   // a hard-stopped guest has no uptime
                guest.UpdatedUtc = DateTime.UtcNow;
                await db.SaveChangesAsync(cancellationToken);
            }
        }

        return Ok(await ToResponseAsync(connection, await LoadGuestsAsync(id, cancellationToken), cancellationToken));
    }

    // ── V6.14 — QEMU VM read endpoints (Config / Stats / Tasks) ──────────────────
    // These mirror the LXC reads one-for-one, routed to the qemu/* API paths. Tasks
    // are node-scoped by vmid (type-agnostic), so the VM Tasks tab reuses the same
    // task-log endpoint as LXC.

    /// <summary>V6.14 — read one VM's configuration + live status for the modal's
    /// (read-only) Config tab.</summary>
    [HttpGet("{id:guid}/qemu/{vmId:int}/config")]
    public Task<ActionResult<ProxmoxLxcDetail>> GetQemuConfig(Guid id, int vmId, CancellationToken cancellationToken) =>
        ReadNodeAsync(id, (p, ct) => apiClient.GetQemuDetailAsync(p, vmId, ct), cancellationToken);

    /// <summary>V6.14 — RRD time-series for a VM, for the Stats tab sparklines.</summary>
    [HttpGet("{id:guid}/qemu/{vmId:int}/rrddata")]
    public Task<ActionResult<IReadOnlyList<ProxmoxLxcRrdPoint>>> GetQemuRrdData(
        Guid id, int vmId, [FromQuery] string timeframe = "hour", CancellationToken cancellationToken = default) =>
        ReadNodeAsync(id, (p, ct) => apiClient.GetQemuRrdDataAsync(p, vmId, timeframe, ct), cancellationToken);

    /// <summary>V6.14 — recent tasks scoped to one VM, newest first. Tasks are
    /// node-scoped by vmid, so this reuses the same listing as the LXC path.</summary>
    [HttpGet("{id:guid}/qemu/{vmId:int}/tasks")]
    public Task<ActionResult<IReadOnlyList<ProxmoxTask>>> GetQemuTasks(Guid id, int vmId, CancellationToken cancellationToken) =>
        ReadNodeAsync(id, (p, ct) => apiClient.GetLxcTasksAsync(p, vmId, limit: 25, ct), cancellationToken);

    /// <summary>V6.14 — the VM's current runtime status; polled by the modal's
    /// real-time Stats view.</summary>
    [HttpGet("{id:guid}/qemu/{vmId:int}/status")]
    public Task<ActionResult<ProxmoxLxcStatus>> GetQemuStatus(Guid id, int vmId, CancellationToken cancellationToken) =>
        ReadNodeAsync(id, (p, ct) => apiClient.GetQemuStatusAsync(p, vmId, ct), cancellationToken);

    /// <summary>
    /// V6.13 — <strong>destroy</strong> an LXC (the irreversible
    /// <c>DELETE /nodes/{node}/lxc/{vmid}</c>). See <see cref="DestroyGuestAsync"/>
    /// for the shared gating / audit; this is the LXC route.
    /// </summary>
    [HttpDelete("{id:guid}/lxc/{vmId:int}")]
    public Task<IActionResult> DestroyLxc(Guid id, int vmId, CancellationToken cancellationToken) =>
        DestroyGuestAsync(id, vmId, qemu: false, cancellationToken);

    /// <summary>
    /// V6.14 — <strong>destroy</strong> a QEMU VM (the irreversible
    /// <c>DELETE /nodes/{node}/qemu/{vmid}</c>). The VM analogue of
    /// <see cref="DestroyLxc"/> — same triple gate, same audit, routed to the
    /// qemu/* path.
    /// </summary>
    [HttpDelete("{id:guid}/qemu/{vmId:int}")]
    public Task<IActionResult> DestroyQemu(Guid id, int vmId, CancellationToken cancellationToken) =>
        DestroyGuestAsync(id, vmId, qemu: true, cancellationToken);

    /// <summary>
    /// Shared destroy handler for both guest kinds. Triple-gated like the console /
    /// "Update now": the global <c>Stashboard:AllowProxmoxDestroy</c> switch, the
    /// per-host <c>AllowDestroy</c> opt-in, and a stopped guest. The gate failures
    /// are deterministic and returned <em>before</em> any Proxmox call (global
    /// off ⇒ 403, host opt-in off ⇒ 403, running guest ⇒ 409). On success the
    /// now-vanished guest row is removed so the card drops immediately (the next
    /// scan would remove it anyway). Every attempt that reaches the host is
    /// audited — success or failure. Needs the API token to hold
    /// <c>VM.Allocate</c> on the host.
    /// </summary>
    private async Task<IActionResult> DestroyGuestAsync(Guid id, int vmId, bool qemu, CancellationToken cancellationToken)
    {
        var noun = qemu ? "VM" : "container";

        // Gate 1: the server-wide master switch. Deterministic 403 before any
        // host lookup or API call.
        if (!await destroySettings.IsEnabledAsync(cancellationToken))
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                error = "Destroy is disabled on this server. Enable it on the Settings page (Settings → Destroy LXC).",
            });

        var connection = await LoadOwnedAsync(id, tracking: false, cancellationToken);
        if (connection is null) return NotFound();

        // Gate 2: the per-host opt-in.
        if (!connection.AllowDestroy)
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                error = "Destroy is not enabled for this host. Turn on 'Allow destroy' in the host settings.",
            });

        // The node row is host-controlled and is never destroyable.
        if (vmId == 0)
            return BadRequest(new { error = "The node itself can't be destroyed." });

        var guest = await db.ProxmoxGuests.AsTracking()
            .FirstOrDefaultAsync(g => g.ProxmoxConnectionId == id && g.VmId == vmId, cancellationToken);
        if (guest is null) return NotFound();

        // Gate 3: refuse a running guest — it must be stopped first. Read from the
        // last scan's persisted state, so this 409 is deterministic and needs no
        // API call; Proxmox itself also refuses to destroy a running guest (defense
        // in depth).
        if (guest.IsRunning)
            return Conflict(new { error = $"Stop the {noun} before destroying it." });

        var guestName = guest.Name;
        var profile = mapper.BuildProfile(connection);
        try
        {
            if (qemu) await apiClient.DeleteQemuAsync(profile, vmId, cancellationToken);
            else await apiClient.DeleteLxcAsync(profile, vmId, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // The host rejected the destroy — audit the failed attempt, then
            // surface the host's own message.
            db.ProxmoxDestroyAudits.Add(BuildDestroyAudit(connection, vmId, guestName, success: false, ex.Message));
            await db.SaveChangesAsync(cancellationToken);
            logger.LogWarning(ex, "{Noun} destroy failed: host {ConnectionId}, vmid {VmId}.", noun, connection.Id, vmId);
            return StatusCode(502, new { error = $"Proxmox rejected the destroy: {ex.Message}" });
        }

        // Success: drop the now-vanished guest row + write the audit in one
        // transaction so the card disappears immediately and the action is
        // recorded atomically.
        db.ProxmoxGuests.Remove(guest);
        db.ProxmoxDestroyAudits.Add(BuildDestroyAudit(connection, vmId, guestName, success: true, error: null));
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "{Noun} destroyed: user {UserId} → host {ConnectionId}, vmid {VmId} ({GuestName}).",
            noun, UserId, connection.Id, vmId, guestName);
        return NoContent();
    }

    /// <summary>Builds one destroy-audit row with the host / guest details
    /// denormalised at write time so the history survives a rename / delete.</summary>
    private ProxmoxDestroyAuditEntity BuildDestroyAudit(
        ProxmoxConnectionEntity connection, int vmId, string? guestName, bool success, string? error)
    {
        var now = DateTime.UtcNow;
        return new ProxmoxDestroyAuditEntity
        {
            Id = Guid.NewGuid(),
            InitiatedByUserId = UserId,
            ProxmoxConnectionId = connection.Id,
            ConnectionName = connection.Name,
            NodeName = connection.NodeName,
            VmId = vmId,
            GuestName = guestName,
            Success = success,
            Error = error,
            DestroyedUtc = now,
            CreatedUtc = now,
        };
    }

    /// <summary>
    /// V6.13.1 — <strong>create</strong> an LXC from a template
    /// (<c>POST /nodes/{node}/lxc</c>). Double-gated like destroy minus the
    /// running-guest check (there is no guest yet): the global
    /// <c>Stashboard:AllowProxmoxCreate</c> switch and the per-host
    /// <c>AllowCreate</c> opt-in. Gate failures are deterministic and returned
    /// before any Proxmox call (global off / host opt-in off ⇒ 403). A vmid already
    /// present in the host's last scan is rejected (409) before the API call; the
    /// locally-checkable guards (valid CIDR/MAC, positive sizes, vmid range) return
    /// 400. On success the host is re-scanned so the new card appears without
    /// waiting for the schedule, and the refreshed host is returned. Every attempt
    /// that reaches the host is audited — success or failure. Needs the API token
    /// to hold <c>VM.Allocate</c> on the host.
    /// </summary>
    [HttpPost("{id:guid}/lxc")]
    public async Task<IActionResult> CreateLxc(
        Guid id, [FromBody] ProxmoxLxcCreateRequest request, CancellationToken cancellationToken)
    {
        // Gate 1: the server-wide master switch. Deterministic 403 before any
        // host lookup or API call.
        if (!await createSettings.IsEnabledAsync(cancellationToken))
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                error = "Create is disabled on this server. Enable it on the Settings page (Settings → Create LXC).",
            });

        var connection = await LoadOwnedAsync(id, tracking: true, cancellationToken);
        if (connection is null) return NotFound();

        // Gate 2: the per-host opt-in.
        if (!connection.AllowCreate)
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                error = "Create is not enabled for this host. Turn on 'Allow create' in the host settings.",
            });

        var hostname = string.IsNullOrWhiteSpace(request.Hostname) ? null : request.Hostname.Trim();
        var spec = new ProxmoxLxcCreate(
            request.VmId,
            request.OsTemplate.Trim(),
            request.RootfsStorage.Trim(),
            request.RootfsSizeGib,
            hostname,
            string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim(),
            string.IsNullOrWhiteSpace(request.Tags) ? null : request.Tags.Trim(),
            request.Password,
            request.SshPublicKeys,
            request.Cores,
            request.MemoryMib,
            request.SwapMib,
            request.Net0,
            request.Unprivileged,
            request.Onboot,
            request.Start,
            request.Nesting,
            string.IsNullOrWhiteSpace(request.Nameserver) ? null : request.Nameserver.Trim(),
            string.IsNullOrWhiteSpace(request.SearchDomain) ? null : request.SearchDomain.Trim(),
            request.AddToHa);

        var problems = ProxmoxLxcCreateValidator.Validate(spec);
        if (problems.Count > 0)
            return BadRequest(new { error = string.Join(" ", problems) });

        // Reject a vmid we already know about from the last scan — a clean 409
        // before the API call (Proxmox also refuses it; this is defense in depth +
        // a friendlier message).
        var vmidTaken = await db.ProxmoxGuests.AsNoTracking()
            .AnyAsync(g => g.ProxmoxConnectionId == id && g.VmId == request.VmId, cancellationToken);
        if (vmidTaken)
            return Conflict(new { error = $"VMID {request.VmId} is already in use on this host." });

        var profile = mapper.BuildProfile(connection);
        try
        {
            await apiClient.CreateLxcAsync(profile, spec, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            db.ProxmoxCreateAudits.Add(BuildCreateAudit(connection, spec, success: false, ex.Message));
            await db.SaveChangesAsync(cancellationToken);
            logger.LogWarning(ex, "LXC create failed: host {ConnectionId}, CT {VmId}.", connection.Id, spec.VmId);
            return StatusCode(502, new { error = $"Proxmox rejected the create: {ex.Message}" });
        }

        db.ProxmoxCreateAudits.Add(BuildCreateAudit(connection, spec, success: true, error: null));
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "LXC created: user {UserId} → host {ConnectionId}, CT {VmId} ({Hostname}).",
            UserId, connection.Id, spec.VmId, hostname);

        // Best-effort HA registration — the container already exists, so a cluster
        // without HA configured must not turn the whole create into a failure.
        if (spec.AddToHa)
        {
            try { await apiClient.AddHaResourceAsync(profile, spec.VmId, cancellationToken); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                logger.LogWarning(ex, "LXC created but HA registration failed: host {ConnectionId}, CT {VmId}.",
                    connection.Id, spec.VmId);
            }
        }

        // Re-scan so the brand-new container appears as a card immediately (the
        // scan's upsert adds the new guest), then return the refreshed host.
        await scanService.CheckConnectionAsync(connection, cancellationToken);
        return Ok(await ToResponseAsync(connection, await LoadGuestsAsync(id, cancellationToken), cancellationToken));
    }

    /// <summary>V6.13.1 — the next free guest id (<c>GET /cluster/nextid</c>) to
    /// default the create form's vmid.</summary>
    [HttpGet("{id:guid}/lxc/nextid")]
    public Task<ActionResult<ProxmoxNextIdResponse>> GetNextVmId(Guid id, CancellationToken cancellationToken) =>
        ReadNodeAsync(id, async (p, ct) => new ProxmoxNextIdResponse(await apiClient.GetNextVmIdAsync(p, ct)), cancellationToken);

    /// <summary>V6.13.1 — the available container templates across the node's
    /// template-capable storages, for the create form's template dropdown.</summary>
    [HttpGet("{id:guid}/lxc/templates")]
    public Task<ActionResult<IReadOnlyList<ProxmoxTemplate>>> GetTemplates(Guid id, CancellationToken cancellationToken) =>
        ReadNodeAsync(id, (p, ct) => apiClient.ListTemplatesAsync(p, ct), cancellationToken);

    /// <summary>Builds one create-audit row with the host / spec details
    /// denormalised at write time so the history survives a rename / delete.</summary>
    private ProxmoxCreateAuditEntity BuildCreateAudit(
        ProxmoxConnectionEntity connection, ProxmoxLxcCreate spec, bool success, string? error)
    {
        var now = DateTime.UtcNow;
        return new ProxmoxCreateAuditEntity
        {
            Id = Guid.NewGuid(),
            InitiatedByUserId = UserId,
            ProxmoxConnectionId = connection.Id,
            ConnectionName = connection.Name,
            NodeName = connection.NodeName,
            VmId = spec.VmId,
            Hostname = spec.Hostname,
            Template = spec.OsTemplate,
            Success = success,
            Error = error,
            CreatedAtUtc = now,
            CreatedUtc = now,
        };
    }

    // ── V8.0 — clone & snapshot ──────────────────────────────────────────────

    /// <summary>
    /// V8.0 — <strong>clone</strong> an existing LXC into a new one
    /// (<c>POST …/lxc/{vmid}/clone</c>). Double-gated like create: the global
    /// <c>Stashboard:AllowProxmoxClone</c> switch and the per-host
    /// <c>AllowClone</c> opt-in (both deterministic 403s before any API call). The
    /// destination vmid is validated (range) and rejected (409) when it's already in
    /// the host's last scan, mirroring create. On success the host is re-scanned so
    /// the cloned guest's card appears without waiting for the schedule, and the
    /// refreshed host is returned. Every attempt that reaches the host is audited —
    /// success or failure. Needs the API token to hold <c>VM.Allocate</c>.
    /// </summary>
    [HttpPost("{id:guid}/lxc/{vmId:int}/clone")]
    public async Task<IActionResult> CloneLxc(
        Guid id, int vmId, [FromBody] ProxmoxLxcCloneRequest request, CancellationToken cancellationToken)
    {
        var (error, connection) = await LoadCloneAllowedAsync(id, tracking: true, cancellationToken);
        if (error is not null) return error;

        var spec = new ProxmoxLxcClone(
            request.NewVmId,
            string.IsNullOrWhiteSpace(request.Hostname) ? null : request.Hostname.Trim(),
            string.IsNullOrWhiteSpace(request.TargetStorage) ? null : request.TargetStorage.Trim(),
            request.Full,
            string.IsNullOrWhiteSpace(request.SnapName) ? null : request.SnapName.Trim(),
            string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim());

        var problems = ProxmoxLxcCloneValidator.Validate(spec);
        if (problems.Count > 0)
            return BadRequest(new { error = string.Join(" ", problems) });

        // Reject a destination vmid we already know about from the last scan — a
        // clean 409 before the API call (Proxmox also refuses it; defense in depth +
        // a friendlier message), mirroring create.
        var vmidTaken = await db.ProxmoxGuests.AsNoTracking()
            .AnyAsync(g => g.ProxmoxConnectionId == id && g.VmId == request.NewVmId, cancellationToken);
        if (vmidTaken)
            return Conflict(new { error = $"VMID {request.NewVmId} is already in use on this host." });

        var target = spec.Hostname is { Length: > 0 } h ? $"CT {request.NewVmId} ({h})" : $"CT {request.NewVmId}";
        var profile = mapper.BuildProfile(connection!);
        try
        {
            await apiClient.CloneLxcAsync(profile, vmId, spec, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            await WriteCloneAuditAsync(connection!, vmId, ProxmoxCloneAction.Clone, target, success: false, ex.Message, cancellationToken);
            logger.LogWarning(ex, "LXC clone failed: host {ConnectionId}, source CT {VmId} → CT {NewVmId}.",
                connection!.Id, vmId, request.NewVmId);
            return StatusCode(502, new { error = $"Proxmox rejected the clone: {ex.Message}" });
        }

        await WriteCloneAuditAsync(connection!, vmId, ProxmoxCloneAction.Clone, target, success: true, error: null, cancellationToken);
        logger.LogInformation(
            "LXC cloned: user {UserId} → host {ConnectionId}, source CT {VmId} → CT {NewVmId}.",
            UserId, connection!.Id, vmId, request.NewVmId);

        // Re-scan so the brand-new clone appears as a card immediately, then return
        // the refreshed host (mirrors create).
        await scanService.CheckConnectionAsync(connection!, cancellationToken);
        return Ok(await ToResponseAsync(connection!, await LoadGuestsAsync(id, cancellationToken), cancellationToken));
    }

    /// <summary>V8.0 — lists an LXC's snapshots
    /// (<c>GET …/lxc/{vmid}/snapshot</c>) for the modal's Snapshots tab. A read, so
    /// it only needs host ownership; the write actions below carry the clone gate.</summary>
    [HttpGet("{id:guid}/lxc/{vmId:int}/snapshots")]
    public Task<ActionResult<IReadOnlyList<ProxmoxSnapshotResponse>>> ListSnapshots(
        Guid id, int vmId, CancellationToken cancellationToken) =>
        ReadNodeAsync(id, async (p, ct) =>
            (IReadOnlyList<ProxmoxSnapshotResponse>)(await apiClient.ListSnapshotsAsync(p, vmId, ct))
                .Select(s => new ProxmoxSnapshotResponse(s.Name, s.Description, s.SnapTime, s.Parent, s.Vmstate))
                .ToList(),
            cancellationToken);

    /// <summary>V8.0 — takes a snapshot of an LXC
    /// (<c>POST …/lxc/{vmid}/snapshot</c>). Double-gated + audited like clone. The
    /// snapshot name is validated locally; Proxmox stays authoritative (e.g. a
    /// duplicate name is its 400, surfaced verbatim).</summary>
    [HttpPost("{id:guid}/lxc/{vmId:int}/snapshots")]
    public async Task<IActionResult> CreateSnapshot(
        Guid id, int vmId, [FromBody] ProxmoxSnapshotCreateRequest request, CancellationToken cancellationToken)
    {
        var (error, connection) = await LoadCloneAllowedAsync(id, tracking: false, cancellationToken);
        if (error is not null) return error;

        var name = request.Name.Trim();
        if (!ProxmoxLxcCloneValidator.IsValidSnapshotName(name))
            return BadRequest(new { error = "Snapshot name must start with a letter and use only letters, digits, '-' or '_'." });

        var description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
        try
        {
            await apiClient.CreateSnapshotAsync(mapper.BuildProfile(connection!), vmId, name, description, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            await WriteCloneAuditAsync(connection!, vmId, ProxmoxCloneAction.SnapshotCreate, name, success: false, ex.Message, cancellationToken);
            logger.LogWarning(ex, "LXC snapshot create failed: host {ConnectionId}, CT {VmId}, snap {Snap}.", connection!.Id, vmId, name);
            return StatusCode(502, new { error = $"Proxmox rejected the snapshot: {ex.Message}" });
        }

        await WriteCloneAuditAsync(connection!, vmId, ProxmoxCloneAction.SnapshotCreate, name, success: true, error: null, cancellationToken);
        logger.LogInformation("LXC snapshot taken: user {UserId} → host {ConnectionId}, CT {VmId}, snap {Snap}.", UserId, connection!.Id, vmId, name);
        return NoContent();
    }

    /// <summary>V8.0 — rolls an LXC back to a snapshot
    /// (<c>POST …/lxc/{vmid}/snapshot/{name}/rollback</c>). Double-gated + audited;
    /// the UI double-confirms because this <em>discards</em> any state newer than
    /// the snapshot.</summary>
    [HttpPost("{id:guid}/lxc/{vmId:int}/snapshots/{name}/rollback")]
    public async Task<IActionResult> RollbackSnapshot(
        Guid id, int vmId, string name, CancellationToken cancellationToken)
    {
        var (error, connection) = await LoadCloneAllowedAsync(id, tracking: false, cancellationToken);
        if (error is not null) return error;

        if (!ProxmoxLxcCloneValidator.IsValidSnapshotName(name))
            return BadRequest(new { error = "Invalid snapshot name." });

        try
        {
            await apiClient.RollbackSnapshotAsync(mapper.BuildProfile(connection!), vmId, name, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            await WriteCloneAuditAsync(connection!, vmId, ProxmoxCloneAction.SnapshotRollback, name, success: false, ex.Message, cancellationToken);
            logger.LogWarning(ex, "LXC snapshot rollback failed: host {ConnectionId}, CT {VmId}, snap {Snap}.", connection!.Id, vmId, name);
            return StatusCode(502, new { error = $"Proxmox rejected the rollback: {ex.Message}" });
        }

        await WriteCloneAuditAsync(connection!, vmId, ProxmoxCloneAction.SnapshotRollback, name, success: true, error: null, cancellationToken);
        logger.LogInformation("LXC rolled back: user {UserId} → host {ConnectionId}, CT {VmId}, snap {Snap}.", UserId, connection!.Id, vmId, name);
        return NoContent();
    }

    /// <summary>V8.0 — deletes one LXC snapshot
    /// (<c>DELETE …/lxc/{vmid}/snapshot/{name}</c>). Double-gated + audited; the UI
    /// double-confirms.</summary>
    [HttpDelete("{id:guid}/lxc/{vmId:int}/snapshots/{name}")]
    public async Task<IActionResult> DeleteSnapshot(
        Guid id, int vmId, string name, CancellationToken cancellationToken)
    {
        var (error, connection) = await LoadCloneAllowedAsync(id, tracking: false, cancellationToken);
        if (error is not null) return error;

        if (!ProxmoxLxcCloneValidator.IsValidSnapshotName(name))
            return BadRequest(new { error = "Invalid snapshot name." });

        try
        {
            await apiClient.DeleteSnapshotAsync(mapper.BuildProfile(connection!), vmId, name, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            await WriteCloneAuditAsync(connection!, vmId, ProxmoxCloneAction.SnapshotDelete, name, success: false, ex.Message, cancellationToken);
            logger.LogWarning(ex, "LXC snapshot delete failed: host {ConnectionId}, CT {VmId}, snap {Snap}.", connection!.Id, vmId, name);
            return StatusCode(502, new { error = $"Proxmox rejected the snapshot delete: {ex.Message}" });
        }

        await WriteCloneAuditAsync(connection!, vmId, ProxmoxCloneAction.SnapshotDelete, name, success: true, error: null, cancellationToken);
        logger.LogInformation("LXC snapshot deleted: user {UserId} → host {ConnectionId}, CT {VmId}, snap {Snap}.", UserId, connection!.Id, vmId, name);
        return NoContent();
    }

    /// <summary>V8.0 — the clone/snapshot audit rows for one guest, newest first,
    /// for the modal's Audit tab. A read, so it only needs host ownership; the
    /// history outlives a later gate-off so it is intentionally ungated.</summary>
    [HttpGet("{id:guid}/lxc/{vmId:int}/clone-audit")]
    public async Task<ActionResult<IReadOnlyList<ProxmoxCloneAuditResponse>>> GetCloneAudit(
        Guid id, int vmId, CancellationToken cancellationToken)
    {
        var connection = await LoadOwnedAsync(id, tracking: false, cancellationToken);
        if (connection is null) return NotFound();

        var rows = await db.ProxmoxCloneAudits.AsNoTracking()
            .Where(a => a.ProxmoxConnectionId == id && a.VmId == vmId)
            .OrderByDescending(a => a.CreatedAtUtc)
            .Take(100)
            .ToListAsync(cancellationToken);

        return Ok(rows.Select(a => new ProxmoxCloneAuditResponse(
            a.Id, a.ProxmoxConnectionId, a.ConnectionName, a.NodeName, a.VmId,
            a.Action, a.Target, a.Success, a.Error, a.CreatedAtUtc)).ToList());
    }

    /// <summary>Shared two-gate check for the clone/snapshot write actions: the
    /// global <c>AllowProxmoxClone</c> switch (deterministic 403 before any host
    /// lookup) and the per-host <c>AllowClone</c> opt-in (403). Returns the loaded
    /// connection on success.</summary>
    private async Task<(IActionResult? Error, ProxmoxConnectionEntity? Connection)> LoadCloneAllowedAsync(
        Guid id, bool tracking, CancellationToken cancellationToken)
    {
        if (!await cloneSettings.IsEnabledAsync(cancellationToken))
            return (StatusCode(StatusCodes.Status403Forbidden, new
            {
                error = "Clone/snapshot is disabled on this server. Enable it on the Settings page (Settings → Clone/snapshot LXC).",
            }), null);

        var connection = await LoadOwnedAsync(id, tracking, cancellationToken);
        if (connection is null) return (NotFound(), null);

        if (!connection.AllowClone)
            return (StatusCode(StatusCodes.Status403Forbidden, new
            {
                error = "Clone/snapshot is not enabled for this host. Turn on 'Allow clone/snapshot' in the host settings.",
            }), null);

        return (null, connection);
    }

    /// <summary>Writes one clone/snapshot audit row (host details denormalised at
    /// write time so the history survives a rename / delete) and saves it.</summary>
    private async Task WriteCloneAuditAsync(
        ProxmoxConnectionEntity connection, int vmId, ProxmoxCloneAction action, string? target,
        bool success, string? error, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        db.ProxmoxCloneAudits.Add(new ProxmoxCloneAuditEntity
        {
            Id = Guid.NewGuid(),
            InitiatedByUserId = UserId,
            ProxmoxConnectionId = connection.Id,
            ConnectionName = connection.Name,
            NodeName = connection.NodeName,
            VmId = vmId,
            Action = action,
            Target = target,
            Success = success,
            Error = error,
            CreatedAtUtc = now,
            CreatedUtc = now,
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>V6.5 / V6.9 — edit one LXC's config: the scalar subset
    /// (cores / memory / swap / hostname / onboot) plus structured
    /// network / mount / rootfs add/update/remove operations. Locally-checkable
    /// safety guards (rootfs removal, duplicate names/paths, malformed IP/CIDR,
    /// impossible sizes) are rejected up front with a 400; everything else writes
    /// through to <c>PUT …/lxc/{vmid}/config</c>, which needs the API token to
    /// hold the relevant <c>VM.Config.*</c> right. Returns 204 on success; a
    /// permission / validation failure from Proxmox surfaces as a 502 with the
    /// host's own message, verbatim.</summary>
    [HttpPut("{id:guid}/lxc/{vmId:int}/config")]
    public async Task<IActionResult> UpdateLxcConfig(
        Guid id, int vmId, [FromBody] ProxmoxLxcConfigUpdateRequest request, CancellationToken cancellationToken)
    {
        var connection = await LoadOwnedAsync(id, tracking: false, cancellationToken);
        if (connection is null) return NotFound();

        var hostname = string.IsNullOrWhiteSpace(request.Hostname) ? null : request.Hostname.Trim();
        var update = new ProxmoxLxcConfigUpdate(
            request.Cores, request.MemoryMib, request.SwapMib, hostname, request.Onboot,
            request.Networks, request.Mounts, request.Rootfs);

        var problems = ProxmoxLxcConfigValidator.Validate(update);
        if (problems.Count > 0)
            return BadRequest(new { error = string.Join(" ", problems) });

        var profile = mapper.BuildProfile(connection);
        try
        {
            await apiClient.UpdateLxcConfigAsync(profile, vmId, update, cancellationToken);
            return NoContent();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return StatusCode(502, new { error = $"Proxmox rejected the config update: {ex.Message}" });
        }
    }

    // ── V6.8 — PVE node card ─────────────────────────────────────────────────

    /// <summary>V6.8 — the node's own health snapshot (CPU / RAM / load / root
    /// fs / kernel / subscription) for the node card + modal Overview.</summary>
    [HttpGet("{id:guid}/node/status")]
    public Task<ActionResult<ProxmoxNodeStatus>> GetNodeStatus(Guid id, CancellationToken cancellationToken) =>
        ReadNodeAsync(id, (p, ct) => apiClient.GetNodeStatusAsync(p, ct), cancellationToken);

    /// <summary>V6.8 — the node's RRD time-series for the CPU/RAM + Network
    /// sparklines.</summary>
    [HttpGet("{id:guid}/node/rrddata")]
    public Task<ActionResult<IReadOnlyList<ProxmoxNodeRrdPoint>>> GetNodeRrdData(
        Guid id, [FromQuery] string timeframe = "hour", CancellationToken cancellationToken = default) =>
        ReadNodeAsync(id, (p, ct) => apiClient.GetNodeRrdDataAsync(p, timeframe, ct), cancellationToken);

    /// <summary>V6.8 — per-storage-pool usage for the Storage/SMART tab.</summary>
    [HttpGet("{id:guid}/node/storage")]
    public Task<ActionResult<IReadOnlyList<ProxmoxNodeStorage>>> GetNodeStorage(
        Guid id, CancellationToken cancellationToken) =>
        ReadNodeAsync(id, (p, ct) => apiClient.GetNodeStoragesAsync(p, ct), cancellationToken);

    /// <summary>V6.8 — physical disks + SMART health summary for the
    /// Storage/SMART tab.</summary>
    [HttpGet("{id:guid}/node/disks")]
    public Task<ActionResult<IReadOnlyList<ProxmoxNodeDisk>>> GetNodeDisks(
        Guid id, CancellationToken cancellationToken) =>
        ReadNodeAsync(id, (p, ct) => apiClient.GetNodeDisksAsync(p, ct), cancellationToken);

    /// <summary>V6.8 — detailed SMART for one disk, loaded on demand when a disk
    /// row is expanded. The disk device path is a query param (it contains
    /// slashes).</summary>
    [HttpGet("{id:guid}/node/disks/smart")]
    public async Task<ActionResult<ProxmoxNodeDiskSmart>> GetNodeDiskSmart(
        Guid id, [FromQuery] string disk, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(disk)) return BadRequest(new { error = "disk is required." });
        return await ReadNodeAsync(id, (p, ct) => apiClient.GetNodeDiskSmartAsync(p, disk, ct), cancellationToken);
    }

    /// <summary>V6.8 — configured network interfaces for the Network tab.</summary>
    [HttpGet("{id:guid}/node/network")]
    public Task<ActionResult<IReadOnlyList<ProxmoxNodeNetworkInterface>>> GetNodeNetwork(
        Guid id, CancellationToken cancellationToken) =>
        ReadNodeAsync(id, (p, ct) => apiClient.GetNodeNetworkInterfacesAsync(p, ct), cancellationToken);

    /// <summary>V6.8 — CPU/board temperatures + fan RPMs over SSH (the
    /// Sensors/Temperature tab). Not in the Proxmox REST API. Returns a
    /// 200 with <c>available=false</c> + a reason when SSH or lm-sensors is
    /// missing — never an error, so the tab degrades gracefully.</summary>
    [HttpGet("{id:guid}/node/sensors")]
    public async Task<ActionResult<ProxmoxNodeSensors>> GetNodeSensors(Guid id, CancellationToken cancellationToken)
    {
        var connection = await LoadOwnedAsync(id, tracking: false, cancellationToken);
        if (connection is null) return NotFound();

        var profile = mapper.BuildProfile(connection);
        return Ok(await guestInspector.ReadNodeSensorsAsync(profile, cancellationToken));
    }

    // ── V6.8.2 — PVE node deep telemetry (SSH collectors) ────────────────────
    // Each mirrors the sensors endpoint: a 200 carrying an `available` flag, so a
    // missing source (no SSH, no lvs/smartctl, no thin pools) degrades to "not
    // available" in the UI rather than surfacing as an error.

    /// <summary>V6.8.2 — per-core CPU utilisation + steal and MemAvailable over
    /// SSH, for the CPU/RAM tab's per-core bars and the Overview steal indicator.</summary>
    [HttpGet("{id:guid}/node/cpu")]
    public Task<ActionResult<ProxmoxNodeCpuStats>> GetNodeCpuStats(Guid id, CancellationToken cancellationToken) =>
        ReadNodeSshAsync(id, (p, ct) => guestInspector.ReadNodeCpuStatsAsync(p, ct), cancellationToken);

    /// <summary>V6.8.2 — per-disk IO throughput / IOPS / latency over SSH, for the
    /// Storage/SMART tab's IO section.</summary>
    [HttpGet("{id:guid}/node/diskio")]
    public Task<ActionResult<ProxmoxNodeDiskIo>> GetNodeDiskIo(Guid id, CancellationToken cancellationToken) =>
        ReadNodeSshAsync(id, (p, ct) => guestInspector.ReadNodeDiskIoAsync(p, ct), cancellationToken);

    /// <summary>V6.8.2 — LVM-thin pool fill levels over SSH, for the Storage/SMART
    /// tab's thin-pool warnings.</summary>
    [HttpGet("{id:guid}/node/thinpools")]
    public Task<ActionResult<ProxmoxNodeThinPools>> GetNodeThinPools(Guid id, CancellationToken cancellationToken) =>
        ReadNodeSshAsync(id, (p, ct) => guestInspector.ReadNodeThinPoolsAsync(p, ct), cancellationToken);

    /// <summary>V6.8.2 — per-interface throughput / errors / link over SSH, for
    /// the Network tab's interface rows.</summary>
    [HttpGet("{id:guid}/node/interfaces")]
    public Task<ActionResult<ProxmoxNodeInterfaceStats>> GetNodeInterfaceStats(Guid id, CancellationToken cancellationToken) =>
        ReadNodeSshAsync(id, (p, ct) => guestInspector.ReadNodeInterfaceStatsAsync(p, ct), cancellationToken);

    /// <summary>V6.8.2 — one disk's last SMART self-test + critical counters over
    /// SSH, loaded on demand when a disk row is expanded. The device path is a
    /// query param (it contains slashes).</summary>
    [HttpGet("{id:guid}/node/disks/selftest")]
    public async Task<ActionResult<ProxmoxDiskSelfTest>> GetNodeDiskSelfTest(
        Guid id, [FromQuery] string disk, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(disk)) return BadRequest(new { error = "disk is required." });
        return await ReadNodeSshAsync(id, (p, ct) => guestInspector.ReadNodeDiskSelfTestAsync(p, disk, ct), cancellationToken);
    }

    /// <summary>Shared shell for the V6.8.2 SSH-collector endpoints: own-check the
    /// connection, build the profile, run the collector. The collectors never
    /// throw (they return Available=false on any failure), so there's no 502 path
    /// — unlike the REST-API reads in <see cref="ReadNodeAsync{T}"/>.</summary>
    private async Task<ActionResult<T>> ReadNodeSshAsync<T>(
        Guid id, Func<ProxmoxConnectionProfile, CancellationToken, Task<T>> read, CancellationToken cancellationToken)
    {
        var connection = await LoadOwnedAsync(id, tracking: false, cancellationToken);
        if (connection is null) return NotFound();
        return Ok(await read(mapper.BuildProfile(connection), cancellationToken));
    }

    // ── V6.8.1 — PVE node alerting ───────────────────────────────────────────

    /// <summary>V6.8.1 — the node's alert configuration (enable flag, per-category
    /// mask, threshold overrides) plus the global defaults for placeholders.
    /// Returns the muted default when the node has never been configured.</summary>
    [HttpGet("{id:guid}/node/alerts/settings")]
    public async Task<ActionResult<ProxmoxNodeAlertSettingsResponse>> GetNodeAlertSettings(
        Guid id, CancellationToken cancellationToken)
    {
        var connection = await LoadOwnedAsync(id, tracking: false, cancellationToken);
        if (connection is null) return NotFound();

        var settings = await db.ProxmoxNodeAlertSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.ProxmoxConnectionId == id, cancellationToken);
        return Ok(ProxmoxNodeAlertMapper.ToResponse(settings));
    }

    /// <summary>V6.8.1 — upsert the node's alert configuration. The toggle is the
    /// node analogue of enabling/disabling a Docker watch.</summary>
    [HttpPut("{id:guid}/node/alerts/settings")]
    public async Task<ActionResult<ProxmoxNodeAlertSettingsResponse>> UpdateNodeAlertSettings(
        Guid id, [FromBody] ProxmoxNodeAlertSettingsUpdateRequest request, CancellationToken cancellationToken)
    {
        var connection = await LoadOwnedAsync(id, tracking: false, cancellationToken);
        if (connection is null) return NotFound();

        if (ValidateThresholds(request.Thresholds) is { } error)
            return BadRequest(new { error });

        var settings = await db.ProxmoxNodeAlertSettings.AsTracking()
            .FirstOrDefaultAsync(s => s.ProxmoxConnectionId == id, cancellationToken);
        if (settings is null)
        {
            settings = new ProxmoxNodeAlertSettingsEntity
            {
                Id = Guid.NewGuid(),
                ProxmoxConnectionId = id,
                CreatedUtc = DateTime.UtcNow,
            };
            db.ProxmoxNodeAlertSettings.Add(settings);
        }

        ProxmoxNodeAlertMapper.ApplyUpdate(settings, request);
        await db.SaveChangesAsync(cancellationToken);
        return Ok(ProxmoxNodeAlertMapper.ToResponse(settings));
    }

    /// <summary>V6.8.1 — the node's currently-active alerts (severity + metric +
    /// value + threshold + first-seen) for the Alerts tab. Empty when nothing is
    /// firing or the node isn't opted in.</summary>
    [HttpGet("{id:guid}/node/alerts")]
    public async Task<ActionResult<IReadOnlyList<ProxmoxNodeAlertResponse>>> GetNodeAlerts(
        Guid id, CancellationToken cancellationToken)
    {
        var connection = await LoadOwnedAsync(id, tracking: false, cancellationToken);
        if (connection is null) return NotFound();

        var active = await db.ProxmoxNodeAlertStates.AsNoTracking()
            .Where(s => s.ProxmoxConnectionId == id && s.ActiveLevel != HealthLevel.Ok)
            .OrderBy(s => s.Category)
            .ToListAsync(cancellationToken);
        return Ok(active.Select(ProxmoxNodeAlertMapper.ToAlertResponse).ToList());
    }

    /// <summary>V6.8.1 — run one alert evaluation immediately (the alerting
    /// analogue of "Check now"), then return the active set. A no-op when the
    /// node isn't opted in; may send a notification exactly as a scheduled tick
    /// would.</summary>
    [HttpPost("{id:guid}/node/alerts/check")]
    public async Task<ActionResult<IReadOnlyList<ProxmoxNodeAlertResponse>>> CheckNodeAlerts(
        Guid id, CancellationToken cancellationToken)
    {
        var connection = await LoadOwnedAsync(id, tracking: true, cancellationToken);
        if (connection is null) return NotFound();

        await alertService.EvaluateConnectionAsync(connection, cancellationToken);

        var active = await db.ProxmoxNodeAlertStates.AsNoTracking()
            .Where(s => s.ProxmoxConnectionId == id && s.ActiveLevel != HealthLevel.Ok)
            .OrderBy(s => s.Category)
            .ToListAsync(cancellationToken);
        return Ok(active.Select(ProxmoxNodeAlertMapper.ToAlertResponse).ToList());
    }

    /// <summary>Validates the threshold overrides: percentages 1..100, °C 1..150,
    /// and warn &lt; crit for any pair where both are supplied. Returns the first
    /// problem message, or <c>null</c> when valid.</summary>
    private static string? ValidateThresholds(ProxmoxNodeAlertThresholdValues t)
    {
        static string? Pct(int? v, string name) =>
            v is { } x && (x < 1 || x > 100) ? $"{name} must be between 1 and 100." : null;
        static string? Temp(int? v, string name) =>
            v is { } x && (x < 1 || x > 150) ? $"{name} must be between 1 and 150." : null;
        static string? Pair(int? warn, int? crit, string name) =>
            warn is { } w && crit is { } c && w >= c ? $"{name} warn must be below crit." : null;

        return Pct(t.CpuWarn, "CPU warn") ?? Pct(t.CpuCrit, "CPU crit")
            ?? Pct(t.MemWarn, "RAM warn") ?? Pct(t.MemCrit, "RAM crit")
            ?? Pct(t.StorageWarn, "Storage warn") ?? Pct(t.StorageCrit, "Storage crit")
            ?? Temp(t.TempWarn, "Temp warn") ?? Temp(t.TempCrit, "Temp crit")
            ?? Pair(t.CpuWarn, t.CpuCrit, "CPU")
            ?? Pair(t.MemWarn, t.MemCrit, "RAM")
            ?? Pair(t.StorageWarn, t.StorageCrit, "Storage")
            ?? Pair(t.TempWarn, t.TempCrit, "Temp");
    }

    /// <summary>Shared shell for the V6.8 node read endpoints: load + own-check
    /// the connection, build the profile, run the API read, and map an
    /// unreachable host to a 502 with the host's message (mirrors the LXC reads).</summary>
    private async Task<ActionResult<T>> ReadNodeAsync<T>(
        Guid id, Func<ProxmoxConnectionProfile, CancellationToken, Task<T>> read, CancellationToken cancellationToken)
    {
        var connection = await LoadOwnedAsync(id, tracking: false, cancellationToken);
        if (connection is null) return NotFound();

        var profile = mapper.BuildProfile(connection);
        try
        {
            return Ok(await read(profile, cancellationToken));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return StatusCode(502, new { error = $"Proxmox host unreachable: {ex.Message}" });
        }
    }

    /// <summary>
    /// V6.7.1 — one-click "Update now" on the Proxmox <strong>node</strong>:
    /// applies pending package updates by SSHing to the host and running
    /// <c>apt-get dist-upgrade</c>, streaming the output as NDJSON. Triple-gated
    /// like the LXC console (global switch + per-host <c>AllowUpdates</c> + SSH)
    /// and audited start-to-finish.
    /// </summary>
    [HttpPost("{id:guid}/node/update")]
    public Task ApplyNodeUpdates(Guid id, CancellationToken cancellationToken) =>
        ApplyUpdatesAsync(id, vmId: 0, cancellationToken);

    /// <summary>
    /// V6.7.1 — one-click "Update now" inside a single <strong>LXC</strong>: runs
    /// <c>pct exec &lt;vmid&gt; -- apt-get dist-upgrade</c> over SSH, streaming
    /// the output as NDJSON. Same gates + audit as the node path.
    /// </summary>
    [HttpPost("{id:guid}/lxc/{vmId:int}/update")]
    public Task ApplyLxcUpdates(Guid id, int vmId, CancellationToken cancellationToken) =>
        ApplyUpdatesAsync(id, vmId, cancellationToken);

    /// <summary>
    /// V6.7.1 — the exact command a one-click "Update now" will run for this
    /// target (node when <c>vmId = 0</c>, else the LXC). Read-only and
    /// deterministic — surfaced so the confirm dialog can show + let the operator
    /// copy it, the way the Docker "Update command" panel does.
    /// </summary>
    [HttpGet("{id:guid}/update-command")]
    public async Task<ActionResult<ProxmoxUpdateCommandResponse>> GetUpdateCommand(
        Guid id, [FromQuery] int vmId, CancellationToken cancellationToken)
    {
        var connection = await LoadOwnedAsync(id, tracking: false, cancellationToken);
        if (connection is null) return NotFound();
        return Ok(new ProxmoxUpdateCommandResponse(updateApplier.BuildCommand(vmId)));
    }

    /// <summary>
    /// Shared apply path for the node (<paramref name="vmId"/> = 0) and single-LXC
    /// cases. Validates the three gates, then streams the apt output line-by-line
    /// while recording a single audit row (start → finalise). Writes a structured
    /// terminal frame so the browser can render success / failure deterministically.
    /// </summary>
    private async Task ApplyUpdatesAsync(Guid id, int vmId, CancellationToken cancellationToken)
    {
        // Gates are plain HTTP results — we haven't switched the response into
        // streaming mode yet, so an error here is a normal JSON response.
        var gate = await ResolveUpdateGatesAsync(id, cancellationToken);
        if (gate.Error is not null) { await WriteGateResultAsync(gate.StatusCode, gate.Error); return; }
        var connection = gate.Connection!;
        var profile = gate.Profile!;

        Response.StatusCode = StatusCodes.Status200OK;
        Response.ContentType = "application/x-ndjson";
        Response.Headers["Cache-Control"] = "no-cache";
        Response.Headers["X-Accel-Buffering"] = "no";
        await Response.Body.FlushAsync(cancellationToken);

        TargetUpdateOutcome outcome;
        try
        {
            outcome = await RunTargetUpdateAsync(connection, profile, vmId,
                (line, ct) => WriteFrameAsync(new { stream = "stdout", message = line }, ct),
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Client disconnected mid-run; the audit row is already finalised.
            return;
        }

        if (outcome.Result is { } result)
        {
            await WriteFrameAsync(new
            {
                stream = "end",
                success = result.IsSuccess,
                exitStatus = result.ExitStatus,
                nonDebian = result.NonDebian,
                error = result.Error,
            }, cancellationToken);
        }
        else
        {
            await WriteFrameAsync(new { stream = "error", message = outcome.EndError ?? "Update failed." },
                CancellationToken.None);
        }
    }

    /// <summary>
    /// V6.11 — apply pending updates across several guests on a host in one
    /// streaming run. Same triple gate as the single path, validated once up
    /// front; then each requested guest's apt log streams in sequence, framed with
    /// <c>guest-start</c> / <c>stdout</c> / <c>guest-end</c> markers (and a final
    /// <c>all-done</c> summary). Each guest gets its own audit session, finalised
    /// before the next begins — so the audit trail is one row per guest, exactly
    /// as if each had been updated individually. The node (<c>vmId 0</c>) may be
    /// included — a host-wide "Update all" can sweep the node together with its
    /// LXCs; it runs the same node upgrade as the node-card "Update now".
    /// </summary>
    [HttpPost("{id:guid}/lxc/update/bulk")]
    public async Task ApplyBulkLxcUpdates(
        Guid id, [FromBody] ProxmoxBulkUpdateRequest request, CancellationToken cancellationToken)
    {
        var gate = await ResolveUpdateGatesAsync(id, cancellationToken);
        if (gate.Error is not null) { await WriteGateResultAsync(gate.StatusCode, gate.Error); return; }
        var connection = gate.Connection!;
        var profile = gate.Profile!;

        // Resolve the requested vmids to real guest rows on this host (node + LXCs);
        // anything unknown is ignored so a stale / hand-crafted request can't drive
        // the loop somewhere unexpected. The node sorts first (vmId 0) so it runs
        // before the containers it hosts.
        var requested = (request.VmIds ?? []).Distinct().ToHashSet();
        var guests = await db.ProxmoxGuests.AsNoTracking()
            .Where(g => g.ProxmoxConnectionId == id && requested.Contains(g.VmId))
            .OrderBy(g => g.VmId)
            .ToListAsync(cancellationToken);

        Response.StatusCode = StatusCodes.Status200OK;
        Response.ContentType = "application/x-ndjson";
        Response.Headers["Cache-Control"] = "no-cache";
        Response.Headers["X-Accel-Buffering"] = "no";
        await Response.Body.FlushAsync(cancellationToken);

        var succeeded = 0;
        var failed = 0;
        foreach (var guest in guests)
        {
            if (cancellationToken.IsCancellationRequested) break;
            await WriteFrameAsync(new { stream = "guest-start", vmId = guest.VmId, name = guest.Name }, cancellationToken);

            TargetUpdateOutcome outcome;
            try
            {
                outcome = await RunTargetUpdateAsync(connection, profile, guest.VmId,
                    (line, ct) => WriteFrameAsync(new { stream = "stdout", vmId = guest.VmId, message = line }, ct),
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            var result = outcome.Result;
            var success = result?.IsSuccess ?? false;
            if (success) succeeded++; else failed++;
            await WriteFrameAsync(new
            {
                stream = "guest-end",
                vmId = guest.VmId,
                name = guest.Name,
                success,
                exitStatus = result?.ExitStatus,
                nonDebian = result?.NonDebian ?? false,
                error = result?.Error ?? outcome.EndError,
            }, cancellationToken);
        }

        await WriteFrameAsync(new { stream = "all-done", total = guests.Count, succeeded, failed }, cancellationToken);
    }

    /// <summary>Result of resolving the three update gates: either an
    /// <see cref="Error"/> (+ status) to return, or the owned
    /// <see cref="Connection"/> + decrypted <see cref="Profile"/> to proceed.</summary>
    private sealed record UpdateGateResult(
        int StatusCode, string? Error, ProxmoxConnectionEntity? Connection, ProxmoxConnectionProfile? Profile);

    /// <summary>Validates the global switch + per-host <c>AllowUpdates</c> + SSH,
    /// shared by the single and bulk apply paths so the gate logic can never
    /// drift.</summary>
    private async Task<UpdateGateResult> ResolveUpdateGatesAsync(Guid id, CancellationToken cancellationToken)
    {
        if (!await updateSettings.IsEnabledAsync(cancellationToken))
            return new(StatusCodes.Status403Forbidden,
                "Update now is disabled on this server. Enable it on the Settings page (Settings → Proxmox updates).",
                null, null);

        var connection = await LoadOwnedAsync(id, tracking: false, cancellationToken);
        if (connection is null)
            return new(StatusCodes.Status404NotFound, "Proxmox host not found.", null, null);

        if (!connection.AllowUpdates)
            return new(StatusCodes.Status403Forbidden,
                "Update now is not enabled for this host. Turn on 'Allow apply updates' in the host settings.",
                null, null);

        var profile = mapper.BuildProfile(connection);
        if (profile.Ssh is null)
            return new(StatusCodes.Status409Conflict,
                "This Proxmox host is missing SSH host / username / private key — applying updates needs SSH.",
                null, null);

        return new(StatusCodes.Status200OK, null, connection, profile);
    }

    /// <summary>Outcome of one target's update run. <see cref="Result"/> is
    /// <c>null</c> when the run threw an unexpected error (already finalised in the
    /// audit row, with the message on <see cref="EndError"/>).</summary>
    private sealed record TargetUpdateOutcome(
        string? TargetName, ProxmoxGuestType TargetType,
        ProxmoxUpdateApplyResult? Result, HostShellSessionEndReason EndReason, string? EndError);

    /// <summary>
    /// Runs one target's apt upgrade end-to-end: opens the audit session, streams
    /// the output through <paramref name="onLine"/>, refreshes the pending-update
    /// count on success, and finalises the audit row regardless of outcome.
    /// Re-throws on client cancellation (after finalising) so the caller can stop
    /// the stream; any other failure is captured on the returned outcome so the
    /// caller can emit an error frame and (for bulk) carry on to the next guest.
    /// Used by both the single and bulk apply paths.
    /// </summary>
    private async Task<TargetUpdateOutcome> RunTargetUpdateAsync(
        ProxmoxConnectionEntity connection, ProxmoxConnectionProfile profile, int vmId,
        Func<string, CancellationToken, Task> onLine, CancellationToken cancellationToken)
    {
        var targetType = vmId == 0 ? ProxmoxGuestType.Node : ProxmoxGuestType.Lxc;
        var targetName = vmId == 0
            ? connection.NodeName
            : await db.ProxmoxGuests.AsNoTracking()
                .Where(g => g.ProxmoxConnectionId == connection.Id && g.VmId == vmId)
                .Select(g => g.Name)
                .FirstOrDefaultAsync(cancellationToken);

        var session = new ProxmoxUpdateSessionEntity
        {
            Id = Guid.NewGuid(),
            InitiatedByUserId = UserId,
            ProxmoxConnectionId = connection.Id,
            ConnectionName = connection.Name,
            NodeName = connection.NodeName,
            TargetType = targetType,
            VmId = vmId,
            TargetName = targetName,
            StartedUtc = DateTime.UtcNow,
            CreatedUtc = DateTime.UtcNow,
            EndReason = HostShellSessionEndReason.Active,
        };
        db.ProxmoxUpdateSessions.Add(session);
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Proxmox Update now started: user {UserId} → host {ConnectionId}, target {TargetType} {VmId}, session {SessionId}.",
            UserId, connection.Id, targetType, vmId, session.Id);

        try
        {
            var result = await updateApplier.ApplyAsync(profile, vmId, onLine, cancellationToken);

            // On success, immediately recompute this target's pending-update count
            // and persist it (mirrors the Docker "Update now" auto-recheck) so the
            // dashboard count + badge drop without a manual Check now.
            if (result.IsSuccess)
                await RefreshTargetCountAsync(connection, profile, vmId, cancellationToken);

            var endReason = result.IsSuccess ? HostShellSessionEndReason.RemoteClosed : HostShellSessionEndReason.Error;
            await FinaliseAuditAsync(session.Id, result.ExitStatus, result.BytesStreamed, endReason, result.Error);

            logger.LogInformation(
                "Proxmox Update now finished: session {SessionId}, exit {Exit}, {Bytes} B, reason {Reason}.",
                session.Id, result.ExitStatus, result.BytesStreamed, endReason);

            return new(targetName, targetType, result, endReason, result.Error);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Client disconnected mid-run; finalise the row and let the caller bail.
            await FinaliseAuditAsync(session.Id, null, 0, HostShellSessionEndReason.ClosedByServer, "Client disconnected.");
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Proxmox Update now failed for session {SessionId}.", session.Id);
            await FinaliseAuditAsync(session.Id, null, 0, HostShellSessionEndReason.Error, ex.Message);
            return new(targetName, targetType, null, HostShellSessionEndReason.Error, ex.Message);
        }
    }

    // ── V6.11 — update-check webhook (the Proxmox analogue of the Docker watch
    //    webhook). Owner-scoped rotate / delete here; the public receiver lives in
    //    ProxmoxWebhooksController. Off by default — the token doesn't exist until
    //    the owner creates one. ─────────────────────────────────────────────────

    /// <summary>V6.11 — generate (or rotate) the host's webhook token. The first
    /// call creates one; a subsequent call rotates it, invalidating the old URL
    /// immediately.</summary>
    [HttpPost("{id:guid}/webhook/rotate")]
    public async Task<ActionResult<ProxmoxConnectionResponse>> RotateWebhookToken(Guid id, CancellationToken cancellationToken)
    {
        var connection = await LoadOwnedAsync(id, tracking: true, cancellationToken);
        if (connection is null) return NotFound();

        // Belt-and-braces collision guard (256-bit token — collisions are
        // astronomically unlikely); mirrors the Docker watch path.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var candidate = webhookTokenGenerator.Generate();
            var collides = await db.ProxmoxConnections.AsNoTracking()
                .AnyAsync(c => c.WebhookToken == candidate, cancellationToken);
            if (collides) continue;

            connection.WebhookToken = candidate;
            connection.UpdatedUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            return Ok(await ToResponseAsync(connection, await LoadGuestsAsync(id, cancellationToken), cancellationToken));
        }

        return StatusCode(StatusCodes.Status500InternalServerError,
            new { error = "Could not generate a unique webhook token. Please retry." });
    }

    /// <summary>V6.11 — drop the host's webhook token. The host immediately stops
    /// accepting webhook deliveries; the schedule-driven scan continues
    /// untouched.</summary>
    [HttpDelete("{id:guid}/webhook")]
    public async Task<ActionResult<ProxmoxConnectionResponse>> DeleteWebhookToken(Guid id, CancellationToken cancellationToken)
    {
        var connection = await LoadOwnedAsync(id, tracking: true, cancellationToken);
        if (connection is null) return NotFound();

        if (connection.WebhookToken is null && connection.LastWebhookReceivedUtc is null)
            return Ok(await ToResponseAsync(connection, await LoadGuestsAsync(id, cancellationToken), cancellationToken));

        connection.WebhookToken = null;
        connection.LastWebhookReceivedUtc = null;
        connection.UpdatedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return Ok(await ToResponseAsync(connection, await LoadGuestsAsync(id, cancellationToken), cancellationToken));
    }

    /// <summary>
    /// Re-reads one target's pending-update count after a successful apply and
    /// persists it onto the guest row, so the dashboard's count + badge drop
    /// immediately. Node (<paramref name="vmId"/> = 0) reads the count over the
    /// REST API; an LXC reads it over SSH (the same paths the scan uses).
    /// Best-effort — a failure here never fails the apply (the upgrade already
    /// happened); the next scheduled scan will reconcile.
    /// </summary>
    private async Task RefreshTargetCountAsync(
        ProxmoxConnectionEntity connection, ProxmoxConnectionProfile profile, int vmId, CancellationToken cancellationToken)
    {
        try
        {
            int? count;
            string? error = null;
            if (vmId == 0)
            {
                count = await apiClient.GetNodePendingUpdatesAsync(profile, cancellationToken);
            }
            else
            {
                var inspection = await guestInspector.CountPendingUpdatesAsync(profile, vmId, cancellationToken);
                count = inspection.PendingUpdates;
                error = inspection.Error;
            }

            var guest = await db.ProxmoxGuests.AsTracking()
                .FirstOrDefaultAsync(g => g.ProxmoxConnectionId == connection.Id && g.VmId == vmId, cancellationToken);
            if (guest is null) return;

            guest.PendingUpdates = count;
            if (vmId != 0) guest.LastError = error;
            guest.LastCheckedUtc = DateTime.UtcNow;
            guest.UpdatedUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Post-apply update-count refresh failed for host {ConnectionId}, target {VmId}.", connection.Id, vmId);
        }
    }

    private static readonly JsonSerializerOptions NdjsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly byte[] NewlineBytes = "\n"u8.ToArray();

    private async Task WriteFrameAsync(object frame, CancellationToken cancellationToken)
    {
        await JsonSerializer.SerializeAsync(Response.Body, frame, NdjsonOptions, cancellationToken);
        await Response.Body.WriteAsync(NewlineBytes, cancellationToken);
        await Response.Body.FlushAsync(cancellationToken);
    }

    /// <summary>Emit a gate failure as a normal JSON error (the response hasn't
    /// switched to streaming mode yet).</summary>
    private async Task WriteGateResultAsync(int statusCode, string error)
    {
        Response.StatusCode = statusCode;
        Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(Response.Body, new { error }, NdjsonOptions);
    }

    private async Task FinaliseAuditAsync(
        Guid sessionId, int? exitStatus, long bytes, HostShellSessionEndReason endReason, string? error)
    {
        try
        {
            var row = await db.ProxmoxUpdateSessions.FirstOrDefaultAsync(s => s.Id == sessionId);
            if (row is null) return;
            row.EndedUtc = DateTime.UtcNow;
            row.ExitStatus = exitStatus;
            row.BytesToClient = bytes;
            row.EndReason = endReason;
            row.Error = error;
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            // Never let an audit-write failure crash the request teardown.
            logger.LogError(ex, "Failed to finalise Proxmox update audit row {SessionId}.", sessionId);
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private Task<ProxmoxConnectionEntity?> LoadOwnedAsync(Guid id, bool tracking, CancellationToken cancellationToken)
    {
        var userId = UserId;
        var query = tracking ? db.ProxmoxConnections.AsTracking() : db.ProxmoxConnections.AsNoTracking();
        return query.FirstOrDefaultAsync(c => c.Id == id && c.UserId == userId, cancellationToken);
    }

    private async Task<IReadOnlyList<ProxmoxGuestEntity>> LoadGuestsAsync(Guid connectionId, CancellationToken cancellationToken) =>
        await db.ProxmoxGuests.AsNoTracking()
            .Where(g => g.ProxmoxConnectionId == connectionId)
            .ToListAsync(cancellationToken);

    /// <summary>V7.9 — map a connection with its guests' linked-service info resolved.</summary>
    private async Task<ProxmoxConnectionResponse> ToResponseAsync(
        ProxmoxConnectionEntity connection, IReadOnlyList<ProxmoxGuestEntity> guests, CancellationToken cancellationToken) =>
        mapper.ToResponse(connection, guests, await LoadGuestServiceLinksAsync(connection.Id, cancellationToken));

    /// <summary>V7.9 — each guest's linked service on this host, keyed by vmid.
    /// Restricted to services the current user owns (the link is single-service
    /// per guest, so first-wins is exact).</summary>
    private async Task<IReadOnlyDictionary<int, (Guid ServiceId, string ServiceName)>> LoadGuestServiceLinksAsync(
        Guid connectionId, CancellationToken cancellationToken)
    {
        var userId = UserId;
        var links = await db.WebResourceProxmoxGuestLinks.AsNoTracking()
            .Where(l => l.ProxmoxConnectionId == connectionId)
            .Join(db.WebResources.Where(s => s.UserId == userId),
                l => l.WebResourceId, s => s.Id,
                (l, s) => new { l.VmId, ServiceId = s.Id, ServiceName = s.Name })
            .ToListAsync(cancellationToken);
        return links
            .GroupBy(l => l.VmId)
            .ToDictionary(g => g.Key, g => (g.First().ServiceId, g.First().ServiceName));
    }

    private static bool IsUniqueNameViolation(DbUpdateException ex) =>
        UniqueConstraintViolation.Matches(ex,
            "IX_ProxmoxConnections_UserId_Name",
            "ProxmoxConnections.UserId", "ProxmoxConnections.Name");
}
