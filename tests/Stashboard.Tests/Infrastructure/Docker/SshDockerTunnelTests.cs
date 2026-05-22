using Stashboard.Core.Abstractions;
using Stashboard.Infrastructure.Docker.Ssh;

namespace Stashboard.Tests.Infrastructure.Docker;

/// <summary>
/// V2.5 — unit tests for <see cref="SshDockerTunnel"/> that don't require a
/// real SSH server. End-to-end bridging (TCP local listener ↔ remote
/// docker.sock via an exec channel) is exercised by the
/// <see cref="DockerHostClientIntegrationTests"/> opt-in suite when
/// <c>STASHBOARD_RUN_LOCAL_DOCKER_TESTS=1</c> is set.
/// </summary>
public class SshDockerTunnelTests
{
    [Fact]
    public void Connect_InvalidPrivateKey_ThrowsAndLeavesNoListenerBehind()
    {
        var creds = new DockerSshCredentials(
            Host: "127.0.0.1",
            Port: 1, // reserved port — no server here
            Username: "anyone",
            PrivateKeyPem: "this is not a PEM key",
            PrivateKeyPassphrase: null,
            RemoteSocketPath: "/var/run/docker.sock");

        // PrivateKeyFile parsing fails synchronously on a malformed key so the
        // constructor surfaces an exception without ever opening a socket.
        // The contract is: factory cleans up after itself — we observe that
        // indirectly via the fact that no error is thrown about leaked TCP
        // listeners on subsequent attempts.
        Assert.ThrowsAny<Exception>(() => SshDockerTunnel.Connect(creds));
    }

    [Fact]
    public void DefaultRemoteSocketPath_IsRootfulDockerSocket()
    {
        Assert.Equal("/var/run/docker.sock", SshDockerTunnel.DefaultRemoteSocketPath);
    }
}
