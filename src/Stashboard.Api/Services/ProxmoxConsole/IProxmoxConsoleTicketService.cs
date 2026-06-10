namespace Stashboard.Api.Services.ProxmoxConsole;

/// <summary>
/// V6.6 — a redeemed Proxmox-console ticket. Carries the identity the ticket was
/// minted for plus the console target (host connection + LXC vmid + command) so
/// the (header-less) WebSocket handshake can recover what to run and where,
/// without trusting any of it from the query string.
/// </summary>
public sealed record ProxmoxConsoleTicket(
    Guid UserId,
    Guid ConnectionId,
    int VmId,
    IReadOnlyList<string> Command);

/// <summary>
/// V6.6 — mints and redeems the short-lived, single-use tickets that
/// authenticate the LXC-console WebSocket. Same rationale as the V5.3 host
/// terminal / V5.7 container exec: a browser <c>WebSocket</c> can't send the JWT
/// header and a query-string JWT leaks into proxy logs, so an authenticated POST
/// mints a ticket bound to <c>(userId, connectionId, vmId, command)</c> and the
/// socket opens with <c>?ticket=…</c>. Tickets are single-use and expire after a
/// short TTL (<c>Stashboard:ProxmoxConsole:TicketTtlSeconds</c>).
/// </summary>
public interface IProxmoxConsoleTicketService
{
    /// <summary>Mints a ticket bound to the given user, host connection, LXC and
    /// command, and returns the opaque token the client passes on the WebSocket
    /// query string.</summary>
    string Issue(Guid userId, Guid connectionId, int vmId, IReadOnlyList<string> command);

    /// <summary>Redeems a token. Returns the bound payload when the token is
    /// valid and unexpired (and removes it so it can't be reused); returns
    /// <c>null</c> for an unknown, expired or already-redeemed token.</summary>
    ProxmoxConsoleTicket? Redeem(string token);
}
