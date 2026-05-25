namespace Stashboard.Infrastructure.Docker.Ssh;

/// <summary>
/// V2.5 — the SSH-specific seam behind <see cref="SshDockerTunnel"/>. Opening a
/// channel spawns one remote command whose stdin/stdout are bridged to a single
/// accepted local TCP connection. Extracting this interface lets the tunnel's
/// accept-loop / byte-bridging orchestration be tested with an in-memory fake,
/// keeping the (untestable without a live server) SSH.NET glue thin.
/// </summary>
internal interface IRemoteCommandTransport : IDisposable
{
    /// <summary>
    /// Runs <paramref name="commandText"/> to completion and reports whether it
    /// exited with status 0. Used once per tunnel to probe remote capabilities
    /// (e.g. whether the docker CLI is on PATH).
    /// </summary>
    bool RunSucceeds(string commandText);

    /// <summary>
    /// Opens a streaming channel for a long-lived bridge command. The returned
    /// channel owns its remote command and is disposed when the bridge ends.
    /// </summary>
    IRemoteCommandChannel Open(string commandText, CancellationToken cancellationToken);
}

/// <summary>
/// A single in-flight remote command: write request bytes to <see cref="Input"/>,
/// read response bytes from <see cref="Output"/>. <see cref="Completion"/> finishes
/// when the remote command exits.
/// </summary>
internal interface IRemoteCommandChannel : IDisposable
{
    Stream Input { get; }
    Stream Output { get; }
    Task Completion { get; }
}
