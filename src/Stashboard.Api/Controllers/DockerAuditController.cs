using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Stashboard.Api.Auth;
using Stashboard.Api.Contracts;
using Stashboard.Api.Data;
using Stashboard.Api.Mapping;
using Stashboard.Core.Entities;

namespace Stashboard.Api.Controllers;

/// <summary>
/// V5.8 — read-only session audit viewer. Surfaces the audit rows that V5.3
/// (host terminal) and V5.7 (container exec) already persist to
/// <c>HostShellSessions</c> / <c>DockerExecSessions</c>, plus — as a convenience
/// — the V2.7 update-attempt log and the V5.5 image-prune log, so all four
/// trails are readable in one place. No write/delete verbs: audit rows are
/// immutable from the UI.
/// </summary>
/// <remarks>
/// Every endpoint is owner-scoped and paginated (<c>?skip=&amp;take=</c>, page
/// size capped) and newest-first. The shell / exec / update logs scope by the
/// denormalised <c>InitiatedByUserId</c> so the history survives a connection
/// delete; the prune log also includes scheduled runs (no initiating user) that
/// ran against a connection the caller owns. An optional <c>?connectionId=</c>
/// narrows any tab to a single connection.
/// </remarks>
[ApiController]
[Authorize]
[Route("api/docker")]
public class DockerAuditController(ApplicationDbContext db, IDockerWatchMapper watchMapper) : ControllerBase
{
    private const int DefaultPageSize = 50;
    private const int MaxPageSize = 200;

    [HttpGet("host-shell/sessions")]
    public async Task<ActionResult<IReadOnlyList<HostShellSessionResponse>>> GetHostShellSessions(
        [FromQuery] int skip = 0,
        [FromQuery] int take = DefaultPageSize,
        [FromQuery] Guid? connectionId = null,
        CancellationToken cancellationToken = default)
    {
        var userId = User.GetUserId();
        var (s, t) = Page(skip, take);

        var query = db.HostShellSessions.AsNoTracking()
            .Where(x => x.InitiatedByUserId == userId);
        if (connectionId is { } cid)
            query = query.Where(x => x.DockerConnectionId == cid);

        var rows = await query
            .OrderByDescending(x => x.StartedUtc)
            .Skip(s).Take(t)
            .ToListAsync(cancellationToken);

        return Ok(rows.Select(MapHostShell).ToList());
    }

    [HttpGet("container-exec/sessions")]
    public async Task<ActionResult<IReadOnlyList<DockerExecSessionResponse>>> GetContainerExecSessions(
        [FromQuery] int skip = 0,
        [FromQuery] int take = DefaultPageSize,
        [FromQuery] Guid? connectionId = null,
        CancellationToken cancellationToken = default)
    {
        var userId = User.GetUserId();
        var (s, t) = Page(skip, take);

        var query = db.DockerExecSessions.AsNoTracking()
            .Where(x => x.InitiatedByUserId == userId);
        if (connectionId is { } cid)
            query = query.Where(x => x.DockerConnectionId == cid);

        var rows = await query
            .OrderByDescending(x => x.StartedUtc)
            .Skip(s).Take(t)
            .ToListAsync(cancellationToken);

        return Ok(rows.Select(MapExec).ToList());
    }

    /// <summary>V6.6 — LXC-console sessions (the Proxmox analogue of
    /// container-exec). Absolute route under <c>api/proxmox</c> since these are
    /// scoped by Proxmox host, not Docker connection; surfaced on the same Audit
    /// page so all session trails live in one place. Owner-scoped via the
    /// denormalised <c>InitiatedByUserId</c>; <c>?connectionId=</c> narrows to a
    /// single Proxmox host.</summary>
    [HttpGet("/api/proxmox/console/sessions")]
    public async Task<ActionResult<IReadOnlyList<ProxmoxConsoleSessionResponse>>> GetProxmoxConsoleSessions(
        [FromQuery] int skip = 0,
        [FromQuery] int take = DefaultPageSize,
        [FromQuery] Guid? connectionId = null,
        CancellationToken cancellationToken = default)
    {
        var userId = User.GetUserId();
        var (s, t) = Page(skip, take);

        var query = db.ProxmoxConsoleSessions.AsNoTracking()
            .Where(x => x.InitiatedByUserId == userId);
        if (connectionId is { } cid)
            query = query.Where(x => x.ProxmoxConnectionId == cid);

        var rows = await query
            .OrderByDescending(x => x.StartedUtc)
            .Skip(s).Take(t)
            .ToListAsync(cancellationToken);

        return Ok(rows.Select(MapProxmoxConsole).ToList());
    }

    /// <summary>V6.7.1 — Proxmox "Update now" runs (apply pending package
    /// updates on the node or inside an LXC). Absolute route under
    /// <c>api/proxmox</c> since these are scoped by Proxmox host; surfaced on the
    /// same Audit page. Owner-scoped via the denormalised
    /// <c>InitiatedByUserId</c>; <c>?connectionId=</c> narrows to a single host.</summary>
    [HttpGet("/api/proxmox/updates/sessions")]
    public async Task<ActionResult<IReadOnlyList<ProxmoxUpdateSessionResponse>>> GetProxmoxUpdateSessions(
        [FromQuery] int skip = 0,
        [FromQuery] int take = DefaultPageSize,
        [FromQuery] Guid? connectionId = null,
        CancellationToken cancellationToken = default)
    {
        var userId = User.GetUserId();
        var (s, t) = Page(skip, take);

        var query = db.ProxmoxUpdateSessions.AsNoTracking()
            .Where(x => x.InitiatedByUserId == userId);
        if (connectionId is { } cid)
            query = query.Where(x => x.ProxmoxConnectionId == cid);

        var rows = await query
            .OrderByDescending(x => x.StartedUtc)
            .Skip(s).Take(t)
            .ToListAsync(cancellationToken);

        return Ok(rows.Select(MapProxmoxUpdate).ToList());
    }

    /// <summary>V6.11 — LXC monitoring changes (enable / disable / snooze /
    /// unsnooze, per guest). Absolute route under <c>api/proxmox</c> since these
    /// are scoped by Proxmox host; surfaced on the same Audit page. Owner-scoped
    /// via the denormalised <c>InitiatedByUserId</c>; <c>?connectionId=</c>
    /// narrows to a single host.</summary>
    [HttpGet("/api/proxmox/monitoring/sessions")]
    public async Task<ActionResult<IReadOnlyList<ProxmoxMonitoringAuditResponse>>> GetProxmoxMonitoringAudits(
        [FromQuery] int skip = 0,
        [FromQuery] int take = DefaultPageSize,
        [FromQuery] Guid? connectionId = null,
        CancellationToken cancellationToken = default)
    {
        var userId = User.GetUserId();
        var (s, t) = Page(skip, take);

        var query = db.ProxmoxMonitoringAudits.AsNoTracking()
            .Where(x => x.InitiatedByUserId == userId);
        if (connectionId is { } cid)
            query = query.Where(x => x.ProxmoxConnectionId == cid);

        var rows = await query
            .OrderByDescending(x => x.ChangedUtc)
            .Skip(s).Take(t)
            .ToListAsync(cancellationToken);

        return Ok(rows.Select(MapProxmoxMonitoring).ToList());
    }

    /// <summary>V6.13 — LXC destroys (the irreversible
    /// <c>DELETE …/lxc/{vmid}</c>). Absolute route under <c>api/proxmox</c> since
    /// these are scoped by Proxmox host; surfaced on the same Audit page.
    /// Owner-scoped via the denormalised <c>InitiatedByUserId</c>;
    /// <c>?connectionId=</c> narrows to a single host.</summary>
    [HttpGet("/api/proxmox/destroy/sessions")]
    public async Task<ActionResult<IReadOnlyList<ProxmoxDestroyAuditResponse>>> GetProxmoxDestroyAudits(
        [FromQuery] int skip = 0,
        [FromQuery] int take = DefaultPageSize,
        [FromQuery] Guid? connectionId = null,
        CancellationToken cancellationToken = default)
    {
        var userId = User.GetUserId();
        var (s, t) = Page(skip, take);

        var query = db.ProxmoxDestroyAudits.AsNoTracking()
            .Where(x => x.InitiatedByUserId == userId);
        if (connectionId is { } cid)
            query = query.Where(x => x.ProxmoxConnectionId == cid);

        var rows = await query
            .OrderByDescending(x => x.DestroyedUtc)
            .Skip(s).Take(t)
            .ToListAsync(cancellationToken);

        return Ok(rows.Select(MapProxmoxDestroy).ToList());
    }

    /// <summary>V6.13.1 — LXC creates (the <c>POST …/lxc</c> provision-from-
    /// template). Absolute route under <c>api/proxmox</c> since these are scoped by
    /// Proxmox host; surfaced on the same Audit page. Owner-scoped via the
    /// denormalised <c>InitiatedByUserId</c>; <c>?connectionId=</c> narrows to a
    /// single host.</summary>
    [HttpGet("/api/proxmox/create/sessions")]
    public async Task<ActionResult<IReadOnlyList<ProxmoxCreateAuditResponse>>> GetProxmoxCreateAudits(
        [FromQuery] int skip = 0,
        [FromQuery] int take = DefaultPageSize,
        [FromQuery] Guid? connectionId = null,
        CancellationToken cancellationToken = default)
    {
        var userId = User.GetUserId();
        var (s, t) = Page(skip, take);

        var query = db.ProxmoxCreateAudits.AsNoTracking()
            .Where(x => x.InitiatedByUserId == userId);
        if (connectionId is { } cid)
            query = query.Where(x => x.ProxmoxConnectionId == cid);

        var rows = await query
            .OrderByDescending(x => x.CreatedAtUtc)
            .Skip(s).Take(t)
            .ToListAsync(cancellationToken);

        return Ok(rows.Select(MapProxmoxCreate).ToList());
    }

    /// <summary>V8.1 — LXC restores (the <c>POST …/lxc</c> + <c>restore=1</c>
    /// disaster-recovery path). Absolute route under <c>api/proxmox</c> since these
    /// are scoped by Proxmox host; surfaced on the same Audit page. Owner-scoped via
    /// the denormalised <c>InitiatedByUserId</c>; <c>?connectionId=</c> narrows to a
    /// single host.</summary>
    [HttpGet("/api/proxmox/restore/sessions")]
    public async Task<ActionResult<IReadOnlyList<ProxmoxRestoreAuditResponse>>> GetProxmoxRestoreAudits(
        [FromQuery] int skip = 0,
        [FromQuery] int take = DefaultPageSize,
        [FromQuery] Guid? connectionId = null,
        CancellationToken cancellationToken = default)
    {
        var userId = User.GetUserId();
        var (s, t) = Page(skip, take);

        var query = db.ProxmoxRestoreAudits.AsNoTracking()
            .Where(x => x.InitiatedByUserId == userId);
        if (connectionId is { } cid)
            query = query.Where(x => x.ProxmoxConnectionId == cid);

        var rows = await query
            .OrderByDescending(x => x.CreatedAtUtc)
            .Skip(s).Take(t)
            .ToListAsync(cancellationToken);

        return Ok(rows.Select(MapProxmoxRestore).ToList());
    }

    [HttpGet("update-attempts")]
    public async Task<ActionResult<IReadOnlyList<DockerUpdateAttemptResponse>>> GetUpdateAttempts(
        [FromQuery] int skip = 0,
        [FromQuery] int take = DefaultPageSize,
        [FromQuery] Guid? connectionId = null,
        CancellationToken cancellationToken = default)
    {
        var userId = User.GetUserId();
        var (s, t) = Page(skip, take);

        var query = db.DockerUpdateAttempts.AsNoTracking()
            .Where(x => x.InitiatedByUserId == userId);
        if (connectionId is { } cid)
            query = query.Where(x => x.DockerConnectionId == cid);

        var rows = await query
            .OrderByDescending(x => x.CompletedUtc)
            .Skip(s).Take(t)
            .ToListAsync(cancellationToken);

        return Ok(rows.Select(a => watchMapper.ToResponse(a)).ToList());
    }

    /// <summary>V7.6 — Compose file changes (save / restore / apply) made through
    /// the diff-and-apply flow. Owner-scoped via the denormalised
    /// <c>InitiatedByUserId</c>; <c>?connectionId=</c> narrows to a single
    /// connection.</summary>
    [HttpGet("compose-changes")]
    public async Task<ActionResult<IReadOnlyList<ComposeChangeAuditResponse>>> GetComposeChanges(
        [FromQuery] int skip = 0,
        [FromQuery] int take = DefaultPageSize,
        [FromQuery] Guid? connectionId = null,
        CancellationToken cancellationToken = default)
    {
        var userId = User.GetUserId();
        var (s, t) = Page(skip, take);

        var query = db.ComposeChangeAudits.AsNoTracking()
            .Where(x => x.InitiatedByUserId == userId);
        if (connectionId is { } cid)
            query = query.Where(x => x.DockerConnectionId == cid);

        var rows = await query
            .OrderByDescending(x => x.ChangedUtc)
            .Skip(s).Take(t)
            .ToListAsync(cancellationToken);

        return Ok(rows.Select(MapComposeChange).ToList());
    }

    [HttpGet("prune-runs")]
    public async Task<ActionResult<IReadOnlyList<DockerPruneRunResponse>>> GetPruneRuns(
        [FromQuery] int skip = 0,
        [FromQuery] int take = DefaultPageSize,
        [FromQuery] Guid? connectionId = null,
        CancellationToken cancellationToken = default)
    {
        var userId = User.GetUserId();
        var (s, t) = Page(skip, take);

        // Prune runs include scheduled runs (no initiating user). Scope to the
        // caller's own connections so those still show up, plus any manual run
        // the caller triggered (which survives the connection being deleted).
        var ownedConnectionIds = await db.DockerConnections.AsNoTracking()
            .Where(c => c.UserId == userId)
            .Select(c => c.Id)
            .ToListAsync(cancellationToken);

        var query = db.DockerPruneRuns.AsNoTracking().AsQueryable();
        if (connectionId is { } cid)
        {
            // Explicit filter: only honour it for a connection the caller owns.
            if (!ownedConnectionIds.Contains(cid))
                return Ok(new List<DockerPruneRunResponse>());
            query = query.Where(x => x.DockerConnectionId == cid);
        }
        else
        {
            query = query.Where(x =>
                x.InitiatedByUserId == userId ||
                (x.DockerConnectionId != null && ownedConnectionIds.Contains(x.DockerConnectionId.Value)));
        }

        var rows = await query
            .OrderByDescending(x => x.StartedUtc)
            .Skip(s).Take(t)
            .ToListAsync(cancellationToken);

        return Ok(rows.Select(MapPruneRun).ToList());
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static (int Skip, int Take) Page(int skip, int take)
    {
        var s = skip < 0 ? 0 : skip;
        var t = take <= 0 ? DefaultPageSize : Math.Min(take, MaxPageSize);
        return (s, t);
    }

    private static HostShellSessionResponse MapHostShell(HostShellSessionEntity x) => new(
        x.Id,
        x.DockerConnectionId,
        x.ConnectionName,
        x.SshHost,
        x.SshUsername,
        x.StartedUtc,
        x.EndedUtc,
        x.BytesFromClient,
        x.BytesToClient,
        x.EndReason,
        x.Error);

    private static DockerExecSessionResponse MapExec(DockerExecSessionEntity x) => new(
        x.Id,
        x.DockerConnectionId,
        x.ConnectionName,
        x.ContainerName,
        x.Command,
        x.StartedUtc,
        x.EndedUtc,
        x.BytesFromClient,
        x.BytesToClient,
        x.EndReason,
        x.Error);

    private static ProxmoxConsoleSessionResponse MapProxmoxConsole(ProxmoxConsoleSessionEntity x) => new(
        x.Id,
        x.ProxmoxConnectionId,
        x.ConnectionName,
        x.NodeName,
        x.VmId,
        x.GuestName,
        x.Command,
        x.StartedUtc,
        x.EndedUtc,
        x.BytesFromClient,
        x.BytesToClient,
        x.EndReason,
        x.Error);

    private static ProxmoxUpdateSessionResponse MapProxmoxUpdate(ProxmoxUpdateSessionEntity x) => new(
        x.Id,
        x.ProxmoxConnectionId,
        x.ConnectionName,
        x.NodeName,
        x.TargetType,
        x.VmId,
        x.TargetName,
        x.StartedUtc,
        x.EndedUtc,
        x.ExitStatus,
        x.BytesToClient,
        x.EndReason,
        x.Error);

    private static ProxmoxMonitoringAuditResponse MapProxmoxMonitoring(ProxmoxMonitoringAuditEntity x) => new(
        x.Id,
        x.ProxmoxConnectionId,
        x.ConnectionName,
        x.NodeName,
        x.VmId,
        x.GuestName,
        x.ChangeType,
        x.MonitoringEnabled,
        x.SnoozedUntil,
        x.Bulk,
        x.ChangedUtc);

    private static ProxmoxDestroyAuditResponse MapProxmoxDestroy(ProxmoxDestroyAuditEntity x) => new(
        x.Id,
        x.ProxmoxConnectionId,
        x.ConnectionName,
        x.NodeName,
        x.VmId,
        x.GuestName,
        x.Success,
        x.Error,
        x.DestroyedUtc);

    private static ProxmoxCreateAuditResponse MapProxmoxCreate(ProxmoxCreateAuditEntity x) => new(
        x.Id,
        x.ProxmoxConnectionId,
        x.ConnectionName,
        x.NodeName,
        x.VmId,
        x.Hostname,
        x.Template,
        x.Success,
        x.Error,
        x.CreatedAtUtc);

    private static ProxmoxRestoreAuditResponse MapProxmoxRestore(ProxmoxRestoreAuditEntity x) => new(
        x.Id,
        x.ProxmoxConnectionId,
        x.ConnectionName,
        x.NodeName,
        x.VmId,
        x.BackupVolid,
        x.Overwrote,
        x.Success,
        x.Error,
        x.CreatedAtUtc);

    private static ComposeChangeAuditResponse MapComposeChange(ComposeChangeAuditEntity x) => new(
        x.Id,
        x.DockerConnectionId,
        x.ConnectionName,
        x.ComposeProject,
        x.FileName,
        x.ChangeType,
        string.IsNullOrEmpty(x.ChangedServices)
            ? []
            : x.ChangedServices.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        x.Success,
        x.Error,
        x.ChangedUtc);

    private static DockerPruneRunResponse MapPruneRun(DockerPruneRunEntity run) => new(
        run.Id,
        run.Trigger,
        run.Status,
        run.IncludedUnused,
        run.ImagesDeleted,
        run.SpaceReclaimedBytes,
        run.StartedUtc,
        run.CompletedUtc,
        run.Error);
}
