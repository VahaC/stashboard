namespace Stashboard.Core.Abstractions;

/// <summary>
/// V5.3 — initial PTY window size, in character cells. The browser measures
/// its terminal with xterm's fit addon and sends this on connect so the remote
/// shell starts at the right size.
/// </summary>
public readonly record struct HostShellWindow(uint Columns, uint Rows);

/// <summary>
/// V5.3 — a live interactive shell channel on a remote host. <see cref="Stream"/>
/// is duplex: reading drains the host's terminal output, writing forwards the
/// browser's keystrokes. <see cref="Completion"/> finishes when the remote shell
/// exits. Disposing tears down the PTY and the underlying SSH session.
/// </summary>
public interface IHostShellChannel : IDisposable
{
    /// <summary>Duplex PTY stream — read = host → browser, write = browser → host.</summary>
    Stream Stream { get; }

    /// <summary>Completes when the remote shell exits or the channel closes.</summary>
    Task Completion { get; }

    /// <summary>
    /// Attempts a live terminal resize. Returns <c>false</c> when the transport
    /// can't change the PTY window after creation (SSH.NET 2024.2.0 does not
    /// expose <c>window-change</c>), in which case the terminal keeps working at
    /// its initial size — only auto-resize degrades.
    /// </summary>
    bool TryResize(uint columns, uint rows);
}

/// <summary>
/// V5.3 — opens an interactive PTY on a remote host over SSH. This is the
/// transport seam behind the browser host terminal: the connector owns the SSH
/// glue, while the WebSocket ↔ byte pump (idle timeout, byte counting, resize
/// dispatch) is orchestrated above it against this interface so it stays
/// testable without a live server.
/// </summary>
public interface IHostShellConnector
{
    /// <summary>
    /// Connects with the supplied credentials and opens an interactive shell at
    /// the requested window size. Throws on connect / auth failure (the caller
    /// surfaces it as a failed handshake); the SSH session is cleaned up before
    /// re-throwing so nothing leaks.
    /// </summary>
    /// <param name="initialCommand">
    /// V6.6 — optional command written to the PTY immediately after it opens
    /// (followed by a newline). The V5.3 host terminal passes <c>null</c> for a
    /// plain login shell; the Proxmox LXC console passes
    /// <c>exec pct exec &lt;vmid&gt; -- &lt;shell&gt;</c> so the login shell is
    /// replaced by a shell inside the container — when that inner shell exits,
    /// the SSH channel closes and the session ends cleanly.
    /// </param>
    IHostShellChannel Connect(
        DockerSshCredentials credentials,
        HostShellWindow window,
        string? initialCommand = null,
        CancellationToken cancellationToken = default);
}
