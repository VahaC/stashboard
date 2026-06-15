using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Stashboard.Core.Abstractions;
using Stashboard.Core.Enums;
using Stashboard.Infrastructure.Docker;

namespace Stashboard.Tests.Infrastructure.Docker;

/// <summary>
/// V5.4 — unit tests for <see cref="DockerProjectUpdater"/>. Covers the two
/// dispatch paths (compose-aware project recreate vs. per-service raw
/// fallback), the depends-on topological ordering of the fallback, and the
/// "never throw — every failure is a typed status" contract the controller
/// relies on for writing parent + child audit rows.
/// </summary>
public class DockerProjectUpdaterTests
{
    private const string OldImageId = "sha256:1111111111111111111111111111111111111111111111111111111111111111";
    private const string NewImageId = "sha256:2222222222222222222222222222222222222222222222222222222222222222";
    private const string OldDigest = "sha256:aaaa00000000000000000000000000000000000000000000000000000000aaaa";
    private const string NewDigest = "sha256:bbbb11111111111111111111111111111111111111111111111111111111bbbb";

    private static DockerHostTransport LocalSocket() =>
        new(DockerHostType.LocalSocket, null, null);

    private static DockerUpdateProfile MakeProfile(string image, string container) => new(
        ImageReference: image,
        RegistryHost: "docker.io",
        Repository: $"library/{image.Split(':')[0]}",
        Tag: image.Contains(':') ? image.Split(':', 2)[1] : "latest",
        ContainerName: container,
        HostTransport: LocalSocket(),
        RegistryCredentials: null,
        RegistryAuthType: RegistryAuthType.Auto,
        AwsAccessKeyId: null,
        AwsSecretAccessKey: null,
        AwsRegion: null,
        ComposePathMapping: null);

    private static DockerProjectServiceTarget Target(string serviceName, string container, string image,
        IReadOnlyDictionary<string, string>? labels = null) =>
        new(serviceName, container, MakeProfile(image, container),
            WatchId: null, WebResourceId: null,
            Labels: labels ?? new Dictionary<string, string>(StringComparer.Ordinal));

    /// <summary>V7.1 — compose-managed containers advertise their project
    /// directory via the standard working_dir label; the updater reads it
    /// from the targets instead of a connection-wide path.</summary>
    private static Dictionary<string, string> WithWorkingDir(
        string dir, Dictionary<string, string>? extra = null)
    {
        var labels = extra is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(extra, StringComparer.Ordinal);
        labels["com.docker.compose.project.working_dir"] = dir;
        return labels;
    }

    // ── compose-aware project recreate ──────────────────────────────────────

    [Fact]
    public async Task UpdateProject_ComposeAvailable_DispatchesOneProjectLevelRecreate()
    {
        var compose = new Mock<IComposeCommandRunner>();
        compose.Setup(r => r.IsAvailableAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        compose.Setup(r => r.RecreateProjectAsync(It.IsAny<ComposeProjectRecreateRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ComposeRunResult(ComposeRunnerStatus.Success, 0, "OK", null));

        var (factory, _, _) = BuildClientFactory(
            preStateByContainer: ("web", BuildInspect(running: true, healthy: true, imageId: OldImageId)),
            preStateExtras: new[] { ("db", BuildInspect(running: true, healthy: true, imageId: OldImageId)) },
            postStateByContainer: ("web", BuildInspect(running: true, healthy: true, imageId: NewImageId)),
            postStateExtras: new[] { ("db", BuildInspect(running: true, healthy: true, imageId: NewImageId)) });

        var updater = new DockerProjectUpdater(
            compose.Object, Mock.Of<IDockerImageUpdater>(), factory.Object, new ImageReferenceParser(),
            NullLogger<DockerProjectUpdater>.Instance);

        var profile = new DockerProjectUpdateProfile(
            "myproject", LocalSocket(), null,
            new[]
            {
                Target("web", "web", "nginx:1.27", WithWorkingDir("/compose-projects/home")),
                Target("db", "db", "postgres:16", WithWorkingDir("/compose-projects/home")),
            });

        var result = await updater.UpdateProjectAsync(profile);

        Assert.Equal(DockerProjectUpdateMode.Compose, result.Mode);
        Assert.Equal(DockerUpdateAttemptStatus.Success, result.AggregateStatus);
        Assert.Equal(2, result.ServiceResults.Count);
        Assert.All(result.ServiceResults, r => Assert.Equal(DockerUpdateAttemptStatus.Success, r.Status));

        // V7.1 — the project path comes from the containers' working_dir label.
        compose.Verify(r => r.RecreateProjectAsync(
            It.Is<ComposeProjectRecreateRequest>(req => req.ProjectPath == "/compose-projects/home"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UpdateProject_PathMapping_TranslatesHostPrefixIntoContainerPath()
    {
        var compose = new Mock<IComposeCommandRunner>();
        compose.Setup(r => r.IsAvailableAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        compose.Setup(r => r.RecreateProjectAsync(It.IsAny<ComposeProjectRecreateRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ComposeRunResult(ComposeRunnerStatus.Success, 0, "OK", null));

        var (factory, _, _) = BuildClientFactory(
            preStateByContainer: ("web", BuildInspect(running: true, healthy: true, imageId: OldImageId)),
            postStateByContainer: ("web", BuildInspect(running: true, healthy: true, imageId: NewImageId)));

        var updater = new DockerProjectUpdater(
            compose.Object, Mock.Of<IDockerImageUpdater>(), factory.Object, new ImageReferenceParser(),
            NullLogger<DockerProjectUpdater>.Instance);

        // Host stacks live in /opt/stacks; Stashboard mounts them at /compose.
        var profile = new DockerProjectUpdateProfile(
            "myproject", LocalSocket(),
            new ComposePathMapping("/opt/stacks", "/compose"),
            new[] { Target("web", "web", "nginx:1.27", WithWorkingDir("/opt/stacks/myproject")) });

        var result = await updater.UpdateProjectAsync(profile);

        Assert.Equal(DockerProjectUpdateMode.Compose, result.Mode);
        compose.Verify(r => r.RecreateProjectAsync(
            It.Is<ComposeProjectRecreateRequest>(req => req.ProjectPath == "/compose/myproject"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UpdateProject_NoWorkingDirLabel_FallsBackToRawRecreate()
    {
        // Without the working_dir label there is no project directory to run
        // compose in — even with the CLI available the updater must fall back.
        var compose = new Mock<IComposeCommandRunner>();
        compose.Setup(r => r.IsAvailableAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var imageUpdater = new Mock<IDockerImageUpdater>();
        imageUpdater
            .Setup(u => u.UpdateAsync(It.IsAny<DockerUpdateProfile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DockerUpdateResult(DockerUpdateAttemptStatus.Success, OldDigest, NewDigest, null));

        var updater = new DockerProjectUpdater(
            compose.Object, imageUpdater.Object, Mock.Of<IDockerClientFactory>(), new ImageReferenceParser(),
            NullLogger<DockerProjectUpdater>.Instance);

        var profile = new DockerProjectUpdateProfile(
            "myproject", LocalSocket(), null,
            new[] { Target("web", "web", "nginx:1.27") });

        var result = await updater.UpdateProjectAsync(profile);

        Assert.Equal(DockerProjectUpdateMode.Recreate, result.Mode);
        compose.Verify(r => r.RecreateProjectAsync(
            It.IsAny<ComposeProjectRecreateRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UpdateProject_ComposeFails_AggregateRecreateFailedWithErrorOnAllServices()
    {
        var compose = new Mock<IComposeCommandRunner>();
        compose.Setup(r => r.IsAvailableAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        compose.Setup(r => r.RecreateProjectAsync(It.IsAny<ComposeProjectRecreateRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ComposeRunResult(
                ComposeRunnerStatus.CommandFailed, 1, null, "docker compose up -d failed: port is already allocated"));

        var (factory, _, _) = BuildClientFactory(
            preStateByContainer: ("web", BuildInspect(running: true, healthy: true, imageId: OldImageId)));

        var updater = new DockerProjectUpdater(
            compose.Object, Mock.Of<IDockerImageUpdater>(), factory.Object, new ImageReferenceParser(),
            NullLogger<DockerProjectUpdater>.Instance);

        var profile = new DockerProjectUpdateProfile(
            "myproject", LocalSocket(), null,
            new[] { Target("web", "web", "nginx:1.27", WithWorkingDir("/compose-projects/home")) });

        var result = await updater.UpdateProjectAsync(profile);

        Assert.Equal(DockerUpdateAttemptStatus.RecreateFailed, result.AggregateStatus);
        Assert.NotNull(result.Error);
        Assert.Contains("port is already allocated", result.Error);
        // Per-service rows are still written so the audit log records the
        // failure against each container.
        Assert.All(result.ServiceResults, r =>
        {
            Assert.Equal(DockerUpdateAttemptStatus.RecreateFailed, r.Status);
            Assert.Contains("port is already allocated", r.Error);
        });
    }

    [Fact]
    public async Task UpdateProject_ComposeReportsSuccessButContainerUnhealthy_DowngradesAggregate()
    {
        var compose = new Mock<IComposeCommandRunner>();
        compose.Setup(r => r.IsAvailableAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        compose.Setup(r => r.RecreateProjectAsync(It.IsAny<ComposeProjectRecreateRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ComposeRunResult(ComposeRunnerStatus.Success, 0, "OK", null));

        // Post-state: web is healthy, db reports "unhealthy".
        var (factory, _, _) = BuildClientFactory(
            preStateByContainer: ("web", BuildInspect(running: true, healthy: true, imageId: OldImageId)),
            preStateExtras: new[] { ("db", BuildInspect(running: true, healthy: true, imageId: OldImageId)) },
            postStateByContainer: ("web", BuildInspect(running: true, healthy: true, imageId: NewImageId)),
            postStateExtras: new[] { ("db", BuildInspect(running: true, healthy: false, imageId: NewImageId)) });

        var updater = new DockerProjectUpdater(
            compose.Object, Mock.Of<IDockerImageUpdater>(), factory.Object, new ImageReferenceParser(),
            NullLogger<DockerProjectUpdater>.Instance);

        var profile = new DockerProjectUpdateProfile(
            "myproject", LocalSocket(), null,
            new[]
            {
                Target("web", "web", "nginx:1.27", WithWorkingDir("/compose-projects/home")),
                Target("db", "db", "postgres:16", WithWorkingDir("/compose-projects/home")),
            });

        var result = await updater.UpdateProjectAsync(profile);

        Assert.Equal(DockerUpdateAttemptStatus.RecreateFailed, result.AggregateStatus);
        var web = result.ServiceResults.Single(r => r.ServiceName == "web");
        var db = result.ServiceResults.Single(r => r.ServiceName == "db");
        Assert.Equal(DockerUpdateAttemptStatus.Success, web.Status);
        Assert.Equal(DockerUpdateAttemptStatus.RecreateFailed, db.Status);
        Assert.Contains("unhealthy", db.Error);
    }

    // ── raw recreate fallback ───────────────────────────────────────────────

    [Fact]
    public async Task UpdateProject_ComposeCliUnavailable_DelegatesPerServiceToImageUpdater()
    {
        var compose = new Mock<IComposeCommandRunner>();
        compose.Setup(r => r.IsAvailableAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var imageUpdater = new Mock<IDockerImageUpdater>();
        imageUpdater
            .Setup(u => u.UpdateAsync(It.IsAny<DockerUpdateProfile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DockerUpdateProfile p, CancellationToken _) =>
                new DockerUpdateResult(DockerUpdateAttemptStatus.Success, OldDigest, NewDigest, null,
                    HealthVerified: true, HealthVerifiedUtc: DateTime.UtcNow));

        var updater = new DockerProjectUpdater(
            compose.Object, imageUpdater.Object, Mock.Of<IDockerClientFactory>(), new ImageReferenceParser(),
            NullLogger<DockerProjectUpdater>.Instance);

        var profile = new DockerProjectUpdateProfile(
            "myproject", LocalSocket(), null,
            new[]
            {
                Target("web", "web", "nginx:1.27", WithWorkingDir("/compose-projects/home")),
                Target("db", "db", "postgres:16", WithWorkingDir("/compose-projects/home")),
            });

        var result = await updater.UpdateProjectAsync(profile);

        Assert.Equal(DockerProjectUpdateMode.Recreate, result.Mode);
        Assert.Equal(DockerUpdateAttemptStatus.Success, result.AggregateStatus);
        imageUpdater.Verify(u => u.UpdateAsync(
            It.IsAny<DockerUpdateProfile>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        compose.Verify(r => r.RecreateProjectAsync(
            It.IsAny<ComposeProjectRecreateRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UpdateProject_RemoteHost_ForcesRawRecreatePath()
    {
        // Compose-aware path is local-socket only; a TCP host must fall back.
        var compose = new Mock<IComposeCommandRunner>();
        var imageUpdater = new Mock<IDockerImageUpdater>();
        imageUpdater
            .Setup(u => u.UpdateAsync(It.IsAny<DockerUpdateProfile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DockerUpdateResult(DockerUpdateAttemptStatus.Success, OldDigest, NewDigest, null));

        var updater = new DockerProjectUpdater(
            compose.Object, imageUpdater.Object, Mock.Of<IDockerClientFactory>(), new ImageReferenceParser(),
            NullLogger<DockerProjectUpdater>.Instance);

        var profile = new DockerProjectUpdateProfile(
            "myproject",
            new DockerHostTransport(DockerHostType.TcpTls, "tcp://remote:2376", null),
            null,
            new[] { Target("web", "web", "nginx:1.27", WithWorkingDir("/compose-projects/home")) });

        var result = await updater.UpdateProjectAsync(profile);

        Assert.Equal(DockerProjectUpdateMode.Recreate, result.Mode);
        // The Compose CLI is never probed when the transport is remote.
        compose.Verify(r => r.IsAvailableAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UpdateProject_RawPath_OrdersServicesByDependsOnLabel()
    {
        var compose = new Mock<IComposeCommandRunner>();
        compose.Setup(r => r.IsAvailableAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var order = new List<string>();
        var imageUpdater = new Mock<IDockerImageUpdater>();
        imageUpdater
            .Setup(u => u.UpdateAsync(It.IsAny<DockerUpdateProfile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DockerUpdateProfile p, CancellationToken _) =>
            {
                order.Add(p.ContainerName);
                return new DockerUpdateResult(DockerUpdateAttemptStatus.Success, OldDigest, NewDigest, null);
            });

        var updater = new DockerProjectUpdater(
            compose.Object, imageUpdater.Object, Mock.Of<IDockerClientFactory>(), new ImageReferenceParser(),
            NullLogger<DockerProjectUpdater>.Instance);

        // api depends_on db, db depends_on nothing → expect db before api.
        // Input order intentionally puts api first to prove the sort.
        var apiLabels = new Dictionary<string, string>
        {
            ["com.docker.compose.depends_on"] = "db:service_healthy:false",
        };
        var profile = new DockerProjectUpdateProfile(
            "myproject", LocalSocket(), null,
            new[]
            {
                Target("api", "api", "myapi:1", apiLabels),
                Target("db", "db", "postgres:16"),
            });

        var result = await updater.UpdateProjectAsync(profile);

        Assert.Equal(DockerUpdateAttemptStatus.Success, result.AggregateStatus);
        Assert.Equal(new[] { "db", "api" }, order);
    }

    [Fact]
    public async Task UpdateProject_RawPath_OnePartialFailure_AggregatesToRecreateFailedButProcessesAll()
    {
        var compose = new Mock<IComposeCommandRunner>();
        compose.Setup(r => r.IsAvailableAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var imageUpdater = new Mock<IDockerImageUpdater>();
        imageUpdater
            .Setup(u => u.UpdateAsync(It.Is<DockerUpdateProfile>(p => p.ContainerName == "db"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DockerUpdateResult(DockerUpdateAttemptStatus.Success, OldDigest, NewDigest, null));
        imageUpdater
            .Setup(u => u.UpdateAsync(It.Is<DockerUpdateProfile>(p => p.ContainerName == "api"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DockerUpdateResult(DockerUpdateAttemptStatus.PullFailed, null, null, "manifest unknown"));

        var updater = new DockerProjectUpdater(
            compose.Object, imageUpdater.Object, Mock.Of<IDockerClientFactory>(), new ImageReferenceParser(),
            NullLogger<DockerProjectUpdater>.Instance);

        var profile = new DockerProjectUpdateProfile(
            "myproject", LocalSocket(), null,
            new[] { Target("db", "db", "postgres:16"), Target("api", "api", "myapi:1") });

        var result = await updater.UpdateProjectAsync(profile);

        Assert.Equal(DockerUpdateAttemptStatus.RecreateFailed, result.AggregateStatus);
        Assert.Equal(2, result.ServiceResults.Count);
        Assert.Equal(DockerUpdateAttemptStatus.Success,
            result.ServiceResults.Single(s => s.ContainerName == "db").Status);
        Assert.Equal(DockerUpdateAttemptStatus.PullFailed,
            result.ServiceResults.Single(s => s.ContainerName == "api").Status);
    }

    [Fact]
    public async Task UpdateProject_EmptyServices_ReturnsContainerNotFoundWithoutDispatch()
    {
        var compose = new Mock<IComposeCommandRunner>();
        var imageUpdater = new Mock<IDockerImageUpdater>();
        var updater = new DockerProjectUpdater(
            compose.Object, imageUpdater.Object, Mock.Of<IDockerClientFactory>(), new ImageReferenceParser(),
            NullLogger<DockerProjectUpdater>.Instance);

        var profile = new DockerProjectUpdateProfile(
            "myproject", LocalSocket(), null,
            Array.Empty<DockerProjectServiceTarget>());

        var result = await updater.UpdateProjectAsync(profile);

        Assert.Equal(DockerUpdateAttemptStatus.ContainerNotFound, result.AggregateStatus);
        Assert.Empty(result.ServiceResults);
        compose.Verify(r => r.IsAvailableAsync(It.IsAny<CancellationToken>()), Times.Never);
        imageUpdater.Verify(u => u.UpdateAsync(
            It.IsAny<DockerUpdateProfile>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── topological ordering unit ──────────────────────────────────────────

    [Fact]
    public void OrderByDependsOn_DiamondGraph_ProducesValidTopologicalOrder()
    {
        // Graph: api → (web, worker) → db. db has no deps.
        var db = Target("db", "db", "postgres:16");
        var web = Target("web", "web", "nginx:1", new Dictionary<string, string>
        {
            ["com.docker.compose.depends_on"] = "db:service_healthy:false",
        });
        var worker = Target("worker", "worker", "worker:1", new Dictionary<string, string>
        {
            ["com.docker.compose.depends_on"] = "db",
        });
        var api = Target("api", "api", "api:1", new Dictionary<string, string>
        {
            ["com.docker.compose.depends_on"] = "web, worker",
        });

        var ordered = DockerProjectUpdater.OrderByDependsOn(new[] { api, web, worker, db });

        var index = ordered.Select((s, i) => (s.ServiceName, i))
            .ToDictionary(t => t.ServiceName, t => t.i);

        Assert.True(index["db"] < index["web"]);
        Assert.True(index["db"] < index["worker"]);
        Assert.True(index["web"] < index["api"]);
        Assert.True(index["worker"] < index["api"]);
    }

    [Fact]
    public void OrderByDependsOn_CycleBetweenServices_FallsBackToInputOrderWithoutThrowing()
    {
        // a → b → a (impossible in real compose, but we must not loop forever).
        var a = Target("a", "a", "img:1", new Dictionary<string, string>
        {
            ["com.docker.compose.depends_on"] = "b",
        });
        var b = Target("b", "b", "img:1", new Dictionary<string, string>
        {
            ["com.docker.compose.depends_on"] = "a",
        });

        var ordered = DockerProjectUpdater.OrderByDependsOn(new[] { a, b });

        Assert.Equal(2, ordered.Count);
        Assert.Contains(ordered, t => t.ServiceName == "a");
        Assert.Contains(ordered, t => t.ServiceName == "b");
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static ContainerInspectResponse BuildInspect(bool running, bool healthy, string imageId)
    {
        var state = new ContainerState
        {
            Running = running,
            Health = new Health { Status = healthy ? "healthy" : "unhealthy" },
        };
        return new ContainerInspectResponse
        {
            ID = $"id-{imageId[..16]}",
            Image = imageId,
            State = state,
        };
    }

    private static (Mock<IDockerClientFactory> factory, Mock<IContainerOperations> containers, Mock<IImageOperations> images)
        BuildClientFactory(
            (string Container, ContainerInspectResponse Inspect) preStateByContainer,
            IEnumerable<(string Container, ContainerInspectResponse Inspect)>? preStateExtras = null,
            (string Container, ContainerInspectResponse Inspect)? postStateByContainer = null,
            IEnumerable<(string Container, ContainerInspectResponse Inspect)>? postStateExtras = null)
    {
        var containersOp = new Mock<IContainerOperations>();
        var imagesOp = new Mock<IImageOperations>();
        var client = new Mock<IDockerClient>();
        client.SetupGet(c => c.Containers).Returns(containersOp.Object);
        client.SetupGet(c => c.Images).Returns(imagesOp.Object);

        // First call per container = pre-state; subsequent = post-state.
        var callCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var pre = new Dictionary<string, ContainerInspectResponse>(StringComparer.OrdinalIgnoreCase)
        {
            [preStateByContainer.Container] = preStateByContainer.Inspect,
        };
        foreach (var (n, i) in preStateExtras ?? Array.Empty<(string, ContainerInspectResponse)>()) pre[n] = i;
        var post = new Dictionary<string, ContainerInspectResponse>(StringComparer.OrdinalIgnoreCase);
        if (postStateByContainer is { } p) post[p.Container] = p.Inspect;
        foreach (var (n, i) in postStateExtras ?? Array.Empty<(string, ContainerInspectResponse)>()) post[n] = i;

        containersOp.Setup(c => c.InspectContainerAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string id, CancellationToken _) =>
            {
                callCount[id] = callCount.GetValueOrDefault(id) + 1;
                if (callCount[id] == 1 && pre.TryGetValue(id, out var preState)) return preState;
                return post.GetValueOrDefault(id) ?? pre.GetValueOrDefault(id)
                    ?? throw new DockerContainerNotFoundException(
                        System.Net.HttpStatusCode.NotFound, $"no such container: {id}");
            });

        // Image inspect returns a synthetic RepoDigest matching docker.io.
        imagesOp.Setup(i => i.InspectImageAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string imageId, CancellationToken _) =>
            {
                var digest = imageId == OldImageId ? OldDigest : NewDigest;
                return new ImageInspectResponse
                {
                    ID = imageId,
                    RepoDigests = new List<string> { $"docker.io/library/nginx@{digest}" },
                };
            });

        var factory = new Mock<IDockerClientFactory>();
        factory.Setup(f => f.Create(
                It.IsAny<DockerHostType>(), It.IsAny<string?>(),
                It.IsAny<DockerTlsMaterial?>(), It.IsAny<DockerSshCredentials?>()))
            .Returns(client.Object);
        return (factory, containersOp, imagesOp);
    }
}
