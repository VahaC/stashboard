using Renci.SshNet;
using Stashboard.Core.Abstractions;

namespace Stashboard.Infrastructure.Docker.Ssh;

/// <summary>
/// V7.1 — shared one-shot SSH command execution for the Compose reader and
/// writer (extracted from the V7.0 reader). Opens a short-lived
/// <see cref="SshClient"/> per call — same pattern as the Proxmox SSH
/// inspector — and returns exit status + captured stdout/stderr.
/// </summary>
internal static class SshCommandExecutor
{
    private const int ConnectTimeoutSeconds = 10;
    private const int CommandTimeoutSeconds = 20;

    /// <summary>POSIX single-quote escaping so a path with spaces (or quotes)
    /// survives the remote shell.</summary>
    internal static string QuoteForShell(string value) =>
        "'" + value.Replace("'", "'\\''") + "'";

    internal static Task<SshCommandOutcome> RunAsync(
        DockerSshCredentials credentials, string commandText, CancellationToken cancellationToken) =>
        RunAsync(credentials, commandText, cancellationToken, commandTimeout: null);

    internal static Task<SshCommandOutcome> RunAsync(
        DockerSshCredentials credentials, string commandText, CancellationToken cancellationToken,
        TimeSpan? commandTimeout)
    {
        // SSH.NET's command API is synchronous at heart; run it off the request
        // thread the same way the rest of the SSH surface does.
        return Task.Run(() =>
        {
            var keyFile = SshPrivateKeyLoader.Load(credentials);
            using var ssh = new SshClient(credentials.Host, credentials.Port, credentials.Username, keyFile)
            {
                KeepAliveInterval = Timeout.InfiniteTimeSpan,
            };
            ssh.ConnectionInfo.Timeout = TimeSpan.FromSeconds(ConnectTimeoutSeconds);
            ssh.Connect();
            try
            {
                using var cmd = ssh.CreateCommand(commandText);
                cmd.CommandTimeout = commandTimeout ?? TimeSpan.FromSeconds(CommandTimeoutSeconds);
                using var registration = cancellationToken.Register(() => { try { cmd.CancelAsync(); } catch { /* best effort */ } });
                var stdout = cmd.Execute();
                return new SshCommandOutcome(cmd.ExitStatus ?? -1, stdout, cmd.Error);
            }
            finally
            {
                try { ssh.Disconnect(); } catch { /* idempotent */ }
            }
        }, cancellationToken);
    }
}
