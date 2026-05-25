using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Stashboard.Core.Abstractions;

namespace Stashboard.Infrastructure.Docker.Ssh;

/// <summary>
/// V2.5 — opens an SSH connection to <see cref="DockerSshCredentials.Host"/>
/// and exposes a local TCP endpoint that the <c>Docker.DotNet</c> HTTP client
/// can dial. Each accepted local connection spawns a fresh remote exec channel
/// (<c>docker system dial-stdio</c> by default, with a <c>socat</c> fallback)
/// that bridges the local TCP bytes to the remote UNIX socket.
/// </summary>
/// <remarks>
/// Mirrors the approach Docker CLI uses internally for <c>docker -H ssh://...</c>:
/// instead of relying on UNIX-socket forwarding (which OpenSSH supports via
/// <c>direct-streamlocal@openssh.com</c> but SSH.NET does not expose), we
/// shell out to <c>docker system dial-stdio</c> on the remote — every modern
/// Docker host has it. The remote command is chosen once at construction by
/// probing for the docker CLI; hosts without it (e.g. a Podman host exposing a
/// docker-compatible socket) fall back to <c>socat - UNIX-CONNECT:&lt;path&gt;</c>.
///
/// Lifetime: created per <c>IDockerClient</c> and disposed alongside it.
/// Disposal stops the accept loop, closes any in-flight bridge sessions and
/// disconnects the transport. Calling thread is not required to dispose
/// synchronously — the accept loop runs on a background <c>Task.Run</c>.
/// </remarks>
public sealed class SshDockerTunnel : IDisposable
{
    private readonly IRemoteCommandTransport _transport;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly string _bridgeCommand;
    private readonly ILogger? _logger;
    private readonly Task _acceptLoop;
    private bool _disposed;

    /// <summary>Default remote socket path — the rootful Docker daemon location.</summary>
    public const string DefaultRemoteSocketPath = "/var/run/docker.sock";

    /// <summary>
    /// Local <c>tcp://127.0.0.1:{port}</c> URI the Docker client should dial.
    /// Allocated on construction so the caller can pass it straight into
    /// <c>new DockerClientConfiguration(new Uri(...))</c>.
    /// </summary>
    public Uri LocalUri { get; }

    private SshDockerTunnel(IRemoteCommandTransport transport, string bridgeCommand, ILogger? logger)
    {
        _transport = transport;
        _bridgeCommand = bridgeCommand;
        _logger = logger;
        _listener = new TcpListener(IPAddress.Loopback, port: 0);
        _listener.Start();
        var endpoint = (IPEndPoint)_listener.LocalEndpoint;
        LocalUri = new Uri($"tcp://127.0.0.1:{endpoint.Port}");
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    /// <summary>
    /// Connects to the remote host with the supplied credentials and starts
    /// the local TCP listener. Throws on connect failure (caller surfaces it
    /// as a <see cref="DockerHostStatus.HostUnreachable"/> outcome).
    /// </summary>
    public static SshDockerTunnel Connect(DockerSshCredentials credentials, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(credentials);

        var transport = SshRemoteCommandTransport.Connect(credentials, logger);
        try
        {
            return Create(transport, credentials.RemoteSocketPath, logger);
        }
        catch
        {
            transport.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Test seam — builds a tunnel over an arbitrary <see cref="IRemoteCommandTransport"/>
    /// (no real SSH connection). Production code uses <see cref="Connect"/>.
    /// </summary>
    internal static SshDockerTunnel Create(
        IRemoteCommandTransport transport, string remoteSocketPath, ILogger? logger = null)
    {
        var bridgeCommand = SelectBridgeCommand(transport, remoteSocketPath, logger);
        return new SshDockerTunnel(transport, bridgeCommand, logger);
    }

    /// <summary>
    /// Picks the remote bridge command once per tunnel. <c>docker system
    /// dial-stdio</c> is present on every modern Docker host and is what the
    /// Docker CLI itself uses for <c>ssh://</c>, so we prefer it. Only when the
    /// docker CLI is genuinely absent do we fall back to <c>socat</c> talking to
    /// the socket file directly.
    /// </summary>
    internal static string SelectBridgeCommand(
        IRemoteCommandTransport transport, string remoteSocketPath, ILogger? logger)
    {
        if (transport.RunSucceeds("command -v docker"))
            return "docker system dial-stdio";

        logger?.LogDebug(
            "docker CLI not found on remote host; using socat bridge to {Socket}.", remoteSocketPath);
        return $"socat - UNIX-CONNECT:{remoteSocketPath}";
    }

    private async Task AcceptLoopAsync()
    {
        var token = _shutdown.Token;
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(token);
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException ex) when (token.IsCancellationRequested)
            {
                _logger?.LogDebug(ex, "SSH tunnel accept loop terminating after shutdown.");
                return;
            }

            _ = Task.Run(() => BridgeSafelyAsync(client, token), token);
        }
    }

    private async Task BridgeSafelyAsync(TcpClient client, CancellationToken token)
    {
        try
        {
            await BridgeAsync(client, token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogDebug(ex, "SSH tunnel bridge session ended with an error.");
        }
        finally
        {
            try { client.Dispose(); } catch { /* best-effort */ }
        }
    }

    private async Task BridgeAsync(TcpClient client, CancellationToken token)
    {
        client.NoDelay = true;
        using var tcpStream = client.GetStream();
        using var channel = _transport.Open(_bridgeCommand, token);

        var clientToRemote = CopyAsync(tcpStream, channel.Input, token);
        var remoteToClient = CopyAsync(channel.Output, tcpStream, token);

        var finished = await Task.WhenAny(clientToRemote, remoteToClient);
        try { channel.Input.Close(); } catch { /* peer already closed */ }
        try { client.Client.Shutdown(SocketShutdown.Both); } catch { /* idempotent */ }

        // Drain whichever side hasn't finished so we don't return early and
        // leak the remote channel.
        try { await Task.WhenAll(clientToRemote, remoteToClient); }
        catch { /* already logged via BridgeSafelyAsync */ }
        try { await channel.Completion; } catch { /* command exit ignored */ }
        await finished;
    }

    private static async Task CopyAsync(Stream source, Stream destination, CancellationToken token)
    {
        var buffer = new byte[16 * 1024];
        while (!token.IsCancellationRequested)
        {
            int read;
            try { read = await source.ReadAsync(buffer, token); }
            catch (IOException) { return; }
            catch (ObjectDisposedException) { return; }
            if (read <= 0) return;
            try
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), token);
                await destination.FlushAsync(token);
            }
            catch (IOException) { return; }
            catch (ObjectDisposedException) { return; }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _shutdown.Cancel(); } catch { /* idempotent */ }
        try { _listener.Stop(); } catch { /* idempotent */ }
        try { _acceptLoop.Wait(TimeSpan.FromSeconds(2)); } catch { /* best-effort */ }
        try { _transport.Dispose(); } catch { /* idempotent */ }
        _shutdown.Dispose();
    }
}
