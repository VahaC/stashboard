using System.Text;
using Microsoft.Extensions.Logging;
using Renci.SshNet;
using Stashboard.Core.Abstractions;
using Stashboard.Infrastructure.Docker.Ssh;

namespace Stashboard.Infrastructure.Proxmox;

/// <summary>
/// V6.7.1 — production <see cref="IProxmoxUpdateApplier"/>. SSHes to the Proxmox
/// host and runs <c>apt-get update &amp;&amp; apt-get -y dist-upgrade</c> either
/// directly on the node (<c>vmId == 0</c>) or inside an LXC via
/// <c>pct exec &lt;vmid&gt; -- …</c>, streaming the combined stdout/stderr to the
/// caller line-by-line as it arrives. A fresh, short-lived SSH session is opened
/// per run; the command timeout is generous because a large upgrade can take
/// several minutes. Mirrors <see cref="ProxmoxSshGuestInspector"/>.
/// </summary>
internal sealed class ProxmoxSshUpdateApplier(ILogger<ProxmoxSshUpdateApplier> logger) : IProxmoxUpdateApplier
{
    private const int ConnectTimeoutSeconds = 15;
    // apt-get update + dist-upgrade on a large guest can run for minutes; give
    // it a wide ceiling so a real upgrade isn't cut off mid-flight.
    private const int CommandTimeoutSeconds = 20 * 60;

    /// <summary>Exit code the remote guard uses to flag a non-apt target.</summary>
    private const int NonDebianExitCode = 3;

    public async Task<ProxmoxUpdateApplyResult> ApplyAsync(
        ProxmoxConnectionProfile profile,
        int vmId,
        Func<string, CancellationToken, Task> onLine,
        CancellationToken cancellationToken = default)
    {
        if (profile.Ssh is null)
            return new ProxmoxUpdateApplyResult(null, 0, NonDebian: false,
                "SSH is not configured for this Proxmox host.");

        var command = BuildCommand(vmId);

        try
        {
            return await Task.Run(() => RunStreaming(profile.Ssh, command, onLine, cancellationToken), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Proxmox apply-updates failed for vmid {VmId}", vmId);
            return new ProxmoxUpdateApplyResult(null, 0, NonDebian: false, ex.Message);
        }
    }

    /// <summary>
    /// The remote one-liner. A guard short-circuits non-Debian targets with a
    /// distinct exit code; the upgrade is non-interactive (keeps existing config
    /// files on conflict) and stderr is merged into stdout so the caller sees a
    /// single ordered stream. No single quotes inside the script — it is wrapped
    /// in single quotes (twice, for the LXC case), so embedding one would break
    /// the quoting.
    /// </summary>
    public string BuildCommand(int vmId)
    {
        const string script =
            "export DEBIAN_FRONTEND=noninteractive; " +
            "if ! command -v apt-get >/dev/null 2>&1; then echo NOAPT; exit 3; fi; " +
            "apt-get update && apt-get -y -o Dpkg::Options::=--force-confold -o Dpkg::Use-Pty=0 dist-upgrade";

        return vmId == 0
            ? $"sh -c '{script}' 2>&1"
            : $"pct exec {vmId} -- sh -c '{script}' 2>&1";
    }

    private ProxmoxUpdateApplyResult RunStreaming(
        DockerSshCredentials credentials, string commandText,
        Func<string, CancellationToken, Task> onLine, CancellationToken cancellationToken)
    {
        var keyFile = SshPrivateKeyLoader.Load(credentials);
        using var ssh = new SshClient(credentials.Host, credentials.Port, credentials.Username, keyFile)
        {
            KeepAliveInterval = Timeout.InfiniteTimeSpan,
        };
        ssh.ConnectionInfo.Timeout = TimeSpan.FromSeconds(ConnectTimeoutSeconds);
        ssh.Connect();

        long bytes = 0;
        try
        {
            using var cmd = ssh.CreateCommand(commandText);
            cmd.CommandTimeout = TimeSpan.FromSeconds(CommandTimeoutSeconds);
            using var registration = cancellationToken.Register(() => { try { cmd.CancelAsync(); } catch { /* best effort */ } });

            var async = cmd.BeginExecute();
            using (var reader = new StreamReader(cmd.OutputStream, Encoding.UTF8))
            {
                string? line;
                while ((line = reader.ReadLine()) is not null)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    bytes += Encoding.UTF8.GetByteCount(line) + 1;
                    // The callback writes to the response body; run it synchronously
                    // on this pump thread so back-pressure naturally throttles reads.
                    onLine(line, cancellationToken).GetAwaiter().GetResult();
                }
            }

            cmd.EndExecute(async);
            var exit = cmd.ExitStatus ?? -1;

            if (exit == NonDebianExitCode)
                return new ProxmoxUpdateApplyResult(exit, bytes, NonDebian: true,
                    "Not a Debian/apt-based target — nothing to upgrade.");

            var error = exit == 0 ? null : $"apt-get exited with status {exit}.";
            return new ProxmoxUpdateApplyResult(exit, bytes, NonDebian: false, error);
        }
        finally
        {
            try { ssh.Disconnect(); } catch { /* idempotent */ }
        }
    }
}
