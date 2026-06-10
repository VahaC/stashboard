using System.Net.WebSockets;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Stashboard.Api.Auth;
using Stashboard.Api.Contracts;
using Stashboard.Api.Data;
using Stashboard.Api.Mapping;
using Stashboard.Api.Services.HostShell;
using Stashboard.Api.Services.ProxmoxConsole;
using Stashboard.Core.Abstractions;
using Stashboard.Core.Entities;
using Stashboard.Core.Enums;
using Stashboard.Core.Options;

namespace Stashboard.Api.Controllers;

/// <summary>
/// V6.6 — the browser Proxmox LXC console: an interactive shell <em>inside</em>
/// an LXC, reached by SSHing to the Proxmox host and running
/// <c>pct exec &lt;vmid&gt; -- &lt;shell&gt;</c>. Path:
/// <c>/api/proxmox/connections/{connectionId}/lxc/{vmId}/console</c>.
/// </summary>
/// <remarks>
/// This is a blend of the two shipped shell phases: it reuses the V5.3 SSH PTY
/// transport (<see cref="IHostShellConnector"/>) like the host terminal — the
/// console needs SSH credentials on the Proxmox host — and the V5.7 per-session
/// command + two-way gate like container exec. It is gated three ways, all
/// required: the global <c>Stashboard:AllowProxmoxConsole</c> switch (DB-backed,
/// managed at Settings → LXC console), the per-host <c>AllowConsole</c> opt-in,
/// and ownership of a Proxmox host with SSH configured.
///
/// Reuses the V5.3 transport verbatim: a browser <c>WebSocket</c> can't send the
/// JWT header, so the authenticated <c>POST ticket</c> mints a single-use,
/// short-lived ticket (binding the chosen LXC + command) and the <c>GET ws</c>
/// upgrade authenticates with that ticket alone
/// (<see cref="AllowAnonymousAttribute"/>). The byte pump
/// (<see cref="HostShellSession"/>) and WebSocket adapter
/// (<see cref="WebSocketShellClientTransport"/>) are shared with the host
/// terminal and container exec.
/// </remarks>
[ApiController]
[Authorize]
[Route("api/proxmox/connections/{connectionId:guid}/lxc/{vmId:int}/console")]
public class ProxmoxConsoleController(
    ApplicationDbContext db,
    IProxmoxConnectionMapper connectionMapper,
    IProxmoxConsoleTicketService ticketService,
    IProxmoxConsoleSessionRegistry sessionRegistry,
    IHostShellConnector connector,
    IProxmoxConsoleSettingsService consoleSettings,
    IServiceScopeFactory scopeFactory,
    IHostApplicationLifetime appLifetime,
    IOptions<ProxmoxConsoleOptions> consoleOptions,
    ILogger<ProxmoxConsoleController> logger) : ControllerBase
{
    [HttpPost("ticket")]
    public async Task<ActionResult<ProxmoxConsoleTicketResponse>> CreateTicket(
        Guid connectionId,
        int vmId,
        [FromBody] ProxmoxConsoleTicketRequest? request,
        CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        var connection = await LoadOwnedConnectionAsync(connectionId, userId, cancellationToken);
        var globalEnabled = await consoleSettings.IsEnabledAsync(cancellationToken);
        var gate = CheckEligibility(connection, globalEnabled);
        if (gate is not null) return gate;

        var command = ParseCommand(request?.Command);
        var ticket = ticketService.Issue(userId, connectionId, vmId, command);
        var response = new ProxmoxConsoleTicketResponse(
            Ticket: ticket,
            ExpiresInSeconds: Math.Max(1, consoleOptions.Value.TicketTtlSeconds),
            WebSocketPath: $"/api/proxmox/connections/{connectionId}/lxc/{vmId}/console/ws");
        return Ok(response);
    }

    [AllowAnonymous]
    [HttpGet("ws")]
    public async Task<IActionResult> OpenWebSocket(
        Guid connectionId,
        int vmId,
        [FromQuery] string? ticket,
        [FromQuery] uint cols = 80,
        [FromQuery] uint rows = 24,
        CancellationToken cancellationToken = default)
    {
        if (!HttpContext.WebSockets.IsWebSocketRequest)
            return BadRequest(new { error = "This endpoint expects a WebSocket upgrade." });

        // 1) Authenticate via the single-use ticket (no JWT header on a WS).
        var redeemed = ticketService.Redeem(ticket ?? string.Empty);
        if (redeemed is null || redeemed.ConnectionId != connectionId || redeemed.VmId != vmId)
            return Unauthorized(new { error = "Invalid or expired LXC-console ticket." });

        var userId = redeemed.UserId;

        // 2) Re-validate eligibility — flags / ownership may have changed in the
        //    seconds since the ticket was minted (defense in depth).
        var connection = await LoadOwnedConnectionAsync(connectionId, userId, cancellationToken);
        var globalEnabled = await consoleSettings.IsEnabledAsync(cancellationToken);
        if (CheckEligibility(connection, globalEnabled) is not null || connection is null)
            return StatusCode(StatusCodes.Status403Forbidden,
                new { error = "The LXC console is not available for this host." });

        var ssh = connectionMapper.BuildProfile(connection).Ssh;
        if (ssh is null)
            return StatusCode(StatusCodes.Status409Conflict,
                new { error = "This Proxmox host is missing SSH host / username / private key — the console needs SSH." });

        // 3) Enforce concurrency caps before accepting the socket.
        using var lease = sessionRegistry.TryAcquire(userId, connectionId, out var rejection);
        if (lease is null)
        {
            using var rejectedSocket = await HttpContext.WebSockets.AcceptWebSocketAsync();
            await TryCloseAsync(rejectedSocket, rejection ?? "Session limit reached.", cancellationToken);
            return new EmptyResult();
        }

        using var socket = await HttpContext.WebSockets.AcceptWebSocketAsync();
        await RunSessionAsync(socket, connection, ssh, redeemed, userId, new HostShellWindow(cols, rows), cancellationToken);
        return new EmptyResult();
    }

    // ── session orchestration ─────────────────────────────────────────────────

    private async Task RunSessionAsync(
        WebSocket socket,
        ProxmoxConnectionEntity connection,
        DockerSshCredentials ssh,
        ProxmoxConsoleTicket ticket,
        Guid userId,
        HostShellWindow window,
        CancellationToken cancellationToken)
    {
        var commandText = string.Join(' ', ticket.Command);
        // `exec` replaces the login shell so the SSH channel closes when the inner
        // shell exits. V6.8 — vmId 0 is the **node** itself: run the chosen shell
        // directly on the host (no `pct exec` wrapper). For an LXC, `pct exec … --`
        // runs the shell inside the guest.
        var isNode = ticket.VmId == 0;
        var initialCommand = isNode
            ? $"exec {commandText}"
            : $"exec pct exec {ticket.VmId} -- {commandText}";

        var sessionId = await WriteAuditStartAsync(connection, userId, ticket.VmId, commandText, cancellationToken);
        logger.LogInformation(
            "Proxmox console opened: user {UserId} → host {ConnectionId} (ssh://{User}@{Host}), target {Target}, command \"{Command}\", session {SessionId}.",
            userId, connection.Id, ssh.Username, ssh.Host, isNode ? "node" : $"CT {ticket.VmId}", commandText, sessionId);

        var client = new WebSocketShellClientTransport(socket);
        IHostShellChannel? channel = null;
        HostShellSessionResult result;
        try
        {
            // ssh.Connect() blocks on the network handshake — keep it off the
            // request thread.
            channel = await Task.Run(
                () => connector.Connect(ssh, window, initialCommand, cancellationToken), cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "LXC console SSH connect failed for host {ConnectionId}, CT {VmId}.", connection.Id, ticket.VmId);
            await client.CloseAsync($"SSH connection failed: {ex.Message}", CancellationToken.None);
            await WriteAuditEndAsync(sessionId, HostShellSessionEndReason.Error, 0, 0, ex.Message);
            return;
        }

        try
        {
            var idleTimeout = consoleOptions.Value.IdleTimeoutSeconds > 0
                ? TimeSpan.FromSeconds(consoleOptions.Value.IdleTimeoutSeconds)
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
            "LXC console closed: session {SessionId}, reason {Reason}, {BytesIn} B in / {BytesOut} B out.",
            sessionId, result.EndReason, result.BytesFromClient, result.BytesToClient);
    }

    private async Task<Guid> WriteAuditStartAsync(
        ProxmoxConnectionEntity connection, Guid userId, int vmId, string command, CancellationToken cancellationToken)
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
            Command = command,
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
            logger.LogError(ex, "Failed to finalise LXC-console audit row {SessionId}.", sessionId);
        }
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Validates the gates the LXC console requires (global switch + per-host
    /// opt-in) plus host ownership. Returns a non-null error result when blocked,
    /// or <c>null</c> when eligible. The SSH-configured check happens in the WS
    /// path (it needs the decrypted profile).
    /// </summary>
    private ActionResult? CheckEligibility(ProxmoxConnectionEntity? connection, bool globalEnabled)
    {
        if (!globalEnabled)
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                error = "The LXC console is disabled on this server. Enable it on the Settings page (Settings → LXC console).",
            });

        if (connection is null)
            return NotFound();

        if (!connection.AllowConsole)
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                error = "The LXC console is not enabled for this host. Turn on 'Allow LXC console' in the host settings.",
            });

        return null;
    }

    /// <summary>
    /// Splits the operator-supplied command into argv. Whitespace-separated, no
    /// shell quoting (most sessions are just a shell path like <c>/bin/bash</c>
    /// or <c>/bin/sh</c>). Falls back to the configured default when blank.
    /// </summary>
    private string[] ParseCommand(string? command)
    {
        var raw = string.IsNullOrWhiteSpace(command) ? consoleOptions.Value.DefaultCommand : command;
        var parts = raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length > 0 ? parts : new[] { "/bin/bash" };
    }

    private Task<ProxmoxConnectionEntity?> LoadOwnedConnectionAsync(
        Guid connectionId, Guid userId, CancellationToken cancellationToken) =>
        db.ProxmoxConnections.AsNoTracking()
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
