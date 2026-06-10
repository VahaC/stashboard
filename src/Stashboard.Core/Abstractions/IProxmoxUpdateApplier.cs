namespace Stashboard.Core.Abstractions;

/// <summary>
/// V6.7.1 — outcome of an <see cref="IProxmoxUpdateApplier.ApplyAsync"/> run.
/// </summary>
/// <param name="ExitStatus">Exit status of the remote <c>apt-get</c> pipeline
/// (0 = success), or <c>null</c> when no status was produced (e.g. SSH connect
/// failed before the command ran).</param>
/// <param name="BytesStreamed">Total bytes of output forwarded to the caller's
/// line callback.</param>
/// <param name="NonDebian">The target had no <c>apt-get</c> — nothing was
/// applied. Surfaced distinctly so the UI can say "not a Debian/apt target"
/// rather than report a generic failure.</param>
/// <param name="Error">Transport-level failure (SSH connect/auth), or
/// <c>null</c>. A non-zero <see cref="ExitStatus"/> is reported here as well so
/// the caller has a single failure signal.</param>
public sealed record ProxmoxUpdateApplyResult(
    int? ExitStatus,
    long BytesStreamed,
    bool NonDebian,
    string? Error)
{
    /// <summary>The run applied cleanly (apt exited 0 on a Debian target).</summary>
    public bool IsSuccess => !NonDebian && Error is null && ExitStatus == 0;
}

/// <summary>
/// V6.7.1 — applies pending package updates on a Proxmox target by SSHing to the
/// host and running <c>apt-get update &amp;&amp; apt-get -y dist-upgrade</c>,
/// either directly on the node (<c>vmId == 0</c>) or inside an LXC via
/// <c>pct exec &lt;vmid&gt; -- …</c>. Output is streamed line-by-line to the
/// caller as it arrives (the operation can take minutes), mirroring how the
/// monitoring count is read over SSH (<see cref="IProxmoxGuestInspector"/>).
/// Behind an interface so the controller is testable without SSH.
/// </summary>
public interface IProxmoxUpdateApplier
{
    /// <param name="profile">Decrypted host profile; <see cref="ProxmoxConnectionProfile.Ssh"/>
    /// must be configured (applying updates needs SSH).</param>
    /// <param name="vmId"><c>0</c> to upgrade the node itself; otherwise the LXC vmid.</param>
    /// <param name="onLine">Invoked for each output line as it streams in.</param>
    Task<ProxmoxUpdateApplyResult> ApplyAsync(
        ProxmoxConnectionProfile profile,
        int vmId,
        Func<string, CancellationToken, Task> onLine,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The exact remote command <see cref="ApplyAsync"/> will run for
    /// <paramref name="vmId"/> (node when <c>0</c>, else <c>pct exec</c> into the
    /// LXC). Exposed so the UI can show — and let the operator copy — what's
    /// about to execute before confirming, the way the Docker "Update command"
    /// panel does. Pure: no SSH, no side effects.
    /// </summary>
    string BuildCommand(int vmId);
}
