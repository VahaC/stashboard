namespace Stashboard.Api.Services.ContainerExec;

/// <summary>
/// V5.7 — a redeemed container-exec ticket. Carries the identity the ticket was
/// minted for plus the exec target (container + command) so the (header-less)
/// WebSocket handshake can recover what to run and where, without trusting any
/// of it from the query string.
/// </summary>
public sealed record ContainerExecTicket(
    Guid UserId,
    Guid ConnectionId,
    string ContainerName,
    IReadOnlyList<string> Command);

/// <summary>
/// V5.7 — mints and redeems the short-lived, single-use tickets that
/// authenticate the container-exec WebSocket. Same rationale as the V5.3 host
/// terminal: a browser <c>WebSocket</c> can't send the JWT header and a
/// query-string JWT leaks into proxy logs, so an authenticated POST mints a
/// ticket bound to <c>(userId, connectionId, container, command)</c> and the
/// socket opens with <c>?ticket=…</c>. Tickets are single-use and expire after
/// a short TTL (<c>Stashboard:ContainerExec:TicketTtlSeconds</c>).
/// </summary>
public interface IContainerExecTicketService
{
    /// <summary>Mints a ticket bound to the given user, connection, container
    /// and command, and returns the opaque token the client passes on the
    /// WebSocket query string.</summary>
    string Issue(Guid userId, Guid connectionId, string containerName, IReadOnlyList<string> command);

    /// <summary>Redeems a token. Returns the bound payload when the token is
    /// valid and unexpired (and removes it so it can't be reused); returns
    /// <c>null</c> for an unknown, expired or already-redeemed token.</summary>
    ContainerExecTicket? Redeem(string token);
}
