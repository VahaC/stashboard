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
/// Per-service Docker watch endpoints. A watch lives under the service's
/// <see cref="DockerConnectionEntity"/> — creating one requires the parent
/// connection to be configured first. Path:
/// <c>/api/services/{webResourceId}/docker/watches[/{watchId}]</c>.
/// </summary>
[ApiController]
[Authorize]
[Route("api/services/{webResourceId:guid}/docker/watches")]
public class DockerWatchesController(
    ApplicationDbContext db,
    IDockerWatchMapper mapper,
    IDockerConnectionMapper connectionMapper,
    IDockerUpdateChecker updateChecker,
    IDockerWebhookTokenGenerator webhookTokenGenerator,
    IDockerImageUpdater imageUpdater,
    ISelfUpdateLauncher selfUpdateLauncher,
    IDockerHostClient hostClient,
    IDockerLogStreamer logStreamer,
    IDockerStatsStreamer statsStreamer) : ControllerBase
{
    private Guid UserId => User.GetUserId();

    [HttpGet]
    public async Task<ActionResult<List<DockerWatchResponse>>> List(Guid webResourceId, CancellationToken cancellationToken)
    {
        if (!await OwnsServiceAsync(webResourceId, cancellationToken)) return NotFound();
        var watches = await db.DockerWatches.AsNoTracking()
            .Where(w => w.WebResourceId == webResourceId)
            .OrderBy(w => w.Label)
            .ToListAsync(cancellationToken);
        return Ok(watches.Select(mapper.ToResponse).ToList());
    }

    [HttpGet("{watchId:guid}")]
    public async Task<ActionResult<DockerWatchResponse>> Get(Guid webResourceId, Guid watchId, CancellationToken cancellationToken)
    {
        if (!await OwnsServiceAsync(webResourceId, cancellationToken)) return NotFound();
        var watch = await LoadWatchAsync(webResourceId, watchId, tracking: false, cancellationToken);
        return watch is null ? NotFound() : Ok(mapper.ToResponse(watch));
    }

    [HttpPost]
    public async Task<ActionResult<DockerWatchResponse>> Create(
        Guid webResourceId,
        [FromBody] DockerWatchUpsertRequest request,
        CancellationToken cancellationToken)
    {
        if (!await OwnsServiceAsync(webResourceId, cancellationToken)) return NotFound();

        // Watches need a connection assigned to the parent service before they
        // make sense — the host transport for every check lives there. V3.6:
        // the watch now stores the connection id directly (it's the owning
        // host), resolved here from the service's assignment.
        var connectionId = await db.WebResources.AsNoTracking()
            .Where(s => s.Id == webResourceId)
            .Select(s => s.DockerConnectionId)
            .FirstOrDefaultAsync(cancellationToken);
        if (connectionId is null)
            return BadRequest(new { error = "Assign a Docker connection to this service first." });

        var watch = new DockerWatchEntity
        {
            Id = Guid.NewGuid(),
            DockerConnectionId = connectionId.Value,
            WebResourceId = webResourceId,
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
            new { webResourceId, watchId = watch.Id },
            mapper.ToResponse(watch));
    }

    [HttpPut("{watchId:guid}")]
    public async Task<ActionResult<DockerWatchResponse>> Update(
        Guid webResourceId,
        Guid watchId,
        [FromBody] DockerWatchUpsertRequest request,
        CancellationToken cancellationToken)
    {
        if (!await OwnsServiceAsync(webResourceId, cancellationToken)) return NotFound();

        var watch = await LoadWatchAsync(webResourceId, watchId, tracking: true, cancellationToken);
        if (watch is null) return NotFound();

        try { mapper.ApplyUpsert(watch, request); }
        catch (FormatException ex) { return BadRequest(new { error = ex.Message }); }

        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException ex) when (IsDuplicateContainerViolation(ex))
        {
            return Conflict(new { error = $"Container '{watch.ContainerName}' is already tracked on this Docker connection." });
        }

        return Ok(mapper.ToResponse(watch));
    }

    [HttpDelete("{watchId:guid}")]
    public async Task<IActionResult> Delete(Guid webResourceId, Guid watchId, CancellationToken cancellationToken)
    {
        if (!await OwnsServiceAsync(webResourceId, cancellationToken)) return NotFound();
        var deleted = await db.DockerWatches
            .Where(w => w.WebResourceId == webResourceId && w.Id == watchId)
            .ExecuteDeleteAsync(cancellationToken);
        return deleted == 0 ? NotFound() : NoContent();
    }

    [HttpPost("{watchId:guid}/check")]
    public async Task<ActionResult<DockerWatchResponse>> Check(Guid webResourceId, Guid watchId, CancellationToken cancellationToken)
    {
        if (!await OwnsServiceAsync(webResourceId, cancellationToken)) return NotFound();

        var watch = await LoadWatchAsync(webResourceId, watchId, tracking: true, cancellationToken);
        if (watch is null) return NotFound();

        var connection = await LoadAssignedConnectionAsync(webResourceId, cancellationToken);
        if (connection is null)
            return BadRequest(new { error = "This service has no Docker connection assigned." });

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
    /// Per-watch reachability probe. Reuses the service's stored Docker
    /// connection for host transport and resolves "Keep" tri-state secrets
    /// against the existing watch when <paramref name="watchId"/> is supplied.
    /// </summary>
    [HttpPost("test")]
    public async Task<ActionResult<DockerWatchTestResponse>> TestWatch(
        Guid webResourceId,
        [FromBody] DockerWatchTestRequest request,
        [FromQuery] Guid? watchId,
        CancellationToken cancellationToken)
    {
        if (!await OwnsServiceAsync(webResourceId, cancellationToken)) return NotFound();

        var connection = await LoadAssignedConnectionAsync(webResourceId, cancellationToken);
        if (connection is null)
            return BadRequest(new { error = "Assign a Docker connection to this service first." });

        var existing = watchId is null
            ? null
            : await LoadWatchAsync(webResourceId, watchId.Value, tracking: false, cancellationToken);

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

    /// <summary>
    /// V2.6 — generate (or rotate) the per-watch webhook token. The same
    /// endpoint creates a brand-new token (first call) and rotates an
    /// existing one (subsequent calls); the previous token becomes
    /// immediately invalid.
    /// </summary>
    [HttpPost("{watchId:guid}/webhook/rotate")]
    public async Task<ActionResult<DockerWatchResponse>> RotateWebhookToken(
        Guid webResourceId, Guid watchId, CancellationToken cancellationToken)
    {
        if (!await OwnsServiceAsync(webResourceId, cancellationToken)) return NotFound();

        var watch = await LoadWatchAsync(webResourceId, watchId, tracking: true, cancellationToken);
        if (watch is null) return NotFound();

        // Loop up to a few times in the (astronomically unlikely) event the
        // generated token collides with an existing one. 32 random bytes give
        // 256 bits of entropy — birthday-bound at 2^128 watches, so the loop
        // is a belt-and-braces guard against a future migration that backs
        // the token with fewer bytes.
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

    /// <summary>
    /// V2.6 — drop the per-watch webhook token. The watch immediately stops
    /// accepting webhook deliveries; the schedule-driven scan continues
    /// untouched.
    /// </summary>
    [HttpDelete("{watchId:guid}/webhook")]
    public async Task<ActionResult<DockerWatchResponse>> DeleteWebhookToken(
        Guid webResourceId, Guid watchId, CancellationToken cancellationToken)
    {
        if (!await OwnsServiceAsync(webResourceId, cancellationToken)) return NotFound();

        var watch = await LoadWatchAsync(webResourceId, watchId, tracking: true, cancellationToken);
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
    /// V2.7 — one-click "Update now". Pulls the latest image for the
    /// watch's tag against the configured host and recreates the running
    /// container with the new image (Compose labels survive — the
    /// container continues to look Compose-managed afterwards). The
    /// attempt is logged in <c>DockerUpdateAttempts</c> regardless of
    /// outcome. After a successful recreate the watch is immediately
    /// re-checked so the dashboard flips back to "Up to date" without
    /// the user having to click Check now.
    /// </summary>
    [HttpPost("{watchId:guid}/update")]
    public async Task<ActionResult<DockerWatchUpdateResponse>> UpdateNow(
        Guid webResourceId, Guid watchId, CancellationToken cancellationToken)
    {
        if (!await OwnsServiceAsync(webResourceId, cancellationToken)) return NotFound();

        var watch = await LoadWatchAsync(webResourceId, watchId, tracking: true, cancellationToken);
        if (watch is null) return NotFound();

        var connection = await LoadAssignedConnectionAsync(webResourceId, cancellationToken);
        if (connection is null)
            return BadRequest(new { error = "This service has no Docker connection assigned." });

        if (!watch.Enabled)
            return BadRequest(new { error = "Enable this watch before running Update now." });

        var profile = mapper.BuildUpdateProfile(watch, connection);
        var result = await SelfUpdateGate.UpdateAsync(selfUpdateLauncher, imageUpdater, profile, cancellationToken);

        // V9.2 — a scheduled self-update records the target digest (the watch's
        // LatestDigest) so the startup reconciler can confirm success/failure by
        // comparing the container's actual digest to it.
        var attempt = new DockerUpdateAttemptEntity
        {
            Id = Guid.NewGuid(),
            WebResourceId = webResourceId,
            DockerWatchId = watch.Id,
            DockerConnectionId = connection.Id,
            InitiatedByUserId = UserId,
            Status = result.Status,
            ImageReference = watch.ImageReference,
            ContainerName = watch.ContainerName,
            PreviousDigest = result.Status == DockerUpdateAttemptStatus.Scheduled ? watch.CurrentDigest : result.PreviousDigest,
            NewDigest = result.Status == DockerUpdateAttemptStatus.Scheduled ? watch.LatestDigest : result.NewDigest,
            Error = result.Error,
            CompletedUtc = DateTime.UtcNow,
            CreatedUtc = DateTime.UtcNow,
            HealthVerified = result.HealthVerified,
            HealthVerifiedUtc = result.HealthVerifiedUtc,
        };
        db.DockerUpdateAttempts.Add(attempt);

        // On success, immediately re-check the digest so the watch's
        // status flips to UpToDate (or stays UpdateAvailable for the rare
        // case the registry published yet another digest between the
        // pull and the inspect — same code path as `POST /check`).
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

    /// <summary>
    /// V3.1 — Container Inspect viewer. Surfaces a slimmed snapshot of the
    /// container's <c>docker inspect</c> output (image digest, command, env,
    /// mounts, networks, labels, restart policy, health state, ports) so the
    /// user can debug a misconfigured container without shell access to the
    /// host. Read-only — no daemon mutations, no audit row.
    /// </summary>
    [HttpGet("{watchId:guid}/inspect")]
    public async Task<ActionResult<DockerContainerInspect>> Inspect(
        Guid webResourceId, Guid watchId, CancellationToken cancellationToken)
    {
        if (!await OwnsServiceAsync(webResourceId, cancellationToken)) return NotFound();

        var watch = await LoadWatchAsync(webResourceId, watchId, tracking: false, cancellationToken);
        if (watch is null) return NotFound();

        var connection = await LoadAssignedConnectionAsync(webResourceId, cancellationToken);
        if (connection is null)
            return BadRequest(new { error = "This service has no Docker connection assigned." });

        var transport = connectionMapper.BuildTransport(connection);
        DockerContainerInspectResult result;
        try
        {
            result = await hostClient.InspectContainerAsync(transport, watch.ContainerName, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { error = $"Docker host unreachable: {ex.Message}" });
        }

        return result.Status switch
        {
            DockerHostStatus.Ok when result.Inspect is not null => Ok(result.Inspect),
            DockerHostStatus.ContainerNotFound => NotFound(new { error = result.Error }),
            DockerHostStatus.HostUnreachable => StatusCode(StatusCodes.Status502BadGateway, new { error = result.Error }),
            DockerHostStatus.UnsupportedHostType => BadRequest(new { error = result.Error }),
            _ => StatusCode(StatusCodes.Status502BadGateway, new { error = result.Error ?? "Failed to inspect container." }),
        };
    }

    /// <summary>
    /// V3.3 — Real-time container logs. Streams stdcopy-multiplexed log
    /// frames from the daemon to the browser as newline-delimited JSON
    /// (NDJSON) — one <c>{stream,timestamp,message}</c> object per line,
    /// terminated by <c>\n</c>. Read-only — never writes to stdin.
    /// <para>
    /// <c>follow=true</c> tails the container until the client cancels
    /// (default, used by the live-tail UI). <c>follow=false</c> drains the
    /// existing log and ends — used by the "Download" button to fetch a
    /// snapshot. <c>tail</c> bounds the initial backlog (default 200);
    /// <c>since</c> is a Unix-seconds lower bound; stdout / stderr can be
    /// filtered independently (at least one must remain enabled).
    /// </para>
    /// </summary>
    [HttpGet("{watchId:guid}/logs")]
    public async Task<IActionResult> StreamLogs(
        Guid webResourceId,
        Guid watchId,
        [FromQuery] bool follow = true,
        [FromQuery] int? tail = 200,
        [FromQuery] long? since = null,
        [FromQuery] bool timestamps = true,
        [FromQuery] bool stdout = true,
        [FromQuery] bool stderr = true,
        CancellationToken cancellationToken = default)
    {
        if (!await OwnsServiceAsync(webResourceId, cancellationToken)) return NotFound();

        var watch = await LoadWatchAsync(webResourceId, watchId, tracking: false, cancellationToken);
        if (watch is null) return NotFound();

        var connection = await LoadAssignedConnectionAsync(webResourceId, cancellationToken);
        if (connection is null)
            return BadRequest(new { error = "This service has no Docker connection assigned." });

        if (!stdout && !stderr)
            return BadRequest(new { error = "At least one of stdout / stderr must be enabled." });

        var transport = connectionMapper.BuildTransport(connection);
        var request = new DockerLogStreamRequest(
            Tail: tail is > 0 ? tail : null,
            Since: since is { } s ? DateTimeOffset.FromUnixTimeSeconds(s) : null,
            Follow: follow,
            Timestamps: timestamps,
            IncludeStdout: stdout,
            IncludeStderr: stderr);

        // Newline-delimited JSON — one log line per HTTP chunk. Friendlier to
        // fetch + ReadableStream on the browser side than SSE (which can't
        // carry an Authorization header through EventSource) and keeps the
        // controller free of SignalR ceremony for a one-way read-only stream.
        Response.StatusCode = StatusCodes.Status200OK;
        Response.ContentType = "application/x-ndjson";
        Response.Headers["Cache-Control"] = "no-cache";
        Response.Headers["X-Accel-Buffering"] = "no";

        // Many proxies buffer the response body until they see the first byte;
        // flush headers eagerly so the browser commits to streaming mode even
        // before the first log line arrives (idle containers).
        await Response.Body.FlushAsync(cancellationToken);

        var jsonOptions = Stashboard.Api.Serialization.StreamingJson.Options;

        DockerLogStreamResult streamResult;
        try
        {
            streamResult = await logStreamer.StreamLogsAsync(
                transport,
                watch.ContainerName,
                request,
                async (line, ct) =>
                {
                    var payload = new
                    {
                        stream = line.Stream.ToString().ToLowerInvariant(),
                        timestamp = line.TimestampUtc,
                        message = line.Message,
                    };
                    await System.Text.Json.JsonSerializer.SerializeAsync(Response.Body, payload, jsonOptions, ct);
                    await Response.Body.WriteAsync(NewlineBytes, ct);
                    await Response.Body.FlushAsync(ct);
                },
                cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            await WriteTerminalErrorAsync(jsonOptions, $"Docker host unreachable: {ex.Message}", cancellationToken);
            return new EmptyResult();
        }

        if (!streamResult.IsSuccess)
            await WriteTerminalErrorAsync(jsonOptions, streamResult.Error ?? "Log stream failed.", cancellationToken);

        return new EmptyResult();
    }

    private async Task WriteTerminalErrorAsync(
        System.Text.Json.JsonSerializerOptions jsonOptions, string message, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return;
        try
        {
            var payload = new { stream = "error", message };
            await System.Text.Json.JsonSerializer.SerializeAsync(Response.Body, payload, jsonOptions, cancellationToken);
            await Response.Body.WriteAsync(NewlineBytes, cancellationToken);
            await Response.Body.FlushAsync(cancellationToken);
        }
        catch (Exception)
        {
            // Best-effort error frame — if the response body is already
            // closed (browser disconnected mid-stream) we can't tell the
            // client anything anyway.
        }
    }

    private static readonly byte[] NewlineBytes = "\n"u8.ToArray();

    /// <summary>
    /// V3.4 — Live container stats. Streams one
    /// <c>{cpuPercent, memoryUsageBytes, memoryLimitBytes, memoryPercent,
    /// networkRxBytes, networkTxBytes, blockReadBytes, blockWriteBytes,
    /// onlineCpus, timestampUtc}</c> NDJSON frame per second from the
    /// daemon's <c>/containers/{id}/stats?stream=true</c> endpoint until
    /// the client cancels.
    /// <para>
    /// <c>oneShot=true</c> returns exactly one snapshot (no PreCPUStats
    /// baseline → <c>cpuPercent = null</c>) and disconnects — useful for
    /// the "Refresh" button in the UI when continuous streaming isn't
    /// needed. Read-only — same admin-only permission gate as the rest of
    /// the Docker actions.
    /// </para>
    /// </summary>
    [HttpGet("{watchId:guid}/stats")]
    public async Task<IActionResult> StreamStats(
        Guid webResourceId,
        Guid watchId,
        [FromQuery] bool oneShot = false,
        CancellationToken cancellationToken = default)
    {
        if (!await OwnsServiceAsync(webResourceId, cancellationToken)) return NotFound();

        var watch = await LoadWatchAsync(webResourceId, watchId, tracking: false, cancellationToken);
        if (watch is null) return NotFound();

        var connection = await LoadAssignedConnectionAsync(webResourceId, cancellationToken);
        if (connection is null)
            return BadRequest(new { error = "This service has no Docker connection assigned." });

        var transport = connectionMapper.BuildTransport(connection);
        var request = new DockerStatsStreamRequest(Stream: !oneShot, OneShot: oneShot);

        Response.StatusCode = StatusCodes.Status200OK;
        Response.ContentType = "application/x-ndjson";
        Response.Headers["Cache-Control"] = "no-cache";
        Response.Headers["X-Accel-Buffering"] = "no";

        await Response.Body.FlushAsync(cancellationToken);

        var jsonOptions = Stashboard.Api.Serialization.StreamingJson.Options;

        DockerStatsStreamResult streamResult;
        try
        {
            streamResult = await statsStreamer.StreamStatsAsync(
                transport,
                watch.ContainerName,
                request,
                async (sample, ct) =>
                {
                    await System.Text.Json.JsonSerializer.SerializeAsync(Response.Body, sample, jsonOptions, ct);
                    await Response.Body.WriteAsync(NewlineBytes, ct);
                    await Response.Body.FlushAsync(ct);
                },
                cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            await WriteTerminalErrorAsync(jsonOptions, $"Docker host unreachable: {ex.Message}", cancellationToken);
            return new EmptyResult();
        }

        if (!streamResult.IsSuccess)
            await WriteTerminalErrorAsync(jsonOptions, streamResult.Error ?? "Stats stream failed.", cancellationToken);

        return new EmptyResult();
    }

    /// <summary>
    /// V2.7 — per-watch update history. Newest first. The audit row is
    /// append-only — there is no DELETE on it directly; rows go away
    /// only when the parent watch or service is deleted (cascade).
    /// </summary>
    [HttpGet("{watchId:guid}/updates")]
    public async Task<ActionResult<List<DockerUpdateAttemptResponse>>> ListUpdates(
        Guid webResourceId, Guid watchId, CancellationToken cancellationToken)
    {
        if (!await OwnsServiceAsync(webResourceId, cancellationToken)) return NotFound();

        var watchExists = await db.DockerWatches.AsNoTracking()
            .AnyAsync(w => w.WebResourceId == webResourceId && w.Id == watchId, cancellationToken);
        if (!watchExists) return NotFound();

        var attempts = await db.DockerUpdateAttempts.AsNoTracking()
            .Where(a => a.DockerWatchId == watchId)
            .OrderByDescending(a => a.CompletedUtc)
            .Take(50)
            .ToListAsync(cancellationToken);

        return Ok(attempts.Select(mapper.ToResponse).ToList());
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private Task<bool> OwnsServiceAsync(Guid webResourceId, CancellationToken cancellationToken)
    {
        var userId = UserId;
        return db.WebResources.AsNoTracking()
            .AnyAsync(s => s.Id == webResourceId && s.UserId == userId, cancellationToken);
    }

    private Task<DockerWatchEntity?> LoadWatchAsync(Guid webResourceId, Guid watchId, bool tracking, CancellationToken cancellationToken)
    {
        var query = tracking ? db.DockerWatches.AsTracking() : db.DockerWatches.AsNoTracking();
        return query.FirstOrDefaultAsync(w => w.WebResourceId == webResourceId && w.Id == watchId, cancellationToken);
    }

    /// <summary>Resolve the connection a service is currently assigned to.
    /// Returns null when the service exists but the user hasn't picked a
    /// connection yet — the caller should surface that as a BadRequest.</summary>
    private Task<DockerConnectionEntity?> LoadAssignedConnectionAsync(Guid webResourceId, CancellationToken cancellationToken) =>
        db.WebResources.AsNoTracking()
            .Where(s => s.Id == webResourceId && s.DockerConnectionId != null)
            .Select(s => s.DockerConnection!)
            .FirstOrDefaultAsync(cancellationToken);

    private static bool IsDuplicateContainerViolation(DbUpdateException ex) =>
        Stashboard.Api.Data.UniqueConstraintViolation.Matches(ex,
            "IX_DockerWatches_DockerConnectionId_ContainerName",
            "DockerWatches.DockerConnectionId", "DockerWatches.ContainerName");
}
