using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Stashboard.Api.Auth;
using Stashboard.Api.Contracts;
using Stashboard.Api.Data;
using Stashboard.Api.Mapping;
using Stashboard.Core.Abstractions;
using Stashboard.Core.Entities;

namespace Stashboard.Api.Controllers;

/// <summary>
/// User-scoped Docker daemon connections. A user can own many named
/// connections (e.g. <c>home-server</c>, <c>vps-prod</c>); each
/// <see cref="WebResourceEntity"/> assigns itself to one. Path:
/// <c>/api/docker/connections[/{id}]</c>.
/// </summary>
[ApiController]
[Authorize]
[Route("api/docker/connections")]
public class DockerConnectionsController(
    ApplicationDbContext db,
    IDockerConnectionMapper mapper,
    IDockerHostClient hostClient) : ControllerBase
{
    private Guid UserId => User.GetUserId();

    [HttpGet]
    public async Task<ActionResult<List<DockerConnectionResponse>>> List(CancellationToken cancellationToken)
    {
        var userId = UserId;
        var connections = await db.DockerConnections.AsNoTracking()
            .Where(c => c.UserId == userId)
            .OrderBy(c => c.Name)
            .ToListAsync(cancellationToken);

        // Single batched count keyed by connection id so the dropdown can show
        // "(used by 3 services)" without a N+1.
        var usage = await db.WebResources.AsNoTracking()
            .Where(s => s.UserId == userId && s.DockerConnectionId != null)
            .GroupBy(s => s.DockerConnectionId!.Value)
            .Select(g => new { Id = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Id, x => x.Count, cancellationToken);

        return Ok(connections
            .Select(c => mapper.ToResponse(c, usage.GetValueOrDefault(c.Id, 0)))
            .ToList());
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<DockerConnectionResponse>> Get(Guid id, CancellationToken cancellationToken)
    {
        var connection = await LoadOwnedAsync(id, tracking: false, cancellationToken);
        if (connection is null) return NotFound();
        var usage = await CountUsageAsync(id, cancellationToken);
        return Ok(mapper.ToResponse(connection, usage));
    }

    [HttpPost]
    public async Task<ActionResult<DockerConnectionResponse>> Create(
        [FromBody] DockerConnectionUpsertRequest request,
        CancellationToken cancellationToken)
    {
        var connection = new DockerConnectionEntity
        {
            Id = Guid.NewGuid(),
            UserId = UserId,
            CreatedUtc = DateTime.UtcNow,
        };
        mapper.ApplyUpsert(connection, request);
        db.DockerConnections.Add(connection);

        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException ex) when (IsUniqueNameViolation(ex))
        {
            return Conflict(new { error = $"A Docker connection with name '{connection.Name}' already exists." });
        }

        return CreatedAtAction(nameof(Get), new { id = connection.Id }, mapper.ToResponse(connection, 0));
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<DockerConnectionResponse>> Update(
        Guid id,
        [FromBody] DockerConnectionUpsertRequest request,
        CancellationToken cancellationToken)
    {
        var connection = await LoadOwnedAsync(id, tracking: true, cancellationToken);
        if (connection is null) return NotFound();

        mapper.ApplyUpsert(connection, request);

        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException ex) when (IsUniqueNameViolation(ex))
        {
            return Conflict(new { error = $"A Docker connection with name '{connection.Name}' already exists." });
        }

        var usage = await CountUsageAsync(id, cancellationToken);
        return Ok(mapper.ToResponse(connection, usage));
    }

    /// <summary>
    /// Delete the connection. Refuses with 409 if any services reference it —
    /// the UI should prompt the user to reassign or unassign first.
    /// </summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var connection = await LoadOwnedAsync(id, tracking: false, cancellationToken);
        if (connection is null) return NotFound();

        var usage = await CountUsageAsync(id, cancellationToken);
        if (usage > 0)
        {
            return Conflict(new
            {
                error = $"{usage} service(s) use this connection. Reassign them first.",
                usageCount = usage,
            });
        }

        await db.DockerConnections
            .Where(c => c.Id == id)
            .ExecuteDeleteAsync(cancellationToken);
        return NoContent();
    }

    [HttpPost("test")]
    public async Task<ActionResult<DockerConnectionPingResponse>> Test(
        [FromBody] DockerConnectionPingRequest request,
        [FromQuery] Guid? connectionId,
        CancellationToken cancellationToken)
    {
        // When testing an existing connection, resolve Keep against its persisted
        // TLS so the user doesn't have to re-enter the PEM blobs to validate a
        // host-URL change. Without a connectionId, Keep yields no secrets — that's
        // the "create new" flow.
        var existing = connectionId is null
            ? null
            : await LoadOwnedAsync(connectionId.Value, tracking: false, cancellationToken);

        var transport = mapper.BuildTransport(request, existing);
        var ping = await hostClient.PingAsync(transport, cancellationToken);
        return Ok(new DockerConnectionPingResponse(ping.HostReachable, ping.Error));
    }

    [HttpGet("{id:guid}/containers")]
    public async Task<ActionResult<List<DockerContainerInfo>>> ListContainers(
        Guid id,
        CancellationToken cancellationToken)
    {
        var connection = await LoadOwnedAsync(id, tracking: false, cancellationToken);
        if (connection is null) return NotFound();

        var transport = mapper.BuildTransport(connection);
        IReadOnlyList<DockerContainerSummary> containers;
        try
        {
            containers = await hostClient.ListContainersAsync(transport, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { error = $"Docker host unreachable: {ex.Message}" });
        }

        var result = containers
            .Select(c =>
            {
                c.Labels.TryGetValue("com.docker.compose.project", out var project);
                c.Labels.TryGetValue("com.docker.compose.service", out var service);
                c.Labels.TryGetValue("com.docker.compose.project.config_files", out var configFiles);
                return new DockerContainerInfo(c.Name, c.Image, c.ImageId, c.State, c.Status, project, service, configFiles);
            })
            .ToList();

        return Ok(result);
    }

    [HttpGet("{id:guid}/containers/{containerName}/update-command")]
    public async Task<ActionResult<DockerUpdateCommandResponse>> UpdateCommand(
        Guid id,
        string containerName,
        CancellationToken cancellationToken)
    {
        var connection = await LoadOwnedAsync(id, tracking: false, cancellationToken);
        if (connection is null) return NotFound();

        var transport = mapper.BuildTransport(connection);
        DockerUpdateCommandPlan? plan;
        try
        {
            plan = await hostClient.GenerateUpdateCommandAsync(transport, containerName, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { error = $"Docker host unreachable: {ex.Message}" });
        }

        if (plan is null) return NotFound(new { error = $"Container '{containerName}' not found on the host." });

        return Ok(new DockerUpdateCommandResponse(plan.Command, plan.Source, plan.Warning));
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private Task<DockerConnectionEntity?> LoadOwnedAsync(Guid id, bool tracking, CancellationToken cancellationToken)
    {
        var userId = UserId;
        var query = tracking ? db.DockerConnections.AsTracking() : db.DockerConnections.AsNoTracking();
        return query.FirstOrDefaultAsync(c => c.Id == id && c.UserId == userId, cancellationToken);
    }

    private Task<int> CountUsageAsync(Guid id, CancellationToken cancellationToken) =>
        db.WebResources.AsNoTracking()
            .CountAsync(s => s.DockerConnectionId == id, cancellationToken);

    private static bool IsUniqueNameViolation(DbUpdateException ex) =>
        Stashboard.Api.Data.UniqueConstraintViolation.Matches(ex,
            "IX_DockerConnections_UserId_Name",
            "DockerConnections.UserId", "DockerConnections.Name");
}
