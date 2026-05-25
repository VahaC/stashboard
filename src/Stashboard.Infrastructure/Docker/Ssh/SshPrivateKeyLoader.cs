using System.Text;
using Renci.SshNet;
using Stashboard.Core.Abstractions;

namespace Stashboard.Infrastructure.Docker.Ssh;

/// <summary>
/// V2.5 / V5.3 — loads the PEM private key (with an optional passphrase) from
/// the decrypted <see cref="DockerSshCredentials"/>. SSH.NET's
/// <see cref="PrivateKeyFile"/> auto-detects RSA / DSA / ECDSA / Ed25519
/// formats, so the user just pastes whatever <c>ssh-keygen</c> produced.
/// Shared by the Docker tunnel (V2.5) and the host-terminal connector (V5.3)
/// so key parsing lives in exactly one place.
/// </summary>
internal static class SshPrivateKeyLoader
{
    public static PrivateKeyFile Load(DockerSshCredentials credentials)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        var bytes = Encoding.UTF8.GetBytes(credentials.PrivateKeyPem);
        using var stream = new MemoryStream(bytes);
        return string.IsNullOrEmpty(credentials.PrivateKeyPassphrase)
            ? new PrivateKeyFile(stream)
            : new PrivateKeyFile(stream, credentials.PrivateKeyPassphrase);
    }
}
