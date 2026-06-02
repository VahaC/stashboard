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
