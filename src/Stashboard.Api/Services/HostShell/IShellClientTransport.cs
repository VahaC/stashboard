namespace Stashboard.Api.Services.HostShell;

/// <summary>
/// V5.3 — one inbound message from the browser side of a host-terminal session.
/// The WebSocket carries two kinds of traffic: binary frames are raw terminal
/// input (keystrokes / paste) and text frames are JSON control messages
/// (currently just <c>resize</c>). <see cref="Closed"/> is surfaced when the
/// peer closes the socket.
/// </summary>
public abstract record ShellClientMessage
{
    private ShellClientMessage() { }

    /// <summary>Raw stdin bytes to forward to the host PTY.</summary>
    public sealed record Input(ReadOnlyMemory<byte> Data) : ShellClientMessage;

    /// <summary>A terminal resize request from the browser.</summary>
    public sealed record Resize(uint Columns, uint Rows) : ShellClientMessage;

    /// <summary>The browser closed the connection.</summary>
    public sealed record Closed : ShellClientMessage;
}

/// <summary>
/// V5.3 — the browser side of a host-terminal session, abstracted away from the
/// concrete <c>WebSocket</c> so the <see cref="HostShellSession"/> byte pump
/// (idle timeout, byte counting, resize dispatch) can be unit-tested with an
/// in-memory fake — the same split that keeps <c>SshDockerTunnel</c> testable.
/// </summary>
public interface IShellClientTransport
{
    /// <summary>Receives the next message from the browser. Returns
    /// <see cref="ShellClientMessage.Closed"/> when the peer closes.</summary>
    ValueTask<ShellClientMessage> ReceiveAsync(CancellationToken cancellationToken);

    /// <summary>Sends terminal output bytes to the browser.</summary>
    ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken);

    /// <summary>Closes the connection with a short human-readable reason.</summary>
    ValueTask CloseAsync(string reason, CancellationToken cancellationToken);
}
