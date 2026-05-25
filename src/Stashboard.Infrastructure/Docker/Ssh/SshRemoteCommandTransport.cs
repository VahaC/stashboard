using Microsoft.Extensions.Logging;
using Renci.SshNet;
using Renci.SshNet.Common;
using Stashboard.Core.Abstractions;

namespace Stashboard.Infrastructure.Docker.Ssh;

/// <summary>
/// V2.5 — production <see cref="IRemoteCommandTransport"/> backed by SSH.NET.
/// Owns a single connected <see cref="SshClient"/> and spawns a fresh exec
/// channel per bridge session. Connect/auth happens in <see cref="Connect"/>;
/// failures there surface as the usual SSH.NET exceptions so callers can map
/// them to a host-unreachable outcome.
/// </summary>
internal sealed class SshRemoteCommandTransport : IRemoteCommandTransport
{
    private readonly SshClient _ssh;
    private readonly ILogger? _logger;

    private SshRemoteCommandTransport(SshClient ssh, ILogger? logger)
    {
        _ssh = ssh;
        _logger = logger;
    }

    /// <summary>
    /// Connects to the remote host with the supplied credentials. Throws on
    /// connect/auth failure (the SSH client is disposed before re-throwing so
    /// no socket leaks).
    /// </summary>
    public static SshRemoteCommandTransport Connect(DockerSshCredentials credentials, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(credentials);

        var keyFile = SshPrivateKeyLoader.Load(credentials);
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

        return new SshRemoteCommandTransport(ssh, logger);
    }

    public bool RunSucceeds(string commandText)
    {
        try
        {
            using var cmd = _ssh.CreateCommand(commandText);
            cmd.Execute();
            return cmd.ExitStatus == 0;
        }
        catch (SshException ex)
        {
            _logger?.LogDebug(ex, "Remote probe command '{Command}' failed.", commandText);
            return false;
        }
    }

    public IRemoteCommandChannel Open(string commandText, CancellationToken cancellationToken)
    {
        var cmd = _ssh.CreateCommand(commandText);
        try
        {
            return new SshRemoteCommandChannel(cmd, cancellationToken);
        }
        catch
        {
            cmd.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        try { _ssh.Dispose(); } catch { /* idempotent */ }
    }

    /// <summary>
    /// Wraps an <see cref="SshCommand"/> running in streaming mode. The call
    /// order below is load-bearing: <see cref="SshCommand.ExecuteAsync"/> opens
    /// the channel, and <see cref="SshCommand.CreateInputStream"/> throws
    /// (<see cref="InvalidOperationException"/>) unless the channel is already
    /// open — see the documented usage example on <c>CreateInputStream</c>.
    /// Reversing these two lines silently breaks every bridge session.
    /// </summary>
    private sealed class SshRemoteCommandChannel : IRemoteCommandChannel
    {
        private readonly SshCommand _cmd;

        public Stream Input { get; }
        public Stream Output { get; }
        public Task Completion { get; }

        public SshRemoteCommandChannel(SshCommand cmd, CancellationToken cancellationToken)
        {
            _cmd = cmd;
            Completion = cmd.ExecuteAsync(cancellationToken);
            Input = cmd.CreateInputStream();
            Output = cmd.OutputStream;
        }

        public void Dispose()
        {
            try { Input.Dispose(); } catch { /* peer already closed */ }
            try { _cmd.Dispose(); } catch { /* idempotent */ }
        }
    }
}
