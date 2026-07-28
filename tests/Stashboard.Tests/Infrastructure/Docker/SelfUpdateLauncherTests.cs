using System.Net;
using System.Text.Json;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Stashboard.Core.Abstractions;
using Stashboard.Core.Enums;
using Stashboard.Infrastructure.Docker;

namespace Stashboard.Tests.Infrastructure.Docker;

/// <summary>
/// V9.2 — unit tests for <see cref="SelfUpdateLauncher"/>. A mock
/// <see cref="IDockerClientFactory"/> drives self-detection and the detached
/// helper launch without a real daemon. The container hostname (which Docker
/// sets to the short container id, letting us inspect our own container) is
/// supplied through the <see cref="SelfUpdateLauncher.OwnContainerHostname"/>
/// test seam.
/// </summary>
public class SelfUpdateLauncherTests
{
    private const string SelfHostname = "abc123def456";
    private const string SelfContainerId = "abc123def456789000000000000000000000000000000000000000000000aaaa";
    private const string OtherContainerId = "ffff1111ffff1111ffff1111ffff1111ffff1111ffff1111ffff1111ffffbbbb";
    private const string SelfImageId = "sha256:imageimageimageimageimageimageimageimageimageimageimageimageaaaa";
    private const string HelperContainerId = "helper-container-id";

    private static DockerUpdateProfile SelfProfile(string containerName = "stashboard-app") => new(
        ImageReference: "vahac/stashboard:latest",
        RegistryHost: "docker.io",
        Repository: "vahac/stashboard",
        Tag: "latest",
        ContainerName: containerName,
        HostTransport: new DockerHostTransport(DockerHostType.LocalSocket, null, null),
        RegistryCredentials: null,
        RegistryAuthType: RegistryAuthType.Auto,
        AwsAccessKeyId: null,
        AwsSecretAccessKey: null,
        AwsRegion: null);

    private static DockerProjectUpdateProfile ProjectProfile(params (string Service, string Container)[] services) => new(
        ProjectName: "stashboard",
        HostTransport: new DockerHostTransport(DockerHostType.LocalSocket, null, null),
        ComposePathMapping: null,
        Services: services
            .Select(s => new DockerProjectServiceTarget(
                s.Service, s.Container, SelfProfile(s.Container),
                WatchId: null, WebResourceId: null,
                Labels: new Dictionary<string, string>()))
            .ToList());

    [Fact]
    public async Task IsSelfTarget_SshTransport_ToOwnDaemon_StillDetectsSelf()
    {
        // Regression: self-update must work over an SSH tunnel (and TCP), not
        // just a local socket — self is decided by the id match, not the
        // transport. A Stashboard that reaches its own daemon over SSH is self.
        var harness = Harness.Build();
        var profile = SelfProfile("stashboard-app") with
        {
            HostTransport = new DockerHostTransport(
                DockerHostType.Ssh, null, null,
                new DockerSshCredentials("dockerhost", 22, "root", "key-pem", null, "/var/run/docker.sock")),
        };

        Assert.True(await harness.Launcher.IsSelfTargetAsync(profile));
    }

    [Fact]
    public async Task IsSelfTarget_RemoteDaemon_OwnIdNotPresent_ReturnsFalse()
    {
        // Against a genuinely remote daemon our own container id isn't found
        // there, so the id match self-corrects to not-self — no transport gate
        // needed.
        var harness = Harness.Build(selfFound: false);
        var profile = SelfProfile("some-remote-container") with
        {
            HostTransport = new DockerHostTransport(DockerHostType.TcpTls, "tcp://remote:2376", null),
        };

        Assert.False(await harness.Launcher.IsSelfTargetAsync(profile));
    }

    [Fact]
    public async Task IsSelfTarget_NameMatchesOwnContainer_ReturnsTrue()
    {
        var harness = Harness.Build();

        Assert.True(await harness.Launcher.IsSelfTargetAsync(SelfProfile("stashboard-app")));
    }

    [Fact]
    public async Task IsSelfTarget_DifferentContainer_ReturnsFalse()
    {
        var harness = Harness.Build(targetContainerId: OtherContainerId);

        Assert.False(await harness.Launcher.IsSelfTargetAsync(SelfProfile("some-other-app")));
    }

    [Fact]
    public async Task IsSelfTarget_FullContainerId_ReturnsTrue()
    {
        var harness = Harness.Build();

        Assert.True(await harness.Launcher.IsSelfTargetAsync(SelfProfile(SelfContainerId)));
    }

    [Fact]
    public async Task IsSelfTarget_HostnameUnset_ReturnsFalse()
    {
        var harness = Harness.Build();
        harness.Launcher.OwnContainerId = () => null;

        Assert.False(await harness.Launcher.IsSelfTargetAsync(SelfProfile()));
    }

    [Fact]
    public async Task IsSelfTarget_OwnContainerNotFound_ReturnsFalse()
    {
        var harness = Harness.Build(selfFound: false);

        Assert.False(await harness.Launcher.IsSelfTargetAsync(SelfProfile()));
    }

    [Fact]
    public async Task IsSelfInProject_ProjectContainsOwnContainer_ReturnsTrue()
    {
        var harness = Harness.Build(); // every target name resolves to SelfContainerId
        var profile = ProjectProfile(("web", "some-sidecar"), ("app", "stashboard-app"));

        Assert.True(await harness.Launcher.IsSelfInProjectAsync(profile));
    }

    [Fact]
    public async Task IsSelfInProject_NoServiceIsSelf_ReturnsFalse()
    {
        var harness = Harness.Build(targetContainerId: OtherContainerId);
        var profile = ProjectProfile(("web", "nginx"), ("db", "postgres"));

        Assert.False(await harness.Launcher.IsSelfInProjectAsync(profile));
    }

    [Fact]
    public async Task LaunchProject_StartsHelperWithProjectCommand()
    {
        CreateContainerParameters? captured = null;
        var harness = Harness.Build(captureCreate: p => captured = p);
        var profile = ProjectProfile(("app", "stashboard-app"));

        var result = await harness.Launcher.LaunchProjectAsync(profile);

        Assert.True(result.Started);
        Assert.Equal(new[] { "dotnet", "Stashboard.Api.dll" }, captured!.Entrypoint);
        Assert.Equal(new[] { "self-update-project" }, captured.Cmd);
        var profileEnv = Assert.Single(captured.Env, e => e.StartsWith(SelfUpdateProtocol.ProfileEnvVar + "="));
        var json = profileEnv[(SelfUpdateProtocol.ProfileEnvVar.Length + 1)..];
        var roundTripped = JsonSerializer.Deserialize<DockerProjectUpdateProfile>(json, SelfUpdateProtocol.Options)!;
        Assert.Equal("stashboard", roundTripped.ProjectName);
        Assert.Equal(new[] { "stashboard-app" }, roundTripped.Services.Select(s => s.ContainerName));
    }

    [Fact]
    public async Task Launch_HappyPath_CreatesAndStartsDetachedHelper()
    {
        CreateContainerParameters? captured = null;
        var harness = Harness.Build(captureCreate: p => captured = p);

        var result = await harness.Launcher.LaunchAsync(SelfProfile());

        Assert.True(result.Started);
        Assert.Null(result.Error);

        Assert.NotNull(captured);
        Assert.Equal(SelfUpdateLauncher.HelperContainerName, captured!.Name);
        // Runs our own (already-local) image so no pre-pull is needed.
        Assert.Equal(SelfImageId, captured.Image);
        // Entrypoint + Cmd are set explicitly so the helper runs
        // `dotnet Stashboard.Api.dll self-update` regardless of the image's
        // own ENTRYPOINT (which would otherwise double the dotnet invocation).
        Assert.Equal(new[] { "dotnet", "Stashboard.Api.dll" }, captured.Entrypoint);
        Assert.Equal(new[] { "self-update" }, captured.Cmd);
        // Always auto-removed on exit so the throwaway helper never lingers.
        Assert.True(captured.HostConfig.AutoRemove);
        Assert.Equal(RestartPolicyKind.No, captured.HostConfig.RestartPolicy.Name);

        harness.Containers.Verify(c => c.StartContainerAsync(
            HelperContainerId, It.IsAny<ContainerStartParameters>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Launch_ReplicatesParentMountsOntoHelper()
    {
        CreateContainerParameters? captured = null;
        var harness = Harness.Build(captureCreate: p => captured = p);

        await harness.Launcher.LaunchAsync(SelfProfile());

        var mounts = captured!.HostConfig.Mounts;
        Assert.NotNull(mounts);
        // The Docker socket bind must ride along so the helper can recreate us.
        Assert.Contains(mounts!, m => m.Type == "bind" && m.Source == "/var/run/docker.sock");
        // Named volumes are re-bound by name, not by their /var/lib path.
        Assert.Contains(mounts!, m => m.Type == "volume" && m.Source == "stashboard-data" && m.Target == "/app/Data");
    }

    [Fact]
    public async Task Launch_SerializesProfileIntoHelperEnvAndRoundTrips()
    {
        CreateContainerParameters? captured = null;
        var harness = Harness.Build(captureCreate: p => captured = p);
        var profile = SelfProfile();

        await harness.Launcher.LaunchAsync(profile);

        var profileEnv = Assert.Single(captured!.Env, e => e.StartsWith(SelfUpdateProtocol.ProfileEnvVar + "="));
        var json = profileEnv[(SelfUpdateProtocol.ProfileEnvVar.Length + 1)..];
        var roundTripped = JsonSerializer.Deserialize<DockerUpdateProfile>(json, SelfUpdateProtocol.Options);

        Assert.Equal(profile, roundTripped);
    }

    [Fact]
    public async Task Launch_RemovesStaleHelperBeforeCreating()
    {
        var harness = Harness.Build();

        await harness.Launcher.LaunchAsync(SelfProfile());

        harness.Containers.Verify(c => c.RemoveContainerAsync(
            SelfUpdateLauncher.HelperContainerName,
            It.Is<ContainerRemoveParameters>(p => p.Force == true),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Launch_SelfUnresolvable_ReturnsActionableError()
    {
        var harness = Harness.Build(selfFound: false);

        var result = await harness.Launcher.LaunchAsync(SelfProfile());

        Assert.False(result.Started);
        Assert.NotNull(result.Error);
        Assert.Contains("resolve", result.Error, StringComparison.OrdinalIgnoreCase);

        harness.Containers.Verify(c => c.CreateContainerAsync(
            It.IsAny<CreateContainerParameters>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Launch_CreateFails_ReturnsErrorAndDoesNotStart()
    {
        var harness = Harness.Build();
        harness.Containers.Setup(c => c.CreateContainerAsync(
                It.IsAny<CreateContainerParameters>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DockerApiException(HttpStatusCode.InternalServerError, "no space left on device"));

        var result = await harness.Launcher.LaunchAsync(SelfProfile());

        Assert.False(result.Started);
        Assert.Contains("helper container failed", result.Error);
        harness.Containers.Verify(c => c.StartContainerAsync(
            It.IsAny<string>(), It.IsAny<ContainerStartParameters>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Harness ──────────────────────────────────────────────────────────────

    private sealed class Harness
    {
        public required SelfUpdateLauncher Launcher { get; init; }
        public required Mock<IContainerOperations> Containers { get; init; }

        public static Harness Build(
            bool selfFound = true,
            Action<CreateContainerParameters>? captureCreate = null,
            string targetContainerId = SelfContainerId)
        {
            var selfInspect = new ContainerInspectResponse
            {
                ID = SelfContainerId,
                Image = SelfImageId,
                Name = "/stashboard-app",
                Config = new Config { Image = "vahac/stashboard:latest" },
                HostConfig = new HostConfig(),
                Mounts = new List<MountPoint>
                {
                    new() { Type = "bind", Source = "/var/run/docker.sock", Destination = "/var/run/docker.sock", RW = true },
                    new() { Type = "volume", Name = "stashboard-data", Source = "/var/lib/docker/volumes/stashboard-data/_data", Destination = "/app/Data", RW = true },
                },
            };

            var containers = new Mock<IContainerOperations>();

            if (selfFound)
            {
                containers.Setup(c => c.InspectContainerAsync(SelfHostname, It.IsAny<CancellationToken>()))
                    .ReturnsAsync(selfInspect);
            }
            else
            {
                containers.Setup(c => c.InspectContainerAsync(SelfHostname, It.IsAny<CancellationToken>()))
                    .ThrowsAsync(new DockerContainerNotFoundException(HttpStatusCode.NotFound, "missing"));
            }

            // Inspecting the watch's target container (any name other than our
            // own id) resolves to targetContainerId — equal to SelfContainerId
            // for the "this is me" cases, different for the non-self case.
            containers.Setup(c => c.InspectContainerAsync(
                    It.Is<string>(s => s != SelfHostname), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ContainerInspectResponse { ID = targetContainerId, Name = "/target" });

            containers.Setup(c => c.RemoveContainerAsync(
                    It.IsAny<string>(), It.IsAny<ContainerRemoveParameters>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new DockerContainerNotFoundException(HttpStatusCode.NotFound, "no leftover helper"));
            containers.Setup(c => c.CreateContainerAsync(
                    It.IsAny<CreateContainerParameters>(), It.IsAny<CancellationToken>()))
                .Callback<CreateContainerParameters, CancellationToken>((p, _) => captureCreate?.Invoke(p))
                .ReturnsAsync(new CreateContainerResponse { ID = HelperContainerId });
            containers.Setup(c => c.StartContainerAsync(
                    It.IsAny<string>(), It.IsAny<ContainerStartParameters>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);

            var client = new Mock<IDockerClient>();
            client.SetupGet(c => c.Containers).Returns(containers.Object);

            var factory = new Mock<IDockerClientFactory>();
            factory.Setup(f => f.Create(
                    It.IsAny<DockerHostType>(), It.IsAny<string?>(),
                    It.IsAny<DockerTlsMaterial?>(), It.IsAny<DockerSshCredentials?>()))
                .Returns(client.Object);

            var launcher = new SelfUpdateLauncher(factory.Object, NullLogger<SelfUpdateLauncher>.Instance)
            {
                OwnContainerId = () => SelfHostname,
            };

            return new Harness
            {
                Launcher = launcher,
                Containers = containers,
            };
        }
    }
}
