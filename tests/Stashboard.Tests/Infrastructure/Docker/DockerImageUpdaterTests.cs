using System.Net;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Stashboard.Core.Abstractions;
using Stashboard.Core.Enums;
using Stashboard.Core.Options;
using Stashboard.Infrastructure.Docker;

namespace Stashboard.Tests.Infrastructure.Docker;

/// <summary>
/// V2.7 — unit tests for <see cref="DockerImageUpdater"/>. Substitutes a
/// mock <see cref="IDockerClientFactory"/> so we can drive every code
/// path (happy recreate, pull failure, host unreachable, container
/// missing, recreate failure after a successful pull) without needing a
/// real Docker daemon. The real <see cref="ImageReferenceParser"/> is
/// wired in for the same reason it is in
/// <see cref="DockerHostClientTests"/> — its <c>library/</c> normalisation
/// is part of the digest-resolution behaviour under test.
/// </summary>
public class DockerImageUpdaterTests
{
    private const string OldImageId = "sha256:1111111111111111111111111111111111111111111111111111111111111111";
    private const string NewImageId = "sha256:2222222222222222222222222222222222222222222222222222222222222222";
    private const string OldDigest = "sha256:aaaa00000000000000000000000000000000000000000000000000000000aaaa";
    private const string NewDigest = "sha256:bbbb11111111111111111111111111111111111111111111111111111111bbbb";
    private const string NewContainerId = "container-new-id";

    private static DockerUpdateProfile NginxProfile() => new(
        ImageReference: "nginx:1.27",
        RegistryHost: "docker.io",
        Repository: "library/nginx",
        Tag: "1.27",
        ContainerName: "web",
        HostTransport: new DockerHostTransport(DockerHostType.LocalSocket, null, null),
        RegistryCredentials: null,
        RegistryAuthType: RegistryAuthType.Auto,
        AwsAccessKeyId: null,
        AwsSecretAccessKey: null,
        AwsRegion: null);

    [Fact]
    public async Task Update_HappyPath_PullsRecreatesAndReturnsSuccessWithDigests()
    {
        var harness = Harness.BuildHappyPath();

        var result = await harness.Updater.UpdateAsync(NginxProfile());

        Assert.Equal(DockerUpdateAttemptStatus.Success, result.Status);
        Assert.Equal(OldDigest, result.PreviousDigest);
        Assert.Equal(NewDigest, result.NewDigest);
        Assert.Null(result.Error);
        Assert.True(result.IsSuccess);

        // Validate the recreate actually happened in the right order.
        harness.Containers.Verify(c => c.StopContainerAsync(
            "container-old-id", It.IsAny<ContainerStopParameters>(), It.IsAny<CancellationToken>()), Times.Once);
        harness.Containers.Verify(c => c.RemoveContainerAsync(
            "container-old-id", It.IsAny<ContainerRemoveParameters>(), It.IsAny<CancellationToken>()), Times.Once);
        harness.Containers.Verify(c => c.CreateContainerAsync(
            It.IsAny<CreateContainerParameters>(), It.IsAny<CancellationToken>()), Times.Once);
        harness.Containers.Verify(c => c.StartContainerAsync(
            NewContainerId, It.IsAny<ContainerStartParameters>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Update_HappyPath_PreservesContainerNameAndImageReferenceOnRecreate()
    {
        CreateContainerParameters? captured = null;
        var harness = Harness.BuildHappyPath(captureCreate: p => captured = p);

        await harness.Updater.UpdateAsync(NginxProfile());

        Assert.NotNull(captured);
        Assert.Equal("web", captured!.Name);
        Assert.Equal("nginx:1.27", captured.Image);
    }

    [Fact]
    public async Task Update_HappyPath_PreservesComposeLabelsSoContainerStaysComposeManaged()
    {
        CreateContainerParameters? captured = null;
        var harness = Harness.BuildHappyPath(
            inspectLabels: new Dictionary<string, string>
            {
                ["com.docker.compose.project"] = "myproject",
                ["com.docker.compose.service"] = "web",
                ["com.docker.compose.config-hash"] = "abc123",
                ["custom.label"] = "keep-me",
            },
            captureCreate: p => captured = p);

        await harness.Updater.UpdateAsync(NginxProfile());

        Assert.NotNull(captured);
        Assert.NotNull(captured!.Labels);
        Assert.Equal("myproject", captured.Labels["com.docker.compose.project"]);
        Assert.Equal("web", captured.Labels["com.docker.compose.service"]);
        Assert.Equal("keep-me", captured.Labels["custom.label"]);
    }

    [Fact]
    public async Task Update_HappyPath_PassesAnonymousAuthForPublicRegistry()
    {
        AuthConfig? capturedAuth = null;
        var harness = Harness.BuildHappyPath(captureAuth: a => capturedAuth = a);

        await harness.Updater.UpdateAsync(NginxProfile());

        // No credentials configured -> the pull request goes out without
        // an AuthConfig (anonymous Docker Hub pull).
        Assert.Null(capturedAuth);
    }

    [Fact]
    public async Task Update_HappyPath_PassesBasicAuthWhenRegistryCredentialsConfigured()
    {
        AuthConfig? capturedAuth = null;
        var harness = Harness.BuildHappyPath(captureAuth: a => capturedAuth = a);
        var profile = NginxProfile() with
        {
            RegistryCredentials = new RegistryCredentials("ghcr-user", "ghcr-token"),
        };

        await harness.Updater.UpdateAsync(profile);

        Assert.NotNull(capturedAuth);
        Assert.Equal("ghcr-user", capturedAuth!.Username);
        Assert.Equal("ghcr-token", capturedAuth.Password);
        Assert.Equal("docker.io", capturedAuth.ServerAddress);
    }

    [Fact]
    public async Task Update_HappyPath_BuildsCanonicalPullReferenceForDockerHubShorthand()
    {
        ImagesCreateParameters? capturedPull = null;
        var harness = Harness.BuildHappyPath(capturePull: p => capturedPull = p);

        await harness.Updater.UpdateAsync(NginxProfile());

        // Docker Hub: strip the implicit `library/` prefix so the pulled
        // image's RepoTags match the user-typed reference (`nginx:1.27`,
        // not `library/nginx:1.27`).
        Assert.NotNull(capturedPull);
        Assert.Equal("nginx", capturedPull!.FromImage);
        Assert.Equal("1.27", capturedPull.Tag);
    }

    [Fact]
    public async Task Update_HappyPath_BuildsCanonicalPullReferenceForGhcr()
    {
        ImagesCreateParameters? capturedPull = null;
        var harness = Harness.BuildHappyPath(
            registryHost: "ghcr.io", repository: "owner/repo",
            capturePull: p => capturedPull = p);
        var profile = NginxProfile() with
        {
            ImageReference = "ghcr.io/owner/repo:v1",
            RegistryHost = "ghcr.io",
            Repository = "owner/repo",
            Tag = "v1",
        };

        await harness.Updater.UpdateAsync(profile);

        // GHCR / any non-Docker-Hub registry: include the host so the
        // engine knows where to pull from.
        Assert.NotNull(capturedPull);
        Assert.Equal("ghcr.io/owner/repo", capturedPull!.FromImage);
        Assert.Equal("v1", capturedPull.Tag);
    }

    [Fact]
    public async Task Update_ContainerNotFound_ReturnsContainerNotFoundAndNeverPulls()
    {
        var harness = Harness.BuildContainerNotFound();

        var result = await harness.Updater.UpdateAsync(NginxProfile());

        Assert.Equal(DockerUpdateAttemptStatus.ContainerNotFound, result.Status);
        Assert.Null(result.NewDigest);
        // Pull must not run when the container we'd be updating doesn't
        // exist — pulling an image we won't use just burns rate-limit
        // quota.
        harness.Images.Verify(i => i.CreateImageAsync(
            It.IsAny<ImagesCreateParameters>(), It.IsAny<AuthConfig?>(),
            It.IsAny<IProgress<JSONMessage>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Update_DaemonUnreachable_ReturnsHostUnreachable()
    {
        var harness = Harness.BuildContainerThrows(new HttpRequestException("connection refused"));

        var result = await harness.Updater.UpdateAsync(NginxProfile());

        Assert.Equal(DockerUpdateAttemptStatus.HostUnreachable, result.Status);
        Assert.Contains("connection refused", result.Error);
    }

    [Fact]
    public async Task Update_PullFailure_LeavesOldContainerUntouched()
    {
        var harness = Harness.BuildPullFails(new DockerApiException(HttpStatusCode.Forbidden, "rate limit"));

        var result = await harness.Updater.UpdateAsync(NginxProfile());

        Assert.Equal(DockerUpdateAttemptStatus.PullFailed, result.Status);
        Assert.Equal(OldDigest, result.PreviousDigest);
        Assert.Contains("rate limit", result.Error);

        // The old container must not be stopped or removed when the new
        // image never landed locally — the user should keep running the
        // version that's actually working.
        harness.Containers.Verify(c => c.StopContainerAsync(
            It.IsAny<string>(), It.IsAny<ContainerStopParameters>(), It.IsAny<CancellationToken>()), Times.Never);
        harness.Containers.Verify(c => c.RemoveContainerAsync(
            It.IsAny<string>(), It.IsAny<ContainerRemoveParameters>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── V3.2 — async post-start health verification ─────────────────────────

    [Fact]
    public async Task Update_NoHealthcheck_FlipsHealthVerifiedTrueOnFirstPoll()
    {
        // Verification enabled but the container has no HEALTHCHECK
        // (State.Health = null). The updater treats "Running + no
        // healthcheck" as healthy on the first poll so we don't sit idle
        // for the whole window when there's nothing to wait for.
        var harness = Harness.BuildHappyPath(
            options: new DockerUpdateOptions
            {
                HealthVerificationMaxAttempts = 5,
                HealthVerificationIntervalSeconds = 1,
            },
            postStartHealth: new Health?[] { null });

        var result = await harness.Updater.UpdateAsync(NginxProfile());

        Assert.Equal(DockerUpdateAttemptStatus.Success, result.Status);
        Assert.True(result.HealthVerified);
        Assert.NotNull(result.HealthVerifiedUtc);
        // Only one inspect on the new container id — we shouldn't keep
        // polling once a no-healthcheck container is verified.
        harness.Containers.Verify(c => c.InspectContainerAsync(
            NewContainerId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Update_HealthcheckBecomesHealthy_StopsPollingAndMarksVerified()
    {
        // Container is `starting` for the first two polls, then `healthy`.
        // The loop must stop the moment it sees `healthy` — no extra
        // inspects.
        var harness = Harness.BuildHappyPath(
            options: new DockerUpdateOptions
            {
                HealthVerificationMaxAttempts = 10,
                HealthVerificationIntervalSeconds = 1,
            },
            postStartHealth: new Health?[]
            {
                new() { Status = "starting" },
                new() { Status = "starting" },
                new() { Status = "healthy" },
                new() { Status = "healthy" }, // should never be reached
            });

        var result = await harness.Updater.UpdateAsync(NginxProfile());

        Assert.Equal(DockerUpdateAttemptStatus.Success, result.Status);
        Assert.True(result.HealthVerified);
        Assert.NotNull(result.HealthVerifiedUtc);
        harness.Containers.Verify(c => c.InspectContainerAsync(
            NewContainerId, It.IsAny<CancellationToken>()), Times.Exactly(3));
    }

    [Fact]
    public async Task Update_HealthcheckReportsUnhealthy_DowngradesToRecreateFailed()
    {
        // Container becomes `unhealthy` within the window — the recreate
        // succeeded from Docker's POV but the audit row must reflect that
        // the container isn't actually working.
        var harness = Harness.BuildHappyPath(
            options: new DockerUpdateOptions
            {
                HealthVerificationMaxAttempts = 10,
                HealthVerificationIntervalSeconds = 1,
            },
            postStartHealth: new Health?[]
            {
                new() { Status = "starting" },
                new() { Status = "unhealthy" },
            });

        var result = await harness.Updater.UpdateAsync(NginxProfile());

        Assert.Equal(DockerUpdateAttemptStatus.RecreateFailed, result.Status);
        Assert.False(result.HealthVerified);
        Assert.Null(result.HealthVerifiedUtc);
        Assert.NotNull(result.Error);
        Assert.Contains("unhealthy", result.Error, StringComparison.OrdinalIgnoreCase);
        // The new digest still travels with the audit row even on the
        // downgrade — the user needs to know what image is now running.
        Assert.Equal(NewDigest, result.NewDigest);
    }

    [Fact]
    public async Task Update_HealthcheckTimesOut_DowngradesToRecreateFailedAndIncludesWindow()
    {
        // Container stays `starting` for the entire window — Docker never
        // converged on a healthy state. The result is RecreateFailed and
        // the error string surfaces the configured window so the user
        // can decide whether to extend it or treat the container as
        // genuinely broken.
        var harness = Harness.BuildHappyPath(
            options: new DockerUpdateOptions
            {
                HealthVerificationMaxAttempts = 3,
                HealthVerificationIntervalSeconds = 5,
            },
            postStartHealth: new Health?[]
            {
                new() { Status = "starting" },
            });

        var result = await harness.Updater.UpdateAsync(NginxProfile());

        Assert.Equal(DockerUpdateAttemptStatus.RecreateFailed, result.Status);
        Assert.False(result.HealthVerified);
        Assert.Null(result.HealthVerifiedUtc);
        Assert.NotNull(result.Error);
        // 3 attempts × 5 s = 15 s window — the message must surface it.
        Assert.Contains("15", result.Error);
        harness.Containers.Verify(c => c.InspectContainerAsync(
            NewContainerId, It.IsAny<CancellationToken>()), Times.Exactly(3));
    }

    [Fact]
    public async Task Update_VerificationDisabled_PreservesV27BehaviorWithHealthVerifiedFalse()
    {
        // HealthVerificationMaxAttempts = 0 → the V2.7 fast path. Result
        // is Success but HealthVerified is reported as false on the
        // attempt row because we explicitly didn't observe a healthy
        // state. Audit consumers can rely on the flag meaning "we waited
        // and saw healthy".
        var harness = Harness.BuildHappyPath(
            options: new DockerUpdateOptions { HealthVerificationMaxAttempts = 0 });

        var result = await harness.Updater.UpdateAsync(NginxProfile());

        Assert.Equal(DockerUpdateAttemptStatus.Success, result.Status);
        Assert.False(result.HealthVerified);
    }

    [Fact]
    public async Task Update_RecreateFails_AfterPullSucceeded_SurfacesActionableError()
    {
        // CreateContainer throws after the old container has been removed.
        var harness = Harness.BuildHappyPath();
        harness.Containers.Setup(c => c.CreateContainerAsync(
                It.IsAny<CreateContainerParameters>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DockerApiException(HttpStatusCode.InternalServerError, "out of disk space"));

        var result = await harness.Updater.UpdateAsync(NginxProfile());

        Assert.Equal(DockerUpdateAttemptStatus.RecreateFailed, result.Status);
        // The NewDigest we resolved straight after pull travels with the
        // failure result so the audit row still records what *would* have
        // been activated. Helps the user reason about state when they go
        // poking around with `docker ps -a`.
        Assert.Equal(NewDigest, result.NewDigest);
        Assert.Contains("docker ps -a", result.Error);
    }

    // ── V5.2 — true Compose-aware recreate ───────────────────────────────────

    // V7.1 — the project directory now comes from the container's own
    // working_dir label; the profile only carries the optional path mapping.
    private static DockerUpdateProfile ComposeProfile() => NginxProfile();

    private static Dictionary<string, string> ComposeLabels() => new()
    {
        ["com.docker.compose.project"] = "myproject",
        ["com.docker.compose.service"] = "web",
        ["com.docker.compose.project.working_dir"] = "/compose-projects/home",
    };

    [Fact]
    public async Task Update_ComposeConfigured_LocalSocket_RecreatesViaComposeAndSkipsRawRecreate()
    {
        var compose = Harness.SucceedingCompose();
        var harness = Harness.BuildComposeHappyPath(compose);

        var result = await harness.Updater.UpdateAsync(ComposeProfile());

        Assert.Equal(DockerUpdateAttemptStatus.Success, result.Status);
        Assert.Equal(OldDigest, result.PreviousDigest);
        Assert.Equal(NewDigest, result.NewDigest);

        // The compose CLI did the work — pass the project path + the service
        // name read from the com.docker.compose.service label.
        compose.Verify(r => r.RecreateServiceAsync(
            It.Is<ComposeRecreateRequest>(req => req.ProjectPath == "/compose-projects/home" && req.ServiceName == "web"),
            It.IsAny<CancellationToken>()), Times.Once);

        // The raw stop / remove / create sequence must NOT run — compose owns
        // the lifecycle.
        harness.Containers.Verify(c => c.StopContainerAsync(
            It.IsAny<string>(), It.IsAny<ContainerStopParameters>(), It.IsAny<CancellationToken>()), Times.Never);
        harness.Containers.Verify(c => c.RemoveContainerAsync(
            It.IsAny<string>(), It.IsAny<ContainerRemoveParameters>(), It.IsAny<CancellationToken>()), Times.Never);
        harness.Containers.Verify(c => c.CreateContainerAsync(
            It.IsAny<CreateContainerParameters>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Update_ComposePathMapping_TranslatesHostPrefixIntoContainerPath()
    {
        // V7.1 — host stacks live in /compose-projects; Stashboard mounts them
        // at /mnt/stacks. The working_dir label is a host path and must be
        // translated before the compose CLI (running inside Stashboard) sees it.
        var compose = Harness.SucceedingCompose();
        var harness = Harness.BuildComposeHappyPath(compose);
        var profile = ComposeProfile() with
        {
            ComposePathMapping = new ComposePathMapping("/compose-projects", "/mnt/stacks"),
        };

        var result = await harness.Updater.UpdateAsync(profile);

        Assert.Equal(DockerUpdateAttemptStatus.Success, result.Status);
        compose.Verify(r => r.RecreateServiceAsync(
            It.Is<ComposeRecreateRequest>(req => req.ProjectPath == "/mnt/stacks/home" && req.ServiceName == "web"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Update_ComposeRecreateFails_ReturnsRecreateFailedAndNeverTouchesContainerDirectly()
    {
        var compose = new Mock<IComposeCommandRunner>();
        compose.Setup(r => r.IsAvailableAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        compose.Setup(r => r.RecreateServiceAsync(It.IsAny<ComposeRecreateRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ComposeRunResult(
                ComposeRunnerStatus.CommandFailed, 1, null, "docker compose up -d failed: port is already allocated"));
        var harness = Harness.BuildComposeHappyPath(compose);

        var result = await harness.Updater.UpdateAsync(ComposeProfile());

        Assert.Equal(DockerUpdateAttemptStatus.RecreateFailed, result.Status);
        Assert.Equal(OldDigest, result.PreviousDigest);
        Assert.Contains("port is already allocated", result.Error);

        // No destructive raw recreate when compose fails — the old container
        // is still running because compose never removed it.
        harness.Containers.Verify(c => c.RemoveContainerAsync(
            It.IsAny<string>(), It.IsAny<ContainerRemoveParameters>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Update_ComposeProjectPathMissing_ReturnsRecreateFailedWithBindMountHint()
    {
        var compose = new Mock<IComposeCommandRunner>();
        compose.Setup(r => r.IsAvailableAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        compose.Setup(r => r.RecreateServiceAsync(It.IsAny<ComposeRecreateRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ComposeRunResult(
                ComposeRunnerStatus.ProjectPathNotFound, null, null,
                "Compose project directory '/compose-projects/home' was not found inside the container."));
        var harness = Harness.BuildComposeHappyPath(compose);

        var result = await harness.Updater.UpdateAsync(ComposeProfile());

        Assert.Equal(DockerUpdateAttemptStatus.RecreateFailed, result.Status);
        Assert.Contains("bind-mounted", result.Error);
    }

    [Fact]
    public async Task Update_ComposeConfigured_RunsHealthVerificationOnRecreatedContainer()
    {
        var compose = Harness.SucceedingCompose();
        var harness = Harness.BuildComposeHappyPath(
            compose,
            options: new DockerUpdateOptions
            {
                HealthVerificationMaxAttempts = 5,
                HealthVerificationIntervalSeconds = 1,
            },
            postStartHealth: new Health?[] { new() { Status = "healthy" } });

        var result = await harness.Updater.UpdateAsync(ComposeProfile());

        Assert.Equal(DockerUpdateAttemptStatus.Success, result.Status);
        Assert.True(result.HealthVerified);
        Assert.NotNull(result.HealthVerifiedUtc);
    }

    [Fact]
    public async Task Update_ComposeCliUnavailable_FallsBackToRawRecreate()
    {
        // Local socket + project path + compose-managed container, but the
        // compose CLI isn't installed in the image → graceful raw recreate.
        var harness = Harness.BuildHappyPath(inspectLabels: ComposeLabels());

        var result = await harness.Updater.UpdateAsync(ComposeProfile());

        Assert.Equal(DockerUpdateAttemptStatus.Success, result.Status);
        harness.Containers.Verify(c => c.CreateContainerAsync(
            It.IsAny<CreateContainerParameters>(), It.IsAny<CancellationToken>()), Times.Once);
        harness.Compose.Verify(r => r.RecreateServiceAsync(
            It.IsAny<ComposeRecreateRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Update_ContainerNotComposeManaged_UsesRawRecreateWithoutProbingCli()
    {
        // Project path configured but the container has no
        // com.docker.compose.service label → not a compose container, raw path.
        var compose = Harness.SucceedingCompose();
        var harness = Harness.BuildHappyPath(inspectLabels: null, compose: compose);

        var result = await harness.Updater.UpdateAsync(ComposeProfile());

        Assert.Equal(DockerUpdateAttemptStatus.Success, result.Status);
        harness.Containers.Verify(c => c.CreateContainerAsync(
            It.IsAny<CreateContainerParameters>(), It.IsAny<CancellationToken>()), Times.Once);
        compose.Verify(r => r.IsAvailableAsync(It.IsAny<CancellationToken>()), Times.Never);
        compose.Verify(r => r.RecreateServiceAsync(
            It.IsAny<ComposeRecreateRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Update_RemoteHost_IgnoresComposeProjectPathAndUsesRawRecreate()
    {
        // Compose-aware recreate is local-socket only — a TCP+TLS host with a
        // project path configured still goes through the raw recreate.
        var compose = Harness.SucceedingCompose();
        var harness = Harness.BuildHappyPath(inspectLabels: ComposeLabels(), compose: compose);
        var profile = ComposeProfile() with
        {
            HostTransport = new DockerHostTransport(DockerHostType.TcpTls, "tcp://remote:2376", null),
        };

        var result = await harness.Updater.UpdateAsync(profile);

        Assert.Equal(DockerUpdateAttemptStatus.Success, result.Status);
        harness.Containers.Verify(c => c.CreateContainerAsync(
            It.IsAny<CreateContainerParameters>(), It.IsAny<CancellationToken>()), Times.Once);
        compose.Verify(r => r.IsAvailableAsync(It.IsAny<CancellationToken>()), Times.Never);
        compose.Verify(r => r.RecreateServiceAsync(
            It.IsAny<ComposeRecreateRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Harness ──────────────────────────────────────────────────────────────

    private sealed class Harness
    {
        public required DockerImageUpdater Updater { get; init; }
        public required Mock<IContainerOperations> Containers { get; init; }
        public required Mock<IImageOperations> Images { get; init; }
        public required Mock<IComposeCommandRunner> Compose { get; init; }

        public static Harness BuildHappyPath(
            string registryHost = "docker.io",
            string repository = "library/nginx",
            IDictionary<string, string>? inspectLabels = null,
            Action<CreateContainerParameters>? captureCreate = null,
            Action<AuthConfig?>? captureAuth = null,
            Action<ImagesCreateParameters>? capturePull = null,
            DockerUpdateOptions? options = null,
            Mock<IComposeCommandRunner>? compose = null,
            // V3.2 — drives the post-start health poll. When non-null the
            // happy-path harness feeds these <c>State.Health</c> snapshots
            // out of consecutive <c>InspectContainerAsync</c> calls on the
            // *new* container id so the verification loop can converge.
            IReadOnlyList<Health?>? postStartHealth = null)
        {
            var inspect = new ContainerInspectResponse
            {
                ID = "container-old-id",
                Image = OldImageId,
                Name = "/web",
                Config = new Config
                {
                    Image = "nginx:1.27",
                    Env = new List<string> { "FOO=bar" },
                    Labels = inspectLabels ?? new Dictionary<string, string>(),
                },
                HostConfig = new HostConfig
                {
                    NetworkMode = "bridge",
                    RestartPolicy = new RestartPolicy { Name = RestartPolicyKind.UnlessStopped },
                },
                NetworkSettings = new NetworkSettings
                {
                    Networks = new Dictionary<string, EndpointSettings>
                    {
                        ["bridge"] = new EndpointSettings
                        {
                            IPAddress = "172.17.0.2",
                            EndpointID = "stale-endpoint-id",
                            Aliases = new List<string> { "web" },
                        },
                    },
                },
            };

            var (factory, daemon) = BuildMockFactory(d =>
            {
                // Initial inspect on the *container name* yields the old
                // container's state — that's what the updater uses to
                // template the recreate.
                d.Containers.Setup(c => c.InspectContainerAsync(
                        "web", It.IsAny<CancellationToken>()))
                    .ReturnsAsync(inspect);

                // V3.2 — the health verification loop polls
                // InspectContainerAsync on the new container id. When the
                // caller supplied a sequence of Health snapshots, walk it
                // attempt-by-attempt; the last entry is used for any
                // additional polls so the sequence can be shorter than
                // HealthVerificationMaxAttempts. The default (null) yields
                // a Running container with no healthcheck, matching the
                // V2.7 happy path.
                var healthIndex = 0;
                d.Containers.Setup(c => c.InspectContainerAsync(
                        NewContainerId, It.IsAny<CancellationToken>()))
                    .ReturnsAsync(() =>
                    {
                        Health? health;
                        if (postStartHealth is { Count: > 0 })
                        {
                            var idx = Math.Min(healthIndex, postStartHealth.Count - 1);
                            health = postStartHealth[idx];
                            healthIndex++;
                        }
                        else
                        {
                            health = null;
                        }
                        return new ContainerInspectResponse
                        {
                            ID = NewContainerId,
                            Image = NewImageId,
                            State = new ContainerState
                            {
                                Running = true,
                                Status = "running",
                                Health = health,
                            },
                        };
                    });

                d.Images.Setup(i => i.CreateImageAsync(
                        It.IsAny<ImagesCreateParameters>(), It.IsAny<AuthConfig?>(),
                        It.IsAny<IProgress<JSONMessage>>(), It.IsAny<CancellationToken>()))
                    .Callback<ImagesCreateParameters, AuthConfig?, IProgress<JSONMessage>, CancellationToken>(
                        (pull, auth, _, _) =>
                        {
                            capturePull?.Invoke(pull);
                            captureAuth?.Invoke(auth);
                        })
                    .Returns(Task.CompletedTask);

                d.Images.Setup(i => i.InspectImageAsync(OldImageId, It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new ImageInspectResponse
                    {
                        ID = OldImageId,
                        RepoDigests = new List<string> { $"{registryHost}/{repository}@{OldDigest}" },
                    });
                d.Images.Setup(i => i.InspectImageAsync(NewImageId, It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new ImageInspectResponse
                    {
                        ID = NewImageId,
                        RepoDigests = new List<string> { $"{registryHost}/{repository}@{NewDigest}" },
                    });
                d.Images.Setup(i => i.InspectImageAsync(
                        It.Is<string>(s => s != OldImageId && s != NewImageId),
                        It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new ImageInspectResponse
                    {
                        ID = NewImageId,
                        RepoDigests = new List<string> { $"{registryHost}/{repository}@{NewDigest}" },
                    });

                d.Containers.Setup(c => c.StopContainerAsync(
                        It.IsAny<string>(), It.IsAny<ContainerStopParameters>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(true);
                d.Containers.Setup(c => c.RemoveContainerAsync(
                        It.IsAny<string>(), It.IsAny<ContainerRemoveParameters>(), It.IsAny<CancellationToken>()))
                    .Returns(Task.CompletedTask);
                d.Containers.Setup(c => c.CreateContainerAsync(
                        It.IsAny<CreateContainerParameters>(), It.IsAny<CancellationToken>()))
                    .Callback<CreateContainerParameters, CancellationToken>((p, _) => captureCreate?.Invoke(p))
                    .ReturnsAsync(new CreateContainerResponse { ID = NewContainerId });
                d.Containers.Setup(c => c.StartContainerAsync(
                        It.IsAny<string>(), It.IsAny<ContainerStartParameters>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(true);
            });
            var composeMock = compose ?? NotAvailableCompose();
            return new Harness
            {
                Updater = BuildUpdater(factory, composeMock.Object, options),
                Containers = daemon.Containers,
                Images = daemon.Images,
                Compose = composeMock,
            };
        }

        public static Harness BuildContainerNotFound()
        {
            var (factory, daemon) = BuildMockFactory(d =>
            {
                d.Containers.Setup(c => c.InspectContainerAsync(
                        It.IsAny<string>(), It.IsAny<CancellationToken>()))
                    .ThrowsAsync(new DockerContainerNotFoundException(HttpStatusCode.NotFound, "missing"));
            });
            var compose = NotAvailableCompose();
            return new Harness { Updater = BuildUpdater(factory, compose.Object), Containers = daemon.Containers, Images = daemon.Images, Compose = compose };
        }

        public static Harness BuildContainerThrows(Exception ex)
        {
            var (factory, daemon) = BuildMockFactory(d =>
            {
                d.Containers.Setup(c => c.InspectContainerAsync(
                        It.IsAny<string>(), It.IsAny<CancellationToken>()))
                    .ThrowsAsync(ex);
            });
            var compose = NotAvailableCompose();
            return new Harness { Updater = BuildUpdater(factory, compose.Object), Containers = daemon.Containers, Images = daemon.Images, Compose = compose };
        }

        public static Harness BuildPullFails(Exception pullException)
        {
            var (factory, daemon) = BuildMockFactory(d =>
            {
                d.Containers.Setup(c => c.InspectContainerAsync(
                        It.IsAny<string>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new ContainerInspectResponse
                    {
                        ID = "container-old-id",
                        Image = OldImageId,
                        Name = "/web",
                        Config = new Config { Image = "nginx:1.27" },
                    });
                d.Images.Setup(i => i.InspectImageAsync(OldImageId, It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new ImageInspectResponse
                    {
                        ID = OldImageId,
                        RepoDigests = new List<string> { $"docker.io/library/nginx@{OldDigest}" },
                    });
                d.Images.Setup(i => i.CreateImageAsync(
                        It.IsAny<ImagesCreateParameters>(), It.IsAny<AuthConfig?>(),
                        It.IsAny<IProgress<JSONMessage>>(), It.IsAny<CancellationToken>()))
                    .ThrowsAsync(pullException);
            });
            var compose = NotAvailableCompose();
            return new Harness { Updater = BuildUpdater(factory, compose.Object), Containers = daemon.Containers, Images = daemon.Images, Compose = compose };
        }

        /// <summary>
        /// V5.2 — happy path through the Compose-aware recreate. The container
        /// carries the <c>com.docker.compose.service</c> label and the compose
        /// runner reports success; the updater re-inspects the container by name
        /// (so the second InspectContainerAsync returns the recreated container)
        /// rather than running the raw stop / remove / create sequence.
        /// </summary>
        public static Harness BuildComposeHappyPath(
            Mock<IComposeCommandRunner> compose,
            DockerUpdateOptions? options = null,
            IReadOnlyList<Health?>? postStartHealth = null)
        {
            var oldInspect = new ContainerInspectResponse
            {
                ID = "container-old-id",
                Image = OldImageId,
                Name = "/web",
                Config = new Config
                {
                    Image = "nginx:1.27",
                    Labels = new Dictionary<string, string>
                    {
                        ["com.docker.compose.project"] = "myproject",
                        ["com.docker.compose.service"] = "web",
                        // V7.1 — the updater reads the project directory from here.
                        ["com.docker.compose.project.working_dir"] = "/compose-projects/home",
                    },
                },
                HostConfig = new HostConfig { NetworkMode = "bridge" },
            };

            var (factory, daemon) = BuildMockFactory(d =>
            {
                // The container name is inspected twice: once up-front (old
                // container → previous digest + compose service label) and once
                // after `docker compose up` (recreated container → new digest).
                d.Containers.SetupSequence(c => c.InspectContainerAsync(
                        "web", It.IsAny<CancellationToken>()))
                    .ReturnsAsync(oldInspect)
                    .ReturnsAsync(new ContainerInspectResponse
                    {
                        ID = NewContainerId,
                        Image = NewImageId,
                        Name = "/web",
                        State = new ContainerState { Running = true, Status = "running" },
                    });

                // Health-verification polls inspect the new container id.
                var healthIndex = 0;
                d.Containers.Setup(c => c.InspectContainerAsync(
                        NewContainerId, It.IsAny<CancellationToken>()))
                    .ReturnsAsync(() =>
                    {
                        Health? health = postStartHealth is { Count: > 0 }
                            ? postStartHealth[Math.Min(healthIndex++, postStartHealth.Count - 1)]
                            : null;
                        return new ContainerInspectResponse
                        {
                            ID = NewContainerId,
                            Image = NewImageId,
                            State = new ContainerState { Running = true, Status = "running", Health = health },
                        };
                    });

                d.Images.Setup(i => i.InspectImageAsync(OldImageId, It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new ImageInspectResponse
                    {
                        ID = OldImageId,
                        RepoDigests = new List<string> { $"docker.io/library/nginx@{OldDigest}" },
                    });
                d.Images.Setup(i => i.InspectImageAsync(NewImageId, It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new ImageInspectResponse
                    {
                        ID = NewImageId,
                        RepoDigests = new List<string> { $"docker.io/library/nginx@{NewDigest}" },
                    });
            });

            return new Harness
            {
                Updater = BuildUpdater(factory, compose.Object, options),
                Containers = daemon.Containers,
                Images = daemon.Images,
                Compose = compose,
            };
        }

        /// <summary>A compose runner that reports the CLI as absent — keeps the
        /// V2.7-era tests on the raw recreate path.</summary>
        private static Mock<IComposeCommandRunner> NotAvailableCompose()
        {
            var mock = new Mock<IComposeCommandRunner>();
            mock.Setup(r => r.IsAvailableAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);
            return mock;
        }

        /// <summary>A compose runner that reports the CLI present and the recreate
        /// successful.</summary>
        public static Mock<IComposeCommandRunner> SucceedingCompose()
        {
            var mock = new Mock<IComposeCommandRunner>();
            mock.Setup(r => r.IsAvailableAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
            mock.Setup(r => r.RecreateServiceAsync(It.IsAny<ComposeRecreateRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ComposeRunResult(ComposeRunnerStatus.Success, 0, "Recreated web", null));
            return mock;
        }

        private static DockerImageUpdater BuildUpdater(
            IDockerClientFactory factory,
            IComposeCommandRunner compose,
            DockerUpdateOptions? options = null)
        {
            // We don't need a real ECR token provider for these tests —
            // the AwsEcr path is exercised via direct mock injection in
            // the dedicated ECR test. Any provider that's never called
            // will do for the rest.
            var ecrMock = new Mock<IAwsEcrTokenProvider>(MockBehavior.Strict);
            var updater = new DockerImageUpdater(
                factory, new ImageReferenceParser(),
                ecrMock.Object,
                compose,
                Options.Create(options ?? new DockerUpdateOptions
                {
                    // V3.2 — short-circuit the post-start health poll for the
                    // V2.7-era tests that don't care about it. The dedicated
                    // health-verification tests build their own harness with
                    // poll attempts enabled.
                    HealthVerificationMaxAttempts = 0,
                    HealthVerificationIntervalSeconds = 1,
                }),
                NullLogger<DockerImageUpdater>.Instance);
            // Sub the delay so the verification loop never burns wall-clock
            // time when a test does enable attempts.
            updater.DelayAsync = (_, _) => Task.CompletedTask;
            return updater;
        }

        private record DaemonMocks(
            Mock<IDockerClient> Client,
            Mock<IContainerOperations> Containers,
            Mock<IImageOperations> Images,
            Mock<INetworkOperations> Networks,
            Mock<ISystemOperations> System);

        private static (IDockerClientFactory Factory, DaemonMocks Daemon) BuildMockFactory(Action<DaemonMocks> setup)
        {
            var client = new Mock<IDockerClient>();
            var containers = new Mock<IContainerOperations>();
            var images = new Mock<IImageOperations>();
            var networks = new Mock<INetworkOperations>();
            var system = new Mock<ISystemOperations>();
            client.SetupGet(c => c.Containers).Returns(containers.Object);
            client.SetupGet(c => c.Images).Returns(images.Object);
            client.SetupGet(c => c.Networks).Returns(networks.Object);
            client.SetupGet(c => c.System).Returns(system.Object);

            var daemon = new DaemonMocks(client, containers, images, networks, system);
            setup(daemon);

            var factory = new Mock<IDockerClientFactory>();
            factory.Setup(f => f.Create(
                    It.IsAny<DockerHostType>(), It.IsAny<string?>(),
                    It.IsAny<DockerTlsMaterial?>(), It.IsAny<DockerSshCredentials?>()))
                .Returns(client.Object);
            return (factory.Object, daemon);
        }
    }
}
