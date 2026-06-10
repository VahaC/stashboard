namespace Stashboard.Core.Options;

/// <summary>
/// V6.6 — tunables for the browser Proxmox LXC console. Bound from the
/// <c>Stashboard:ProxmoxConsole</c> config section (or
/// <c>STASHBOARD_Stashboard__ProxmoxConsole__*</c> environment variables). The
/// master on/off switch lives on <see cref="StashboardOptions.AllowProxmoxConsole"/>;
/// this class only carries the safety limits and the default shell. Defaults are
/// conservative so an operator who flips the feature on still gets sane caps
/// without extra config. Mirrors <see cref="ContainerExecOptions"/>.
/// </summary>
public sealed class ProxmoxConsoleOptions
{
    public const string SectionName = "Stashboard:ProxmoxConsole";

    /// <summary>Maximum concurrent console sessions a single user may hold across
    /// all of their Proxmox hosts. Guards against a runaway tab count.</summary>
    public int MaxSessionsPerUser { get; set; } = 3;

    /// <summary>Maximum concurrent console sessions against one Proxmox host,
    /// summed across every user that can reach it.</summary>
    public int MaxSessionsPerHost { get; set; } = 5;

    /// <summary>Server-side inactivity timeout, in seconds. When no bytes flow
    /// in either direction for this long, the server closes the session
    /// regardless of client state. <c>0</c> disables the idle timeout.</summary>
    public int IdleTimeoutSeconds { get; set; } = 600;

    /// <summary>How long a minted connect ticket stays valid, in seconds.
    /// Tickets are single-use; this only bounds the window between the
    /// authenticated POST and the WebSocket upgrade.</summary>
    public int TicketTtlSeconds { get; set; } = 30;

    /// <summary>Shell launched inside the LXC when the client doesn't specify a
    /// command. Debian-based templates ship <c>/bin/bash</c>; the user can
    /// override per session (e.g. <c>/bin/sh</c> for an Alpine guest) from the
    /// Console panel.</summary>
    public string DefaultCommand { get; set; } = "/bin/bash";
}
