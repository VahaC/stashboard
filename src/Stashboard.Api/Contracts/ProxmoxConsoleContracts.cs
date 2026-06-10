namespace Stashboard.Api.Contracts;

/// <summary>
/// V6.6 — response to the authenticated "mint a Proxmox-console ticket" POST.
/// The client opens the WebSocket at <see cref="WebSocketPath"/> with
/// <c>?ticket={Ticket}&amp;cols=&amp;rows=</c>. The command chosen on the POST
/// is bound to the ticket server-side, so it never travels on the query string.
/// <see cref="ExpiresInSeconds"/> is informational so the UI can warn if the
/// user waits too long. Mirrors <see cref="ContainerExecTicketResponse"/>.
/// </summary>
public sealed record ProxmoxConsoleTicketResponse(
    string Ticket,
    int ExpiresInSeconds,
    string WebSocketPath);

/// <summary>
/// V6.6 — optional body for the ticket POST. <see cref="Command"/> is the shell
/// (or full command line) to run inside the LXC; when null/blank the server
/// falls back to the configured default (<c>/bin/bash</c>). Kept minimal — the
/// command is the only per-session parameter.
/// </summary>
public sealed record ProxmoxConsoleTicketRequest(string? Command);

/// <summary>
/// V6.6 — app-wide LXC-console master switch, managed from the Settings page.
/// This is the global gate; a per-host opt-in (<c>AllowConsole</c>) and SSH
/// credentials are also required before a console can be opened.
/// </summary>
public sealed record ProxmoxConsoleSettingsResponse(bool Enabled);

/// <summary>V6.6 — update payload for the LXC-console master switch.</summary>
public sealed record UpdateProxmoxConsoleSettingsRequest(bool Enabled);
