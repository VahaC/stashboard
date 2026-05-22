using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Stashboard.Api.Auth;
using Stashboard.Api.Contracts;
using Stashboard.Api.Data;
using Stashboard.Api.Mapping;
using Stashboard.Core.Abstractions;
using Stashboard.Core.Entities;
using Stashboard.Core.Enums;
using Stashboard.Core.Options;

namespace Stashboard.Api.Controllers;

/// <summary>
/// V3.5 — Docker instances page. Exposes a per-connection view of every
/// container on the host plus lifecycle actions (Start / Stop / Restart /
/// Remove) so the user can manage containers without SSH-ing into the
/// daemon. Path:
/// <c>/api/docker/connections/{connectionId}/instance</c>.
/// </summary>
/// <remarks>
/// All endpoints are user-scoped via the parent <see cref="DockerConnectionEntity"/>.
/// Lifecycle actions write to the existing <see cref="DockerUpdateAttemptEntity"/>
/// audit table with the new V3.5 <see cref="DockerContainerActionType"/>
/// discriminator so the per-watch history view and this page share one
/// activity log.
/// </remarks>
[ApiController]
[Authorize]
[Route("api/docker/connections/{connectionId:guid}/instance")]
public class DockerInstancesController(
    ApplicationDbContext db,
    IDockerConnectionMapper connectionMapper,
    IDockerWatchMapper watchMapper,
    IDockerHostClient hostClient,
    IDockerLogStreamer logStreamer,
    IDockerStatsStreamer statsStreamer,
    IOptions<StashboardOptions> options) : ControllerBase
{
    private Guid UserId => User.GetUserId();
    private bool AllowRemoval => options.Value.AllowContainerRemoval;

    /// <summary>
    /// V3.5 — list every container on the connected daemon (all states,
    /// not just running). Joins on the user's own watches so each card
    /// can render a "Open in service" deep link when one exists.
    /// </summary>
    [HttpGet("containers")]
    public async Task<ActionResult<List<DockerContainerCard>>> ListContainers(
        Guid connectionId, CancellationToken cancellationToken)
    {
        var connection = await LoadOwnedConnectionAsync(connectionId, cancellationToken);
        if (connection is null) return NotFound();

        var transport = connectionMapper.BuildTransport(connection);
        IReadOnlyList<DockerContainerDetail> details;
        try
        {
            details = await hostClient.ListContainerDetailsAsync(transport, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return StatusCode(StatusCodes.Status502BadGateway,
                new { error = $"Docker host unreachable: {ex.Message}" });
        }

        var watches = await db.DockerWatches.AsNoTracking()
            .Where(w => w.DockerConnectionId == connectionId)
            .Select(w => new { w.Id, w.WebResourceId, w.ContainerName, w.ImageReference })
            .ToListAsync(cancellationToken);
        var watchByContainer = watches
            .GroupBy(w => w.ContainerName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var liveContainerNames = new HashSet<string>(
            details.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);

        var cards = details
            .Select(c =>
            {
                watchByContainer.TryGetValue(c.Name, out var watchLink);
                return new DockerContainerCard(
                    Id: c.Id,
                    Name: c.Name,
                    Image: c.Image,
                    ImageId: c.ImageId,
                    State: c.State,
                    Status: c.Status,
                    CreatedUtc: c.CreatedUtc,
                    Ports: c.Ports
                        .Select(p => new DockerContainerPortMapping(p.PrivatePort, p.PublicPort, p.Type, p.Ip))
                        .OrderBy(p => p.PrivatePort)
                        .ToList(),
                    ComposeProject: c.ComposeProject,
                    ComposeService: c.ComposeService,
                    WatchId: watchLink?.Id,
                    WebResourceId: watchLink?.WebResourceId);
            })
            .ToList();

        // Tracked containers whose name no longer appears on the Docker host
        // surface as ghost cards so the user can see them and clean up.
        foreach (var orphan in watches.Where(w => !liveContainerNames.Contains(w.ContainerName)))
        {
            cards.Add(new DockerContainerCard(
                Id: string.Empty,
                Name: orphan.ContainerName,
                Image: orphan.ImageReference,
                ImageId: string.Empty,
                State: "not found",
                Status: "Container not found on host",
                CreatedUtc: null,
                Ports: [],
                ComposeProject: null,
                ComposeService: null,
                WatchId: orphan.Id,
                WebResourceId: orphan.WebResourceId));
        }

        return Ok(cards);
    }

    [HttpPost("containers/{containerName}/start")]
    public Task<ActionResult<DockerContainerActionResponse>> Start(
        Guid connectionId, string containerName, CancellationToken cancellationToken) =>
        ExecuteActionAsync(connectionId, containerName, DockerContainerActionType.Start,
            (transport, ct) => hostClient.StartContainerAsync(transport, containerName, ct),
            cancellationToken);

    [HttpPost("containers/{containerName}/stop")]
    public Task<ActionResult<DockerContainerActionResponse>> Stop(
        Guid connectionId, string containerName, CancellationToken cancellationToken) =>
        ExecuteActionAsync(connectionId, containerName, DockerContainerActionType.Stop,
            (transport, ct) => hostClient.StopContainerAsync(transport, containerName, waitSeconds: 10, ct),
            cancellationToken);

    [HttpPost("containers/{containerName}/restart")]
    public Task<ActionResult<DockerContainerActionResponse>> Restart(
        Guid connectionId, string containerName, CancellationToken cancellationToken) =>
        ExecuteActionAsync(connectionId, containerName, DockerContainerActionType.Restart,
            (transport, ct) => hostClient.RestartContainerAsync(transport, containerName, waitSeconds: 10, ct),
            cancellationToken);

    /// <summary>
    /// V3.5 — destructive. For running containers, gated behind the
    /// <c>Stashboard.AllowContainerRemoval</c> feature flag. V5.0:
    /// exited and dead containers can always be removed — they're
    /// already stopped, so the flag only protects live containers.
    /// </summary>
    [HttpDelete("containers/{containerName}")]
    public async Task<ActionResult<DockerContainerActionResponse>> Remove(
        Guid connectionId, string containerName,
        [FromQuery] bool force = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(containerName))
            return BadRequest(new { error = "Container name is required." });

        if (!AllowRemoval)
        {
            var connection = await LoadOwnedConnectionAsync(connectionId, cancellationToken);
            if (connection is null) return NotFound();

            var transport = connectionMapper.BuildTransport(connection);
            try
            {
                var details = await hostClient.ListContainerDetailsAsync(transport, cancellationToken);
                var target = details.FirstOrDefault(c =>
                    string.Equals(c.Name, containerName, StringComparison.OrdinalIgnoreCase));
                var state = target?.State?.ToLowerInvariant() ?? "";
                if (state is not ("exited" or "dead"))
                    return StatusCode(StatusCodes.Status403Forbidden, new
                    {
                        error = "Container removal is disabled for running containers. Set Stashboard:AllowContainerRemoval=true to enable it.",
                    });
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                return StatusCode(StatusCodes.Status502BadGateway,
                    new { error = $"Docker host unreachable: {ex.Message}" });
            }
        }

        return await ExecuteActionAsync(connectionId, containerName, DockerContainerActionType.Remove,
            (transport, ct) => hostClient.RemoveContainerAsync(transport, containerName, force, ct),
            cancellationToken);
    }

    // ── V3.5 — diagnostics for un-tracked containers ─────────────────────────
    //
    // The same Inspect / Logs / Stats payloads the V3.1 / V3.3 / V3.4
    // watch-scoped endpoints serve, but addressed by connection + container
    // name instead of watchId. Lets the V3.5 ContainerModal show diagnostics
    // for any container on the host — including ones the user hasn't (yet)
    // wired up to a service-scoped watch.

    /// <summary>
    /// V3.5 — slimmed <c>docker inspect</c> snapshot, addressed by
    /// connection + container name. Read-only. Same masking heuristics as
    /// the V3.1 watch-scoped variant (env values whose key matches the
    /// secret heuristic arrive as <c>masked: true</c>).
    /// </summary>
    [HttpGet("containers/{containerName}/inspect")]
    public async Task<ActionResult<DockerContainerInspect>> Inspect(
        Guid connectionId, string containerName, CancellationToken cancellationToken)
    {
        var connection = await LoadOwnedConnectionAsync(connectionId, cancellationToken);
        if (connection is null) return NotFound();
        if (string.IsNullOrWhiteSpace(containerName))
            return BadRequest(new { error = "Container name is required." });

        var transport = connectionMapper.BuildTransport(connection);
        DockerContainerInspectResult result;
        try
        {
            result = await hostClient.InspectContainerAsync(transport, containerName, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return StatusCode(StatusCodes.Status502BadGateway,
                new { error = $"Docker host unreachable: {ex.Message}" });
        }

        return result.Status switch
        {
            DockerHostStatus.Ok when result.Inspect is not null => Ok(result.Inspect),
            DockerHostStatus.ContainerNotFound => NotFound(new { error = result.Error }),
            DockerHostStatus.HostUnreachable =>
                StatusCode(StatusCodes.Status502BadGateway, new { error = result.Error }),
            DockerHostStatus.UnsupportedHostType => BadRequest(new { error = result.Error }),
            _ => StatusCode(StatusCodes.Status502BadGateway,
                new { error = result.Error ?? "Failed to inspect container." }),
        };
    }

    /// <summary>
    /// V3.5 — NDJSON stream of stdcopy-multiplexed container logs, addressed
    /// by connection + container name. Same wire format and query parameters
    /// as the V3.3 watch-scoped endpoint.
    /// </summary>
    [HttpGet("containers/{containerName}/logs")]
    public async Task<IActionResult> StreamLogs(
        Guid connectionId,
        string containerName,
        [FromQuery] bool follow = true,
        [FromQuery] int? tail = 200,
        [FromQuery] long? since = null,
        [FromQuery] bool timestamps = true,
        [FromQuery] bool stdout = true,
        [FromQuery] bool stderr = true,
        CancellationToken cancellationToken = default)
    {
        var connection = await LoadOwnedConnectionAsync(connectionId, cancellationToken);
        if (connection is null) return NotFound();
        if (string.IsNullOrWhiteSpace(containerName))
            return BadRequest(new { error = "Container name is required." });
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

        Response.StatusCode = StatusCodes.Status200OK;
        Response.ContentType = "application/x-ndjson";
        Response.Headers["Cache-Control"] = "no-cache";
        Response.Headers["X-Accel-Buffering"] = "no";
        await Response.Body.FlushAsync(cancellationToken);

        var jsonOptions = new System.Text.Json.JsonSerializerOptions
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        };

        DockerLogStreamResult streamResult;
        try
        {
            streamResult = await logStreamer.StreamLogsAsync(
                transport, containerName, request,
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
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
            && !cancellationToken.IsCancellationRequested)
        {
            await WriteTerminalErrorAsync(jsonOptions, $"Docker host unreachable: {ex.Message}", cancellationToken);
            return new EmptyResult();
        }

        if (!streamResult.IsSuccess)
            await WriteTerminalErrorAsync(jsonOptions, streamResult.Error ?? "Log stream failed.", cancellationToken);

        return new EmptyResult();
    }

    /// <summary>
    /// V3.5 — NDJSON stream of computed CPU / memory / network / block-I/O
    /// samples, addressed by connection + container name. Same wire format
    /// and <c>oneShot</c> semantics as the V3.4 watch-scoped endpoint.
    /// </summary>
    [HttpGet("containers/{containerName}/stats")]
    public async Task<IActionResult> StreamStats(
        Guid connectionId,
        string containerName,
        [FromQuery] bool oneShot = false,
        CancellationToken cancellationToken = default)
    {
        var connection = await LoadOwnedConnectionAsync(connectionId, cancellationToken);
        if (connection is null) return NotFound();
        if (string.IsNullOrWhiteSpace(containerName))
            return BadRequest(new { error = "Container name is required." });

        var transport = connectionMapper.BuildTransport(connection);
        var request = new DockerStatsStreamRequest(Stream: !oneShot, OneShot: oneShot);

        Response.StatusCode = StatusCodes.Status200OK;
        Response.ContentType = "application/x-ndjson";
        Response.Headers["Cache-Control"] = "no-cache";
        Response.Headers["X-Accel-Buffering"] = "no";
        await Response.Body.FlushAsync(cancellationToken);

        var jsonOptions = new System.Text.Json.JsonSerializerOptions
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        };

        DockerStatsStreamResult streamResult;
        try
        {
            streamResult = await statsStreamer.StreamStatsAsync(
                transport, containerName, request,
                async (sample, ct) =>
                {
                    await System.Text.Json.JsonSerializer.SerializeAsync(Response.Body, sample, jsonOptions, ct);
                    await Response.Body.WriteAsync(NewlineBytes, ct);
                    await Response.Body.FlushAsync(ct);
                },
                cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
            && !cancellationToken.IsCancellationRequested)
        {
            await WriteTerminalErrorAsync(jsonOptions, $"Docker host unreachable: {ex.Message}", cancellationToken);
            return new EmptyResult();
        }

        if (!streamResult.IsSuccess)
            await WriteTerminalErrorAsync(jsonOptions, streamResult.Error ?? "Stats stream failed.", cancellationToken);

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
        catch
        {
            // Best-effort error frame; the response body is likely already
            // closed if we landed here.
        }
    }

    private static readonly byte[] NewlineBytes = "\n"u8.ToArray();

    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// V3.5 — common pipeline for the lifecycle action endpoints. Loads
    /// the connection (owner-scoped), dispatches the host call, writes
    /// the audit row regardless of outcome, and translates the
    /// <see cref="DockerContainerActionResult"/> into the right HTTP
    /// status. The audit row is always written so a failed Stop / Start
    /// also shows up in the per-connection activity log.
    /// </summary>
    private async Task<ActionResult<DockerContainerActionResponse>> ExecuteActionAsync(
        Guid connectionId,
        string containerName,
        DockerContainerActionType actionType,
        Func<DockerHostTransport, CancellationToken, Task<DockerContainerActionResult>> action,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(containerName))
            return BadRequest(new { error = "Container name is required." });

        var connection = await LoadOwnedConnectionAsync(connectionId, cancellationToken);
        if (connection is null) return NotFound();

        var transport = connectionMapper.BuildTransport(connection);
        DockerContainerActionResult result;
        try
        {
            result = await action(transport, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            result = new DockerContainerActionResult(DockerHostStatus.HostUnreachable, null,
                $"Docker host unreachable: {ex.Message}");
        }

        // Try to link the audit row to this connection's existing watch (if
        // any) so the per-watch history view also picks up these instance-page
        // actions. Best-effort: a missing match just leaves the FK null.
        var userId = UserId;
        var watchLink = await db.DockerWatches.AsNoTracking()
            .Where(w => w.DockerConnectionId == connectionId && w.ContainerName == containerName)
            .Select(w => new { w.Id, w.WebResourceId, w.ImageReference })
            .FirstOrDefaultAsync(cancellationToken);

        var attempt = new DockerUpdateAttemptEntity
        {
            Id = Guid.NewGuid(),
            WebResourceId = watchLink?.WebResourceId,
            DockerWatchId = watchLink?.Id,
            DockerConnectionId = connection.Id,
            ContainerId = result.ContainerId,
            InitiatedByUserId = userId,
            ActionType = actionType,
            Status = MapStatus(result.Status),
            ImageReference = watchLink?.ImageReference ?? string.Empty,
            ContainerName = containerName,
            Error = result.Error,
            CompletedUtc = DateTime.UtcNow,
            CreatedUtc = DateTime.UtcNow,
        };
        db.DockerUpdateAttempts.Add(attempt);
        await db.SaveChangesAsync(cancellationToken);

        var response = new DockerContainerActionResponse(watchMapper.ToResponse(attempt));
        return result.Status switch
        {
            DockerHostStatus.Ok => Ok(response),
            DockerHostStatus.ContainerNotFound => NotFound(new { error = result.Error, attempt = response.Attempt }),
            DockerHostStatus.HostUnreachable =>
                StatusCode(StatusCodes.Status502BadGateway, new { error = result.Error, attempt = response.Attempt }),
            DockerHostStatus.UnsupportedHostType => BadRequest(new { error = result.Error, attempt = response.Attempt }),
            _ => StatusCode(StatusCodes.Status502BadGateway, new { error = result.Error ?? "Action failed.", attempt = response.Attempt }),
        };
    }

    private Task<DockerConnectionEntity?> LoadOwnedConnectionAsync(Guid connectionId, CancellationToken cancellationToken)
    {
        var userId = UserId;
        return db.DockerConnections.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == connectionId && c.UserId == userId, cancellationToken);
    }

    /// <summary>
    /// Maps a <see cref="DockerHostStatus"/> from a lifecycle op onto the
    /// existing <see cref="DockerUpdateAttemptStatus"/> enum. V3.5 didn't
    /// add a new status family because the existing values already cover
    /// the cases an instance-page action can run into (success, host
    /// unreachable, container not found, generic failure mapped onto
    /// <c>RecreateFailed</c>).
    /// </summary>
    private static DockerUpdateAttemptStatus MapStatus(DockerHostStatus status) => status switch
    {
        DockerHostStatus.Ok => DockerUpdateAttemptStatus.Success,
        DockerHostStatus.HostUnreachable => DockerUpdateAttemptStatus.HostUnreachable,
        DockerHostStatus.ContainerNotFound => DockerUpdateAttemptStatus.ContainerNotFound,
        _ => DockerUpdateAttemptStatus.RecreateFailed,
    };
}
