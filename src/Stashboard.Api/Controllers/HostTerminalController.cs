using System.Net.WebSockets;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Stashboard.Api.Auth;
using Stashboard.Api.Auth.PersonalAccessTokens;
using Stashboard.Api.Contracts;
using Stashboard.Api.Data;
using Stashboard.Api.Mapping;
using Stashboard.Api.Services.HostShell;
using Stashboard.Core.Abstractions;
using Stashboard.Core.Entities;
using Stashboard.Core.Enums;
using Stashboard.Core.Options;

namespace Stashboard.Api.Controllers;

/// <summary>
/// V5.3 — the browser host terminal: an interactive SSH shell on the Docker
/// host. Path: <c>/api/docker/connections/{connectionId}/host-shell</c>.
/// </summary>
/// <remarks>
/// This is the most dangerous surface in the product (host-level RCE), so it is
/// gated three ways, all required: the global <c>Stashboard:AllowHostShell</c>
/// flag, the per-connection <c>AllowHostShell</c> opt-in, and ownership of an
/// <see cref="DockerHostType.Ssh"/> connection. There is no role system in
/// Stashboard — every connection is owned by exactly one user — so "admin only"
/// reduces to "the owner, with both flags on".
///
/// Auth split: a browser <c>WebSocket</c> can't send the JWT header, so the
/// authenticated <c>POST ticket</c> mints a single-use, short-lived ticket and
/// the <c>GET ws</c> upgrade authenticates with that ticket alone
/// (<see cref="AllowAnonymousAttribute"/>).
/// </remarks>
[ApiController]
[Authorize]
[Route("api/docker/connections/{connectionId:guid}/host-shell")]
public class HostTerminalController(
    ApplicationDbContext db,
    IDockerConnectionMapper connectionMapper,
    IHostShellTicketService ticketService,
    IHostShellSessionRegistry sessionRegistry,
    IHostShellConnector connector,
    IHostShellSettingsService hostShellSettings,
    IServiceScopeFactory scopeFactory,
    IHostApplicationLifetime appLifetime,
    IOptions<HostShellOptions> hostShellOptions,
    ILogger<HostTerminalController> logger) : ControllerBase
{
    [HttpPost("ticket")]
    [DenyPersonalAccessToken]
    public async Task<ActionResult<HostShellTicketResponse>> CreateTicket(
        Guid connectionId, CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        var connection = await LoadOwnedConnectionAsync(connectionId, userId, cancellationToken);
        var globalEnabled = await hostShellSettings.IsEnabledAsync(cancellationToken);
        var gate = CheckEligibility(connection, globalEnabled);
        if (gate is not null) return gate;

        var ticket = ticketService.Issue(userId, connectionId);
        var response = new HostShellTicketResponse(
            Ticket: ticket,
            ExpiresInSeconds: Math.Max(1, hostShellOptions.Value.TicketTtlSeconds),
            WebSocketPath: $"/api/docker/connections/{connectionId}/host-shell/ws");
        return Ok(response);
    }

    [AllowAnonymous]
    [HttpGet("ws")]
    public async Task<IActionResult> OpenWebSocket(
        Guid connectionId,
        [FromQuery] string? ticket,
        [FromQuery] uint cols = 80,
        [FromQuery] uint rows = 24,
        CancellationToken cancellationToken = default)
    {
        if (!HttpContext.WebSockets.IsWebSocketRequest)
            return BadRequest(new { error = "This endpoint expects a WebSocket upgrade." });

        // 1) Authenticate via the single-use ticket (no JWT header on a WS).
        var redeemed = ticketService.Redeem(ticket ?? string.Empty);
        if (redeemed is null || redeemed.ConnectionId != connectionId)
            return Unauthorized(new { error = "Invalid or expired host-terminal ticket." });

        var userId = redeemed.UserId;

        // 2) Re-validate eligibility — flags / ownership may have changed in the
        //    seconds since the ticket was minted (defense in depth).
        var connection = await LoadOwnedConnectionAsync(connectionId, userId, cancellationToken);
        var globalEnabled = await hostShellSettings.IsEnabledAsync(cancellationToken);
        if (CheckEligibility(connection, globalEnabled) is not null || connection is null)
            return StatusCode(StatusCodes.Status403Forbidden,
                new { error = "Host terminal is not available for this connection." });

        var ssh = connectionMapper.BuildTransport(connection).Ssh;
        if (ssh is null)
            return StatusCode(StatusCodes.Status409Conflict,
                new { error = "This SSH connection is missing host / username / private key." });

        // 3) Enforce concurrency caps before accepting the socket.
        using var lease = sessionRegistry.TryAcquire(userId, connectionId, out var rejection);
        if (lease is null)
        {
            // Accept then immediately close with the reason so the browser shows
            // a clean message rather than a raw handshake failure.
            using var rejectedSocket = await HttpContext.WebSockets.AcceptWebSocketAsync();
            await TryCloseAsync(rejectedSocket, rejection ?? "Session limit reached.", cancellationToken);
            return new EmptyResult();
        }

        using var socket = await HttpContext.WebSockets.AcceptWebSocketAsync();
        await RunSessionAsync(socket, connection, ssh, userId, new HostShellWindow(cols, rows), cancellationToken);
        return new EmptyResult();
    }

    // ── session orchestration ─────────────────────────────────────────────────

    private async Task RunSessionAsync(
        WebSocket socket,
        DockerConnectionEntity connection,
        DockerSshCredentials ssh,
        Guid userId,
        HostShellWindow window,
        CancellationToken cancellationToken)
    {
        var sessionId = await WriteAuditStartAsync(connection, userId, cancellationToken);
        logger.LogInformation(
            "Host terminal opened: user {UserId} → connection {ConnectionId} (ssh://{User}@{Host}), session {SessionId}.",
            userId, connection.Id, ssh.Username, ssh.Host, sessionId);

        var client = new WebSocketShellClientTransport(socket);
        IHostShellChannel? channel = null;
        HostShellSessionResult result;
        try
        {
            // ssh.Connect() blocks on the network handshake — keep it off the
            // request thread.
            channel = await Task.Run(() => connector.Connect(ssh, window, cancellationToken: cancellationToken), cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Host terminal SSH connect failed for connection {ConnectionId}.", connection.Id);
            await client.CloseAsync($"SSH connection failed: {ex.Message}", CancellationToken.None);
            await WriteAuditEndAsync(sessionId, HostShellSessionEndReason.Error, 0, 0, ex.Message);
            return;
        }

        try
        {
            var idleTimeout = hostShellOptions.Value.IdleTimeoutSeconds > 0
                ? TimeSpan.FromSeconds(hostShellOptions.Value.IdleTimeoutSeconds)
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
            "Host terminal closed: session {SessionId}, reason {Reason}, {BytesIn} B in / {BytesOut} B out.",
            sessionId, result.EndReason, result.BytesFromClient, result.BytesToClient);
    }

    private async Task<Guid> WriteAuditStartAsync(
        DockerConnectionEntity connection, Guid userId, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var scopedDb = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var row = new HostShellSessionEntity
        {
            Id = Guid.NewGuid(),
            InitiatedByUserId = userId,
            DockerConnectionId = connection.Id,
            ConnectionName = connection.Name,
            SshHost = connection.SshHost,
            SshUsername = connection.SshUsername,
            StartedUtc = DateTime.UtcNow,
            CreatedUtc = DateTime.UtcNow,
            EndReason = HostShellSessionEndReason.Active,
        };
        scopedDb.HostShellSessions.Add(row);
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
            var row = await scopedDb.HostShellSessions.FirstOrDefaultAsync(s => s.Id == sessionId);
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
            logger.LogError(ex, "Failed to finalise host-terminal audit row {SessionId}.", sessionId);
        }
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Validates the three gates the host terminal requires. Returns a non-null
    /// error result when blocked, or <c>null</c> when the connection is eligible.
    /// <paramref name="globalEnabled"/> is the DB-backed master switch managed
    /// from the Settings page.
    /// </summary>
    private ActionResult? CheckEligibility(DockerConnectionEntity? connection, bool globalEnabled)
    {
        if (!globalEnabled)
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                error = "Host terminal is disabled on this server. Enable it on the Settings page (Settings → Host terminal).",
            });

        if (connection is null)
            return NotFound();

        if (connection.HostType != DockerHostType.Ssh)
            return BadRequest(new
            {
                error = "Host terminal is only available for SSH tunnel connections.",
            });

        if (!connection.AllowHostShell)
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                error = "Host terminal is not enabled for this connection. Turn on 'Allow host terminal' in the connection settings.",
            });

        return null;
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
