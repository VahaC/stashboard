using System.Net;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Logging;
using Renci.SshNet.Common;
using Stashboard.Core.Abstractions;
using Stashboard.Core.Enums;
using Stashboard.Infrastructure.Docker.Ssh;

namespace Stashboard.Infrastructure.Docker;

/// <summary>
/// Talks to a local or remote Docker daemon via the Docker.DotNet client.
/// </summary>
/// <remarks>
/// Algorithm for <see cref="GetCurrentImageDigestAsync"/> mirrors how
/// Watchtower / Diun do the same job:
/// 1. ContainerInspect by name -> get the local image ID.
/// 2. ImageInspect by that image ID -> get its <c>RepoDigests</c> list.
/// 3. Find the RepoDigest whose registry+repository matches the watch's parsed
///    <c>RegistryHost</c>/<c>Repository</c>. That digest is the manifest digest
///    of the image the container is currently running.
///
/// V2.5 — host transport parameters now travel in a single
/// <see cref="DockerHostTransport"/> bundle so SSH credentials can ride along
/// without growing every method signature.
/// </remarks>
public sealed class DockerHostClient(
    IDockerClientFactory dockerClientFactory,
    IImageReferenceParser imageReferenceParser,
    ILogger<DockerHostClient> logger) : IDockerHostClient
{
    public async Task<DockerHostResult> GetCurrentImageDigestAsync(
        DockerHostTransport transport,
        string containerName,
        string registryHost,
        string repository,
        CancellationToken cancellationToken = default)
    {
        IDockerClient client;
        try
        {
            client = CreateClient(transport);
        }
        catch (NotSupportedException ex)
        {
            return new DockerHostResult(DockerHostStatus.UnsupportedHostType, null, null, null, ex.Message);
        }
        catch (Exception ex) when (IsSshConnectFailure(ex))
        {
            logger.LogWarning(ex, "SSH connection failed for Docker host: {Host}", transport.Ssh?.Host);
            return new DockerHostResult(DockerHostStatus.HostUnreachable, null, null, null,
                $"SSH connection failed: {ex.Message}");
        }

        using (client)
        {
            try
            {
                var container = await InspectContainerSafelyAsync(client, containerName, cancellationToken);
                if (container.Result is not null) return container.Result;
                var imageId = container.Inspect!.Image;

                var image = await InspectImageSafelyAsync(client, imageId, cancellationToken);
                if (image.Result is not null) return image.Result with { ImageId = imageId };

                var match = FindMatchingRepoDigest(image.Inspect!.RepoDigests, registryHost, repository);
                if (match is null)
                {
                    var seen = image.Inspect.RepoDigests is { Count: > 0 }
                        ? string.Join(", ", image.Inspect.RepoDigests)
                        : "(none)";
                    return new DockerHostResult(DockerHostStatus.NoMatchingRepoDigest, null, null, imageId,
                        $"Image has no RepoDigest for {registryHost}/{repository}. Host reported: {seen}.");
                }

                return new DockerHostResult(DockerHostStatus.Ok, match.Value.Digest, match.Value.RepoDigest, imageId, null);
            }
            catch (HttpRequestException ex)
            {
                logger.LogWarning(ex, "Docker host unreachable: {Host}", DescribeHost(transport));
                return new DockerHostResult(DockerHostStatus.HostUnreachable, null, null, null, DescribeError(ex));
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new DockerHostResult(DockerHostStatus.HostUnreachable, null, null, null,
                    "Docker host request timed out.");
            }
        }
    }

    public async Task<DockerHostPingResult> PingAsync(
        DockerHostTransport transport,
        CancellationToken cancellationToken = default)
    {
        IDockerClient client;
        try
        {
            client = CreateClient(transport);
        }
        catch (Exception ex) when (ex is NotSupportedException or ArgumentException)
        {
            return new DockerHostPingResult(false, ex.Message);
        }
        catch (Exception ex) when (IsSshConnectFailure(ex))
        {
            return new DockerHostPingResult(false, $"SSH connection failed: {ex.Message}");
        }

        using (client)
        {
            try
            {
                await client.System.PingAsync(cancellationToken);
                return new DockerHostPingResult(true, null);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or DockerApiException)
            {
                return new DockerHostPingResult(false, $"Docker host unreachable: {DescribeError(ex)}");
            }
        }
    }

    public async Task<DockerHostConnectionResult> TestContainerAsync(
        DockerHostTransport transport,
        string containerName,
        CancellationToken cancellationToken = default)
    {
        IDockerClient client;
        try
        {
            client = CreateClient(transport);
        }
        catch (Exception ex) when (ex is NotSupportedException or ArgumentException)
        {
            return new DockerHostConnectionResult(false, false, ex.Message);
        }
        catch (Exception ex) when (IsSshConnectFailure(ex))
        {
            return new DockerHostConnectionResult(false, false, $"SSH connection failed: {ex.Message}");
        }

        using (client)
        {
            try
            {
                await client.System.PingAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or DockerApiException)
            {
                return new DockerHostConnectionResult(false, false,
                    $"Docker host unreachable: {DescribeError(ex)}");
            }

            try
            {
                await client.Containers.InspectContainerAsync(containerName, cancellationToken);
                return new DockerHostConnectionResult(true, true, null);
            }
            catch (DockerContainerNotFoundException)
            {
                return new DockerHostConnectionResult(true, false,
                    $"Container '{containerName}' not found on the host.");
            }
            catch (HttpRequestException ex)
            {
                return new DockerHostConnectionResult(false, false, DescribeError(ex));
            }
        }
    }

    public async Task<IReadOnlyList<DockerContainerSummary>> ListContainersAsync(
        DockerHostTransport transport,
        CancellationToken cancellationToken = default)
    {
        var client = CreateClient(transport);
        using (client)
        {
            var listed = await client.Containers.ListContainersAsync(
                new ContainersListParameters { All = true },
                cancellationToken);

            // Names come back with a leading '/'; strip it so the UI shows the
            // canonical name (matching what `docker ps` displays).
            return listed
                .Select(c => new DockerContainerSummary(
                    Name: NormalizeName(c.Names),
                    Image: c.Image ?? string.Empty,
                    ImageId: c.ImageID ?? string.Empty,
                    State: c.State ?? string.Empty,
                    Status: c.Status ?? string.Empty,
                    Labels: (IReadOnlyDictionary<string, string>)(c.Labels ?? new Dictionary<string, string>())))
                .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    public async Task<DockerUpdateCommandPlan?> GenerateUpdateCommandAsync(
        DockerHostTransport transport,
        string containerName,
        CancellationToken cancellationToken = default)
    {
        var client = CreateClient(transport);
        using (client)
        {
            ContainerInspectResponse inspect;
            try
            {
                inspect = await client.Containers.InspectContainerAsync(containerName, cancellationToken);
            }
            catch (DockerContainerNotFoundException) { return null; }

            var labels = inspect.Config?.Labels ?? new Dictionary<string, string>();
            labels.TryGetValue("com.docker.compose.service", out var composeService);
            labels.TryGetValue("com.docker.compose.project.config_files", out var composeConfigFiles);

            // Compose-managed container: emit the canonical `docker compose pull`
            // + `up -d` pair, scoped to the first compose file we know about so the
            // command works without the user cd'ing into the project directory.
            if (!string.IsNullOrEmpty(composeService))
            {
                var firstConfig = composeConfigFiles?.Split(',', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
                var fileFlag = string.IsNullOrEmpty(firstConfig) ? string.Empty : $"-f {ShellQuote(firstConfig)} ";
                var cmd = $"docker compose {fileFlag}pull {composeService} && docker compose {fileFlag}up -d {composeService}";
                return new DockerUpdateCommandPlan(cmd, "compose", null);
            }

            // Plain container: we can't faithfully reconstruct the full `docker run`
            // from the inspect data, so emit a minimal pull-and-recreate skeleton
            // and warn the user that they need to fill in the volumes/env/network
            // flags from their original launch command.
            var image = inspect.Config?.Image ?? inspect.Image ?? "image:tag";
            var name = (inspect.Name ?? containerName).TrimStart('/');
            var fallback = $"docker pull {image} && docker stop {name} && docker rm {name} && docker run -d --name {name} {image}";
            return new DockerUpdateCommandPlan(
                fallback,
                "fallback",
                "This container isn't managed by docker compose. The command above is a skeleton — re-add your original --volume, --env, --network, --port and --restart flags before running it.");
        }
    }

    public async Task<DockerContainerInspectResult> InspectContainerAsync(
        DockerHostTransport transport,
        string containerName,
        CancellationToken cancellationToken = default)
    {
        IDockerClient client;
        try
        {
            client = CreateClient(transport);
        }
        catch (NotSupportedException ex)
        {
            return new DockerContainerInspectResult(DockerHostStatus.UnsupportedHostType, null, ex.Message);
        }
        catch (Exception ex) when (IsSshConnectFailure(ex))
        {
            logger.LogWarning(ex, "SSH connection failed for Docker host: {Host}", transport.Ssh?.Host);
            return new DockerContainerInspectResult(DockerHostStatus.HostUnreachable, null,
                $"SSH connection failed: {ex.Message}");
        }

        using (client)
        {
            try
            {
                var inspect = await client.Containers.InspectContainerAsync(containerName, cancellationToken);

                // Best-effort fetch of RepoDigests for the resolved image id.
                // Failure here is non-fatal — the inspect data is still useful
                // without it (e.g. for locally-built images that have no
                // RepoDigests at all).
                IReadOnlyList<string> repoDigests = Array.Empty<string>();
                if (!string.IsNullOrEmpty(inspect.Image))
                {
                    try
                    {
                        var image = await client.Images.InspectImageAsync(inspect.Image, cancellationToken);
                        if (image.RepoDigests is { Count: > 0 })
                            repoDigests = image.RepoDigests.ToArray();
                    }
                    catch (DockerImageNotFoundException) { /* leave empty */ }
                    catch (DockerApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound) { /* leave empty */ }
                }

                var dto = DockerInspectMapper.Map(inspect, repoDigests);
                return new DockerContainerInspectResult(DockerHostStatus.Ok, dto, null);
            }
            catch (DockerContainerNotFoundException)
            {
                return new DockerContainerInspectResult(DockerHostStatus.ContainerNotFound, null,
                    $"Container '{containerName}' not found on the configured Docker host.");
            }
            catch (DockerApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                return new DockerContainerInspectResult(DockerHostStatus.ContainerNotFound, null,
                    $"Container '{containerName}' not found on the configured Docker host.");
            }
            catch (HttpRequestException ex)
            {
                logger.LogWarning(ex, "Docker host unreachable: {Host}", DescribeHost(transport));
                return new DockerContainerInspectResult(DockerHostStatus.HostUnreachable, null, DescribeError(ex));
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new DockerContainerInspectResult(DockerHostStatus.HostUnreachable, null,
                    "Docker host request timed out.");
            }
        }
    }

    // ── V3.5 — instances page (list + lifecycle ops) ─────────────────────────

    public async Task<IReadOnlyList<DockerContainerDetail>> ListContainerDetailsAsync(
        DockerHostTransport transport,
        CancellationToken cancellationToken = default)
    {
        var client = CreateClient(transport);
        using (client)
        {
            var listed = await client.Containers.ListContainersAsync(
                new ContainersListParameters { All = true },
                cancellationToken);

            return listed
                .Select(c =>
                {
                    var labels = c.Labels ?? new Dictionary<string, string>();
                    labels.TryGetValue("com.docker.compose.project", out var project);
                    labels.TryGetValue("com.docker.compose.service", out var service);

                    var ports = (c.Ports ?? Array.Empty<Port>())
                        .Select(p => new DockerContainerPort(
                            PrivatePort: (int)p.PrivatePort,
                            PublicPort: p.PublicPort > 0 ? (int)p.PublicPort : null,
                            Type: p.Type ?? "tcp",
                            Ip: string.IsNullOrEmpty(p.IP) ? null : p.IP))
                        .ToList();

                    var createdUtc = c.Created == default
                        ? (DateTime?)null
                        : DateTime.SpecifyKind(c.Created.ToUniversalTime(), DateTimeKind.Utc);

                    return new DockerContainerDetail(
                        Id: c.ID ?? string.Empty,
                        Name: NormalizeName(c.Names),
                        Image: c.Image ?? string.Empty,
                        ImageId: c.ImageID ?? string.Empty,
                        State: c.State ?? string.Empty,
                        Status: c.Status ?? string.Empty,
                        CreatedUtc: createdUtc,
                        Ports: ports,
                        ComposeProject: project,
                        ComposeService: service,
                        Labels: (IReadOnlyDictionary<string, string>)labels);
                })
                .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    public async Task<DockerResourceAllocation> GetResourceAllocationAsync(
        DockerHostTransport transport,
        string? excludeComposeProject = null,
        CancellationToken cancellationToken = default)
    {
        var client = CreateClient(transport);
        using (client)
        {
            // Running containers only — a stopped container reserves no live capacity.
            var listed = await client.Containers.ListContainersAsync(
                new ContainersListParameters { All = false }, cancellationToken);

            long reservedNanoCpus = 0;
            long reservedMemory = 0;
            var count = 0;
            foreach (var c in listed)
            {
                if (string.IsNullOrEmpty(c.ID)) continue;
                if (excludeComposeProject is not null
                    && c.Labels is not null
                    && c.Labels.TryGetValue("com.docker.compose.project", out var proj)
                    && string.Equals(proj, excludeComposeProject, StringComparison.Ordinal))
                    continue;
                try
                {
                    var inspect = await client.Containers.InspectContainerAsync(c.ID, cancellationToken);
                    if (inspect.HostConfig is null) continue;
                    count++;
                    reservedNanoCpus += inspect.HostConfig.NanoCPUs;
                    reservedMemory += inspect.HostConfig.Memory;
                }
                catch (DockerApiException)
                {
                    // Container vanished between list and inspect — ignore.
                }
            }

            return new DockerResourceAllocation(
                ContainerCount: count,
                ReservedCpus: reservedNanoCpus / 1_000_000_000d,
                ReservedMemoryBytes: reservedMemory);
        }
    }

    public Task<DockerContainerActionResult> StartContainerAsync(
        DockerHostTransport transport, string containerName, CancellationToken cancellationToken = default) =>
        ExecuteActionAsync(transport, containerName, "Start", async (client, id, ct) =>
        {
            await client.Containers.StartContainerAsync(id, new ContainerStartParameters(), ct);
        }, cancellationToken);

    public Task<DockerContainerActionResult> StopContainerAsync(
        DockerHostTransport transport, string containerName, int waitSeconds = 10,
        CancellationToken cancellationToken = default) =>
        ExecuteActionAsync(transport, containerName, "Stop", async (client, id, ct) =>
        {
            await client.Containers.StopContainerAsync(id,
                new ContainerStopParameters { WaitBeforeKillSeconds = (uint)Math.Max(0, waitSeconds) },
                ct);
        }, cancellationToken);

    public Task<DockerContainerActionResult> RestartContainerAsync(
        DockerHostTransport transport, string containerName, int waitSeconds = 10,
        CancellationToken cancellationToken = default) =>
        ExecuteActionAsync(transport, containerName, "Restart", async (client, id, ct) =>
        {
            await client.Containers.RestartContainerAsync(id,
                new ContainerRestartParameters { WaitBeforeKillSeconds = (uint)Math.Max(0, waitSeconds) },
                ct);
        }, cancellationToken);

    public Task<DockerContainerActionResult> RemoveContainerAsync(
        DockerHostTransport transport, string containerName, bool force = false,
        CancellationToken cancellationToken = default) =>
        ExecuteActionAsync(transport, containerName, "Remove", async (client, id, ct) =>
        {
            await client.Containers.RemoveContainerAsync(id,
                new ContainerRemoveParameters { Force = force },
                ct);
        }, cancellationToken);

    public async Task<DockerContainerActionResult> DeleteHostDirectoryAsync(
        DockerHostTransport transport, string path, CancellationToken cancellationToken = default)
    {
        if (transport.Ssh is null)
            return new DockerContainerActionResult(DockerHostStatus.UnsupportedHostType, null,
                "Deleting a host directory is only supported for SSH-connected Docker hosts.");

        var script = $"rm -rf -- {SshCommandExecutor.QuoteForShell(path)}";
        try
        {
            var outcome = await SshCommandExecutor.RunAsync(
                transport.Ssh, script, cancellationToken, TimeSpan.FromSeconds(30));
            if (outcome.ExitStatus == 0)
                return new DockerContainerActionResult(DockerHostStatus.Ok, null, null);

            logger.LogWarning("rm -rf on {Path} exited {Status}: {Stderr}", path, outcome.ExitStatus, outcome.Stderr);
            return new DockerContainerActionResult(DockerHostStatus.ActionFailed, null,
                string.IsNullOrWhiteSpace(outcome.Stderr)
                    ? $"Deleting '{path}' on the host failed (exit {outcome.ExitStatus})."
                    : outcome.Stderr.Trim());
        }
        catch (Exception ex) when (IsSshConnectFailure(ex))
        {
            logger.LogWarning(ex, "SSH connection failed deleting host directory: {Path}", path);
            return new DockerContainerActionResult(DockerHostStatus.HostUnreachable, null,
                $"SSH connection failed: {ex.Message}");
        }
    }

    /// <summary>
    /// V3.5 — shared lifecycle action pipeline. Resolves
    /// <paramref name="containerName"/> to its current container id (so
    /// the caller's audit log captures the id even if the user re-renames
    /// the container between rows), then invokes the per-action callback.
    /// All exception paths translate into a <see cref="DockerContainerActionResult"/>
    /// — the controller never sees a raw Docker.DotNet exception.
    /// </summary>
    private async Task<DockerContainerActionResult> ExecuteActionAsync(
        DockerHostTransport transport,
        string containerName,
        string actionName,
        Func<IDockerClient, string, CancellationToken, Task> action,
        CancellationToken cancellationToken)
    {
        IDockerClient client;
        try
        {
            client = CreateClient(transport);
        }
        catch (NotSupportedException ex)
        {
            return new DockerContainerActionResult(DockerHostStatus.UnsupportedHostType, null, ex.Message);
        }
        catch (Exception ex) when (IsSshConnectFailure(ex))
        {
            logger.LogWarning(ex, "SSH connection failed for Docker host: {Host}", transport.Ssh?.Host);
            return new DockerContainerActionResult(DockerHostStatus.HostUnreachable, null,
                $"SSH connection failed: {ex.Message}");
        }

        using (client)
        {
            string containerId;
            try
            {
                var inspect = await client.Containers.InspectContainerAsync(containerName, cancellationToken);
                containerId = inspect.ID ?? containerName;
            }
            catch (DockerContainerNotFoundException)
            {
                return new DockerContainerActionResult(DockerHostStatus.ContainerNotFound, null,
                    $"Container '{containerName}' not found on the configured Docker host.");
            }
            catch (DockerApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return new DockerContainerActionResult(DockerHostStatus.ContainerNotFound, null,
                    $"Container '{containerName}' not found on the configured Docker host.");
            }
            catch (HttpRequestException ex)
            {
                logger.LogWarning(ex, "Docker host unreachable on {Action} {Container}", actionName, containerName);
                return new DockerContainerActionResult(DockerHostStatus.HostUnreachable, null, DescribeError(ex));
            }

            try
            {
                await action(client, containerId, cancellationToken);
                return new DockerContainerActionResult(DockerHostStatus.Ok, containerId, null);
            }
            catch (DockerContainerNotFoundException)
            {
                return new DockerContainerActionResult(DockerHostStatus.ContainerNotFound, containerId,
                    $"Container '{containerName}' disappeared during {actionName.ToLowerInvariant()}.");
            }
            catch (DockerApiException ex)
            {
                logger.LogWarning(ex, "Docker daemon rejected {Action} on {Container}", actionName, containerName);
                // The daemon returns 304 for Start on an already-running container
                // (and Stop on an already-stopped one). Treat as success — the
                // desired state was already in place.
                if (ex.StatusCode == System.Net.HttpStatusCode.NotModified)
                    return new DockerContainerActionResult(DockerHostStatus.Ok, containerId, null);
                return new DockerContainerActionResult(DockerHostStatus.HostUnreachable, containerId,
                    $"Docker daemon error: {ex.Message}");
            }
            catch (HttpRequestException ex)
            {
                logger.LogWarning(ex, "Docker host unreachable on {Action} {Container}", actionName, containerName);
                return new DockerContainerActionResult(DockerHostStatus.HostUnreachable, containerId, DescribeError(ex));
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new DockerContainerActionResult(DockerHostStatus.HostUnreachable, containerId,
                    $"Docker host {actionName} timed out.");
            }
        }
    }

    // ── V5.5 — image storage summary + prune ────────────────────────────────

    public async Task<DockerImageStorageResult> GetImageStorageAsync(
        DockerHostTransport transport,
        CancellationToken cancellationToken = default)
    {
        IDockerClient client;
        try
        {
            client = CreateClient(transport);
        }
        catch (NotSupportedException ex)
        {
            return new DockerImageStorageResult(DockerHostStatus.UnsupportedHostType, 0, 0, 0, 0, 0, [], ex.Message);
        }
        catch (Exception ex) when (IsSshConnectFailure(ex))
        {
            return new DockerImageStorageResult(DockerHostStatus.HostUnreachable, 0, 0, 0, 0, 0, [],
                $"SSH connection failed: {ex.Message}");
        }

        using (client)
        {
            try
            {
                var images = await client.Images.ListImagesAsync(
                    new ImagesListParameters { All = false }, cancellationToken);

                // "Unused" needs the container list because the Engine's image
                // listing alone can't tell us which images still have a
                // container hanging off them. Includes stopped containers so we
                // never delete a "rollback-to-previous-tag" target.
                var containers = await client.Containers.ListContainersAsync(
                    new ContainersListParameters { All = true }, cancellationToken);

                // Map every image id to the names of the containers using it.
                // Docker's prune never deletes an image referenced by a
                // container (running OR stopped), so this both flags "unused"
                // and tells the user which container to remove to free a
                // dangling-but-in-use image.
                var containersByImageId = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var c in containers)
                {
                    var imageId = c.ImageID ?? string.Empty;
                    if (string.IsNullOrEmpty(imageId)) continue;
                    if (!containersByImageId.TryGetValue(imageId, out var names))
                        containersByImageId[imageId] = names = [];
                    names.Add(NormalizeName(c.Names));
                }

                // Build one summary per image, flagged dangling / unused, then
                // derive every count + byte total from that single pass so the
                // numbers and the drill-down list can never disagree.
                var summaries = images
                    .Select(i =>
                    {
                        // "Dangling" follows the Docker definition: the only
                        // RepoTags value the daemon writes is <none>:<none>.
                        // Some daemons return a null RepoTags list for the same
                        // case, so treat that as dangling too.
                        var isDangling = IsDangling(i);
                        var usedBy = (!string.IsNullOrEmpty(i.ID)
                            && containersByImageId.TryGetValue(i.ID, out var names))
                            ? names
                            : [];
                        var isUnused = usedBy.Count == 0 && !string.IsNullOrEmpty(i.ID);
                        var tags = (i.RepoTags ?? [])
                            .Where(t => !string.IsNullOrEmpty(t) && t != "<none>:<none>")
                            .ToList();
                        var createdUtc = i.Created == default
                            ? (DateTime?)null
                            : DateTime.SpecifyKind(i.Created.ToUniversalTime(), DateTimeKind.Utc);
                        return new DockerImageSummary(
                            Id: i.ID ?? string.Empty,
                            RepoTags: tags,
                            SizeBytes: i.Size,
                            CreatedUtc: createdUtc,
                            IsDangling: isDangling,
                            IsUnused: isUnused,
                            UsedByContainers: usedBy);
                    })
                    .OrderByDescending(s => s.SizeBytes)
                    .ToList();

                return new DockerImageStorageResult(
                    DockerHostStatus.Ok,
                    TotalImageCount: summaries.Count,
                    DanglingImageCount: summaries.Count(s => s.IsDangling),
                    DanglingImageBytes: summaries.Where(s => s.IsDangling).Sum(s => s.SizeBytes),
                    UnusedImageCount: summaries.Count(s => s.IsUnused),
                    UnusedImageBytes: summaries.Where(s => s.IsUnused).Sum(s => s.SizeBytes),
                    Images: summaries,
                    Error: null);
            }
            catch (HttpRequestException ex)
            {
                logger.LogWarning(ex, "Docker host unreachable: {Host}", DescribeHost(transport));
                return new DockerImageStorageResult(DockerHostStatus.HostUnreachable, 0, 0, 0, 0, 0, [], DescribeError(ex));
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new DockerImageStorageResult(DockerHostStatus.HostUnreachable, 0, 0, 0, 0, 0, [],
                    "Docker host request timed out.");
            }
        }
    }

    public async Task<DockerImagePruneResult> PruneImagesAsync(
        DockerHostTransport transport,
        bool includeUnused,
        CancellationToken cancellationToken = default)
    {
        IDockerClient client;
        try
        {
            client = CreateClient(transport);
        }
        catch (NotSupportedException ex)
        {
            return new DockerImagePruneResult(DockerHostStatus.UnsupportedHostType, 0, 0, ex.Message);
        }
        catch (Exception ex) when (IsSshConnectFailure(ex))
        {
            return new DockerImagePruneResult(DockerHostStatus.HostUnreachable, 0, 0,
                $"SSH connection failed: {ex.Message}");
        }

        using (client)
        {
            try
            {
                // dangling=true → standard <none>:<none> images only.
                // dangling=false → also remove any image not referenced by a
                // container (the `docker image prune -a` equivalent). Docker
                // applies the "unreferenced" filter implicitly server-side.
                var filters = new Dictionary<string, IDictionary<string, bool>>
                {
                    ["dangling"] = new Dictionary<string, bool> { [includeUnused ? "false" : "true"] = true },
                };
                var parameters = new ImagesPruneParameters { Filters = filters };
                var response = await client.Images.PruneImagesAsync(parameters, cancellationToken);

                var deletedCount = response.ImagesDeleted?.Count ?? 0;
                var reclaimed = response.SpaceReclaimed;
                return new DockerImagePruneResult(DockerHostStatus.Ok, deletedCount, (long)reclaimed, null);
            }
            catch (DockerApiException ex)
            {
                logger.LogWarning(ex, "Docker daemon rejected prune");
                return new DockerImagePruneResult(DockerHostStatus.HostUnreachable, 0, 0,
                    $"Docker daemon error: {ex.Message}");
            }
            catch (HttpRequestException ex)
            {
                logger.LogWarning(ex, "Docker host unreachable: {Host}", DescribeHost(transport));
                return new DockerImagePruneResult(DockerHostStatus.HostUnreachable, 0, 0, DescribeError(ex));
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new DockerImagePruneResult(DockerHostStatus.HostUnreachable, 0, 0,
                    "Docker host request timed out.");
            }
        }
    }

    private static bool IsDangling(ImagesListResponse image)
    {
        if (image.RepoTags is null || image.RepoTags.Count == 0) return true;
        return image.RepoTags.All(t => string.IsNullOrEmpty(t) || t == "<none>:<none>");
    }

    public async Task<IReadOnlyList<DockerNetworkSummary>> ListNetworksAsync(
        DockerHostTransport transport, CancellationToken cancellationToken = default)
    {
        var client = CreateClient(transport);
        using (client)
        {
            var networks = await client.Networks.ListNetworksAsync(
                new NetworksListParameters(), cancellationToken);
            return networks
                .Select(n => new DockerNetworkSummary(
                    Name: n.Name ?? string.Empty,
                    Driver: string.IsNullOrEmpty(n.Driver) ? null : n.Driver,
                    Subnets: (n.IPAM?.Config ?? new List<IPAMConfig>())
                        .Select(c => c.Subnet)
                        .Where(s => !string.IsNullOrWhiteSpace(s))
                        .Select(s => s!)
                        .ToList()))
                .Where(n => n.Name.Length > 0)
                .OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    public async Task<IReadOnlyList<DockerVolumeUsage>> GetVolumeUsageAsync(
        DockerHostTransport transport, CancellationToken cancellationToken = default)
    {
        // Docker.DotNet 3.x exposes no /system/df, so this issues a raw GET over
        // the same endpoint the client dials (the SSH tunnel's local port for SSH
        // hosts, the unix socket for local). Best-effort: any failure (TCP+TLS
        // https, older daemon, transport error) yields an empty list — the editor
        // simply shows no size rather than erroring.
        IDockerClient client;
        try
        {
            client = CreateClient(transport);
        }
        catch (Exception ex) when (ex is NotSupportedException || IsSshConnectFailure(ex))
        {
            return Array.Empty<DockerVolumeUsage>();
        }

        using (client)
        {
            using var http = CreateRawEngineHttpClient(client.Configuration.EndpointBaseUri);
            if (http is null) return Array.Empty<DockerVolumeUsage>();
            try
            {
                using var response = await http.GetAsync("/system/df", cancellationToken);
                if (!response.IsSuccessStatusCode) return Array.Empty<DockerVolumeUsage>();
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                return await ParseVolumeUsageAsync(stream, cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                or IOException or System.Net.Sockets.SocketException or System.Text.Json.JsonException)
            {
                logger.LogDebug(ex, "Volume disk-usage lookup failed for {Host}", DescribeHost(transport));
                return Array.Empty<DockerVolumeUsage>();
            }
        }
    }

    /// <summary>Builds a one-shot HttpClient bound to the Docker engine endpoint
    /// the client dials. Returns <c>null</c> for schemes the raw call can't reach
    /// (npipe, https/TCP+TLS).</summary>
    private static HttpClient? CreateRawEngineHttpClient(Uri endpoint)
    {
        var timeout = TimeSpan.FromSeconds(15);
        switch (endpoint.Scheme)
        {
            case "unix":
                var socketPath = endpoint.LocalPath;
                var handler = new SocketsHttpHandler
                {
                    ConnectCallback = async (_, ct) =>
                    {
                        var socket = new System.Net.Sockets.Socket(
                            System.Net.Sockets.AddressFamily.Unix,
                            System.Net.Sockets.SocketType.Stream,
                            System.Net.Sockets.ProtocolType.Unspecified);
                        await socket.ConnectAsync(
                            new System.Net.Sockets.UnixDomainSocketEndPoint(socketPath), ct);
                        return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
                    },
                };
                return new HttpClient(handler) { BaseAddress = new Uri("http://localhost"), Timeout = timeout };
            case "tcp":
            case "http":
                return new HttpClient { BaseAddress = new Uri($"http://{endpoint.Host}:{endpoint.Port}"), Timeout = timeout };
            default:
                return null;
        }
    }

    private static async Task<IReadOnlyList<DockerVolumeUsage>> ParseVolumeUsageAsync(
        Stream stream, CancellationToken cancellationToken)
    {
        using var doc = await System.Text.Json.JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!doc.RootElement.TryGetProperty("Volumes", out var volumes)
            || volumes.ValueKind != System.Text.Json.JsonValueKind.Array)
            return Array.Empty<DockerVolumeUsage>();

        var result = new List<DockerVolumeUsage>();
        foreach (var v in volumes.EnumerateArray())
        {
            if (!v.TryGetProperty("Name", out var nameEl)) continue;
            var name = nameEl.GetString();
            if (string.IsNullOrEmpty(name)) continue;

            long? size = null;
            int? refCount = null;
            if (v.TryGetProperty("UsageData", out var usage)
                && usage.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                if (usage.TryGetProperty("Size", out var sizeEl)
                    && sizeEl.TryGetInt64(out var s) && s >= 0) size = s;
                if (usage.TryGetProperty("RefCount", out var rcEl)
                    && rcEl.TryGetInt32(out var rc) && rc >= 0) refCount = rc;
            }
            result.Add(new DockerVolumeUsage(name, size, refCount));
        }
        return result;
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private IDockerClient CreateClient(DockerHostTransport transport) =>
        dockerClientFactory.Create(transport.HostType, transport.HostUrl, transport.Tls, transport.Ssh);

    private static string DescribeHost(DockerHostTransport transport) => transport.HostType switch
    {
        DockerHostType.LocalSocket => "local",
        DockerHostType.TcpTls => transport.HostUrl ?? "remote",
        DockerHostType.Ssh => $"ssh://{transport.Ssh?.Username}@{transport.Ssh?.Host}:{transport.Ssh?.Port}",
        _ => "unknown",
    };

    /// <summary>
    /// SSH.NET surfaces a small family of typed exceptions when the underlying
    /// SSH handshake / forwarding fails. We catch them centrally and convert
    /// them into <see cref="DockerHostStatus.HostUnreachable"/> so the UI shows
    /// the same actionable error path as a TLS handshake failure.
    /// </summary>
    private static bool IsSshConnectFailure(Exception ex) =>
        ex is SshException
        or System.Net.Sockets.SocketException;

    /// <summary>
    /// Flattens an exception's <see cref="Exception.InnerException"/> chain into
    /// a single readable string. Docker.DotNet's transport wraps the real cause
    /// in an opaque <see cref="HttpRequestException"/> whose own message is just
    /// "The requested ... see inner exception for details." — surfacing only
    /// <c>ex.Message</c> hides what actually failed (DNS, TCP reset, the SSH
    /// bridge dying, etc.). Walking the chain keeps the actionable detail.
    /// </summary>
    internal static string DescribeError(Exception ex)
    {
        var messages = new List<string>();
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            var message = current.Message?.Trim();
            if (!string.IsNullOrEmpty(message) && (messages.Count == 0 || messages[^1] != message))
                messages.Add(message);
        }
        return messages.Count == 0 ? ex.GetType().Name : string.Join(" → ", messages);
    }

    private static string NormalizeName(IList<string>? names)
    {
        if (names is null || names.Count == 0) return string.Empty;
        var first = names[0];
        return string.IsNullOrEmpty(first) ? string.Empty : first.TrimStart('/');
    }

    private static string ShellQuote(string value)
    {
        // Wrap in single quotes; replace embedded single quotes with the standard
        // POSIX escape sequence '"'"'. Good enough for compose-file paths which
        // rarely contain quotes.
        if (string.IsNullOrEmpty(value)) return "''";
        return "'" + value.Replace("'", "'\"'\"'") + "'";
    }

    private static async Task<(ContainerInspectResponse? Inspect, DockerHostResult? Result)> InspectContainerSafelyAsync(
        IDockerClient client, string containerName, CancellationToken cancellationToken)
    {
        try
        {
            var inspect = await client.Containers.InspectContainerAsync(containerName, cancellationToken);
            return (inspect, null);
        }
        catch (DockerContainerNotFoundException)
        {
            return (null, new DockerHostResult(DockerHostStatus.ContainerNotFound, null, null, null,
                $"Container '{containerName}' not found on the configured Docker host."));
        }
        catch (DockerApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return (null, new DockerHostResult(DockerHostStatus.ContainerNotFound, null, null, null,
                $"Container '{containerName}' not found on the configured Docker host."));
        }
    }

    private static async Task<(ImageInspectResponse? Inspect, DockerHostResult? Result)> InspectImageSafelyAsync(
        IDockerClient client, string imageId, CancellationToken cancellationToken)
    {
        try
        {
            var inspect = await client.Images.InspectImageAsync(imageId, cancellationToken);
            return (inspect, null);
        }
        catch (DockerImageNotFoundException)
        {
            return (null, new DockerHostResult(DockerHostStatus.ImageNotFound, null, null, null,
                $"Image '{imageId}' not found on the host."));
        }
        catch (DockerApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return (null, new DockerHostResult(DockerHostStatus.ImageNotFound, null, null, null,
                $"Image '{imageId}' not found on the host."));
        }
    }

    private (string RepoDigest, string Digest)? FindMatchingRepoDigest(
        IList<string>? repoDigests, string registryHost, string repository)
    {
        if (repoDigests is null || repoDigests.Count == 0) return null;
        foreach (var entry in repoDigests)
        {
            var atIndex = entry.IndexOf('@');
            if (atIndex <= 0 || atIndex == entry.Length - 1) continue;
            var digest = entry[(atIndex + 1)..];

            if (!imageReferenceParser.TryParse(entry, out var parsed)) continue;
            if (string.Equals(parsed.RegistryHost, registryHost, StringComparison.OrdinalIgnoreCase)
                && string.Equals(parsed.Repository, repository, StringComparison.OrdinalIgnoreCase))
            {
                return (entry, digest);
            }
        }
        return null;
    }
}
