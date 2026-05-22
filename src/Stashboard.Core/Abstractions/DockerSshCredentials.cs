namespace Stashboard.Core.Abstractions;

/// <summary>
/// Decrypted SSH connection material used to forward a local TCP port through
/// to a remote Docker daemon's UNIX socket. Passed into
/// <see cref="IDockerClientFactory"/> alongside the host type / URL so the
/// factory can build an SSH-tunnelled <c>IDockerClient</c>.
/// </summary>
/// <param name="Host">DNS name or IP of the remote SSH server.</param>
/// <param name="Port">SSH port (default 22).</param>
/// <param name="Username">Login username on the remote host.</param>
/// <param name="PrivateKeyPem">OpenSSH-formatted private key (PEM).</param>
/// <param name="PrivateKeyPassphrase">Optional passphrase if the key is encrypted.</param>
/// <param name="RemoteSocketPath">UNIX socket path on the remote host
/// (default <c>/var/run/docker.sock</c>; can be overridden for rootless
/// Docker setups like <c>/run/user/1000/docker.sock</c>).</param>
public sealed record DockerSshCredentials(
    string Host,
    int Port,
    string Username,
    string PrivateKeyPem,
    string? PrivateKeyPassphrase,
    string RemoteSocketPath);
