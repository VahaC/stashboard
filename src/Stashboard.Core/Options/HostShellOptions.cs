namespace Stashboard.Core.Options;

/// <summary>
/// V5.3 — tunables for the browser host terminal. Bound from the
/// <c>Stashboard:HostShell</c> config section (or
/// <c>STASHBOARD_Stashboard__HostShell__*</c> environment variables). The
/// master on/off switch lives on <see cref="StashboardOptions.AllowHostShell"/>;
/// this class only carries the safety limits. Defaults are conservative so an
/// operator who flips the feature on still gets sane caps without extra config.
/// </summary>
public sealed class HostShellOptions
{
    public const string SectionName = "Stashboard:HostShell";

    /// <summary>Maximum concurrent host-terminal sessions a single user may hold
    /// across all of their connections. Guards against a runaway tab count.</summary>
    public int MaxSessionsPerUser { get; set; } = 3;

    /// <summary>Maximum concurrent host-terminal sessions against one Docker
    /// connection (host), summed across every user that can reach it.</summary>
    public int MaxSessionsPerHost { get; set; } = 5;

    /// <summary>Server-side inactivity timeout, in seconds. When no bytes flow
    /// in either direction for this long, the server closes the session
    /// regardless of client state. <c>0</c> disables the idle timeout.</summary>
    public int IdleTimeoutSeconds { get; set; } = 600;

    /// <summary>How long a minted connect ticket stays valid, in seconds. Tickets
    /// are single-use; this only bounds the window between the authenticated POST
    /// and the WebSocket upgrade.</summary>
    public int TicketTtlSeconds { get; set; } = 30;
}
