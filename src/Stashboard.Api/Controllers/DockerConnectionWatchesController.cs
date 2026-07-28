using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Stashboard.Api.Auth;
using Stashboard.Api.Contracts;
using Stashboard.Api.Data;
using Stashboard.Api.Mapping;
using Stashboard.Api.Services;
using Stashboard.Core.Abstractions;
using Stashboard.Core.Entities;
using Stashboard.Core.Enums;

namespace Stashboard.Api.Controllers;

/// <summary>
/// V3.6 — connection-scoped Docker watch endpoints. A watch is a tracked
/// container, owned by its <see cref="DockerConnectionEntity"/> and optionally
/// linked to a <see cref="WebResourceEntity"/> (service). This is the primary
/// surface the Docker page uses to add / edit / remove tracking directly on a
/// container, without first having to attach it to a service. Path:
/// <c>/api/docker/connections/{connectionId}/watches[/{watchId}]</c>.
/// <para>
/// Diagnostics (Inspect / Logs / Stats) are not duplicated here — the Docker
/// page already addresses those by container name via
/// <see cref="DockerInstancesController"/>.
/// </para>
/// </summary>
[ApiController]
[Authorize]
[Route("api/docker/connections/{connectionId:guid}/watches")]
public class DockerConnectionWatchesController(
    ApplicationDbContext db,
    IDockerWatchMapper mapper,
    IDockerUpdateChecker updateChecker,
    IDockerWebhookTokenGenerator webhookTokenGenerator,
    IDockerImageUpdater imageUpdater) : ControllerBase
{
    private Guid UserId => User.GetUserId();

    [HttpGet]
    public async Task<ActionResult<List<DockerWatchResponse>>> List(Guid connectionId, CancellationToken cancellationToken)
    {
        if (!await OwnsConnectionAsync(connectionId, cancellationToken)) return NotFound();
        var watches = await db.DockerWatches.AsNoTracking()
            .Where(w => w.DockerConnectionId == connectionId)
            .OrderBy(w => w.Label)
            .ToListAsync(cancellationToken);
        return Ok(watches.Select(mapper.ToResponse).ToList());
    }

    [HttpGet("{watchId:guid}")]
    public async Task<ActionResult<DockerWatchResponse>> Get(Guid connectionId, Guid watchId, CancellationToken cancellationToken)
    {
        if (!await OwnsConnectionAsync(connectionId, cancellationToken)) return NotFound();
        var watch = await LoadWatchAsync(connectionId, watchId, tracking: false, cancellationToken);
        return watch is null ? NotFound() : Ok(mapper.ToResponse(watch));
    }

    [HttpPost]
    public async Task<ActionResult<DockerWatchResponse>> Create(
        Guid connectionId,
        [FromBody] DockerWatchUpsertRequest request,
        CancellationToken cancellationToken)
    {
        if (!await OwnsConnectionAsync(connectionId, cancellationToken)) return NotFound();

        if (!await ValidateOptionalServiceAsync(request.WebResourceId, cancellationToken))
            return BadRequest(new { error = "The linked service does not exist or isn't yours." });

        var watch = new DockerWatchEntity
        {
            Id = Guid.NewGuid(),
            DockerConnectionId = connectionId,
            WebResourceId = request.WebResourceId,
            UserId = UserId,
            CreatedUtc = DateTime.UtcNow,
        };

        try { mapper.ApplyUpsert(watch, request); }
        catch (FormatException ex) { return BadRequest(new { error = ex.Message }); }

        db.DockerWatches.Add(watch);

        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException ex) when (IsDuplicateContainerViolation(ex))
        {
            return Conflict(new { error = $"Container '{watch.ContainerName}' is already tracked on this Docker connection." });
        }

        return CreatedAtAction(
            nameof(Get),
            new { connectionId, watchId = watch.Id },
            mapper.ToResponse(watch));
    }

    [HttpPut("{watchId:guid}")]
    public async Task<ActionResult<DockerWatchResponse>> Update(
        Guid connectionId,
        Guid watchId,
        [FromBody] DockerWatchUpsertRequest request,
        CancellationToken cancellationToken)
    {
        if (!await OwnsConnectionAsync(connectionId, cancellationToken)) return NotFound();

        var watch = await LoadWatchAsync(connectionId, watchId, tracking: true, cancellationToken);
        if (watch is null) return NotFound();

        if (!await ValidateOptionalServiceAsync(request.WebResourceId, cancellationToken))
            return BadRequest(new { error = "The linked service does not exist or isn't yours." });

        try { mapper.ApplyUpsert(watch, request); }
        catch (FormatException ex) { return BadRequest(new { error = ex.Message }); }

        // The connection-scoped editor can also re-link (or unlink) the
        // service the container belongs to.
        watch.WebResourceId = request.WebResourceId;

        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException ex) when (IsDuplicateContainerViolation(ex))
        {
            return Conflict(new { error = $"Container '{watch.ContainerName}' is already tracked on this Docker connection." });
        }

        return Ok(mapper.ToResponse(watch));
    }

    [HttpDelete("{watchId:guid}")]
    public async Task<IActionResult> Delete(Guid connectionId, Guid watchId, CancellationToken cancellationToken)
    {
        if (!await OwnsConnectionAsync(connectionId, cancellationToken)) return NotFound();
        var deleted = await db.DockerWatches
            .Where(w => w.DockerConnectionId == connectionId && w.Id == watchId)
            .ExecuteDeleteAsync(cancellationToken);
        return deleted == 0 ? NotFound() : NoContent();
    }

    [HttpPost("{watchId:guid}/check")]
    public async Task<ActionResult<DockerWatchResponse>> Check(Guid connectionId, Guid watchId, CancellationToken cancellationToken)
    {
        if (!await OwnsConnectionAsync(connectionId, cancellationToken)) return NotFound();

        var watch = await LoadWatchAsync(connectionId, watchId, tracking: true, cancellationToken);
        if (watch is null) return NotFound();

        var connection = await LoadOwnedConnectionAsync(connectionId, cancellationToken);
        if (connection is null) return NotFound();

        if (!watch.Enabled)
        {
            watch.UpdateStatus = DockerUpdateStatus.Disabled;
            watch.LastCheckedUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            return Ok(mapper.ToResponse(watch));
        }

        var profile = mapper.BuildProfileFromEntity(watch, connection);
        var result = await updateChecker.CheckAsync(profile, cancellationToken);
        DockerWatchStatusWriter.ApplyCheckResult(watch, result);
        await db.SaveChangesAsync(cancellationToken);

        return Ok(mapper.ToResponse(watch));
    }

    /// <summary>
    /// Per-watch reachability probe against the owning connection. Resolves
    /// "Keep" tri-state secrets against the existing watch when
    /// <paramref name="watchId"/> is supplied.
    /// </summary>
    [HttpPost("test")]
    public async Task<ActionResult<DockerWatchTestResponse>> TestWatch(
        Guid connectionId,
        [FromBody] DockerWatchTestRequest request,
        [FromQuery] Guid? watchId,
        CancellationToken cancellationToken)
    {
        var connection = await LoadOwnedConnectionAsync(connectionId, cancellationToken);
        if (connection is null) return NotFound();

        var existing = watchId is null
            ? null
            : await LoadWatchAsync(connectionId, watchId.Value, tracking: false, cancellationToken);

        DockerWatchProfile profile;
        try { profile = mapper.BuildProfileFromTestRequest(request, connection, existing); }
        catch (FormatException ex) { return BadRequest(new { error = ex.Message }); }

        var result = await updateChecker.TestConnectionAsync(profile, cancellationToken);
        return Ok(new DockerWatchTestResponse(
            result.DockerHostReachable,
            result.ContainerFound,
            result.RegistryReachable,
            result.Error));
    }

    /// <summary>V3.6 — generate (or rotate) the per-watch webhook token.</summary>
    [HttpPost("{watchId:guid}/webhook/rotate")]
    public async Task<ActionResult<DockerWatchResponse>> RotateWebhookToken(
        Guid connectionId, Guid watchId, CancellationToken cancellationToken)
    {
        if (!await OwnsConnectionAsync(connectionId, cancellationToken)) return NotFound();

        var watch = await LoadWatchAsync(connectionId, watchId, tracking: true, cancellationToken);
        if (watch is null) return NotFound();

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var candidate = webhookTokenGenerator.Generate();
            var collides = await db.DockerWatches.AsNoTracking()
                .AnyAsync(w => w.WebhookToken == candidate, cancellationToken);
            if (collides) continue;

            watch.WebhookToken = candidate;
            watch.UpdatedUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            return Ok(mapper.ToResponse(watch));
        }

        return StatusCode(StatusCodes.Status500InternalServerError,
            new { error = "Could not generate a unique webhook token. Please retry." });
    }

    /// <summary>Remove the service link from a tracked container, making it standalone.</summary>
    [HttpDelete("{watchId:guid}/service-link")]
    public async Task<ActionResult<DockerWatchResponse>> UnlinkService(
        Guid connectionId, Guid watchId, CancellationToken cancellationToken)
    {
        if (!await OwnsConnectionAsync(connectionId, cancellationToken)) return NotFound();

        var watch = await LoadWatchAsync(connectionId, watchId, tracking: true, cancellationToken);
        if (watch is null) return NotFound();

        if (watch.WebResourceId is null)
            return Ok(mapper.ToResponse(watch));

        watch.WebResourceId = null;
        watch.UpdatedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return Ok(mapper.ToResponse(watch));
    }

    /// <summary>V3.6 — drop the per-watch webhook token.</summary>
    [HttpDelete("{watchId:guid}/webhook")]
    public async Task<ActionResult<DockerWatchResponse>> DeleteWebhookToken(
        Guid connectionId, Guid watchId, CancellationToken cancellationToken)
    {
        if (!await OwnsConnectionAsync(connectionId, cancellationToken)) return NotFound();

        var watch = await LoadWatchAsync(connectionId, watchId, tracking: true, cancellationToken);
        if (watch is null) return NotFound();

        if (watch.WebhookToken is null && watch.LastWebhookReceivedUtc is null)
            return Ok(mapper.ToResponse(watch));

        watch.WebhookToken = null;
        watch.LastWebhookReceivedUtc = null;
        watch.UpdatedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return Ok(mapper.ToResponse(watch));
    }

    /// <summary>
    /// V3.6 — one-click "Update now" against the owning connection. Pulls the
    /// latest image and recreates the container, logs the attempt, then
    /// re-checks the digest so the status flips back to Up-to-date on success.
    /// </summary>
    [HttpPost("{watchId:guid}/update")]
    public async Task<ActionResult<DockerWatchUpdateResponse>> UpdateNow(
        Guid connectionId, Guid watchId, CancellationToken cancellationToken)
    {
        if (!await OwnsConnectionAsync(connectionId, cancellationToken)) return NotFound();

        var watch = await LoadWatchAsync(connectionId, watchId, tracking: true, cancellationToken);
        if (watch is null) return NotFound();

        var connection = await LoadOwnedConnectionAsync(connectionId, cancellationToken);
        if (connection is null) return NotFound();

        if (!watch.Enabled)
            return BadRequest(new { error = "Enable this watch before running Update now." });

        var profile = mapper.BuildUpdateProfile(watch, connection);
        var result = await imageUpdater.UpdateAsync(profile, cancellationToken);

        var attempt = new DockerUpdateAttemptEntity
        {
            Id = Guid.NewGuid(),
            WebResourceId = watch.WebResourceId,
            DockerWatchId = watch.Id,
            DockerConnectionId = connection.Id,
            InitiatedByUserId = UserId,
            Status = result.Status,
            ImageReference = watch.ImageReference,
            ContainerName = watch.ContainerName,
            PreviousDigest = result.PreviousDigest,
            NewDigest = result.NewDigest,
            Error = result.Error,
            CompletedUtc = DateTime.UtcNow,
            CreatedUtc = DateTime.UtcNow,
            HealthVerified = result.HealthVerified,
            HealthVerifiedUtc = result.HealthVerifiedUtc,
        };
        db.DockerUpdateAttempts.Add(attempt);

        if (result.IsSuccess)
        {
            var checkerProfile = mapper.BuildProfileFromEntity(watch, connection);
            var check = await updateChecker.CheckAsync(checkerProfile, cancellationToken);
            DockerWatchStatusWriter.ApplyCheckResult(watch, check);
        }

        await db.SaveChangesAsync(cancellationToken);

        return Ok(new DockerWatchUpdateResponse(
            mapper.ToResponse(attempt),
            mapper.ToResponse(watch)));
    }

    /// <summary>V3.6 — per-watch update / action history. Newest first.</summary>
    [HttpGet("{watchId:guid}/updates")]
    public async Task<ActionResult<List<DockerUpdateAttemptResponse>>> ListUpdates(
        Guid connectionId, Guid watchId, CancellationToken cancellationToken)
    {
        if (!await OwnsConnectionAsync(connectionId, cancellationToken)) return NotFound();

        var watchExists = await db.DockerWatches.AsNoTracking()
            .AnyAsync(w => w.DockerConnectionId == connectionId && w.Id == watchId, cancellationToken);
        if (!watchExists) return NotFound();

        var attempts = await db.DockerUpdateAttempts.AsNoTracking()
            .Where(a => a.DockerWatchId == watchId)
            .OrderByDescending(a => a.CompletedUtc)
            .Take(50)
            .ToListAsync(cancellationToken);

        return Ok(attempts.Select(mapper.ToResponse).ToList());
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private Task<bool> OwnsConnectionAsync(Guid connectionId, CancellationToken cancellationToken)
    {
        var userId = UserId;
        return db.DockerConnections.AsNoTracking()
            .AnyAsync(c => c.Id == connectionId && c.UserId == userId, cancellationToken);
    }

    private Task<DockerConnectionEntity?> LoadOwnedConnectionAsync(Guid connectionId, CancellationToken cancellationToken)
    {
        var userId = UserId;
        return db.DockerConnections.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == connectionId && c.UserId == userId, cancellationToken);
    }

    private Task<DockerWatchEntity?> LoadWatchAsync(Guid connectionId, Guid watchId, bool tracking, CancellationToken cancellationToken)
    {
        var query = tracking ? db.DockerWatches.AsTracking() : db.DockerWatches.AsNoTracking();
        return query.FirstOrDefaultAsync(w => w.DockerConnectionId == connectionId && w.Id == watchId, cancellationToken);
    }

    /// <summary>Returns true when the optional service link is absent or points
    /// at a service this user owns.</summary>
    private async Task<bool> ValidateOptionalServiceAsync(Guid? webResourceId, CancellationToken cancellationToken)
    {
        if (webResourceId is null) return true;
        var userId = UserId;
        return await db.WebResources.AsNoTracking()
            .AnyAsync(s => s.Id == webResourceId.Value && s.UserId == userId, cancellationToken);
    }

    private static bool IsDuplicateContainerViolation(DbUpdateException ex) =>
        Stashboard.Api.Data.UniqueConstraintViolation.Matches(ex,
            "IX_DockerWatches_DockerConnectionId_ContainerName",
            "DockerWatches.DockerConnectionId", "DockerWatches.ContainerName");
}
