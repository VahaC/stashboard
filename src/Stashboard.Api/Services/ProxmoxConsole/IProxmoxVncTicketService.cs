using Stashboard.Core.Abstractions;

namespace Stashboard.Api.Services.ProxmoxConsole;

/// <summary>
/// V8.6 — a redeemed VM-console ticket. Carries the identity the ticket was minted
/// for plus everything the (header-less) WebSocket handshake needs to open the
/// Proxmox VNC relay: the host connection + VM vmid, and the one-time
/// <see cref="VncProxy"/> session (ticket + display port) already minted at
/// POST-ticket time. None of this is trusted from the query string — the socket
/// recovers it from the single-use ticket alone.
/// </summary>
public sealed record ProxmoxVncTicket(
    Guid UserId,
    Guid ConnectionId,
    int VmId,
    ProxmoxVncProxyTicket VncProxy);

/// <summary>
/// V8.6 — mints and redeems the short-lived, single-use tickets that authenticate
/// the VM-console WebSocket. Same rationale as the V6.6 LXC console: a browser
/// <c>WebSocket</c> can't send the JWT header and a query-string JWT leaks into
/// proxy logs, so an authenticated POST mints a ticket bound to
/// <c>(userId, connectionId, vmId, vncProxy)</c> and the socket opens with
/// <c>?ticket=…</c>. The VNC session (its RFB password + display port) is bound
/// server-side so it never travels on the query string either. Tickets are
/// single-use and expire after a short TTL
/// (<c>Stashboard:ProxmoxConsole:TicketTtlSeconds</c>, shared with the LXC
/// console). Mirrors <see cref="IProxmoxConsoleTicketService"/>.
/// </summary>
public interface IProxmoxVncTicketService
{
    /// <summary>Mints a ticket bound to the given user, host connection, VM and
    /// already-minted VNC session, and returns the opaque token the client passes
    /// on the WebSocket query string.</summary>
    string Issue(Guid userId, Guid connectionId, int vmId, ProxmoxVncProxyTicket vncProxy);

    /// <summary>Redeems a token. Returns the bound payload when the token is valid
    /// and unexpired (removing it so it can't be reused); returns <c>null</c> for an
    /// unknown, expired or already-redeemed token.</summary>
    ProxmoxVncTicket? Redeem(string token);
}
