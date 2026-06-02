namespace Stashboard.Core.Options;

/// <summary>
/// V5.7 — tunables for the browser container-exec terminal. Bound from the
/// <c>Stashboard:ContainerExec</c> config section (or
/// <c>STASHBOARD_Stashboard__ContainerExec__*</c> environment variables). The
/// master on/off switch lives on <see cref="StashboardOptions.AllowContainerExec"/>;
/// this class only carries the safety limits and the default command. Defaults
/// are conservative so an operator who flips the feature on still gets sane caps
/// without extra config. Mirrors <see cref="HostShellOptions"/>.
/// </summary>
public sealed class ContainerExecOptions
{
    public const string SectionName = "Stashboard:ContainerExec";

    /// <summary>Maximum concurrent exec sessions a single user may hold across
    /// all of their connections. Guards against a runaway tab count.</summary>
    public int MaxSessionsPerUser { get; set; } = 3;

    /// <summary>Maximum concurrent exec sessions against one Docker connection
    /// (host), summed across every user that can reach it.</summary>
    public int MaxSessionsPerHost { get; set; } = 5;

    /// <summary>Server-side inactivity timeout, in seconds. When no bytes flow
    /// in either direction for this long, the server closes the session
    /// regardless of client state. <c>0</c> disables the idle timeout.</summary>
    public int IdleTimeoutSeconds { get; set; } = 600;

    /// <summary>How long a minted connect ticket stays valid, in seconds.
    /// Tickets are single-use; this only bounds the window between the
    /// authenticated POST and the WebSocket upgrade.</summary>
    public int TicketTtlSeconds { get; set; } = 30;

    /// <summary>Shell used when the client doesn't specify a command. Most
    /// images ship <c>/bin/sh</c>; the user can override per session (e.g.
    /// <c>/bin/bash</c>) from the Exec panel.</summary>
    public string DefaultCommand { get; set; } = "/bin/sh";
}
