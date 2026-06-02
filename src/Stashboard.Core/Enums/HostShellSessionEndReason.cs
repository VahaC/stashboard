namespace Stashboard.Core.Enums;

/// <summary>
/// V5.3 — how a browser host-terminal session ended. Recorded on
/// <see cref="Stashboard.Core.Entities.HostShellSessionEntity"/> so the audit
/// log distinguishes a clean client disconnect from a server-side idle close
/// or a transport error.
/// </summary>
public enum HostShellSessionEndReason
{
    /// <summary>Session is still open (row written at start, not yet finalised).</summary>
    Active = 0,

    /// <summary>The browser closed the WebSocket (user closed the tab / clicked Disconnect).</summary>
    ClosedByClient = 1,

    /// <summary>The remote shell exited (the user typed <c>exit</c> or the PTY ended).</summary>
    RemoteClosed = 2,

    /// <summary>The server-side inactivity timeout fired and closed an idle session.</summary>
    IdleTimeout = 3,

    /// <summary>The server shut the session because a per-user / per-host cap was hit
    /// or the application is stopping.</summary>
    ClosedByServer = 4,

    /// <summary>The session ended on a transport / SSH error. <c>Error</c> carries detail.</summary>
    Error = 5,

    /// <summary>The application process stopped (crash / restart / redeploy) while the
    /// session was still open, so it never finalised cleanly. Applied by the startup
    /// reconciliation sweep to any row left <see cref="Active"/> from a previous run.</summary>
    Interrupted = 6,
}
