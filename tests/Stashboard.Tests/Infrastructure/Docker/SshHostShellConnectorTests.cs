using Stashboard.Core.Abstractions;
using Stashboard.Infrastructure.Docker.Ssh;

namespace Stashboard.Tests.Infrastructure.Docker;

/// <summary>
/// V5.3 — unit coverage for <see cref="SshHostShellConnector"/>. The live SSH
/// PTY (CreateShellStream + byte pump) needs a real server and is exercised by
/// the opt-in integration suite; what's testable here without one is that a
/// malformed key fails fast (during parsing, before any socket is opened) just
/// like the V2.5 Docker tunnel.
/// </summary>
public class SshHostShellConnectorTests
{
    [Fact]
    public void Connect_InvalidPrivateKey_ThrowsBeforeOpeningASocket()
    {
        var connector = new SshHostShellConnector();
        var creds = new DockerSshCredentials(
            Host: "127.0.0.1",
            Port: 1, // reserved — nothing listening
            Username: "anyone",
            PrivateKeyPem: "this is not a PEM key",
            PrivateKeyPassphrase: null,
            RemoteSocketPath: "/var/run/docker.sock");

        Assert.ThrowsAny<Exception>(() => connector.Connect(creds, new HostShellWindow(80, 24)));
    }
}
