using System.Net.WebSockets;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Stashboard.Api.Auth;
using Stashboard.Api.Contracts;
using Stashboard.Api.Data;
using Stashboard.Api.Mapping;
using Stashboard.Api.Services.ContainerExec;
using Stashboard.Api.Services.HostShell;
using Stashboard.Core.Abstractions;
using Stashboard.Core.Entities;
using Stashboard.Core.Enums;
using Stashboard.Core.Options;

namespace Stashboard.Api.Controllers;

/// <summary>
/// V5.7 — the browser container-exec terminal: an interactive shell <em>inside</em>
/// a container, via the Docker daemon's <c>exec</c> API. Path:
/// <c>/api/docker/connections/{connectionId}/containers/{containerName}/exec</c>.
/// </summary>
/// <remarks>
/// Sensitive (it runs arbitrary commands in the workload), so it is gated two
/// ways, both required: the global <c>Stashboard:AllowContainerExec</c> switch
/// (DB-backed, managed at Settings → Container exec) and the per-connection
/// <c>AllowExec</c> opt-in, plus ownership of the connection. Unlike the V5.3
/// host terminal there is <em>no</em> host-type restriction — exec routes
/// through the daemon, so it works for local-socket, TCP+TLS and SSH-tunnel
/// connections alike.
///
/// Reuses the V5.3 transport: a browser <c>WebSocket</c> can't send the JWT
/// header, so the authenticated <c>POST ticket</c> mints a single-use,
/// short-lived ticket (binding the chosen container + command) and the
/// <c>GET ws</c> upgrade authenticates with that ticket alone
/// (<see cref="AllowAnonymousAttribute"/>). The byte pump
/// (<see cref="HostShellSession"/>) and WebSocket adapter
/// (<see cref="WebSocketShellClientTransport"/>) are shared with the host
/// terminal.
/// </remarks>
[ApiController]
[Authorize]
[Route("api/docker/connections/{connectionId:guid}/containers/{containerName}/exec")]
public class ContainerExecController(
    ApplicationDbContext db,
    IDockerConnectionMapper connectionMapper,
    IContainerExecTicketService ticketService,
    IContainerExecSessionRegistry sessionRegistry,
    IContainerExecConnector connector,
    IContainerExecSettingsService execSettings,
    IServiceScopeFactory scopeFactory,
    IHostApplicationLifetime appLifetime,
    IOptions<ContainerExecOptions> execOptions,
    ILogger<ContainerExecController> logger) : ControllerBase
{
    [HttpPost("ticket")]
    public async Task<ActionResult<ContainerExecTicketResponse>> CreateTicket(
        Guid connectionId,
        string containerName,
        [FromBody] ContainerExecTicketRequest? request,
        CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        var connection = await LoadOwnedConnectionAsync(connectionId, userId, cancellationToken);
        var globalEnabled = await execSettings.IsEnabledAsync(cancellationToken);
        var gate = CheckEligibility(connection, globalEnabled);
        if (gate is not null) return gate;

        var command = ParseCommand(request?.Command);
        var ticket = ticketService.Issue(userId, connectionId, containerName, command);
        var response = new ContainerExecTicketResponse(
            Ticket: ticket,
            ExpiresInSeconds: Math.Max(1, execOptions.Value.TicketTtlSeconds),
            WebSocketPath:
                $"/api/docker/connections/{connectionId}/containers/{Uri.EscapeDataString(containerName)}/exec/ws");
        return Ok(response);
    }

    [AllowAnonymous]
    [HttpGet("ws")]
    public async Task<IActionResult> OpenWebSocket(
        Guid connectionId,
        string containerName,
        [FromQuery] string? ticket,
        [FromQuery] uint cols = 80,
        [FromQuery] uint rows = 24,
        CancellationToken cancellationToken = default)
    {
        if (!HttpContext.WebSockets.IsWebSocketRequest)
            return BadRequest(new { error = "This endpoint expects a WebSocket upgrade." });

        // 1) Authenticate via the single-use ticket (no JWT header on a WS).
        var redeemed = ticketService.Redeem(ticket ?? string.Empty);
        if (redeemed is null || redeemed.ConnectionId != connectionId || redeemed.ContainerName != containerName)
            return Unauthorized(new { error = "Invalid or expired container-exec ticket." });

        var userId = redeemed.UserId;

        // 2) Re-validate eligibility — flags / ownership may have changed in the
        //    seconds since the ticket was minted (defense in depth).
        var connection = await LoadOwnedConnectionAsync(connectionId, userId, cancellationToken);
        var globalEnabled = await execSettings.IsEnabledAsync(cancellationToken);
        if (CheckEligibility(connection, globalEnabled) is not null || connection is null)
            return StatusCode(StatusCodes.Status403Forbidden,
                new { error = "Container exec is not available for this connection." });

        var transport = connectionMapper.BuildTransport(connection);

        // 3) Enforce concurrency caps before accepting the socket.
        using var lease = sessionRegistry.TryAcquire(userId, connectionId, out var rejection);
        if (lease is null)
        {
            using var rejectedSocket = await HttpContext.WebSockets.AcceptWebSocketAsync();
            await TryCloseAsync(rejectedSocket, rejection ?? "Session limit reached.", cancellationToken);
            return new EmptyResult();
        }

        using var socket = await HttpContext.WebSockets.AcceptWebSocketAsync();
        var execRequest = new ContainerExecRequest(
            containerName, redeemed.Command, new HostShellWindow(cols, rows));
        await RunSessionAsync(socket, connection, transport, execRequest, userId, cancellationToken);
        return new EmptyResult();
    }

    // ── session orchestration ─────────────────────────────────────────────────

    private async Task RunSessionAsync(
        WebSocket socket,
        DockerConnectionEntity connection,
        DockerHostTransport transport,
        ContainerExecRequest execRequest,
        Guid userId,
        CancellationToken cancellationToken)
    {
        var commandText = string.Join(' ', execRequest.Command);
        var sessionId = await WriteAuditStartAsync(connection, userId, execRequest.ContainerName, commandText, cancellationToken);
        logger.LogInformation(
            "Container exec opened: user {UserId} → connection {ConnectionId}, container {Container}, command \"{Command}\", session {SessionId}.",
            userId, connection.Id, execRequest.ContainerName, commandText, sessionId);

        var client = new WebSocketShellClientTransport(socket);
        IHostShellChannel? channel = null;
        HostShellSessionResult result;
        try
        {
            channel = await connector.ConnectAsync(transport, execRequest, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Container exec connect failed for connection {ConnectionId}, container {Container}.",
                connection.Id, execRequest.ContainerName);
            await client.CloseAsync($"Exec failed: {ex.Message}", CancellationToken.None);
            await WriteAuditEndAsync(sessionId, HostShellSessionEndReason.Error, 0, 0, ex.Message);
            return;
        }

        try
        {
            var idleTimeout = execOptions.Value.IdleTimeoutSeconds > 0
                ? TimeSpan.FromSeconds(execOptions.Value.IdleTimeoutSeconds)
                : Timeout.InfiniteTimeSpan;

            // Link app shutdown into the session token so a graceful stop finalises
            // the row as ClosedByServer (within the host's shutdown window) rather
            // than orphaning it as Active for the startup sweep to reap.
            using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, appLifetime.ApplicationStopping);
            result = await HostShellSession.RunAsync(channel, client, idleTimeout, logger, sessionCts.Token);
        }
        finally
        {
            channel.Dispose();
        }

        await client.CloseAsync(CloseReasonText(result.EndReason), CancellationToken.None);
        await WriteAuditEndAsync(sessionId, result.EndReason, result.BytesFromClient, result.BytesToClient, result.Error);

        logger.LogInformation(
            "Container exec closed: session {SessionId}, reason {Reason}, {BytesIn} B in / {BytesOut} B out.",
            sessionId, result.EndReason, result.BytesFromClient, result.BytesToClient);
    }

    private async Task<Guid> WriteAuditStartAsync(
        DockerConnectionEntity connection, Guid userId, string containerName, string command, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var scopedDb = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var row = new DockerExecSessionEntity
        {
            Id = Guid.NewGuid(),
            InitiatedByUserId = userId,
            DockerConnectionId = connection.Id,
            ConnectionName = connection.Name,
            ContainerName = containerName,
            Command = command,
            StartedUtc = DateTime.UtcNow,
            CreatedUtc = DateTime.UtcNow,
            EndReason = HostShellSessionEndReason.Active,
        };
        scopedDb.DockerExecSessions.Add(row);
        await scopedDb.SaveChangesAsync(cancellationToken);
        return row.Id;
    }

    private async Task WriteAuditEndAsync(
        Guid sessionId, HostShellSessionEndReason endReason, long bytesIn, long bytesOut, string? error)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var scopedDb = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var row = await scopedDb.DockerExecSessions.FirstOrDefaultAsync(s => s.Id == sessionId);
            if (row is null) return;
            row.EndedUtc = DateTime.UtcNow;
            row.BytesFromClient = bytesIn;
            row.BytesToClient = bytesOut;
            row.EndReason = endReason;
            row.Error = error;
            await scopedDb.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            // Never let an audit-write failure crash the request teardown.
            logger.LogError(ex, "Failed to finalise container-exec audit row {SessionId}.", sessionId);
        }
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Validates the two gates container exec requires (global switch +
    /// per-connection opt-in) plus connection ownership. Returns a non-null
    /// error result when blocked, or <c>null</c> when eligible. There is no
    /// host-type gate — exec works through the daemon for every host type.
    /// </summary>
    private ActionResult? CheckEligibility(DockerConnectionEntity? connection, bool globalEnabled)
    {
        if (!globalEnabled)
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                error = "Container exec is disabled on this server. Enable it on the Settings page (Settings → Container exec).",
            });

        if (connection is null)
            return NotFound();

        if (!connection.AllowExec)
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                error = "Container exec is not enabled for this connection. Turn on 'Allow container exec' in the connection settings.",
            });

        return null;
    }

    /// <summary>
    /// Splits the operator-supplied command into argv. Whitespace-separated, no
    /// shell quoting (most sessions are just a shell path like <c>/bin/sh</c> or
    /// <c>/bin/bash</c>). Falls back to the configured default when blank.
    /// </summary>
    private string[] ParseCommand(string? command)
    {
        var raw = string.IsNullOrWhiteSpace(command) ? execOptions.Value.DefaultCommand : command;
        var parts = raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length > 0 ? parts : new[] { "/bin/sh" };
    }

    private Task<DockerConnectionEntity?> LoadOwnedConnectionAsync(
        Guid connectionId, Guid userId, CancellationToken cancellationToken) =>
        db.DockerConnections.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == connectionId && c.UserId == userId, cancellationToken);

    private static string CloseReasonText(HostShellSessionEndReason reason) => reason switch
    {
        HostShellSessionEndReason.IdleTimeout => "Closed: idle timeout.",
        HostShellSessionEndReason.RemoteClosed => "Shell exited.",
        HostShellSessionEndReason.ClosedByServer => "Closed by server.",
        HostShellSessionEndReason.Error => "Closed: session error.",
        _ => "Session ended.",
    };

    private static async Task TryCloseAsync(WebSocket socket, string reason, CancellationToken cancellationToken)
    {
        try
        {
            var trimmed = reason.Length > 120 ? reason[..120] : reason;
            await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, trimmed, cancellationToken);
        }
        catch { /* best-effort */ }
    }
}
