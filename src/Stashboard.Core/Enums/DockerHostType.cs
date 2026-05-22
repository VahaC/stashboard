namespace Stashboard.Core.Enums;

public enum DockerHostType
{
    LocalSocket = 0,
    TcpTls = 1,
    /// <summary>
    /// V2.5 — talk to a remote Docker daemon over an SSH tunnel. The daemon's
    /// UNIX socket on the remote host is forwarded through a freshly-opened
    /// SSH connection per check, avoiding the need to expose <c>2376/tcp</c>
    /// publicly. Authentication uses a private key (PEM) supplied by the user.
    /// </summary>
    Ssh = 2,
}
