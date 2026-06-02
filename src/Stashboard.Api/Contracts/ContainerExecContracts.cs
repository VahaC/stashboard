namespace Stashboard.Api.Contracts;

/// <summary>
/// V5.7 — response to the authenticated "mint a container-exec ticket" POST.
/// The client opens the WebSocket at <see cref="WebSocketPath"/> with
/// <c>?ticket={Ticket}&amp;cols=&amp;rows=</c>. The command chosen on the POST
/// is bound to the ticket server-side, so it never travels on the query string.
/// <see cref="ExpiresInSeconds"/> is informational so the UI can warn if the
/// user waits too long.
/// </summary>
public sealed record ContainerExecTicketResponse(
    string Ticket,
    int ExpiresInSeconds,
    string WebSocketPath);

/// <summary>
/// V5.7 — optional body for the ticket POST. <see cref="Command"/> is the shell
/// (or full command line) to exec; when null/blank the server falls back to the
/// configured default (<c>/bin/sh</c>). Kept minimal — the command is the only
/// per-session parameter.
/// </summary>
public sealed record ContainerExecTicketRequest(string? Command);

/// <summary>
/// V5.7 — app-wide container-exec master switch, managed from the Settings page.
/// This is the global gate; a per-connection opt-in (<c>AllowExec</c>) is also
/// required before a shell can be opened.
/// </summary>
public sealed record ContainerExecSettingsResponse(bool Enabled);

/// <summary>V5.7 — update payload for the container-exec master switch.</summary>
public sealed record UpdateContainerExecSettingsRequest(bool Enabled);
