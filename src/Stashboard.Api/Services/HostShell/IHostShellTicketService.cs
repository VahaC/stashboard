namespace Stashboard.Api.Services.HostShell;

/// <summary>
/// V5.3 — a redeemed host-terminal ticket. Carries the identity the ticket was
/// minted for so the (header-less) WebSocket handshake can recover who is
/// connecting and which connection they're allowed to reach.
/// </summary>
public sealed record HostShellTicket(Guid UserId, Guid ConnectionId);

/// <summary>
/// V5.3 — mints and redeems the short-lived, single-use tickets that
/// authenticate the host-terminal WebSocket. A browser <c>WebSocket</c> cannot
/// send the JWT <c>Authorization</c> header (the same limitation that keeps log
/// / stat streaming on NDJSON-over-fetch) and a query-string JWT leaks into
/// proxy logs, so an authenticated POST mints a ticket bound to
/// <c>(userId, connectionId)</c> and the socket opens with <c>?ticket=…</c>.
///
/// Tickets are single-use (redeeming removes them) and expire after a short TTL
/// (<c>Stashboard:HostShell:TicketTtlSeconds</c>), so the attack window is the
/// few seconds between the POST and the upgrade.
/// </summary>
public interface IHostShellTicketService
{
    /// <summary>Mints a ticket bound to the given user + connection and returns
    /// the opaque token the client passes on the WebSocket query string.</summary>
    string Issue(Guid userId, Guid connectionId);

    /// <summary>Redeems a token. Returns the bound identity when the token is
    /// valid and unexpired (and removes it so it can't be reused); returns
    /// <c>null</c> for an unknown, expired or already-redeemed token.</summary>
    HostShellTicket? Redeem(string token);
}
