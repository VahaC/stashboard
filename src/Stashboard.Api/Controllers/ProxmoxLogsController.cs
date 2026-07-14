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
/// V6.12 — the browser LXC live-logs tail: a <em>read-only</em> stream of a
/// guest's system journal, reached by SSHing to the Proxmox host and running
/// <c>pct exec &lt;vmid&gt; -- journalctl -f</c> (falling back to <c>tail -F</c>
/// of <c>/var/log</c> when the guest has no journald). Path:
/// <c>/api/proxmox/connections/{connectionId}/lxc/{vmId}/logs</c>.
/// </summary>
/// <remarks>
/// This is the observability sibling of the V6.6 <see cref="ProxmoxConsoleController"/>
/// and reuses its transport <em>verbatim</em>: the same single-use ticket service
/// (<see cref="IProxmoxConsoleTicketService"/>), the same concurrency registry
/// (<see cref="IProxmoxConsoleSessionRegistry"/>), the same SSH PTY connector
/// (<see cref="IHostShellConnector"/>), the same byte pump
/// (<see cref="HostShellSession"/>) and WebSocket adapter
/// (<see cref="WebSocketShellClientTransport"/>). It is gated identically — the
/// global <c>Stashboard:AllowProxmoxConsole</c> switch, the per-host
/// <c>AllowConsole</c> opt-in, host ownership, and SSH credentials.
///
/// Three deliberate differences from the console:
/// <list type="bullet">
/// <item>the remote command is built here (never client-supplied) so it is always
/// the read-only journal tail;</item>
/// <item>there is no idle timeout — a quiet guest's tail must not be reaped after
/// ten silent minutes;</item>
/// <item>no audit row is written — nothing is executed beyond a read-only log
/// read, and the interactive console (which a logs user can already open) is where
/// shell sessions are audited.</item>
/// </list>
/// Read-only is a property of the surface, not a security boundary: a logs-gated
/// user has, by construction, passed the identical console gate.
/// </remarks>
[ApiController]
[Authorize]
[Route("api/proxmox/connections/{connectionId:guid}/lxc/{vmId:int}/logs")]
public class ProxmoxLogsController(
    ApplicationDbContext db,
    IProxmoxConnectionMapper connectionMapper,
    IProxmoxConsoleTicketService ticketService,
    IProxmoxConsoleSessionRegistry sessionRegistry,
    IHostShellConnector connector,
    IProxmoxConsoleSettingsService consoleSettings,
    IHostApplicationLifetime appLifetime,
    IOptions<ProxmoxConsoleOptions> consoleOptions,
    ILogger<ProxmoxLogsController> logger) : ControllerBase
{
    /// <summary>How many lines of backlog the live tail seeds before following.</summary>
    private const int FollowBacklog = 200;

    /// <summary>How many lines a one-shot (download) snapshot pulls.</summary>
    private const int SnapshotLines = 5000;

    [HttpPost("ticket")]
    public async Task<ActionResult<ProxmoxLogsTicketResponse>> CreateTicket(
        Guid connectionId,
        int vmId,
        CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        var connection = await LoadOwnedConnectionAsync(connectionId, userId, cancellationToken);
        var globalEnabled = await consoleSettings.IsEnabledAsync(cancellationToken);
        var gate = CheckEligibility(connection, globalEnabled);
        if (gate is not null) return gate;

        // The logs tail is read-only and the remote command is built server-side,
        // so — unlike the console — the ticket binds no command. An empty command
        // also means a logs ticket can't be cross-redeemed at the console WS to get
        // a shell (it would build an empty `pct exec … --` and fail).
        var ticket = ticketService.Issue(userId, connectionId, vmId, Array.Empty<string>());
        var response = new ProxmoxLogsTicketResponse(
            Ticket: ticket,
            ExpiresInSeconds: Math.Max(1, consoleOptions.Value.TicketTtlSeconds),
            WebSocketPath: $"/api/proxmox/connections/{connectionId}/lxc/{vmId}/logs/ws");
        return Ok(response);
    }

    [AllowAnonymous]
    [HttpGet("ws")]
    public async Task<IActionResult> OpenWebSocket(
        Guid connectionId,
        int vmId,
        [FromQuery] string? ticket,
        [FromQuery] bool follow = true,
        CancellationToken cancellationToken = default)
    {
        if (!HttpContext.WebSockets.IsWebSocketRequest)
            return BadRequest(new { error = "This endpoint expects a WebSocket upgrade." });

        // 1) Authenticate via the single-use ticket (no JWT header on a WS).
        var redeemed = ticketService.Redeem(ticket ?? string.Empty);
        if (redeemed is null || redeemed.ConnectionId != connectionId || redeemed.VmId != vmId)
            return Unauthorized(new { error = "Invalid or expired LXC-logs ticket." });

        var userId = redeemed.UserId;

        // 2) Re-validate eligibility — flags / ownership may have changed in the
        //    seconds since the ticket was minted (defense in depth).
        var connection = await LoadOwnedConnectionAsync(connectionId, userId, cancellationToken);
        var globalEnabled = await consoleSettings.IsEnabledAsync(cancellationToken);
        if (CheckEligibility(connection, globalEnabled) is not null || connection is null)
            return StatusCode(StatusCodes.Status403Forbidden,
                new { error = "LXC logs are not available for this host." });

        var ssh = connectionMapper.BuildProfile(connection).Ssh;
        if (ssh is null)
            return StatusCode(StatusCodes.Status409Conflict,
                new { error = "This Proxmox host is missing SSH host / username / private key — the log tail needs SSH." });

        // 3) Enforce concurrency caps (shared with the console) before accepting.
        using var lease = sessionRegistry.TryAcquire(userId, connectionId, out var rejection);
        if (lease is null)
        {
            using var rejectedSocket = await HttpContext.WebSockets.AcceptWebSocketAsync();
            await TryCloseAsync(rejectedSocket, rejection ?? "Session limit reached.", cancellationToken);
            return new EmptyResult();
        }

        using var socket = await HttpContext.WebSockets.AcceptWebSocketAsync();
        await RunSessionAsync(socket, connection, ssh, vmId, follow, cancellationToken);
        return new EmptyResult();
    }

    // ── session orchestration ─────────────────────────────────────────────────

    private async Task RunSessionAsync(
        WebSocket socket,
        ProxmoxConnectionEntity connection,
        DockerSshCredentials ssh,
        int vmId,
        bool follow,
        CancellationToken cancellationToken)
    {
        var initialCommand = BuildLogsCommand(vmId, follow);
        logger.LogInformation(
            "LXC logs opened: user → host {ConnectionId} (ssh://{User}@{Host}), CT {VmId}, follow={Follow}.",
            connection.Id, ssh.Username, ssh.Host, vmId, follow);

        // The PTY window only affects cosmetic wrapping for a read-only tail; a
        // wide, tall window keeps long journal lines from being chopped.
        var window = new HostShellWindow(220, 50);

        var client = new WebSocketShellClientTransport(socket);
        IHostShellChannel? channel;
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
                "LXC logs SSH connect failed for host {ConnectionId}, CT {VmId}.", connection.Id, vmId);
            await client.CloseAsync($"SSH connection failed: {ex.Message}", CancellationToken.None);
            return;
        }

        HostShellSessionResult result;
        try
        {
            // No idle timeout: a live `journalctl -f` on a quiet guest emits no
            // bytes for long stretches and must not be reaped.
            using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, appLifetime.ApplicationStopping);
            result = await HostShellSession.RunAsync(
                channel, client, Timeout.InfiniteTimeSpan, logger, sessionCts.Token);
        }
        finally
        {
            channel.Dispose();
        }

        await client.CloseAsync(CloseReasonText(result.EndReason), CancellationToken.None);
        logger.LogInformation(
            "LXC logs closed: host {ConnectionId}, CT {VmId}, reason {Reason}, {BytesOut} B streamed.",
            connection.Id, vmId, result.EndReason, result.BytesToClient);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds the read-only remote command. Runs inside the guest via
    /// <c>pct exec … -- sh -c '…'</c> so the journald→<c>/var/log</c> fallback and
    /// the colour/pager-disabling env can be expressed in one line (the console's
    /// naive argv split can't). <c>exec</c> replaces the login shell so the SSH
    /// channel closes when the tail ends. <c>SYSTEMD_COLORS=0</c> keeps the stream
    /// plain; <c>--no-pager</c> avoids the interactive pager.
    /// </summary>
    private static string BuildLogsCommand(int vmId, bool follow)
    {
        var inner = follow
            ? $"SYSTEMD_COLORS=0 journalctl --no-pager --no-hostname -o short-iso -n {FollowBacklog} -f 2>/dev/null "
              + $"|| tail -n {FollowBacklog} -F /var/log/syslog 2>/dev/null "
              + $"|| tail -n {FollowBacklog} -F /var/log/messages"
            : $"SYSTEMD_COLORS=0 journalctl --no-pager --no-hostname -o short-iso -n {SnapshotLines} 2>/dev/null "
              + $"|| tail -n {SnapshotLines} /var/log/syslog 2>/dev/null "
              + $"|| tail -n {SnapshotLines} /var/log/messages";
        // `inner` contains no single quotes, so a single-quoted wrap needs no
        // escaping. The host login shell parses the quotes and hands the whole
        // string to `sh -c` inside the guest.
        return $"exec pct exec {vmId} -- sh -c '{inner}'";
    }

    /// <summary>
    /// Validates the gates the logs tail requires (global switch + per-host opt-in)
    /// plus host ownership — identical to the console. Returns a non-null error
    /// result when blocked, or <c>null</c> when eligible. The SSH-configured check
    /// happens in the WS path (it needs the decrypted profile).
    /// </summary>
    private ActionResult? CheckEligibility(ProxmoxConnectionEntity? connection, bool globalEnabled)
    {
        if (!globalEnabled)
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                error = "LXC logs are disabled on this server. Enable the guest console on the Settings page (Settings → Guest console).",
            });

        if (connection is null)
            return NotFound();

        if (!connection.AllowConsole)
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                error = "LXC logs are not enabled for this host. Turn on 'Allow guest console' in the host settings.",
            });

        return null;
    }

    private Task<ProxmoxConnectionEntity?> LoadOwnedConnectionAsync(
        Guid connectionId, Guid userId, CancellationToken cancellationToken) =>
        db.ProxmoxConnections.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == connectionId && c.UserId == userId, cancellationToken);

    private static string CloseReasonText(HostShellSessionEndReason reason) => reason switch
    {
        HostShellSessionEndReason.RemoteClosed => "Log stream ended.",
        HostShellSessionEndReason.ClosedByServer => "Closed by server.",
        HostShellSessionEndReason.Error => "Closed: stream error.",
        _ => "Log stream ended.",
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
