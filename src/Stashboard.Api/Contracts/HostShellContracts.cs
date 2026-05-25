namespace Stashboard.Api.Contracts;

/// <summary>
/// V5.3 — response to the authenticated "mint a host-terminal ticket" POST.
/// The client opens the WebSocket at <see cref="WebSocketPath"/> with
/// <c>?ticket={Ticket}&amp;cols=&amp;rows=</c>. <see cref="ExpiresInSeconds"/>
/// is informational so the UI can warn if the user waits too long.
/// </summary>
public sealed record HostShellTicketResponse(
    string Ticket,
    int ExpiresInSeconds,
    string WebSocketPath);

/// <summary>
/// V5.3 — app-wide host-terminal master switch, managed from the Settings page.
/// This is the global gate; a per-connection opt-in and an SSH connection are
/// also required before a shell can be opened.
/// </summary>
public sealed record HostShellSettingsResponse(bool Enabled);

/// <summary>V5.3 — update payload for the host-terminal master switch.</summary>
public sealed record UpdateHostShellSettingsRequest(bool Enabled);
