using System.Net;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Stashboard.Core.Abstractions;
using Stashboard.Core.Enums;
using Stashboard.Infrastructure.Docker;

namespace Stashboard.Tests.Infrastructure.Docker;

/// <summary>
/// Unit tests for <see cref="DockerHostClient"/>. The daemon connection is
/// substituted via a mock <see cref="IDockerClientFactory"/> so we can fake
/// every container/image inspect outcome without needing a real Docker host.
///
/// The real <see cref="ImageReferenceParser"/> is wired in (not mocked) — the
/// match logic in <c>DockerHostClient</c> depends on its Docker Hub
/// <c>library/</c> normalisation, and using the real parser here is the only
/// way to exercise that path properly.
/// </summary>
public class DockerHostClientTests
{
    private const string RealImageId = "sha256:1111111111111111111111111111111111111111111111111111111111111111";
    private const string NginxDigest = "sha256:abcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcd";
    private const string OtherDigest = "sha256:99999999999999999999999999999999999999999999999999999999999999ff";

    // ── GetCurrentImageDigestAsync ───────────────────────────────────────────

    [Fact]
    public async Task GetCurrentImageDigest_DockerHubLibraryShorthand_Matches()
    {
        var harness = Harness.Build(
            inspectContainer: new ContainerInspectResponse { Image = RealImageId },
            inspectImage: new ImageInspectResponse
            {
                ID = RealImageId,
                RepoDigests = new List<string> { $"nginx@{NginxDigest}" },
            });

        var result = await harness.Client.GetCurrentImageDigestAsync(
            new DockerHostTransport(DockerHostType.LocalSocket, null, null),
            containerName: "web", registryHost: "docker.io", repository: "library/nginx");

        Assert.Equal(DockerHostStatus.Ok, result.Status);
        Assert.Equal(NginxDigest, result.Digest);
        Assert.Equal(RealImageId, result.ImageId);
        Assert.Equal($"nginx@{NginxDigest}", result.MatchedRepoDigest);
        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task GetCurrentImageDigest_DockerHubLibraryExplicit_Matches()
    {
        var harness = Harness.Build(
            inspectContainer: new ContainerInspectResponse { Image = RealImageId },
            inspectImage: new ImageInspectResponse
            {
                ID = RealImageId,
                RepoDigests = new List<string> { $"library/postgres@{NginxDigest}" },
            });

        var result = await harness.Client.GetCurrentImageDigestAsync(
            new DockerHostTransport(DockerHostType.LocalSocket, null, null),
            "db", registryHost: "docker.io", repository: "library/postgres");

        Assert.Equal(DockerHostStatus.Ok, result.Status);
        Assert.Equal(NginxDigest, result.Digest);
    }

    [Fact]
    public async Task GetCurrentImageDigest_Ghcr_Matches()
    {
        var harness = Harness.Build(
            inspectContainer: new ContainerInspectResponse { Image = RealImageId },
            inspectImage: new ImageInspectResponse
            {
                ID = RealImageId,
                RepoDigests = new List<string> { $"ghcr.io/owner/repo@{NginxDigest}" },
            });

        var result = await harness.Client.GetCurrentImageDigestAsync(
            new DockerHostTransport(DockerHostType.LocalSocket, null, null),
            "svc", registryHost: "ghcr.io", repository: "owner/repo");

        Assert.Equal(DockerHostStatus.Ok, result.Status);
        Assert.Equal(NginxDigest, result.Digest);
    }

    [Fact]
    public async Task GetCurrentImageDigest_MultipleRepoDigests_PicksMatching()
    {
        var harness = Harness.Build(
            inspectContainer: new ContainerInspectResponse { Image = RealImageId },
            inspectImage: new ImageInspectResponse
            {
                ID = RealImageId,
                RepoDigests = new List<string>
                {
                    $"ghcr.io/other/image@{OtherDigest}",
                    $"library/nginx@{NginxDigest}",
                },
            });

        var result = await harness.Client.GetCurrentImageDigestAsync(
            new DockerHostTransport(DockerHostType.LocalSocket, null, null),
            "web", "docker.io", "library/nginx");

        Assert.Equal(DockerHostStatus.Ok, result.Status);
        Assert.Equal(NginxDigest, result.Digest);
    }

    [Fact]
    public async Task GetCurrentImageDigest_NoMatch_ReturnsNoMatchingRepoDigest()
    {
        var harness = Harness.Build(
            inspectContainer: new ContainerInspectResponse { Image = RealImageId },
            inspectImage: new ImageInspectResponse
            {
                ID = RealImageId,
                RepoDigests = new List<string> { $"ghcr.io/other/image@{OtherDigest}" },
            });

        var result = await harness.Client.GetCurrentImageDigestAsync(
            new DockerHostTransport(DockerHostType.LocalSocket, null, null),
            "web", "docker.io", "library/nginx");

        Assert.Equal(DockerHostStatus.NoMatchingRepoDigest, result.Status);
        Assert.Null(result.Digest);
        Assert.Equal(RealImageId, result.ImageId);
    }

    [Fact]
    public async Task GetCurrentImageDigest_NoRepoDigests_ReturnsNoMatching()
    {
        var harness = Harness.Build(
            inspectContainer: new ContainerInspectResponse { Image = RealImageId },
            inspectImage: new ImageInspectResponse { ID = RealImageId, RepoDigests = null });

        var result = await harness.Client.GetCurrentImageDigestAsync(
            new DockerHostTransport(DockerHostType.LocalSocket, null, null),
            "web", "docker.io", "library/nginx");

        Assert.Equal(DockerHostStatus.NoMatchingRepoDigest, result.Status);
    }

    [Fact]
    public async Task GetCurrentImageDigest_ContainerNotFound_ReturnsContainerNotFound()
    {
        var harness = Harness.BuildContainerNotFound();

        var result = await harness.Client.GetCurrentImageDigestAsync(
            new DockerHostTransport(DockerHostType.LocalSocket, null, null),
            "missing", "docker.io", "library/nginx");

        Assert.Equal(DockerHostStatus.ContainerNotFound, result.Status);
    }

    [Fact]
    public async Task GetCurrentImageDigest_ImageNotFound_ReturnsImageNotFound()
    {
        var harness = Harness.BuildImageNotFound(RealImageId);

        var result = await harness.Client.GetCurrentImageDigestAsync(
            new DockerHostTransport(DockerHostType.LocalSocket, null, null),
            "web", "docker.io", "library/nginx");

        Assert.Equal(DockerHostStatus.ImageNotFound, result.Status);
        Assert.Equal(RealImageId, result.ImageId);
    }

    [Fact]
    public async Task GetCurrentImageDigest_DaemonUnreachable_ReturnsHostUnreachable()
    {
        var harness = Harness.BuildContainerThrows(new HttpRequestException("dns fail"));

        var result = await harness.Client.GetCurrentImageDigestAsync(
            new DockerHostTransport(DockerHostType.LocalSocket, null, null),
            "web", "docker.io", "library/nginx");

        Assert.Equal(DockerHostStatus.HostUnreachable, result.Status);
        Assert.Contains("dns fail", result.Error);
    }

    [Fact]
    public async Task GetCurrentImageDigest_RequestTimeout_ReturnsHostUnreachable()
    {
        var harness = Harness.BuildContainerThrows(new TaskCanceledException());

        var result = await harness.Client.GetCurrentImageDigestAsync(
            new DockerHostTransport(DockerHostType.LocalSocket, null, null),
            "web", "docker.io", "library/nginx");

        Assert.Equal(DockerHostStatus.HostUnreachable, result.Status);
    }

    [Fact]
    public async Task GetCurrentImageDigest_UnsupportedHostType_ReturnsUnsupported()
    {
        var harness = Harness.BuildFactoryThrows(new NotSupportedException("ssh: not in v1"));

        var result = await harness.Client.GetCurrentImageDigestAsync(
            new DockerHostTransport((DockerHostType)999, null, null),
            "web", "docker.io", "library/nginx");

        Assert.Equal(DockerHostStatus.UnsupportedHostType, result.Status);
    }

    // ── TestConnectionAsync ──────────────────────────────────────────────────

    [Fact]
    public async Task TestConnection_HostReachableContainerFound_ReturnsTrueTrue()
    {
        var harness = Harness.Build(
            inspectContainer: new ContainerInspectResponse { Image = RealImageId },
            inspectImage: new ImageInspectResponse { ID = RealImageId });

        var result = await harness.Client.TestContainerAsync(
            new DockerHostTransport(DockerHostType.LocalSocket, null, null), "web");

        Assert.True(result.HostReachable);
        Assert.True(result.ContainerFound);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task TestConnection_ContainerMissing_ReturnsTrueFalse()
    {
        var harness = Harness.BuildContainerNotFound();

        var result = await harness.Client.TestContainerAsync(
            new DockerHostTransport(DockerHostType.LocalSocket, null, null), "missing");

        Assert.True(result.HostReachable);
        Assert.False(result.ContainerFound);
        Assert.Contains("missing", result.Error);
    }

    [Fact]
    public async Task TestConnection_DaemonUnreachable_ReturnsFalseFalse()
    {
        var harness = Harness.BuildPingThrows(new HttpRequestException("no daemon"));

        var result = await harness.Client.TestContainerAsync(
            new DockerHostTransport(DockerHostType.LocalSocket, null, null), "web");

        Assert.False(result.HostReachable);
        Assert.False(result.ContainerFound);
        Assert.Contains("no daemon", result.Error);
    }

    [Fact]
    public async Task TestConnection_FactoryRejectsHostType_ReturnsErrorWithoutCallingDaemon()
    {
        var harness = Harness.BuildFactoryThrows(new NotSupportedException("ssh: not yet"));

        var result = await harness.Client.TestContainerAsync(
            new DockerHostTransport((DockerHostType)999, null, null), "web");

        Assert.False(result.HostReachable);
        Assert.False(result.ContainerFound);
        Assert.Contains("ssh", result.Error);
    }

    // ── V2.5 SSH transport ──────────────────────────────────────────────────

    [Fact]
    public async Task GetCurrentImageDigest_SshHandshakeFails_ReturnsHostUnreachable()
    {
        // SshOperationTimeoutException inherits from SshException which is the
        // catch-all the host client uses to map SSH errors to HostUnreachable.
        var harness = Harness.BuildFactoryThrows(
            new Renci.SshNet.Common.SshOperationTimeoutException("ssh handshake timeout"));

        var ssh = new DockerSshCredentials("vps.example.com", 22, "docker", "PEM", null, "/var/run/docker.sock");
        var result = await harness.Client.GetCurrentImageDigestAsync(
            new DockerHostTransport(DockerHostType.Ssh, null, null, ssh),
            "web", "docker.io", "library/nginx");

        Assert.Equal(DockerHostStatus.HostUnreachable, result.Status);
        Assert.Contains("SSH connection failed", result.Error);
    }

    [Fact]
    public async Task TestConnection_SshSocketFails_ReturnsHostUnreachable()
    {
        var harness = Harness.BuildFactoryThrows(
            new System.Net.Sockets.SocketException(10061)); // connection refused

        var ssh = new DockerSshCredentials("vps.example.com", 22, "docker", "PEM", null, "/var/run/docker.sock");
        var result = await harness.Client.TestContainerAsync(
            new DockerHostTransport(DockerHostType.Ssh, null, null, ssh), "web");

        Assert.False(result.HostReachable);
        Assert.False(result.ContainerFound);
        Assert.Contains("SSH connection failed", result.Error);
    }

    [Fact]
    public async Task Ping_SshTransport_FactorySeesSshCredentials()
    {
        Stashboard.Core.Abstractions.DockerSshCredentials? captured = null;
        var harness = Harness.BuildPingCaptureCredentials(c => captured = c);

        var ssh = new DockerSshCredentials("vps.example.com", 2200, "ops", "PEM", "phrase", "/run/docker.sock");
        var result = await harness.Client.PingAsync(new DockerHostTransport(DockerHostType.Ssh, null, null, ssh));

        Assert.True(result.HostReachable);
        Assert.NotNull(captured);
        Assert.Equal("vps.example.com", captured!.Host);
        Assert.Equal(2200, captured.Port);
        Assert.Equal("ops", captured.Username);
        Assert.Equal("phrase", captured.PrivateKeyPassphrase);
        Assert.Equal("/run/docker.sock", captured.RemoteSocketPath);
    }

    // ── error flattening (opaque Docker.DotNet wrappers) ────────────────────

    [Fact]
    public async Task Ping_DaemonError_SurfacesInnerExceptionDetail()
    {
        // Docker.DotNet wraps the real cause in an HttpRequestException whose own
        // message is useless ("The requested ... see inner exception for
        // details."). The user must still see what actually failed.
        var inner = new IOException("connection reset by peer");
        var opaque = new HttpRequestException("The requested failed, see inner exception for details.", inner);
        var harness = Harness.BuildPingThrows(opaque);

        var result = await harness.Client.PingAsync(
            new DockerHostTransport(DockerHostType.LocalSocket, null, null));

        Assert.False(result.HostReachable);
        Assert.Contains("connection reset by peer", result.Error);
    }

    [Fact]
    public void DescribeError_WalksInnerExceptionChain()
    {
        var ex = new HttpRequestException(
            "The requested failed, see inner exception for details.",
            new IOException("Unable to read data from the transport connection",
                new System.Net.Sockets.SocketException(10054)));

        var described = DockerHostClient.DescribeError(ex);

        Assert.Contains("The requested failed", described);
        Assert.Contains("Unable to read data from the transport connection", described);
        // The socket exception's own message is appended too.
        Assert.EndsWith(new System.Net.Sockets.SocketException(10054).Message, described);
    }

    [Fact]
    public void DescribeError_CollapsesDuplicateMessages()
    {
        var ex = new HttpRequestException("same", new Exception("same"));

        Assert.Equal("same", DockerHostClient.DescribeError(ex));
    }

    // ── Harness ──────────────────────────────────────────────────────────────

    private sealed class Harness
    {
        public required DockerHostClient Client { get; init; }

        public static Harness Build(ContainerInspectResponse inspectContainer, ImageInspectResponse inspectImage)
        {
            var (factory, _) = BuildMockFactory(daemon =>
            {
                daemon.Containers.Setup(c => c.InspectContainerAsync(
                        It.IsAny<string>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(inspectContainer);
                daemon.Images.Setup(i => i.InspectImageAsync(
                        It.IsAny<string>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(inspectImage);
                daemon.System.Setup(s => s.PingAsync(It.IsAny<CancellationToken>()))
                    .Returns(Task.CompletedTask);
            });
            return new Harness { Client = BuildClient(factory) };
        }

        public static Harness BuildContainerNotFound()
        {
            var (factory, _) = BuildMockFactory(daemon =>
            {
                daemon.Containers.Setup(c => c.InspectContainerAsync(
                        It.IsAny<string>(), It.IsAny<CancellationToken>()))
                    .ThrowsAsync(new DockerContainerNotFoundException(HttpStatusCode.NotFound, "missing"));
                daemon.System.Setup(s => s.PingAsync(It.IsAny<CancellationToken>()))
                    .Returns(Task.CompletedTask);
            });
            return new Harness { Client = BuildClient(factory) };
        }

        public static Harness BuildImageNotFound(string imageId)
        {
            var (factory, _) = BuildMockFactory(daemon =>
            {
                daemon.Containers.Setup(c => c.InspectContainerAsync(
                        It.IsAny<string>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new ContainerInspectResponse { Image = imageId });
                daemon.Images.Setup(i => i.InspectImageAsync(
                        It.IsAny<string>(), It.IsAny<CancellationToken>()))
                    .ThrowsAsync(new DockerImageNotFoundException(HttpStatusCode.NotFound, "missing image"));
            });
            return new Harness { Client = BuildClient(factory) };
        }

        public static Harness BuildContainerThrows(Exception ex)
        {
            var (factory, _) = BuildMockFactory(daemon =>
            {
                daemon.Containers.Setup(c => c.InspectContainerAsync(
                        It.IsAny<string>(), It.IsAny<CancellationToken>()))
                    .ThrowsAsync(ex);
            });
            return new Harness { Client = BuildClient(factory) };
        }

        public static Harness BuildPingThrows(Exception ex)
        {
            var (factory, _) = BuildMockFactory(daemon =>
            {
                daemon.System.Setup(s => s.PingAsync(It.IsAny<CancellationToken>()))
                    .ThrowsAsync(ex);
            });
            return new Harness { Client = BuildClient(factory) };
        }

        public static Harness BuildFactoryThrows(Exception ex)
        {
            var factory = new Mock<IDockerClientFactory>();
            factory.Setup(f => f.Create(It.IsAny<DockerHostType>(), It.IsAny<string?>(), It.IsAny<DockerTlsMaterial?>(), It.IsAny<DockerSshCredentials?>()))
                .Throws(ex);
            return new Harness { Client = BuildClient(factory.Object) };
        }

        /// <summary>V2.5 — builds a harness that captures the SSH credentials
        /// the factory was called with, so we can assert the transport bundle
        /// reaches the factory intact.</summary>
        public static Harness BuildPingCaptureCredentials(Action<DockerSshCredentials?> capture)
        {
            var (factory, _) = BuildMockFactory(daemon =>
            {
                daemon.System.Setup(s => s.PingAsync(It.IsAny<CancellationToken>()))
                    .Returns(Task.CompletedTask);
            });
            // Wrap the mock so we can spy on the SshCredentials argument.
            var inner = factory;
            var wrapping = new Mock<IDockerClientFactory>();
            wrapping.Setup(f => f.Create(It.IsAny<DockerHostType>(), It.IsAny<string?>(), It.IsAny<DockerTlsMaterial?>(), It.IsAny<DockerSshCredentials?>()))
                .Returns((DockerHostType ht, string? hu, DockerTlsMaterial? tls, DockerSshCredentials? ssh) =>
                {
                    capture(ssh);
                    return inner.Create(ht, hu, tls, ssh);
                });
            return new Harness { Client = BuildClient(wrapping.Object) };
        }

        private static DockerHostClient BuildClient(IDockerClientFactory factory) =>
            new(factory, new ImageReferenceParser(), NullLogger<DockerHostClient>.Instance);

        private record DaemonMocks(
            Mock<IDockerClient> Client,
            Mock<IContainerOperations> Containers,
            Mock<IImageOperations> Images,
            Mock<ISystemOperations> System);

        private static (IDockerClientFactory Factory, DaemonMocks Daemon) BuildMockFactory(Action<DaemonMocks> setup)
        {
            var client = new Mock<IDockerClient>();
            var containers = new Mock<IContainerOperations>();
            var images = new Mock<IImageOperations>();
            var system = new Mock<ISystemOperations>();
            client.SetupGet(c => c.Containers).Returns(containers.Object);
            client.SetupGet(c => c.Images).Returns(images.Object);
            client.SetupGet(c => c.System).Returns(system.Object);

            var daemon = new DaemonMocks(client, containers, images, system);
            setup(daemon);

            var factory = new Mock<IDockerClientFactory>();
            factory.Setup(f => f.Create(It.IsAny<DockerHostType>(), It.IsAny<string?>(), It.IsAny<DockerTlsMaterial?>(), It.IsAny<DockerSshCredentials?>()))
                .Returns(client.Object);
            return (factory.Object, daemon);
        }
    }
}
