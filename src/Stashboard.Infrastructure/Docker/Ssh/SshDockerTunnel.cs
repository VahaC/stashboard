using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Renci.SshNet;
using Renci.SshNet.Common;
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
/// shell out to <c>docker system dial-stdio</c> on the remote — every Docker
/// host has it. If the remote command exits with a non-zero status on the
/// very first attempt we retry with <c>socat - UNIX-CONNECT:&lt;path&gt;</c>
/// so legacy hosts without the dial-stdio plumbing still work.
///
/// Lifetime: created per <c>IDockerClient</c> and disposed alongside it.
/// Disposal stops the accept loop, closes any in-flight bridge sessions and
/// disconnects the SSH client. Calling thread is not required to dispose
/// synchronously — the accept loop runs on a background <c>Task.Run</c>.
/// </remarks>
public sealed class SshDockerTunnel : IDisposable
{
    private readonly SshClient _ssh;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly string _remoteSocketPath;
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

    private SshDockerTunnel(SshClient ssh, string remoteSocketPath, ILogger? logger)
    {
        _ssh = ssh;
        _remoteSocketPath = remoteSocketPath;
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

        var keyFile = LoadKeyFile(credentials);
        var ssh = new SshClient(credentials.Host, credentials.Port, credentials.Username, keyFile)
        {
            // Keep the session alive only for the length of one inspect call.
            // Longer keepalives mostly waste packets — the tunnel is short-lived
            // by design (each Docker check rebuilds the IDockerClient).
            KeepAliveInterval = Timeout.InfiniteTimeSpan,
        };

        try
        {
            ssh.Connect();
        }
        catch
        {
            ssh.Dispose();
            throw;
        }

        try
        {
            return new SshDockerTunnel(ssh, credentials.RemoteSocketPath, logger);
        }
        catch
        {
            ssh.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Loads the PEM private key with an optional passphrase. SSH.NET's
    /// <see cref="PrivateKeyFile"/> auto-detects RSA / DSA / ECDSA / Ed25519
    /// formats — the user just pastes whatever <c>ssh-keygen</c> produced.
    /// </summary>
    private static PrivateKeyFile LoadKeyFile(DockerSshCredentials credentials)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(credentials.PrivateKeyPem);
        using var stream = new MemoryStream(bytes);
        return string.IsNullOrEmpty(credentials.PrivateKeyPassphrase)
            ? new PrivateKeyFile(stream)
            : new PrivateKeyFile(stream, credentials.PrivateKeyPassphrase);
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

        // Prefer `docker system dial-stdio` (no extra packages required on
        // any modern Docker host). Falls through to `socat` so legacy hosts
        // without the dial-stdio plumbing still work.
        var primary = "docker system dial-stdio";
        using var cmd = TryCreateCommand(primary, out var primaryOk)
            ?? TryCreateCommand($"socat - UNIX-CONNECT:{_remoteSocketPath}", out _);
        if (cmd is null)
        {
            _logger?.LogWarning("Failed to open SSH exec channel for Docker tunnel.");
            return;
        }

        if (!primaryOk)
            _logger?.LogDebug("Falling back to socat-based SSH bridge for Docker tunnel.");

        // ExecuteAsync must run first: it opens the channel. CreateInputStream
        // throws InvalidOperationException if the channel isn't open yet, so the
        // order here is load-bearing — do not reorder.
        var execTask = cmd.ExecuteAsync(token);
        var inputStream = cmd.CreateInputStream();

        var clientToRemote = CopyAsync(tcpStream, inputStream, token);
        var remoteToClient = CopyAsync(cmd.OutputStream, tcpStream, token);

        var finished = await Task.WhenAny(clientToRemote, remoteToClient);
        try { inputStream.Close(); } catch { /* peer already closed */ }
        try { client.Client.Shutdown(SocketShutdown.Both); } catch { /* idempotent */ }

        // Drain whichever side hasn't finished so we don't return early and
        // leak the remote channel.
        try { await Task.WhenAll(clientToRemote, remoteToClient); }
        catch { /* already logged via BridgeSafelyAsync */ }
        try { await execTask; } catch { /* command exit ignored */ }
        await finished;
    }

    private SshCommand? TryCreateCommand(string commandText, out bool ok)
    {
        try
        {
            var cmd = _ssh.CreateCommand(commandText);
            ok = true;
            return cmd;
        }
        catch (SshException ex)
        {
            _logger?.LogDebug(ex, "Could not create SSH command '{Command}'.", commandText);
            ok = false;
            return null;
        }
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
        try { _ssh.Dispose(); } catch { /* idempotent */ }
        _shutdown.Dispose();
    }
}
