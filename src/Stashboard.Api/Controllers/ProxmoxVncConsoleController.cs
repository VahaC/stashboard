using System.Net.WebSockets;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Stashboard.Api.Auth;
using Stashboard.Api.Contracts;
using Stashboard.Api.Data;
using Stashboard.Api.Mapping;
using Stashboard.Api.Services.ProxmoxConsole;
using Stashboard.Core.Abstractions;
using Stashboard.Core.Entities;
using Stashboard.Core.Enums;
using Stashboard.Core.Options;

namespace Stashboard.Api.Controllers;

/// <summary>
/// V8.6 — the browser Proxmox <strong>VM</strong> console: the VM analogue of the
/// V6.6 LXC console. A VM has no <c>pct exec</c> and no guaranteed SSH /
/// guest-agent, so the SSH-PTY transport can't be reused; instead this opens
/// Proxmox's built-in VNC — the same screen the Proxmox web UI opens — and relays
/// the raw RFB stream to a noVNC canvas in the browser. Path:
/// <c>/api/proxmox/connections/{connectionId}/qemu/{vmId}/console</c>.
/// </summary>
/// <remarks>
/// Reuses the V6.6 scaffold verbatim — the same DB-backed global switch
/// (<see cref="IProxmoxConsoleSettingsService"/>, Settings → LXC console), the same
/// per-host <c>AllowConsole</c> opt-in, the same concurrency registry
/// (<see cref="IProxmoxConsoleSessionRegistry"/>, shared with the LXC console), the
/// same single-use ticket / WS-upgrade dance, and the same audit table
/// (<see cref="ProxmoxConsoleSessionEntity"/>, already guest-kind-agnostic). Only
/// the transport changes: a <see cref="IProxmoxVncRelay"/> (vncproxy → vncwebsocket)
/// instead of an SSH PTY, and the byte pump (<see cref="ProxmoxVncSession"/>)
/// forwards RFB bytes verbatim. The SSH-configured gate is <em>dropped</em> for a VM
/// (VNC uses the API token, not SSH); the API token never reaches the browser.
/// </remarks>
[ApiController]
[Authorize]
[Route("api/proxmox/connections/{connectionId:guid}/qemu/{vmId:int}/console")]
public class ProxmoxVncConsoleController(
    ApplicationDbContext db,
    IProxmoxConnectionMapper connectionMapper,
    IProxmoxVncTicketService ticketService,
    IProxmoxConsoleSessionRegistry sessionRegistry,
    IProxmoxVncRelay relay,
    IProxmoxConsoleSettingsService consoleSettings,
    IServiceScopeFactory scopeFactory,
    IHostApplicationLifetime appLifetime,
    IOptions<ProxmoxConsoleOptions> consoleOptions,
    ILogger<ProxmoxVncConsoleController> logger) : ControllerBase
{
    [HttpPost("ticket")]
    public async Task<ActionResult<ProxmoxVncConsoleTicketResponse>> CreateTicket(
        Guid connectionId,
        int vmId,
        CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        var connection = await LoadOwnedConnectionAsync(connectionId, userId, cancellationToken);
        var globalEnabled = await consoleSettings.IsEnabledAsync(cancellationToken);
        // Gate before any host call — a blocked request never touches Proxmox.
        var gate = CheckEligibility(connection, globalEnabled);
        if (gate is not null) return gate;

        var profile = connectionMapper.BuildProfile(connection!);

        // Mint the one-time VNC session now so the RFB password is ready for the
        // browser's noVNC the moment the socket opens. A host that refuses (stopped
        // VM, token without VM.Console, console-less VM) surfaces as a clean message.
        ProxmoxVncProxyTicket vncProxy;
        try
        {
            vncProxy = await relay.OpenVncProxyAsync(profile, vmId, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "VM console vncproxy failed for host {ConnectionId}, VM {VmId}.", connectionId, vmId);
            return StatusCode(StatusCodes.Status409Conflict, new
            {
                error = "The VNC console is unavailable on this host. The VM must be running and have a "
                    + "VGA console, and the API token needs the VM.Console privilege.",
            });
        }

        var ticket = ticketService.Issue(userId, connectionId, vmId, vncProxy);
        var response = new ProxmoxVncConsoleTicketResponse(
            Ticket: ticket,
            Password: vncProxy.Ticket,
            ExpiresInSeconds: Math.Max(1, consoleOptions.Value.TicketTtlSeconds),
            WebSocketPath: $"/api/proxmox/connections/{connectionId}/qemu/{vmId}/console/ws");
        return Ok(response);
    }

    [AllowAnonymous]
    [HttpGet("ws")]
    public async Task<IActionResult> OpenWebSocket(
        Guid connectionId,
        int vmId,
        [FromQuery] string? ticket,
        CancellationToken cancellationToken = default)
    {
        if (!HttpContext.WebSockets.IsWebSocketRequest)
            return BadRequest(new { error = "This endpoint expects a WebSocket upgrade." });

        // 1) Authenticate via the single-use ticket (no JWT header on a WS).
        var redeemed = ticketService.Redeem(ticket ?? string.Empty);
        if (redeemed is null || redeemed.ConnectionId != connectionId || redeemed.VmId != vmId)
            return Unauthorized(new { error = "Invalid or expired VM-console ticket." });

        var userId = redeemed.UserId;

        // 2) Re-validate eligibility — flags / ownership may have changed in the
        //    seconds since the ticket was minted (defense in depth).
        var connection = await LoadOwnedConnectionAsync(connectionId, userId, cancellationToken);
        var globalEnabled = await consoleSettings.IsEnabledAsync(cancellationToken);
        if (CheckEligibility(connection, globalEnabled) is not null || connection is null)
            return StatusCode(StatusCodes.Status403Forbidden,
                new { error = "The VM console is not available for this host." });

        var profile = connectionMapper.BuildProfile(connection);

        // 3) Enforce concurrency caps before accepting the socket (shared with the
        //    LXC console so a user's total console tally is one number).
        using var lease = sessionRegistry.TryAcquire(userId, connectionId, out var rejection);
        if (lease is null)
        {
            using var rejectedSocket = await HttpContext.WebSockets.AcceptWebSocketAsync();
            await TryCloseAsync(rejectedSocket, rejection ?? "Session limit reached.", cancellationToken);
            return new EmptyResult();
        }

        // noVNC offers the "binary" subprotocol; confirm it in the handshake response
        // so the negotiation is complete (an unconfirmed subprotocol can make a proxy
        // or the browser tear the connection down right after the upgrade).
        var acceptBinary = HttpContext.WebSockets.WebSocketRequestedProtocols.Contains("binary");
        using var socket = acceptBinary
            ? await HttpContext.WebSockets.AcceptWebSocketAsync("binary")
            : await HttpContext.WebSockets.AcceptWebSocketAsync();
        await RunSessionAsync(socket, connection, profile, redeemed, userId, cancellationToken);
        return new EmptyResult();
    }

    // ── session orchestration ─────────────────────────────────────────────────

    private async Task RunSessionAsync(
        WebSocket socket,
        ProxmoxConnectionEntity connection,
        ProxmoxConnectionProfile profile,
        ProxmoxVncTicket ticket,
        Guid userId,
        CancellationToken cancellationToken)
    {
        // CancellationToken.None, not the request token: HttpContext.RequestAborted
        // fires spuriously on a WebSocket-upgraded request (see the ConnectAsync note),
        // and the audit-start row must be written regardless so the session is recorded.
        var sessionId = await WriteAuditStartAsync(connection, userId, ticket.VmId, CancellationToken.None);
        logger.LogInformation(
            "Proxmox VM console opened: user {UserId} → host {ConnectionId} ({ApiBaseUrl}), VM {VmId}, session {SessionId}.",
            userId, connection.Id, connection.ApiBaseUrl, ticket.VmId, sessionId);

        var client = new WebSocketVncChannel(socket);
        IProxmoxVncChannel? host = null;
        ProxmoxVncSessionResult result;
        try
        {
            // NOT the request token: HttpContext.RequestAborted fires spuriously /
            // immediately on a WebSocket-upgraded request (seen behind the Vite dev
            // proxy), which would cancel the Proxmox connect before it starts. The
            // relay applies its own connect timeout; a real client disconnect is
            // detected by the pump (the browser→host read returns a Close).
            host = await relay.ConnectAsync(profile, ticket.VmId, ticket.VncProxy, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "VM console VNC relay connect failed for host {ConnectionId}, VM {VmId}.", connection.Id, ticket.VmId);
            await TryCloseAsync(socket, ProxmoxVncRelayMessage(ex), CancellationToken.None);
            await WriteAuditEndAsync(sessionId, HostShellSessionEndReason.Error, 0, 0, ex.Message);
            return;
        }

        try
        {
            var idleTimeout = consoleOptions.Value.IdleTimeoutSeconds > 0
                ? TimeSpan.FromSeconds(consoleOptions.Value.IdleTimeoutSeconds)
                : Timeout.InfiniteTimeSpan;

            // Drive the pump off app-shutdown only — NOT the request token, which
            // fires spuriously on an upgraded WebSocket (see the ConnectAsync note).
            // A graceful stop still finalises the row as ClosedByServer; a client
            // disconnect is detected by the pump itself (the browser read returns a
            // Close), so the session ends without relying on RequestAborted.
            using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(
                appLifetime.ApplicationStopping);
            result = await ProxmoxVncSession.RunAsync(client, host, idleTimeout, logger, sessionCts.Token);
        }
        finally
        {
            await host.DisposeAsync();
        }

        await TryCloseAsync(socket, CloseReasonText(result.EndReason), CancellationToken.None);
        await WriteAuditEndAsync(sessionId, result.EndReason, result.BytesFromClient, result.BytesToClient, result.Error);

        logger.LogInformation(
            "VM console closed: session {SessionId}, reason {Reason}, {BytesIn} B in / {BytesOut} B out.",
            sessionId, result.EndReason, result.BytesFromClient, result.BytesToClient);
    }

    private async Task<Guid> WriteAuditStartAsync(
        ProxmoxConnectionEntity connection, Guid userId, int vmId, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var scopedDb = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var guestName = await scopedDb.ProxmoxGuests.AsNoTracking()
            .Where(g => g.ProxmoxConnectionId == connection.Id && g.VmId == vmId)
            .Select(g => g.Name)
            .FirstOrDefaultAsync(cancellationToken);
        var row = new ProxmoxConsoleSessionEntity
        {
            Id = Guid.NewGuid(),
            InitiatedByUserId = userId,
            ProxmoxConnectionId = connection.Id,
            ConnectionName = connection.Name,
            NodeName = connection.NodeName,
            VmId = vmId,
            GuestName = guestName,
            // No shell for a VNC session — record the transport so the audit view
            // distinguishes a VM VNC console from an LXC shell session.
            Command = "VNC console",
            StartedUtc = DateTime.UtcNow,
            CreatedUtc = DateTime.UtcNow,
            EndReason = HostShellSessionEndReason.Active,
        };
        scopedDb.ProxmoxConsoleSessions.Add(row);
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
            var row = await scopedDb.ProxmoxConsoleSessions.FirstOrDefaultAsync(s => s.Id == sessionId);
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
            logger.LogError(ex, "Failed to finalise VM-console audit row {SessionId}.", sessionId);
        }
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Validates the gates the VM console requires (global switch + per-host opt-in)
    /// plus host ownership. Returns a non-null error result when blocked, or
    /// <c>null</c> when eligible. Unlike the LXC console there is <em>no</em>
    /// SSH-configured check — a VM's VNC relay uses the API token, not SSH.
    /// </summary>
    private ActionResult? CheckEligibility(ProxmoxConnectionEntity? connection, bool globalEnabled)
    {
        if (!globalEnabled)
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                error = "The guest console is disabled on this server. Enable it on the Settings page (Settings → Guest console).",
            });

        if (connection is null)
            return NotFound();

        if (!connection.AllowConsole)
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                error = "The console is not enabled for this host. Turn on 'Allow guest console' in the host settings.",
            });

        return null;
    }

    private Task<ProxmoxConnectionEntity?> LoadOwnedConnectionAsync(
        Guid connectionId, Guid userId, CancellationToken cancellationToken) =>
        db.ProxmoxConnections.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == connectionId && c.UserId == userId, cancellationToken);

    private static string ProxmoxVncRelayMessage(Exception ex) =>
        ex is ProxmoxVncRelayException ? ex.Message
            : "The Proxmox VNC console could not be opened on this host.";

    private static string CloseReasonText(HostShellSessionEndReason reason) => reason switch
    {
        HostShellSessionEndReason.IdleTimeout => "Closed: idle timeout.",
        HostShellSessionEndReason.RemoteClosed => "VNC session ended.",
        HostShellSessionEndReason.ClosedByServer => "Closed by server.",
        HostShellSessionEndReason.Error => "Closed: session error.",
        _ => "Session ended.",
    };

    private static async Task TryCloseAsync(WebSocket socket, string reason, CancellationToken cancellationToken)
    {
        if (socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived)) return;
        try
        {
            var trimmed = reason.Length > 120 ? reason[..120] : reason;
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, trimmed, cancellationToken);
        }
        catch { /* best-effort */ }
    }
}
