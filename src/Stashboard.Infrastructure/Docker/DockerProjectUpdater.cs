using Docker.DotNet;
using Microsoft.Extensions.Logging;
using Renci.SshNet.Common;
using Stashboard.Core.Abstractions;
using Stashboard.Core.Enums;

namespace Stashboard.Infrastructure.Docker;

/// <summary>
/// V5.4 — bulk "Update project" orchestrator. Dispatches to one of two paths:
/// <list type="bullet">
/// <item>The local-socket / Compose-aware path: shells out one
/// <c>docker compose pull</c> + <c>docker compose up -d</c> against the
/// project root so Compose honours <c>depends_on</c> ordering itself, then
/// re-inspects each container to write per-service audit rows.</item>
/// <item>The raw fallback path: iterates services in best-effort
/// <c>depends_on</c> order, delegating each to <see cref="IDockerImageUpdater"/>.</item>
/// </list>
/// Never throws — every failure mode lands on a typed
/// <see cref="DockerUpdateAttemptStatus"/> on the per-service result so the
/// controller can persist the audit rows for every attempted service.
/// </summary>
public sealed class DockerProjectUpdater(
    IComposeCommandRunner composeCommandRunner,
    IDockerImageUpdater imageUpdater,
    IDockerClientFactory dockerClientFactory,
    IImageReferenceParser imageReferenceParser,
    ILogger<DockerProjectUpdater> logger) : IDockerProjectUpdater
{
    /// <summary>The standard Compose label every Compose-managed container
    /// carries to declare its inter-service dependencies. Set by Compose v2
    /// since 2022 — a comma-separated list of service names.</summary>
    private const string ComposeDependsOnLabel = "com.docker.compose.depends_on";

    public async Task<DockerProjectUpdateResult> UpdateProjectAsync(
        DockerProjectUpdateProfile profile, CancellationToken cancellationToken = default)
    {
        if (profile.Services.Count == 0)
            return new DockerProjectUpdateResult(
                DockerProjectUpdateMode.Recreate,
                DockerUpdateAttemptStatus.ContainerNotFound,
                $"No containers found for compose project '{profile.ProjectName}'.",
                Array.Empty<DockerProjectServiceResult>());

        var useCompose =
            profile.HostTransport.HostType == DockerHostType.LocalSocket
            && !string.IsNullOrWhiteSpace(profile.ComposeProjectPath)
            && await composeCommandRunner.IsAvailableAsync(cancellationToken);

        return useCompose
            ? await UpdateViaComposeAsync(profile, cancellationToken)
            : await UpdateViaRawRecreateAsync(profile, cancellationToken);
    }

    // ── compose path ─────────────────────────────────────────────────────────

    private async Task<DockerProjectUpdateResult> UpdateViaComposeAsync(
        DockerProjectUpdateProfile profile, CancellationToken cancellationToken)
    {
        // Capture each container's pre-update digest so the per-service audit
        // rows can show before/after even though compose itself doesn't tell
        // us which images changed.
        var previousDigests = await SnapshotDigestsAsync(profile, cancellationToken);

        ComposeRunResult run;
        try
        {
            run = await composeCommandRunner.RecreateProjectAsync(
                new ComposeProjectRecreateRequest(profile.ComposeProjectPath!), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "docker compose project recreate of {Project} threw", profile.ProjectName);
            return BuildAllServicesFailed(profile, previousDigests,
                DockerUpdateAttemptStatus.RecreateFailed,
                $"docker compose recreate failed: {ex.Message}",
                DockerProjectUpdateMode.Compose);
        }

        if (run.Status == ComposeRunnerStatus.CliNotAvailable)
            return BuildAllServicesFailed(profile, previousDigests,
                DockerUpdateAttemptStatus.RecreateFailed,
                run.Error ?? "The docker compose CLI is not available inside the Stashboard container.",
                DockerProjectUpdateMode.Compose);

        if (run.Status == ComposeRunnerStatus.ProjectPathNotFound)
            return BuildAllServicesFailed(profile, previousDigests,
                DockerUpdateAttemptStatus.RecreateFailed,
                $"{run.Error} Verify the project directory is bind-mounted read-only into the Stashboard container "
                + "and that ComposeProjectPath points at the in-container mount path.",
                DockerProjectUpdateMode.Compose);

        if (!run.IsSuccess)
            return BuildAllServicesFailed(profile, previousDigests,
                DockerUpdateAttemptStatus.RecreateFailed,
                run.Error ?? "docker compose recreate failed.",
                DockerProjectUpdateMode.Compose);

        // Re-inspect each service to harvest the new digest. We don't run the
        // full health-verification loop here — compose's own up -d only
        // returns when each container has reached its declared dependency
        // condition, so we trust that and just record the post-state.
        var serviceResults = await SnapshotPostUpdateAsync(profile, previousDigests, cancellationToken);
        var aggregate = serviceResults.Any(r => r.Status != DockerUpdateAttemptStatus.Success)
            ? DockerUpdateAttemptStatus.RecreateFailed
            : DockerUpdateAttemptStatus.Success;

        var aggregateError = aggregate == DockerUpdateAttemptStatus.Success
            ? null
            : $"{serviceResults.Count(r => r.Status != DockerUpdateAttemptStatus.Success)} of {serviceResults.Count} "
              + "services did not report a healthy state after the recreate. See per-service rows for details.";

        return new DockerProjectUpdateResult(DockerProjectUpdateMode.Compose, aggregate, aggregateError, serviceResults);
    }

    private async Task<Dictionary<string, string?>> SnapshotDigestsAsync(
        DockerProjectUpdateProfile profile, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        IDockerClient client;
        try { client = dockerClientFactory.Create(profile.HostTransport.HostType, profile.HostTransport.HostUrl, profile.HostTransport.Tls, profile.HostTransport.Ssh); }
        catch { return result; }

        using (client)
        {
            foreach (var svc in profile.Services)
            {
                try
                {
                    var inspect = await client.Containers.InspectContainerAsync(svc.ContainerName, cancellationToken);
                    result[svc.ContainerName] = await TryResolveDigestAsync(
                        client, inspect.Image, svc.UpdateProfile.RegistryHost, svc.UpdateProfile.Repository, cancellationToken);
                }
                catch
                {
                    result[svc.ContainerName] = null;
                }
            }
        }
        return result;
    }

    private async Task<IReadOnlyList<DockerProjectServiceResult>> SnapshotPostUpdateAsync(
        DockerProjectUpdateProfile profile,
        Dictionary<string, string?> previousDigests,
        CancellationToken cancellationToken)
    {
        var results = new List<DockerProjectServiceResult>(profile.Services.Count);
        IDockerClient client;
        try { client = dockerClientFactory.Create(profile.HostTransport.HostType, profile.HostTransport.HostUrl, profile.HostTransport.Tls, profile.HostTransport.Ssh); }
        catch (Exception ex)
        {
            foreach (var svc in profile.Services)
            {
                results.Add(BuildServiceResult(svc, DockerUpdateAttemptStatus.Success,
                    previousDigests.GetValueOrDefault(svc.ContainerName), null,
                    $"Compose reported success but post-inspect failed: {ex.Message}",
                    healthVerified: false, healthVerifiedUtc: null));
            }
            return results;
        }

        using (client)
        {
            foreach (var svc in profile.Services)
            {
                try
                {
                    var inspect = await client.Containers.InspectContainerAsync(svc.ContainerName, cancellationToken);
                    var newDigest = await TryResolveDigestAsync(
                        client, inspect.Image, svc.UpdateProfile.RegistryHost, svc.UpdateProfile.Repository, cancellationToken);
                    var healthStatus = inspect.State?.Health?.Status?.ToLowerInvariant();
                    var running = inspect.State?.Running == true;

                    // The container is considered healthy if it's running and
                    // either has no HEALTHCHECK (Health is null) or reports
                    // "healthy". An "unhealthy" or stopped container becomes
                    // a per-service RecreateFailed even though compose itself
                    // reported success — the bulk aggregate then downgrades.
                    var ok = running && healthStatus is null or "healthy";
                    var status = ok ? DockerUpdateAttemptStatus.Success : DockerUpdateAttemptStatus.RecreateFailed;
                    var error = ok
                        ? null
                        : !running
                            ? "Container is not running after the project recreate."
                            : $"Container reported '{healthStatus}' state after the project recreate.";

                    results.Add(BuildServiceResult(svc, status,
                        previousDigests.GetValueOrDefault(svc.ContainerName), newDigest, error,
                        healthVerified: ok && healthStatus is "healthy" or null,
                        healthVerifiedUtc: ok ? DateTime.UtcNow : null));
                }
                catch (DockerContainerNotFoundException)
                {
                    results.Add(BuildServiceResult(svc, DockerUpdateAttemptStatus.ContainerNotFound,
                        previousDigests.GetValueOrDefault(svc.ContainerName), null,
                        "Container no longer exists after the project recreate.",
                        healthVerified: false, healthVerifiedUtc: null));
                }
                catch (Exception ex)
                {
                    results.Add(BuildServiceResult(svc, DockerUpdateAttemptStatus.RecreateFailed,
                        previousDigests.GetValueOrDefault(svc.ContainerName), null,
                        $"Post-recreate inspect failed: {ex.Message}",
                        healthVerified: false, healthVerifiedUtc: null));
                }
            }
        }
        return results;
    }

    // ── raw recreate path ────────────────────────────────────────────────────

    private async Task<DockerProjectUpdateResult> UpdateViaRawRecreateAsync(
        DockerProjectUpdateProfile profile, CancellationToken cancellationToken)
    {
        var ordered = OrderByDependsOn(profile.Services);
        var results = new List<DockerProjectServiceResult>(ordered.Count);

        foreach (var svc in ordered)
        {
            DockerUpdateResult update;
            try
            {
                update = await imageUpdater.UpdateAsync(svc.UpdateProfile, cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or DockerApiException or SshException)
            {
                update = new DockerUpdateResult(DockerUpdateAttemptStatus.HostUnreachable, null, null,
                    $"Docker host unreachable while updating {svc.ServiceName}: {ex.Message}");
            }

            results.Add(BuildServiceResult(svc, update.Status, update.PreviousDigest, update.NewDigest,
                update.Error, update.HealthVerified, update.HealthVerifiedUtc));
        }

        var aggregate = results.All(r => r.Status == DockerUpdateAttemptStatus.Success)
            ? DockerUpdateAttemptStatus.Success
            : DockerUpdateAttemptStatus.RecreateFailed;
        var aggregateError = aggregate == DockerUpdateAttemptStatus.Success
            ? null
            : $"{results.Count(r => r.Status != DockerUpdateAttemptStatus.Success)} of {results.Count} "
              + "services failed to update. See per-service rows for details.";

        return new DockerProjectUpdateResult(DockerProjectUpdateMode.Recreate, aggregate, aggregateError, results);
    }

    /// <summary>
    /// Best-effort topological order of services by their declared
    /// <c>com.docker.compose.depends_on</c> label (Compose v2 writes this on
    /// every container). Services without a depends_on entry land first; a
    /// cyclic graph falls back to input order.
    /// </summary>
    internal static IReadOnlyList<DockerProjectServiceTarget> OrderByDependsOn(
        IReadOnlyList<DockerProjectServiceTarget> services)
    {
        // Build name → target. Service name comparison is case-sensitive per
        // Compose's own behaviour but missing entries are tolerated.
        var byName = services.ToDictionary(s => s.ServiceName, StringComparer.Ordinal);
        var deps = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var svc in services)
        {
            svc.Labels.TryGetValue(ComposeDependsOnLabel, out var raw);
            var set = new HashSet<string>(StringComparer.Ordinal);
            if (!string.IsNullOrWhiteSpace(raw))
            {
                foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    // Compose writes "service:condition:restart". We only
                    // care about the service token.
                    var name = part.Split(':', 2, StringSplitOptions.RemoveEmptyEntries)[0].Trim();
                    if (byName.ContainsKey(name)) set.Add(name);
                }
            }
            deps[svc.ServiceName] = set;
        }

        var visited = new HashSet<string>(StringComparer.Ordinal);
        var stack = new HashSet<string>(StringComparer.Ordinal);
        var order = new List<DockerProjectServiceTarget>(services.Count);

        void Visit(string name)
        {
            if (!visited.Add(name)) return;
            if (!stack.Add(name)) return; // cycle guard
            try
            {
                foreach (var dep in deps[name])
                    Visit(dep);
                order.Add(byName[name]);
            }
            finally
            {
                stack.Remove(name);
            }
        }

        foreach (var svc in services) Visit(svc.ServiceName);

        // If the visit produced a different count (shouldn't happen) fall
        // back to the input order so we still process every service.
        return order.Count == services.Count ? order : services;
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private DockerProjectUpdateResult BuildAllServicesFailed(
        DockerProjectUpdateProfile profile,
        IReadOnlyDictionary<string, string?> previousDigests,
        DockerUpdateAttemptStatus status,
        string error,
        DockerProjectUpdateMode mode)
    {
        var results = profile.Services
            .Select(svc => BuildServiceResult(svc, status,
                previousDigests.GetValueOrDefault(svc.ContainerName), null, error,
                healthVerified: false, healthVerifiedUtc: null))
            .ToList();
        return new DockerProjectUpdateResult(mode, status, error, results);
    }

    private static DockerProjectServiceResult BuildServiceResult(
        DockerProjectServiceTarget svc,
        DockerUpdateAttemptStatus status,
        string? previousDigest,
        string? newDigest,
        string? error,
        bool healthVerified,
        DateTime? healthVerifiedUtc) =>
        new(
            svc.ServiceName,
            svc.ContainerName,
            svc.WatchId,
            svc.WebResourceId,
            svc.UpdateProfile.ImageReference,
            status,
            previousDigest,
            newDigest,
            error,
            healthVerified,
            healthVerifiedUtc);

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
            // best-effort
        }
        return null;
    }
}
