using Stashboard.Core.Enums;

namespace Stashboard.Core.Abstractions;

/// <summary>Categorical outcome of a Docker host call.</summary>
public enum DockerHostStatus
{
    Ok = 0,
    HostUnreachable = 1,
    ContainerNotFound = 2,
    ImageNotFound = 3,
    /// <summary>The container's image has no <c>RepoDigest</c> matching the configured
    /// registry+repository. Happens for locally-built images, images that were tagged
    /// but never pulled, or when the user typed the wrong image reference.</summary>
    NoMatchingRepoDigest = 4,
    UnsupportedHostType = 5,
}

/// <summary>
/// Result of <see cref="IDockerHostClient.GetCurrentImageDigestAsync"/>.
/// </summary>
public sealed record DockerHostResult(
    DockerHostStatus Status,
    string? Digest,
    string? MatchedRepoDigest,
    string? ImageId,
    string? Error)
{
    public bool IsSuccess => Status == DockerHostStatus.Ok && !string.IsNullOrEmpty(Digest);
}

/// <summary>Result of <see cref="IDockerHostClient.PingAsync"/>.</summary>
public sealed record DockerHostPingResult(bool HostReachable, string? Error);

/// <summary>Result of <see cref="IDockerHostClient.TestContainerAsync"/>.</summary>
public sealed record DockerHostConnectionResult(
    bool HostReachable,
    bool ContainerFound,
    string? Error);

/// <summary>
/// Decrypted TLS material for talking to a remote Docker daemon over TCP. All fields
/// are PEM-encoded strings. Caller is responsible for decrypting before passing in.
/// </summary>
public sealed record DockerTlsMaterial(string CaCert, string ClientCert, string ClientKey);

/// <summary>
/// A container running on the Docker daemon. Exposes the labels needed to suggest
/// an accurate update command (compose vs. plain docker).
/// </summary>
public sealed record DockerContainerSummary(
    string Name,
    string Image,
    string ImageId,
    string State,
    string Status,
    IReadOnlyDictionary<string, string> Labels);

/// <summary>
/// V3.5 — richer container snapshot for the instances page. Carries the
/// extra fields the cards render: container id, creation timestamp,
/// per-port mapping rows (container side + host side), and compose
/// metadata pulled from the standard labels so the page can group cards
/// by compose project without re-parsing the labels client-side.
/// </summary>
public sealed record DockerContainerDetail(
    string Id,
    string Name,
    string Image,
    string ImageId,
    string State,
    string Status,
    DateTime? CreatedUtc,
    IReadOnlyList<DockerContainerPort> Ports,
    string? ComposeProject,
    string? ComposeService,
    IReadOnlyDictionary<string, string> Labels);

/// <summary>
/// V3.5 — one port row out of a container's <c>NetworkSettings.Ports</c>
/// list. <c>PublicPort</c> is <c>null</c> when the port is exposed but not
/// published to the host (e.g. internal-only docker networks).
/// </summary>
public sealed record DockerContainerPort(
    int PrivatePort,
    int? PublicPort,
    string Type,
    string? Ip);

/// <summary>V3.5 — outcome of a single lifecycle op (Start / Stop /
/// Restart / Remove) on a container.</summary>
public sealed record DockerContainerActionResult(
    DockerHostStatus Status,
    /// <summary>Docker container id at action time. <c>null</c> when the
    /// daemon never returned one (host unreachable, container not found).</summary>
    string? ContainerId,
    string? Error)
{
    public bool IsSuccess => Status == DockerHostStatus.Ok;
}

/// <summary>
/// Generated update command for a container plus a hint about how it was derived
/// (compose / docker-run / fallback). The <c>Warning</c> surfaces any caveats —
/// e.g. "container not managed by compose, command is approximate".
/// </summary>
public sealed record DockerUpdateCommandPlan(string Command, string Source, string? Warning);

// ── V3.1 — Container Inspect viewer ─────────────────────────────────────────

/// <summary>
/// V3.1 — outcome of <see cref="IDockerHostClient.InspectContainerAsync"/>.
/// Mirrors the result-style envelope used elsewhere on the interface so the
/// controller can branch on a single status field and stay clear of
/// exception-driven control flow.
/// </summary>
public sealed record DockerContainerInspectResult(
    DockerHostStatus Status,
    DockerContainerInspect? Inspect,
    string? Error)
{
    public bool IsSuccess => Status == DockerHostStatus.Ok && Inspect is not null;
}

/// <summary>
/// V3.1 — slimmed snapshot of a Docker container's <c>inspect</c> payload.
/// Surfaces the same fields the user would see in <c>docker inspect</c> for
/// debugging a misconfigured container — image digest, command, env, mounts,
/// networks, labels, restart policy, health state, and ports — without
/// shipping every field of the Engine response (and without ever shipping a
/// raw env value that looks secret).
/// </summary>
public sealed record DockerContainerInspect(
    string Id,
    string Name,
    /// <summary>Image reference recorded in the container's config
    /// (e.g. <c>nginx:1.27</c>) — what the user actually launched with.</summary>
    string Image,
    /// <summary>Resolved image id (<c>sha256:…</c>) — the local image the
    /// container is actually using.</summary>
    string ImageId,
    /// <summary>The Engine's <c>RepoDigests</c> list for the resolved image.
    /// Useful for diffing against <c>DockerWatchEntity.CurrentDigest</c>.</summary>
    IReadOnlyList<string> ImageRepoDigests,
    /// <summary>Container creation timestamp (UTC), best-effort parsed from
    /// the Engine's RFC3339 string. <c>null</c> when the value is missing or
    /// unparseable.</summary>
    DateTime? CreatedUtc,
    int RestartCount,
    string? Platform,
    string? Driver,
    DockerInspectState State,
    DockerInspectConfig Config,
    DockerInspectHostConfig HostConfig,
    DockerInspectNetworkSettings NetworkSettings,
    IReadOnlyList<DockerInspectMount> Mounts);

/// <summary>V3.1 — runtime state slice of the inspect payload.</summary>
public sealed record DockerInspectState(
    string Status,
    bool Running,
    bool Restarting,
    bool Paused,
    bool OomKilled,
    bool Dead,
    int ExitCode,
    string? Error,
    DateTime? StartedUtc,
    DateTime? FinishedUtc,
    DockerInspectHealth? Health);

/// <summary>V3.1 — current health check result. <c>null</c> when the
/// container has no <c>HEALTHCHECK</c> defined.</summary>
public sealed record DockerInspectHealth(
    /// <summary>One of <c>none</c>, <c>starting</c>, <c>healthy</c>, <c>unhealthy</c>.</summary>
    string Status,
    int FailingStreak,
    IReadOnlyList<DockerInspectHealthLog> Log);

public sealed record DockerInspectHealthLog(
    DateTime? StartUtc,
    DateTime? EndUtc,
    int ExitCode,
    string? Output);

/// <summary>V3.1 — image-level config the container was launched with.</summary>
public sealed record DockerInspectConfig(
    string? Hostname,
    string? User,
    string? WorkingDir,
    string? Image,
    IReadOnlyList<string> Entrypoint,
    IReadOnlyList<string> Cmd,
    /// <summary>Env vars decomposed into <c>(Key, Value, Masked)</c>. Values
    /// for keys matching the secret heuristic (password / token / secret /
    /// key / auth / credential / passwd / api_key) are stripped from the
    /// wire payload — the UI receives <c>Masked = true</c> and an empty
    /// value, never the plaintext.</summary>
    IReadOnlyList<DockerInspectEnvVar> Env,
    IReadOnlyDictionary<string, string> Labels,
    /// <summary>Ports the image declares as exposed (no host mapping
    /// implied). Keys look like <c>80/tcp</c>.</summary>
    IReadOnlyList<string> ExposedPorts);

public sealed record DockerInspectEnvVar(string Key, string? Value, bool Masked);

/// <summary>V3.1 — host-side config — restart policy, network mode, port
/// bindings, resource limits.</summary>
public sealed record DockerInspectHostConfig(
    string? NetworkMode,
    DockerInspectRestartPolicy? RestartPolicy,
    long? MemoryBytes,
    long? CpuShares,
    bool Privileged,
    bool ReadonlyRootfs,
    bool AutoRemove,
    IReadOnlyList<DockerInspectPortBinding> PortBindings);

public sealed record DockerInspectRestartPolicy(string Name, int MaximumRetryCount);

/// <summary>V3.1 — flattened port binding. <c>ContainerPort</c> includes the
/// protocol suffix (<c>80/tcp</c>); <c>HostIp</c> / <c>HostPort</c> are the
/// Engine's binding side (may be empty when the port is exposed but not
/// published to the host).</summary>
public sealed record DockerInspectPortBinding(
    string ContainerPort,
    string? HostIp,
    string? HostPort);

public sealed record DockerInspectNetworkSettings(
    IReadOnlyDictionary<string, DockerInspectNetwork> Networks);

public sealed record DockerInspectNetwork(
    string? NetworkID,
    string? EndpointID,
    string? IPAddress,
    string? Gateway,
    int? IPPrefixLen,
    string? MacAddress,
    IReadOnlyList<string> Aliases);

public sealed record DockerInspectMount(
    string Type,
    string? Name,
    string? Source,
    string Destination,
    string? Driver,
    string Mode,
    bool ReadWrite,
    string Propagation);

/// <summary>
/// V2.5 — bundle of transport parameters that the host client needs to dial
/// out. Lets us add new host types (SSH, future remote variants) without
/// growing the method signature on every call to <see cref="IDockerHostClient"/>.
/// </summary>
/// <param name="HostType">Local socket, TCP+TLS, or SSH.</param>
/// <param name="HostUrl">URL for TCP+TLS hosts; <c>null</c> otherwise.</param>
/// <param name="Tls">Decrypted TLS material for TCP+TLS hosts; <c>null</c> otherwise.</param>
/// <param name="Ssh">Decrypted SSH credentials for SSH hosts; <c>null</c> otherwise.</param>
public sealed record DockerHostTransport(
    DockerHostType HostType,
    string? HostUrl,
    DockerTlsMaterial? Tls,
    DockerSshCredentials? Ssh = null);

/// <summary>
/// Talks to a local or remote Docker daemon. Reads the running container's image
/// manifest digest, lists containers for the UI's "pick a container" dropdown,
/// and generates an update command from <c>ContainerInspect</c> data.
/// </summary>
public interface IDockerHostClient
{
    /// <summary>
    /// Reads the manifest digest of the image currently running in
    /// <paramref name="containerName"/> on the configured Docker host.
    /// </summary>
    Task<DockerHostResult> GetCurrentImageDigestAsync(
        DockerHostTransport transport,
        string containerName,
        string registryHost,
        string repository,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Pings the daemon. Used by the connection-setup form's "Test connection"
    /// button to validate transport/TLS without requiring a container name.
    /// </summary>
    Task<DockerHostPingResult> PingAsync(
        DockerHostTransport transport,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Pings the daemon and inspects <paramref name="containerName"/>. Used by
    /// the per-watch test-connection probe.
    /// </summary>
    Task<DockerHostConnectionResult> TestContainerAsync(
        DockerHostTransport transport,
        string containerName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists containers on the daemon (all states, not just running) so the UI
    /// can present a "pick a container" dropdown.
    /// </summary>
    Task<IReadOnlyList<DockerContainerSummary>> ListContainersAsync(
        DockerHostTransport transport,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Generates the manual update command for <paramref name="containerName"/>
    /// from <c>ContainerInspect</c>. Returns a compose-style command when the
    /// container is managed by docker compose; otherwise a <c>docker pull/run</c>
    /// approximation, with a warning noting the limitation.
    /// </summary>
    Task<DockerUpdateCommandPlan?> GenerateUpdateCommandAsync(
        DockerHostTransport transport,
        string containerName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// V3.1 — fetches a slimmed snapshot of the container's
    /// <c>inspect</c> payload (image digest, command, env, mounts, networks,
    /// labels, restart policy, health state, ports). Values for env keys
    /// matching the secret heuristic are stripped before returning.
    /// Read-only — does not mutate the daemon.
    /// </summary>
    Task<DockerContainerInspectResult> InspectContainerAsync(
        DockerHostTransport transport,
        string containerName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// V3.5 — Docker instances page. Same call as
    /// <see cref="ListContainersAsync"/> but returns the richer detail
    /// snapshot (created timestamp, port mappings, compose metadata,
    /// container id) the cards need.
    /// </summary>
    Task<IReadOnlyList<DockerContainerDetail>> ListContainerDetailsAsync(
        DockerHostTransport transport,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// V7.2 — sums the CPU / memory a host's <em>running</em> containers already
    /// reserve, from each container's <c>HostConfig</c> (<c>NanoCpus</c> /
    /// <c>Memory</c>). Containers whose <c>com.docker.compose.project</c> label
    /// equals <paramref name="excludeComposeProject"/> are skipped so the Compose
    /// resources editor can show "allocated by <em>other</em> containers" without
    /// double-counting the project being edited. Read-only.
    /// </summary>
    Task<DockerResourceAllocation> GetResourceAllocationAsync(
        DockerHostTransport transport,
        string? excludeComposeProject = null,
        CancellationToken cancellationToken = default);

    /// <summary>V3.5 — <c>docker start &lt;container&gt;</c>.</summary>
    Task<DockerContainerActionResult> StartContainerAsync(
        DockerHostTransport transport,
        string containerName,
        CancellationToken cancellationToken = default);

    /// <summary>V3.5 — <c>docker stop &lt;container&gt;</c>. Honours
    /// <paramref name="waitSeconds"/> before SIGKILL.</summary>
    Task<DockerContainerActionResult> StopContainerAsync(
        DockerHostTransport transport,
        string containerName,
        int waitSeconds = 10,
        CancellationToken cancellationToken = default);

    /// <summary>V3.5 — <c>docker restart &lt;container&gt;</c>. Honours
    /// <paramref name="waitSeconds"/> before SIGKILL between stop and
    /// start.</summary>
    Task<DockerContainerActionResult> RestartContainerAsync(
        DockerHostTransport transport,
        string containerName,
        int waitSeconds = 10,
        CancellationToken cancellationToken = default);

    /// <summary>V3.5 — <c>docker rm &lt;container&gt;</c>. Forced removal
    /// is opt-in via <paramref name="force"/>. Destructive — controller
    /// gates this behind the <c>Stashboard.AllowContainerRemoval</c>
    /// feature flag.</summary>
    Task<DockerContainerActionResult> RemoveContainerAsync(
        DockerHostTransport transport,
        string containerName,
        bool force = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// V5.5 — summary of the image storage on a Docker host. Drives the
    /// "Storage" widget on the V3.5 instances page: how many images live
    /// on the host, how many are dangling (<c>&lt;none&gt;:&lt;none&gt;</c>
    /// after a recreate), how much disk those dangling images occupy, and
    /// the same numbers for "unused" images (anything not referenced by a
    /// running or stopped container). Read-only — never mutates the host.
    /// </summary>
    Task<DockerImageStorageResult> GetImageStorageAsync(
        DockerHostTransport transport,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// V5.5 — runs <c>docker image prune</c> on the host. Dangling-only by
    /// default; with <paramref name="includeUnused"/> = <c>true</c> the
    /// daemon also removes any image not referenced by a running or stopped
    /// container (the equivalent of <c>docker image prune -a</c>).
    /// Returns the count of images deleted and bytes reclaimed so the UI
    /// can show "freed 4.2 GiB" without re-querying the storage summary.
    /// </summary>
    Task<DockerImagePruneResult> PruneImagesAsync(
        DockerHostTransport transport,
        bool includeUnused,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// V7.3 — lists the networks already defined on the host (name + driver +
    /// declared subnets) so the Compose network editor can warn when a new
    /// subnet overlaps one already in use. Read-only.
    /// </summary>
    Task<IReadOnlyList<DockerNetworkSummary>> ListNetworksAsync(
        DockerHostTransport transport,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// V7.3 — on-disk usage of the host's named volumes (the Docker Engine
    /// <c>/system/df</c> view) so the Compose volume editor can show "postgres_data
    /// is 4.2 GiB" before the user considers deleting it. Best-effort: returns an
    /// empty list when the transport can't reach <c>/system/df</c> (e.g. TCP+TLS,
    /// older daemon). Read-only.
    /// </summary>
    Task<IReadOnlyList<DockerVolumeUsage>> GetVolumeUsageAsync(
        DockerHostTransport transport,
        CancellationToken cancellationToken = default);
}

/// <summary>V7.3 — one network on the host. <see cref="Subnets"/> are the CIDR
/// blocks from the network's IPAM config (may be empty for driverless / default
/// networks).</summary>
public sealed record DockerNetworkSummary(
    string Name,
    string? Driver,
    IReadOnlyList<string> Subnets);

/// <summary>V7.3 — on-disk usage of one named volume. <see cref="SizeBytes"/> is
/// <c>null</c> when the daemon did not report a size.</summary>
public sealed record DockerVolumeUsage(
    string Name,
    long? SizeBytes,
    int? RefCount);

/// <summary>V5.5 — outcome of <see cref="IDockerHostClient.GetImageStorageAsync"/>.</summary>
public sealed record DockerImageStorageResult(
    DockerHostStatus Status,
    /// <summary>Total number of images on the host (all states).</summary>
    int TotalImageCount,
    /// <summary>Subset of <see cref="TotalImageCount"/> whose only
    /// <c>RepoTag</c> is <c>&lt;none&gt;:&lt;none&gt;</c> — the orphaned
    /// previous-version images recreate leaves behind.</summary>
    int DanglingImageCount,
    /// <summary>Total bytes occupied by dangling images, as reported by
    /// the daemon's <c>Size</c> field. Sum, not deduped by shared layers,
    /// so the actual reclaim may be lower.</summary>
    long DanglingImageBytes,
    /// <summary>Number of images not referenced by any running or stopped
    /// container (a superset of dangling). Powers the opt-in "also prune
    /// unused images" preview.</summary>
    int UnusedImageCount,
    /// <summary>Total bytes occupied by unused images.</summary>
    long UnusedImageBytes,
    /// <summary>V5.5 — every image on the host, each flagged dangling /
    /// unused, so the UI can show exactly <em>which</em> images make up the
    /// counts above (and which would be removed by a prune) instead of just
    /// a number.</summary>
    IReadOnlyList<DockerImageSummary> Images,
    string? Error)
{
    public bool IsSuccess => Status == DockerHostStatus.Ok;
}

/// <summary>
/// V5.5 — one image on a Docker host, with the bits the Storage widget's
/// drill-down needs: the repo tags (so the user sees <c>nginx:1.27</c>
/// rather than an opaque digest), on-disk size, creation time, and whether
/// the image is dangling / unused. <c>RepoTags</c> is empty for a dangling
/// image (the daemon reports <c>&lt;none&gt;:&lt;none&gt;</c>).
/// </summary>
public sealed record DockerImageSummary(
    string Id,
    IReadOnlyList<string> RepoTags,
    long SizeBytes,
    DateTime? CreatedUtc,
    bool IsDangling,
    bool IsUnused,
    /// <summary>Names of the containers (running or stopped) referencing this
    /// image. Empty when <see cref="IsUnused"/> is <c>true</c>. Docker's prune
    /// refuses to delete an in-use image, so this tells the user which
    /// container to remove to reclaim a dangling-but-in-use image.</summary>
    IReadOnlyList<string> UsedByContainers);

/// <summary>V5.5 — outcome of <see cref="IDockerHostClient.PruneImagesAsync"/>.</summary>
public sealed record DockerImagePruneResult(
    DockerHostStatus Status,
    /// <summary>Number of images the daemon deleted (deleted + untagged combined).</summary>
    int ImagesDeleted,
    /// <summary>Bytes the daemon reports reclaimed.</summary>
    long SpaceReclaimedBytes,
    string? Error)
{
    public bool IsSuccess => Status == DockerHostStatus.Ok;
}
