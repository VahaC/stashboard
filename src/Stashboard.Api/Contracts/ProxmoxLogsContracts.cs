namespace Stashboard.Api.Contracts;

/// <summary>
/// V6.12 — response to the authenticated "mint a Proxmox-logs ticket" POST. The
/// client opens the WebSocket at <see cref="WebSocketPath"/> with
/// <c>?ticket={Ticket}&amp;follow=</c>. Unlike the V6.6 console ticket there is no
/// per-session command: the live journal tail is read-only and the remote command
/// (<c>journalctl -f</c> with a <c>/var/log</c> fallback) is built server-side, so
/// nothing about what runs is client-controlled. <see cref="ExpiresInSeconds"/> is
/// informational so the UI can warn if the user waits too long. Mirrors
/// <see cref="ProxmoxConsoleTicketResponse"/>.
/// </summary>
public sealed record ProxmoxLogsTicketResponse(
    string Ticket,
    int ExpiresInSeconds,
    string WebSocketPath);
