using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Logging;
using Stashboard.Core.Abstractions;

namespace Stashboard.Infrastructure.Docker;

/// <summary>
/// V9.2 — see <see cref="ISelfUpdateLauncher"/>. Detects that an "Update now"
/// (single container) or "Update project" (a whole Compose project) targets the
/// Stashboard container itself and offloads the recreate to a detached one-shot
/// helper container so we never stop the very process doing the work.
/// </summary>
/// <remarks>
/// <para>The helper runs <em>this container's own image</em> (already present
/// locally, so no pre-pull is needed) with the <c>self-update</c> /
/// <c>self-update-project</c> command. It then runs the proven
/// <see cref="IDockerImageUpdater"/> / <see cref="IDockerProjectUpdater"/> out of
/// band — surviving because it is an independent container.</para>
/// <para>The helper inherits the parent's mounts verbatim (the Docker socket,
/// any bind-mounted Compose stacks, the data/uploads volumes) so both the raw
/// recreate and the Compose-aware recreate work exactly as they do in process.
/// The helper never opens the database, so sharing the data volume is safe.</para>
/// <para>Works regardless of how the connection reaches the daemon — local
/// socket, SSH tunnel or TCP. The decrypted profile (including any SSH
/// credentials) is serialized to the helper, so the helper talks to the same
/// daemon the same way the parent does.</para>
/// </remarks>
public sealed class SelfUpdateLauncher(
    IDockerClientFactory dockerClientFactory,
    ILogger<SelfUpdateLauncher> logger) : ISelfUpdateLauncher
{
    /// <summary>Fixed name so a crashed helper can be cleaned up on the next
    /// attempt and so its logs are easy to follow (<c>docker logs</c>).</summary>
    internal const string HelperContainerName = SelfUpdateProtocol.HelperContainerName;

    private static readonly Regex ContainerIdRegex = new("[0-9a-f]{64}", RegexOptions.Compiled);

    /// <summary>Test seam: this container's own id. Production reads the real
    /// 64-hex container id from <c>/proc</c> (robust even when the operator sets
    /// a custom <c>hostname</c>), falling back to the <c>HOSTNAME</c> env var
    /// (Docker defaults it to the short container id). Overridable so unit tests
    /// can drive the resolution.</summary>
    public Func<string?> OwnContainerId { get; set; } = DefaultOwnContainerId;

    public async Task<bool> IsSelfTargetAsync(DockerUpdateProfile profile, CancellationToken cancellationToken = default)
    {
        // No transport gate. A Stashboard instance reaching its OWN Docker daemon
        // — over a local socket, an SSH tunnel, or TCP alike — can legitimately be
        // asked to update its own container. The id comparison below is the only
        // thing that decides, and it is self-correcting: against a genuinely
        // remote daemon our own container id simply isn't found there, so we
        // report not-self.
        try
        {
            using var client = CreateClient(profile.HostTransport);

            var self = await ResolveSelfAsync(client, cancellationToken);
            if (self?.ID is null)
            {
                logger.LogInformation(
                    "Self-update detection for {Container}: own container not found on this daemon → not self.",
                    profile.ContainerName);
                return false;
            }

            var isSelf = await IsSameContainerAsync(client, self.ID, profile.ContainerName, cancellationToken);
            logger.LogInformation(
                "Self-update detection for {Container}: own={Own} → isSelf={IsSelf}",
                profile.ContainerName, ShortId(self.ID), isSelf);
            return isSelf;
        }
        catch (Exception ex)
        {
            // Never let detection failure surface — fall back to the normal
            // in-process recreate, which is correct for every non-self target.
            logger.LogWarning(ex, "Self-update target detection failed; treating {Container} as not-self", profile.ContainerName);
            return false;
        }
    }

    public async Task<bool> IsSelfInProjectAsync(DockerProjectUpdateProfile profile, CancellationToken cancellationToken = default)
    {
        try
        {
            using var client = CreateClient(profile.HostTransport);

            var self = await ResolveSelfAsync(client, cancellationToken);
            if (self?.ID is null)
            {
                logger.LogInformation(
                    "Self-update detection for project {Project}: own container not found on this daemon → in-process bulk update.",
                    profile.ProjectName);
                return false;
            }

            foreach (var service in profile.Services)
            {
                if (await IsSameContainerAsync(client, self.ID, service.ContainerName, cancellationToken))
                {
                    logger.LogInformation(
                        "Self-update detection for project {Project}: service {Service} ({Container}) is our own container (own={Own}) → routing the whole project update to a detached helper.",
                        profile.ProjectName, service.ServiceName, service.ContainerName, ShortId(self.ID));
                    return true;
                }
            }

            logger.LogInformation(
                "Self-update detection for project {Project}: none of {Count} service(s) is our own container → in-process bulk update.",
                profile.ProjectName, profile.Services.Count);
            return false;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Self-update project detection failed for {Project}; treating as not-self", profile.ProjectName);
            return false;
        }
    }

    public Task<SelfUpdateLaunchResult> LaunchAsync(DockerUpdateProfile profile, CancellationToken cancellationToken = default) =>
        LaunchHelperAsync(
            profile.HostTransport,
            SelfUpdateProtocol.CommandName,
            JsonSerializer.Serialize(profile, SelfUpdateProtocol.Options),
            $"container {profile.ContainerName}",
            cancellationToken);

    public Task<SelfUpdateLaunchResult> LaunchProjectAsync(DockerProjectUpdateProfile profile, CancellationToken cancellationToken = default) =>
        LaunchHelperAsync(
            profile.HostTransport,
            SelfUpdateProtocol.ProjectCommandName,
            JsonSerializer.Serialize(profile, SelfUpdateProtocol.Options),
            $"compose project '{profile.ProjectName}'",
            cancellationToken);

    // ── shared helper launch ──────────────────────────────────────────────────

    private async Task<SelfUpdateLaunchResult> LaunchHelperAsync(
        DockerHostTransport transport, string command, string profileJson, string targetLabel, CancellationToken cancellationToken)
    {
        IDockerClient client;
        try
        {
            client = CreateClient(transport);
        }
        catch (Exception ex) when (ex is NotSupportedException or ArgumentException)
        {
            return new SelfUpdateLaunchResult(false, $"Docker host configuration is invalid: {ex.Message}");
        }

        using (client)
        {
            ContainerInspectResponse self;
            try
            {
                var resolved = await ResolveSelfAsync(client, cancellationToken);
                if (resolved is null)
                    return new SelfUpdateLaunchResult(false,
                        "Could not resolve the Stashboard container to update itself.");
                self = resolved;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or DockerApiException)
            {
                return new SelfUpdateLaunchResult(false, $"Docker host unreachable: {ex.Message}");
            }

            if (string.IsNullOrEmpty(self.Image))
                return new SelfUpdateLaunchResult(false, "The Stashboard container reports no image to base the self-update helper on.");

            // A helper left over from an interrupted previous attempt would block
            // the create with a name clash — clear it best-effort first.
            await TryRemoveExistingHelperAsync(client, cancellationToken);

            var create = BuildHelperParameters(self, command, profileJson);

            CreateContainerResponse created;
            try
            {
                created = await client.Containers.CreateContainerAsync(create, cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or DockerApiException)
            {
                return new SelfUpdateLaunchResult(false, $"Creating the self-update helper container failed: {ex.Message}");
            }

            try
            {
                await client.Containers.StartContainerAsync(created.ID, new ContainerStartParameters(), cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or DockerApiException)
            {
                return new SelfUpdateLaunchResult(false, $"Starting the self-update helper container failed: {ex.Message}");
            }

            logger.LogInformation(
                "Started detached self-update helper '{Helper}' for {Target}", HelperContainerName, targetLabel);
            return new SelfUpdateLaunchResult(true, null);
        }
    }

    private CreateContainerParameters BuildHelperParameters(ContainerInspectResponse self, string command, string profileJson) =>
        new()
        {
            Name = HelperContainerName,
            // Run our own current image (already local) — the helper just
            // orchestrates; the recreate it performs pulls the new image.
            Image = self.Image,
            // Set the entrypoint explicitly so we don't depend on the image's
            // ENTRYPOINT: the helper must run `dotnet Stashboard.Api.dll <command>`,
            // not have the command appended to a pre-existing ENTRYPOINT (which
            // would double the `dotnet Stashboard.Api.dll`).
            Entrypoint = new List<string> { "dotnet", "Stashboard.Api.dll" },
            Cmd = new List<string> { command },
            Env = new List<string>
            {
                $"{SelfUpdateProtocol.ProfileEnvVar}={profileJson}",
                $"{SelfUpdateProtocol.DelayEnvVar}={SelfUpdateProtocol.DefaultStartDelaySeconds}",
            },
            HostConfig = new HostConfig
            {
                // Inherit every mount the parent has (socket, Compose stacks,
                // data/uploads volumes) so both recreate paths work unchanged.
                Mounts = (self.Mounts ?? new List<MountPoint>()).Select(ToMount).ToList(),
                // Always auto-removed on exit (success or failure) — the helper is
                // throwaway and shouldn't linger in the UI. The rare leftover (e.g.
                // a daemon restart mid-run) is cleared by name before the next
                // launch (TryRemoveExistingHelperAsync).
                AutoRemove = true,
                RestartPolicy = new RestartPolicy { Name = RestartPolicyKind.No },
            },
        };

    /// <summary>
    /// Whether the container <paramref name="containerName"/> resolves to the
    /// same canonical id as our own (<paramref name="ownId"/>). Compares ids, not
    /// names/hostnames, so a cloned container's mismatched hostname or a watch
    /// configured by a different name can't fool it.
    /// </summary>
    private static async Task<bool> IsSameContainerAsync(
        IDockerClient client, string ownId, string containerName, CancellationToken cancellationToken)
    {
        try
        {
            var target = await client.Containers.InspectContainerAsync(containerName, cancellationToken);
            return !string.IsNullOrEmpty(target.ID)
                && string.Equals(ownId, target.ID, StringComparison.OrdinalIgnoreCase);
        }
        catch (DockerContainerNotFoundException)
        {
            return false;
        }
    }

    /// <summary>Converts an inspect <see cref="MountPoint"/> into the
    /// <see cref="Mount"/> shape <c>CreateContainer</c> expects. Volume mounts
    /// are re-bound by name (so Docker resolves the managed volume afresh);
    /// bind mounts keep their host source path.</summary>
    private static Mount ToMount(MountPoint mp) => new()
    {
        Type = mp.Type,
        Source = string.Equals(mp.Type, "volume", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(mp.Name)
            ? mp.Name
            : mp.Source,
        Target = mp.Destination,
        ReadOnly = !mp.RW,
    };

    private async Task<ContainerInspectResponse?> ResolveSelfAsync(IDockerClient client, CancellationToken cancellationToken)
    {
        var id = OwnContainerId();
        if (string.IsNullOrWhiteSpace(id))
            return null;
        try
        {
            return await client.Containers.InspectContainerAsync(id, cancellationToken);
        }
        catch (DockerContainerNotFoundException)
        {
            // Couldn't resolve our own id to a container on this daemon — we can't
            // prove self, so treat as not-self (and fall back to the normal recreate).
            return null;
        }
    }

    private static string? DefaultOwnContainerId() =>
        TryReadContainerIdFromProc() ?? Environment.GetEnvironmentVariable("HOSTNAME");

    /// <summary>
    /// Reads this process's own container id from <c>/proc</c>. Docker records
    /// the id on the bind-mount source paths in <c>/proc/self/mountinfo</c>
    /// (e.g. <c>/var/lib/docker/containers/&lt;id&gt;/hostname</c>) and in the
    /// cgroup paths — both survive a custom hostname, unlike the HOSTNAME env.
    /// Best-effort: returns <c>null</c> if nothing matches (caller falls back to
    /// HOSTNAME).
    /// </summary>
    private static string? TryReadContainerIdFromProc()
    {
        foreach (var path in new[] { "/proc/self/mountinfo", "/proc/self/cgroup" })
        {
            try
            {
                if (!File.Exists(path)) continue;
                foreach (var line in File.ReadLines(path))
                {
                    // Only trust a 64-hex id that sits on a docker / containers
                    // path — a bare 64-hex token elsewhere could be unrelated.
                    if (line.IndexOf("docker", StringComparison.OrdinalIgnoreCase) < 0
                        && line.IndexOf("containers", StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                    var match = ContainerIdRegex.Match(line);
                    if (match.Success) return match.Value;
                }
            }
            catch
            {
                // best effort — try the next source, then HOSTNAME
            }
        }
        return null;
    }

    private static string ShortId(string? id) =>
        string.IsNullOrEmpty(id) ? "?" : id[..Math.Min(12, id.Length)];

    private async Task TryRemoveExistingHelperAsync(IDockerClient client, CancellationToken cancellationToken)
    {
        try
        {
            await client.Containers.RemoveContainerAsync(
                HelperContainerName, new ContainerRemoveParameters { Force = true }, cancellationToken);
        }
        catch (DockerContainerNotFoundException)
        {
            // No leftover helper — the normal case.
        }
        catch (DockerApiException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
        {
            // A helper is mid-removal already — fine, the create will retry the name.
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Removing stale self-update helper '{Helper}' failed", HelperContainerName);
        }
    }

    private IDockerClient CreateClient(DockerHostTransport transport) =>
        dockerClientFactory.Create(transport.HostType, transport.HostUrl, transport.Tls, transport.Ssh);
}
