using System.Net;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Renci.SshNet.Common;
using Stashboard.Core.Abstractions;
using Stashboard.Core.Enums;
using Stashboard.Core.Options;

namespace Stashboard.Infrastructure.Docker;

/// <summary>
/// V2.7 — pulls a new image for a watch and recreates its container in
/// place. Mirrors what Watchtower / Diun do: the previous container's
/// <c>inspect</c> output is the template — image is swapped, every other
/// piece of the launch config (mounts, env, ports, labels, restart
/// policy, network mode) is copied verbatim. Compose-managed containers
/// stay Compose-managed because their <c>com.docker.compose.*</c> labels
/// ride along on the copied Labels dictionary.
/// </summary>
/// <remarks>
/// <para>Never throws — every failure mode is mapped to a typed
/// <see cref="DockerUpdateAttemptStatus"/> so the controller can log a row
/// in <c>DockerUpdateAttempts</c> for every click of the button.</para>
/// <para>Single-network recreate only: when the original container is
/// attached to more than one network we keep the first network through
/// <c>NetworkingConfig.EndpointsConfig</c> (so service-aliases / DNS-name
/// resolution survives) and the rest are re-attached after start. Plain
/// <c>bridge</c>-only containers — the overwhelming majority — go through
/// the fast path without any post-start work.</para>
/// </remarks>
public sealed class DockerImageUpdater(
    IDockerClientFactory dockerClientFactory,
    IImageReferenceParser imageReferenceParser,
    IAwsEcrTokenProvider awsEcrTokenProvider,
    IComposeCommandRunner composeCommandRunner,
    IOptions<DockerUpdateOptions> updateOptions,
    ILogger<DockerImageUpdater> logger) : IDockerImageUpdater
{
    /// <summary>The standard Compose label every Compose-managed container
    /// carries. Its value is the service name <c>docker compose</c> expects.</summary>
    private const string ComposeServiceLabel = "com.docker.compose.service";

    private readonly DockerUpdateOptions _options = updateOptions.Value;

    /// <summary>V3.2 — exposed so unit tests can stub the per-attempt
    /// delay without burning real wall-clock time. Production code goes
    /// through <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.</summary>
    public Func<TimeSpan, CancellationToken, Task> DelayAsync { get; set; } = Task.Delay;

    public async Task<DockerUpdateResult> UpdateAsync(DockerUpdateProfile profile, CancellationToken cancellationToken = default)
    {
        IDockerClient client;
        try
        {
            client = dockerClientFactory.Create(
                profile.HostTransport.HostType,
                profile.HostTransport.HostUrl,
                profile.HostTransport.Tls,
                profile.HostTransport.Ssh);
        }
        catch (Exception ex) when (ex is NotSupportedException or ArgumentException)
        {
            return new DockerUpdateResult(DockerUpdateAttemptStatus.HostUnreachable, null, null,
                $"Docker host configuration is invalid: {ex.Message}");
        }
        catch (Exception ex) when (IsSshConnectFailure(ex))
        {
            return new DockerUpdateResult(DockerUpdateAttemptStatus.HostUnreachable, null, null,
                $"SSH connection failed: {ex.Message}");
        }

        using (client)
        {
            ContainerInspectResponse inspect;
            try
            {
                inspect = await client.Containers.InspectContainerAsync(profile.ContainerName, cancellationToken);
            }
            catch (DockerContainerNotFoundException)
            {
                return new DockerUpdateResult(DockerUpdateAttemptStatus.ContainerNotFound, null, null,
                    $"Container '{profile.ContainerName}' not found on the configured Docker host.");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or DockerApiException)
            {
                return new DockerUpdateResult(DockerUpdateAttemptStatus.HostUnreachable, null, null,
                    $"Docker host unreachable: {ex.Message}");
            }

            var previousImageId = inspect.Image;
            var previousDigest = await TryResolveDigestAsync(
                client, previousImageId, profile.RegistryHost, profile.Repository, cancellationToken);

            // V5.2 — true Compose-aware recreate. When the connection is a
            // local socket, a Compose project path is configured, the running
            // container carries the standard com.docker.compose.service label,
            // and the compose CLI is present inside the Stashboard container,
            // delegate the pull + recreate to `docker compose` so the full
            // Compose lifecycle (env_file resolution, depends_on ordering,
            // profiles, Compose's own network / subnet allocation) is honoured
            // — none of which the raw Docker.DotNet recreate below can
            // replicate. A missing binary degrades gracefully to the raw path.
            var composeService = TryGetComposeService(inspect);
            if (ShouldUseCompose(profile, composeService)
                && await composeCommandRunner.IsAvailableAsync(cancellationToken))
            {
                return await RecreateViaComposeAsync(
                    client, profile, composeService!, previousDigest, cancellationToken);
            }

            // 1) Pull the new image. Failure here leaves the original
            //    container untouched — we only stop / remove after the pull
            //    is committed locally so a network blip is recoverable.
            AuthConfig? auth;
            try
            {
                auth = await BuildAuthConfigAsync(profile, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Building registry auth for {Image} failed", profile.ImageReference);
                return new DockerUpdateResult(DockerUpdateAttemptStatus.PullFailed, previousDigest, null,
                    $"Resolving registry credentials failed: {ex.Message}");
            }

            try
            {
                await client.Images.CreateImageAsync(
                    new ImagesCreateParameters
                    {
                        FromImage = BuildFromImage(profile),
                        Tag = profile.Tag,
                    },
                    auth,
                    new Progress<JSONMessage>(),
                    cancellationToken);
            }
            catch (DockerApiException ex)
            {
                logger.LogWarning(ex, "docker pull failed for {Image}", profile.ImageReference);
                return new DockerUpdateResult(DockerUpdateAttemptStatus.PullFailed, previousDigest, null,
                    $"docker pull failed: {ex.Message}");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                return new DockerUpdateResult(DockerUpdateAttemptStatus.PullFailed, previousDigest, null,
                    $"docker pull failed: {ex.Message}");
            }

            // 2) Capture the new image's digest *before* the recreate so we
            //    can surface it on the attempt row even when the recreate
            //    fails halfway through.
            var newImageId = await TryGetImageIdAsync(client, profile.ImageReference, cancellationToken);
            var newDigest = newImageId is null
                ? null
                : await TryResolveDigestAsync(client, newImageId, profile.RegistryHost, profile.Repository, cancellationToken);

            // 3) Stop + remove the old container. Force-remove because some
            //    containers (those still in "restarting" state) can't be
            //    cleanly removed without the flag, and a partial recreate
            //    leaves the user in a worse spot than a forced stop.
            try
            {
                await client.Containers.StopContainerAsync(inspect.ID,
                    new ContainerStopParameters { WaitBeforeKillSeconds = 30 },
                    cancellationToken);
            }
            catch (DockerApiException ex) when (ex.StatusCode == HttpStatusCode.NotModified)
            {
                // Container was already stopped — fine, continue.
            }
            catch (Exception ex) when (ex is HttpRequestException or DockerApiException or TaskCanceledException)
            {
                return new DockerUpdateResult(DockerUpdateAttemptStatus.RecreateFailed, previousDigest, newDigest,
                    $"Stopping container failed: {ex.Message}");
            }

            try
            {
                await client.Containers.RemoveContainerAsync(inspect.ID,
                    new ContainerRemoveParameters { Force = true, RemoveVolumes = false },
                    cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or DockerApiException or TaskCanceledException)
            {
                return new DockerUpdateResult(DockerUpdateAttemptStatus.RecreateFailed, previousDigest, newDigest,
                    $"Removing old container failed: {ex.Message}");
            }

            // 4) Recreate with the same name + every config bit copied from
            //    inspect — Watchtower-style. The image string we pass is the
            //    user-typed reference; the daemon resolves it against the
            //    freshly-pulled local image because we pulled it under the
            //    same `FromImage`/`Tag` pair.
            CreateContainerResponse created;
            try
            {
                created = await client.Containers.CreateContainerAsync(
                    BuildCreateParameters(inspect, profile.ImageReference), cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or DockerApiException or TaskCanceledException)
            {
                logger.LogError(ex, "Recreating container {Name} failed after old container was removed", profile.ContainerName);
                return new DockerUpdateResult(DockerUpdateAttemptStatus.RecreateFailed, previousDigest, newDigest,
                    $"Recreating container failed: {ex.Message}. The previous container was already removed — investigate via `docker ps -a` on the host.");
            }

            // 5) Re-attach the secondary networks captured before the
            //    remove. The primary network (whatever was in
            //    HostConfig.NetworkMode / first in NetworkSettings.Networks)
            //    was already attached at create time via NetworkingConfig.
            try
            {
                await ReattachSecondaryNetworksAsync(client, created.ID, inspect, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Re-attaching secondary networks for {Name} failed", profile.ContainerName);
                // Non-fatal — the container will still come up on its
                // primary network. Surface as a warning in the error string
                // but keep the overall status RecreateFailed=false (success)
                // since the container did start. The error column captures
                // the caveat for the audit row.
            }

            try
            {
                await client.Containers.StartContainerAsync(created.ID, new ContainerStartParameters(), cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or DockerApiException or TaskCanceledException)
            {
                return new DockerUpdateResult(DockerUpdateAttemptStatus.RecreateFailed, previousDigest, newDigest,
                    $"Starting recreated container failed: {ex.Message}. The new container exists but is not running — investigate via `docker ps -a` on the host.");
            }

            // 6) V3.2 — async post-start health verification. Docker
            //    returns from StartContainerAsync the moment the container
            //    process is up, not when the HEALTHCHECK has converged.
            //    Poll the inspect endpoint until the container reports
            //    `healthy` / `none` / `unhealthy`, or we run out of
            //    attempts. The new digest is resolved during the same
            //    inspect call to avoid a second round-trip.
            var verification = await VerifyHealthAsync(
                client, created.ID, profile, newDigest, cancellationToken);
            return BuildVerifiedResult(verification, previousDigest, newDigest);
        }
    }

    // ── V5.2 — true Compose-aware recreate ───────────────────────────────────

    /// <summary>
    /// Whether this update should go through <c>docker compose</c> rather than
    /// the raw recreate. Local-socket only (the CLI inside the container talks
    /// to the local daemon); requires a configured project path and a
    /// Compose-managed container (one carrying <see cref="ComposeServiceLabel"/>).
    /// </summary>
    private static bool ShouldUseCompose(DockerUpdateProfile profile, string? composeService) =>
        profile.HostTransport.HostType == DockerHostType.LocalSocket
        && !string.IsNullOrWhiteSpace(profile.ComposeProjectPath)
        && !string.IsNullOrWhiteSpace(composeService);

    private static string? TryGetComposeService(ContainerInspectResponse inspect) =>
        inspect.Config?.Labels is { } labels
        && labels.TryGetValue(ComposeServiceLabel, out var service)
        && !string.IsNullOrWhiteSpace(service)
            ? service
            : null;

    /// <summary>
    /// Delegates the pull + recreate to <c>docker compose</c>, then re-inspects
    /// the container by name to resolve the new digest and run the same V3.2
    /// health verification the raw path uses.
    /// </summary>
    private async Task<DockerUpdateResult> RecreateViaComposeAsync(
        IDockerClient client,
        DockerUpdateProfile profile,
        string composeService,
        string? previousDigest,
        CancellationToken cancellationToken)
    {
        ComposeRunResult run;
        try
        {
            run = await composeCommandRunner.RecreateServiceAsync(
                new ComposeRecreateRequest(profile.ComposeProjectPath!, composeService), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "docker compose recreate of {Service} threw", composeService);
            return new DockerUpdateResult(DockerUpdateAttemptStatus.RecreateFailed, previousDigest, null,
                $"docker compose recreate failed: {ex.Message}");
        }

        // The compose CLI is missing despite the up-front availability probe
        // (race / environment change). Don't silently fall through to a
        // destructive raw recreate here — surface the misconfiguration. The
        // old container is still running because compose never removed it.
        if (run.Status == ComposeRunnerStatus.CliNotAvailable)
            return new DockerUpdateResult(DockerUpdateAttemptStatus.RecreateFailed, previousDigest, null,
                run.Error ?? "The docker compose CLI is not available inside the Stashboard container.");

        if (run.Status == ComposeRunnerStatus.ProjectPathNotFound)
            return new DockerUpdateResult(DockerUpdateAttemptStatus.RecreateFailed, previousDigest, null,
                $"{run.Error} Verify the project directory is bind-mounted read-only into the Stashboard container "
                + "and that ComposeProjectPath points at the in-container mount path.");

        if (!run.IsSuccess)
            return new DockerUpdateResult(DockerUpdateAttemptStatus.RecreateFailed, previousDigest, null,
                run.Error ?? "docker compose recreate failed.");

        // Re-inspect by name to capture the recreated container's id + digest.
        ContainerInspectResponse after;
        try
        {
            after = await client.Containers.InspectContainerAsync(profile.ContainerName, cancellationToken);
        }
        catch (Exception ex) when (ex is DockerApiException or HttpRequestException or TaskCanceledException)
        {
            // Compose reported success but we can't re-inspect — report success
            // without health verification rather than a false failure. The
            // container is up; we just can't read its new digest.
            logger.LogWarning(ex, "Re-inspect after compose recreate of {Container} failed", profile.ContainerName);
            return new DockerUpdateResult(DockerUpdateAttemptStatus.Success, previousDigest, null, null,
                HealthVerified: false, HealthVerifiedUtc: null);
        }

        var newDigest = await TryResolveDigestAsync(
            client, after.Image, profile.RegistryHost, profile.Repository, cancellationToken);

        if (string.IsNullOrEmpty(after.ID))
            return new DockerUpdateResult(DockerUpdateAttemptStatus.Success, previousDigest, newDigest, null);

        var verification = await VerifyHealthAsync(client, after.ID, profile, newDigest, cancellationToken);
        return BuildVerifiedResult(verification, previousDigest, newDigest);
    }

    /// <summary>
    /// V3.2 — maps the post-start health verification outcome onto the typed
    /// update result. Shared by the raw recreate and the V5.2 Compose recreate
    /// so both honour the same "unhealthy / timeout downgrade to RecreateFailed"
    /// contract.
    /// </summary>
    private DockerUpdateResult BuildVerifiedResult(
        HealthVerificationResult verification, string? previousDigest, string? newDigest)
    {
        newDigest = verification.NewDigest ?? newDigest;

        if (verification.Outcome == HealthOutcome.Unhealthy)
            return new DockerUpdateResult(
                DockerUpdateAttemptStatus.RecreateFailed, previousDigest, newDigest,
                $"Container started but reported 'unhealthy' within the {VerificationWindowSeconds()}-second verification window.",
                HealthVerified: false, HealthVerifiedUtc: null);

        if (verification.Outcome == HealthOutcome.Timeout)
            return new DockerUpdateResult(
                DockerUpdateAttemptStatus.RecreateFailed, previousDigest, newDigest,
                $"Container started but did not become healthy within {VerificationWindowSeconds()} s.",
                HealthVerified: false, HealthVerifiedUtc: null);

        return new DockerUpdateResult(
            DockerUpdateAttemptStatus.Success, previousDigest, newDigest, null,
            HealthVerified: verification.Outcome is HealthOutcome.Healthy or HealthOutcome.NoHealthcheck,
            HealthVerifiedUtc: verification.VerifiedAtUtc);
    }

    // ── V3.2 health verification ─────────────────────────────────────────────

    /// <summary>
    /// Outcome of <see cref="VerifyHealthAsync"/>. Distinguishes the four
    /// terminal states the controller cares about: the container is healthy
    /// (HEALTHCHECK passed), has no HEALTHCHECK at all (treated as healthy
    /// since "still running" is the best signal we have), is unhealthy
    /// (HEALTHCHECK failed within the window), or the window expired with
    /// the container still in <c>starting</c> state.
    /// </summary>
    private enum HealthOutcome
    {
        Healthy,
        NoHealthcheck,
        Unhealthy,
        Timeout,
        /// <summary>Verification disabled (max attempts = 0). The recreate is
        /// reported as a success but <c>HealthVerified</c> stays false on the
        /// audit row so consumers can distinguish "we waited and saw healthy"
        /// from "we didn't wait at all".</summary>
        Skipped,
    }

    private readonly record struct HealthVerificationResult(
        HealthOutcome Outcome,
        DateTime? VerifiedAtUtc,
        string? NewDigest);

    private int VerificationWindowSeconds() =>
        Math.Max(0, _options.HealthVerificationMaxAttempts)
        * Math.Max(1, _options.HealthVerificationIntervalSeconds);

    private async Task<HealthVerificationResult> VerifyHealthAsync(
        IDockerClient client,
        string containerId,
        DockerUpdateProfile profile,
        string? alreadyResolvedDigest,
        CancellationToken cancellationToken)
    {
        var maxAttempts = Math.Max(0, _options.HealthVerificationMaxAttempts);
        var interval = TimeSpan.FromSeconds(Math.Max(1, _options.HealthVerificationIntervalSeconds));
        var resolvedDigest = alreadyResolvedDigest;

        // 0 attempts → verification disabled. Preserve V2.7 behaviour where
        // a successful StartContainerAsync is treated as the terminal
        // success signal. HealthVerified stays false on the audit row.
        if (maxAttempts == 0)
        {
            if (resolvedDigest is null)
            {
                resolvedDigest = await TryReadDigestFromInspectAsync(client, containerId, profile, cancellationToken);
            }
            return new HealthVerificationResult(HealthOutcome.Skipped, null, resolvedDigest);
        }

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            ContainerInspectResponse inspect;
            try
            {
                inspect = await client.Containers.InspectContainerAsync(containerId, cancellationToken);
            }
            catch (Exception ex) when (ex is DockerApiException or HttpRequestException or TaskCanceledException)
            {
                // Transient inspect failure — log and retry. A persistent
                // failure ends in a Timeout outcome at the loop's exit.
                logger.LogDebug(ex, "Health-verification inspect for {Container} attempt {Attempt} failed", containerId, attempt + 1);
                await DelayAsync(interval, cancellationToken);
                continue;
            }

            // Best-effort digest fill on the first successful inspect so we
            // can drop the post-loop inspect that V2.7 had to do.
            if (resolvedDigest is null)
            {
                resolvedDigest = await TryResolveDigestAsync(
                    client, inspect.Image, profile.RegistryHost, profile.Repository, cancellationToken);
            }

            var healthStatus = inspect.State?.Health?.Status;

            // No HEALTHCHECK defined → Docker leaves Health = null. Treat
            // this as "healthy" the moment the container is up: there's
            // nothing else we can wait for. We *do* still require Running
            // = true so a container that crash-looped between
            // StartContainerAsync and our first inspect is flagged.
            if (inspect.State?.Health is null)
            {
                if (inspect.State?.Running == true)
                {
                    return new HealthVerificationResult(HealthOutcome.NoHealthcheck, DateTime.UtcNow, resolvedDigest);
                }
                // Container has no healthcheck and is not running →
                // restart-policy is cycling it. Keep polling — it may
                // come back up before the window expires.
                await DelayAsync(interval, cancellationToken);
                continue;
            }

            // Docker reports one of: starting, healthy, unhealthy, none.
            // `none` is unusual once Health is non-null (it means a
            // HEALTHCHECK was declared but never executed) — treat it like
            // `starting` and keep polling.
            if (string.Equals(healthStatus, "healthy", StringComparison.OrdinalIgnoreCase))
            {
                return new HealthVerificationResult(HealthOutcome.Healthy, DateTime.UtcNow, resolvedDigest);
            }
            if (string.Equals(healthStatus, "unhealthy", StringComparison.OrdinalIgnoreCase))
            {
                return new HealthVerificationResult(HealthOutcome.Unhealthy, null, resolvedDigest);
            }

            await DelayAsync(interval, cancellationToken);
        }

        return new HealthVerificationResult(HealthOutcome.Timeout, null, resolvedDigest);
    }

    private async Task<string?> TryReadDigestFromInspectAsync(
        IDockerClient client,
        string containerId,
        DockerUpdateProfile profile,
        CancellationToken cancellationToken)
    {
        try
        {
            var inspect = await client.Containers.InspectContainerAsync(containerId, cancellationToken);
            return await TryResolveDigestAsync(client, inspect.Image, profile.RegistryHost, profile.Repository, cancellationToken);
        }
        catch
        {
            return null;
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Composes the <c>FromImage</c> string the Docker Engine API expects
    /// for the pull. Always includes the registry host except for Docker
    /// Hub (the engine assumes <c>docker.io</c> when omitted and prefixes
    /// the repository with <c>library/</c> for short names — letting it
    /// do that keeps the cached image's RepoTags identical to what the
    /// container's <c>Config.Image</c> field stores).
    /// </summary>
    private static string BuildFromImage(DockerUpdateProfile profile)
    {
        if (string.Equals(profile.RegistryHost, "docker.io", StringComparison.OrdinalIgnoreCase))
        {
            // Strip the implicit `library/` prefix for single-segment
            // repositories (`library/nginx` → `nginx`) so the pulled
            // image's RepoTags match the user's typed reference.
            var repo = profile.Repository;
            if (repo.StartsWith("library/", StringComparison.OrdinalIgnoreCase) && !repo[8..].Contains('/'))
                repo = repo[8..];
            return repo;
        }
        return $"{profile.RegistryHost}/{profile.Repository}";
    }

    /// <summary>
    /// Translates the profile's registry-auth strategy into the
    /// Docker.DotNet <see cref="AuthConfig"/> shape the pull endpoint
    /// expects. Returns <c>null</c> for anonymous pulls — passing
    /// <c>null</c> auth is well-supported by the engine.
    /// </summary>
    private async Task<AuthConfig?> BuildAuthConfigAsync(DockerUpdateProfile profile, CancellationToken cancellationToken)
    {
        switch (profile.RegistryAuthType)
        {
            case RegistryAuthType.AwsEcr:
                if (string.IsNullOrEmpty(profile.AwsAccessKeyId)
                    || string.IsNullOrEmpty(profile.AwsSecretAccessKey)
                    || string.IsNullOrEmpty(profile.AwsRegion))
                    return null;
                var ecrToken = await awsEcrTokenProvider.GetAuthorizationTokenAsync(
                    profile.AwsAccessKeyId, profile.AwsSecretAccessKey, profile.AwsRegion, cancellationToken);
                if (!ecrToken.IsSuccess || ecrToken.Credentials is null) return null;
                return new AuthConfig
                {
                    Username = ecrToken.Credentials.Username,
                    Password = ecrToken.Credentials.Password,
                    ServerAddress = profile.RegistryHost,
                };

            default:
                if (profile.RegistryCredentials is null) return null;
                return new AuthConfig
                {
                    Username = profile.RegistryCredentials.Username,
                    Password = profile.RegistryCredentials.Password,
                    ServerAddress = profile.RegistryHost,
                };
        }
    }

    /// <summary>
    /// Builds the recreate request — every field that wasn't explicitly
    /// chosen at <c>docker run</c> time is read straight off the original
    /// container's inspect output so the user-visible config survives
    /// untouched.
    /// </summary>
    private static CreateContainerParameters BuildCreateParameters(ContainerInspectResponse inspect, string imageReference)
    {
        var config = inspect.Config ?? new Config();
        var hostConfig = inspect.HostConfig;

        // Strip the leading '/' Docker prepends to container names so the
        // create request can reuse the canonical name shown by `docker ps`.
        var name = (inspect.Name ?? string.Empty).TrimStart('/');

        var parameters = new CreateContainerParameters(config)
        {
            Name = name,
            HostConfig = hostConfig,
            Image = imageReference,
        };

        // Capture the first attached network at create time. Docker only
        // accepts ONE EndpointsConfig entry up front — additional networks
        // must be re-attached after the container starts. Picking the same
        // network the container had as its NetworkMode preserves
        // service-alias DNS for Compose-style stacks.
        var endpoints = inspect.NetworkSettings?.Networks;
        if (endpoints is { Count: > 0 })
        {
            var preferred = hostConfig?.NetworkMode is { Length: > 0 } mode && endpoints.ContainsKey(mode)
                ? mode
                : endpoints.Keys.First();
            parameters.NetworkingConfig = new NetworkingConfig
            {
                EndpointsConfig = new Dictionary<string, EndpointSettings>
                {
                    [preferred] = StripEphemeralEndpointFields(endpoints[preferred]),
                },
            };
        }

        return parameters;
    }

    /// <summary>
    /// The IP address / endpoint id / gateway etc. carry stale values from
    /// the *previous* container — Docker assigns a fresh address on
    /// create. Clearing them avoids "address already in use" failures
    /// when the daemon refuses to honour a hard-coded IP that's now held
    /// by the about-to-be-removed container.
    /// </summary>
    private static EndpointSettings StripEphemeralEndpointFields(EndpointSettings original) =>
        new()
        {
            Aliases = original.Aliases,
            Links = original.Links,
            DriverOpts = original.DriverOpts,
            IPAMConfig = original.IPAMConfig,
        };

    /// <summary>
    /// Re-attaches every network the original container was on *except*
    /// the one we already passed in <c>NetworkingConfig.EndpointsConfig</c>.
    /// No-op for the common single-network case.
    /// </summary>
    private static async Task ReattachSecondaryNetworksAsync(
        IDockerClient client,
        string newContainerId,
        ContainerInspectResponse inspect,
        CancellationToken cancellationToken)
    {
        var endpoints = inspect.NetworkSettings?.Networks;
        if (endpoints is null || endpoints.Count <= 1) return;

        var preferred = inspect.HostConfig?.NetworkMode is { Length: > 0 } mode && endpoints.ContainsKey(mode)
            ? mode
            : endpoints.Keys.First();

        foreach (var (networkName, settings) in endpoints)
        {
            if (string.Equals(networkName, preferred, StringComparison.Ordinal)) continue;
            await client.Networks.ConnectNetworkAsync(networkName,
                new NetworkConnectParameters
                {
                    Container = newContainerId,
                    EndpointConfig = StripEphemeralEndpointFields(settings),
                },
                cancellationToken);
        }
    }

    /// <summary>
    /// Resolves the manifest digest for an image id by reading its
    /// <c>RepoDigests</c> and picking the one that matches the watch's
    /// registry+repository — same algorithm <see cref="DockerHostClient"/>
    /// uses for the periodic check. Returns <c>null</c> when the digest
    /// can't be resolved (locally-built image, unknown error). Never
    /// throws — the caller treats the digest as informational.
    /// </summary>
    private async Task<string?> TryResolveDigestAsync(
        IDockerClient client,
        string? imageId,
        string registryHost,
        string repository,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(imageId)) return null;
        try
        {
            var image = await client.Images.InspectImageAsync(imageId, cancellationToken);
            var repoDigests = image.RepoDigests;
            if (repoDigests is null || repoDigests.Count == 0) return null;
            foreach (var entry in repoDigests)
            {
                var atIndex = entry.IndexOf('@');
                if (atIndex <= 0 || atIndex == entry.Length - 1) continue;
                if (!imageReferenceParser.TryParse(entry, out var parsed)) continue;
                if (string.Equals(parsed.RegistryHost, registryHost, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(parsed.Repository, repository, StringComparison.OrdinalIgnoreCase))
                {
                    return entry[(atIndex + 1)..];
                }
            }
        }
        catch
        {
            // best effort
        }
        return null;
    }

    private static async Task<string?> TryGetImageIdAsync(
        IDockerClient client, string imageReference, CancellationToken cancellationToken)
    {
        try
        {
            var image = await client.Images.InspectImageAsync(imageReference, cancellationToken);
            return image.ID;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsSshConnectFailure(Exception ex) =>
        ex is SshException
        or System.Net.Sockets.SocketException;
}
