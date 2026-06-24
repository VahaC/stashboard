namespace Stashboard.Api.Contracts;

/// <summary>
/// V8.6 — response to the authenticated "mint a VM-console ticket" POST. The
/// client opens the WebSocket at <see cref="WebSocketPath"/> with
/// <c>?ticket={Ticket}</c>, and hands <see cref="Password"/> to noVNC as the RFB
/// credential. <see cref="Password"/> is the one-time Proxmox <em>vncproxy</em>
/// ticket — an ephemeral, per-session VNC credential, <strong>not</strong> the
/// Proxmox API token (which never leaves the server) — that the guest's VNC server
/// challenges for. <see cref="ExpiresInSeconds"/> is informational so the UI can
/// warn if the user waits too long. Mirrors
/// <see cref="ProxmoxConsoleTicketResponse"/>; there is no command (a VNC console
/// has no shell), and the display port stays server-side bound to the ticket.
/// </summary>
public sealed record ProxmoxVncConsoleTicketResponse(
    string Ticket,
    string Password,
    int ExpiresInSeconds,
    string WebSocketPath);
