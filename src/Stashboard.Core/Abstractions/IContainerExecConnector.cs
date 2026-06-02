namespace Stashboard.Core.Abstractions;

/// <summary>
/// V5.7 — parameters for opening an interactive exec session inside a container.
/// <see cref="Command"/> is the argv to run (defaults to a shell when the client
/// doesn't specify one); <see cref="Window"/> is the initial PTY size, reusing
/// <see cref="HostShellWindow"/> so the byte-pump and transport layers stay
/// shared with the V5.3 host terminal.
/// </summary>
public sealed record ContainerExecRequest(
    string ContainerName,
    IReadOnlyList<string> Command,
    HostShellWindow Window);

/// <summary>
/// V5.7 — opens an interactive PTY <em>inside</em> a running container via the
/// Docker daemon's <c>exec</c> API (<c>POST /containers/{id}/exec</c> +
/// <c>POST /exec/{id}/start</c>). This is the container-exec analogue of
/// <see cref="IHostShellConnector"/>: the Docker.DotNet glue lives behind this
/// seam while the WebSocket ↔ byte pump (idle timeout, byte counting, resize
/// dispatch) is orchestrated above it against <see cref="IHostShellChannel"/>,
/// so it stays unit-testable without a live daemon.
/// </summary>
/// <remarks>
/// Unlike the host terminal (SSH-only — it logs in to the host), exec runs
/// through the daemon connection itself, so it is available for every host
/// type the <see cref="DockerHostTransport"/> can reach (local socket, TCP+TLS,
/// SSH tunnel). Live resize works for real here (the daemon exposes
/// <c>resize</c> on the exec instance), so <see cref="IHostShellChannel.TryResize"/>
/// is honoured rather than a no-op.
/// </remarks>
public interface IContainerExecConnector
{
    /// <summary>
    /// Creates and starts an exec instance, returning a live duplex channel.
    /// Throws on failure (container not found, daemon unreachable); the Docker
    /// client is cleaned up before re-throwing so nothing leaks. The caller
    /// disposes the returned channel to tear down the exec stream and the
    /// underlying daemon connection.
    /// </summary>
    Task<IHostShellChannel> ConnectAsync(
        DockerHostTransport transport,
        ContainerExecRequest request,
        CancellationToken cancellationToken = default);
}
