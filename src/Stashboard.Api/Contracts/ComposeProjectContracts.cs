using System.ComponentModel.DataAnnotations;

namespace Stashboard.Api.Contracts;

/// <summary>
/// V7.1 — one Compose project discovered on a connection from its containers'
/// standard <c>com.docker.compose.project</c> / <c>…project.working_dir</c>
/// labels (<c>GET /api/docker/connections/{id}/compose</c>). Replaces the
/// V7.0 single-project-per-connection model.
/// </summary>
public sealed record ComposeDiscoveredProjectResponse(
    string Name,
    /// <summary>The project directory as the host knows it (the working_dir label).</summary>
    string? WorkingDir,
    /// <summary>The directory Stashboard would actually read: the label path
    /// translated through the connection's path mapping (LocalSocket) or used
    /// as-is on the host (Ssh). <c>null</c> when no label / TcpTls host —
    /// the project then has no Compose file access.</summary>
    string? ResolvedPath,
    int ContainerCount,
    int RunningCount);

/// <summary>
/// V7.0 — wire contract for the Compose viewer
/// (<c>GET /api/docker/connections/{id}/compose/{project}</c>). Mirrors the
/// subset of the Compose v3.x spec Stashboard renders: services plus top-level
/// networks / volumes / secrets / configs name lists.
/// <see cref="UnsupportedFeatures"/> powers the "Read-only — file uses X"
/// banner: constructs the V7.1 editor can't round-trip (<c>x-*</c>
/// extension fields, <c>extends</c>, YAML merge keys) are named here instead
/// of being silently dropped.
/// </summary>
public sealed record ComposeProjectResponse(
    /// <summary>Top-level <c>name:</c> if the file sets one.</summary>
    string? ProjectName,
    /// <summary>The matched Compose file name (e.g. <c>docker-compose.yml</c>).</summary>
    string FileName,
    /// <summary>The connection's in-container project directory (V5.2 bind mount).</summary>
    string ProjectPath,
    IReadOnlyList<ComposeServiceResponse> Services,
    /// <summary>V7.3 — top-level networks with their editable options
    /// (was a bare name list through V7.2).</summary>
    IReadOnlyList<ComposeNetworkResponse> Networks,
    IReadOnlyList<ComposeVolumeResponse> Volumes,
    IReadOnlyList<ComposeFileResourceResponse> Secrets,
    IReadOnlyList<ComposeFileResourceResponse> Configs,
    IReadOnlyList<string> UnsupportedFeatures);

/// <summary>V7.3 — one top-level <c>networks:</c> entry. External entries bind
/// to a pre-existing network; the driver / ipam / driver_opts fields are then
/// unused.</summary>
public sealed record ComposeNetworkResponse(
    string Name,
    bool External,
    string? NameOverride,
    string? Driver,
    string? Subnet,
    string? Gateway,
    IReadOnlyList<ComposeEnvVarResponse> DriverOpts);

/// <summary>V7.3 — one top-level <c>volumes:</c> entry.</summary>
public sealed record ComposeVolumeResponse(
    string Name,
    bool External,
    string? NameOverride,
    string? Driver,
    IReadOnlyList<ComposeEnvVarResponse> DriverOpts);

/// <summary>V7.3 — one top-level <c>secrets:</c> / <c>configs:</c> entry: a host
/// <c>file:</c> path, or an external reference.</summary>
public sealed record ComposeFileResourceResponse(
    string Name,
    bool External,
    string? NameOverride,
    string? File);

/// <summary>
/// V7.0 — one Compose service card. Ports / volumes are normalised to the
/// short syntax (<c>published:target/protocol</c>, <c>source:target[:ro]</c>)
/// regardless of which form the file used.
/// </summary>
public sealed record ComposeServiceResponse(
    string Name,
    string? Image,
    string? ContainerName,
    string? Restart,
    IReadOnlyList<string> Ports,
    IReadOnlyList<string> Volumes,
    IReadOnlyList<ComposeEnvVarResponse> Environment,
    IReadOnlyList<string> EnvFiles,
    IReadOnlyList<string> DependsOn,
    IReadOnlyList<string> Networks,
    /// <summary>V7.2 — the service's resource constraints (CPU / memory / pids /
    /// ulimits / OOM), normalised across the deploy and legacy conventions.</summary>
    ComposeResourceConstraintsResponse Resources,
    /// <summary>V7.1 — <c>labels:</c> as name/value pairs in file order.</summary>
    IReadOnlyList<ComposeEnvVarResponse> Labels,
    /// <summary>V7.1 — <c>command:</c>; exec (list) form folded into a
    /// <c>["json", "style"]</c> flow string.</summary>
    string? Command,
    /// <summary>V7.1 — <c>entrypoint:</c>, same folding as <see cref="Command"/>.</summary>
    string? Entrypoint,
    /// <summary>V7.1 — <c>user:</c>.</summary>
    string? User,
    /// <summary>V7.1 — <c>working_dir:</c>.</summary>
    string? WorkingDir);

/// <summary>V7.0 — one environment variable; <see cref="Value"/> is <c>null</c>
/// for pass-through entries (<c>- KEY</c> with no value).</summary>
public sealed record ComposeEnvVarResponse(string Name, string? Value);

/// <summary>V7.2 — one <c>ulimits:</c> entry; scalar form yields equal
/// soft/hard.</summary>
public sealed record ComposeUlimitDto(string Name, long? Soft, long? Hard);

/// <summary>
/// V7.2 — a service's resource constraints flattened across the two Compose
/// conventions. <see cref="Convention"/> (<c>"deploy"</c> | <c>"legacy"</c>)
/// tells the UI where cpu/mem/pids live so it edits and writes back in the same
/// home (and disables CPU reservation in legacy mode, which has no such key).
/// </summary>
public sealed record ComposeResourceConstraintsResponse(
    string Convention,
    string? CpuLimit,
    string? CpuReservation,
    string? MemLimit,
    string? MemReservation,
    string? PidsLimit,
    long? CpuShares,
    bool? OomKillDisable,
    long? OomScoreAdj,
    string? ShmSize,
    IReadOnlyList<ComposeUlimitDto> Ulimits);

/// <summary>
/// V7.2 — runtime resources reserved by the containers already on a connection's
/// host, summed from each container's <c>HostConfig</c>
/// (<c>GET /api/docker/connections/{id}/compose/{project}/allocation</c>).
/// Cached server-side (~60 s) so opening the editor doesn't re-inspect every
/// container each time. Powers the "already allocated by other containers" line.
/// </summary>
public sealed record ComposeAllocationResponse(
    int ContainerCount,
    double ReservedCpus,
    long ReservedMemoryBytes);

// ── V7.1 — service edit ─────────────────────────────────────────────────────

/// <summary>
/// V7.1 — desired final state of one service's editable fields
/// (<c>PUT /api/docker/connections/{id}/compose/{project}/services/{service}</c>).
/// Send back exactly what the GET returned, with the fields you want changed:
/// the editor diffs per key and only splices keys whose value differs, so an
/// untouched field is a guaranteed zero-diff in the YAML. Scalar <c>null</c> /
/// empty list removes the key.
/// </summary>
public sealed record ComposeServiceEditRequest(
    [MaxLength(500)] string? Image,
    [MaxLength(100)] string? Restart,
    IReadOnlyList<string> Ports,
    IReadOnlyList<string> Volumes,
    IReadOnlyList<ComposeEnvVarResponse> Environment,
    IReadOnlyList<ComposeEnvVarResponse> Labels,
    [MaxLength(2000)] string? Command,
    [MaxLength(2000)] string? Entrypoint,
    [MaxLength(200)] string? User,
    [MaxLength(500)] string? WorkingDir,
    /// <summary>V7.2 — desired resource constraints. <c>null</c> is tolerated
    /// for back-compat (treated as "no resource constraints declared").</summary>
    ComposeResourceConstraintsResponse? Resources = null);

/// <summary>
/// V7.1 — result of a service edit. <see cref="Changed"/> is <c>false</c> when
/// the desired state already matched the file (nothing was written).
/// <see cref="Project"/> is the freshly re-parsed project after the save so
/// the UI can refresh without a second GET.
/// </summary>
public sealed record ComposeServiceEditResponse(
    bool Changed,
    ComposeProjectResponse Project);

/// <summary>
/// V7.1 — tag list for the image dropdown
/// (<c>GET /api/docker/connections/{id}/compose/image-tags?image=…</c>).
/// <see cref="Error"/> is set when the registry refused or the reference is
/// unparseable — the UI then falls back to free-text input.
/// </summary>
public sealed record ComposeImageTagsResponse(
    string Repository,
    IReadOnlyList<string> Tags,
    string? Error);

// ── V7.4 — create a service from scratch ─────────────────────────────────────

/// <summary>
/// V7.4 — desired state of a brand-new service appended to the project's
/// <c>services:</c> map (<c>POST …/compose/{project}/services</c>). The field
/// shape mirrors <see cref="ComposeServiceEditRequest"/> with an extra
/// <see cref="Name"/> (the Compose service key). The backend validates the name
/// (<c>^[a-zA-Z0-9._-]+$</c>, unique) and that an image is set, renders the
/// block, validates the whole file with <c>docker compose config -q</c> and
/// renames it over the original atomically — the same comment-preserving writer
/// the editor uses.
/// </summary>
public sealed record ComposeServiceCreateRequest(
    [Required, MaxLength(255)] string Name,
    [MaxLength(500)] string? Image,
    [MaxLength(100)] string? Restart,
    IReadOnlyList<string> Ports,
    IReadOnlyList<string> Volumes,
    IReadOnlyList<ComposeEnvVarResponse> Environment,
    IReadOnlyList<ComposeEnvVarResponse> Labels,
    [MaxLength(2000)] string? Command,
    [MaxLength(2000)] string? Entrypoint,
    [MaxLength(200)] string? User,
    [MaxLength(500)] string? WorkingDir,
    ComposeResourceConstraintsResponse? Resources = null);

// ── V7.4.1 — create a whole project from scratch ─────────────────────────────

/// <summary>
/// V7.4.1 — bootstrap a brand-new Compose project (<c>POST …/compose/create-project</c>).
/// Writes a new <see cref="FileName"/> holding a top-level <c>name:</c> +
/// <see cref="Services"/> into <see cref="Directory"/> (created when
/// <see cref="CreateDirectory"/> is set), validated by <c>docker compose
/// config -q</c> and renamed atomically. <see cref="Directory"/> is the path as
/// the transport sees it — inside the Stashboard container for LocalSocket
/// (V5.2 bind mount), on the remote host for Ssh. When <see cref="Run"/> is set,
/// <c>docker compose up -d</c> is run afterward so the project's containers come
/// up (and the project becomes discoverable on the Docker page).
/// </summary>
/// <remarks>V7.5 — <see cref="Services"/> became a list (was a single service)
/// so multi-service starter templates (e.g. app + database) bootstrap in one
/// call. The from-scratch wizard sends a one-element list.</remarks>
public sealed record ComposeProjectCreateRequest(
    [Required, MaxLength(255)] string ProjectName,
    [Required, MaxLength(4000)] string Directory,
    /// <summary>Compose file name; defaults to <c>docker-compose.yml</c>. Must be
    /// a bare file name (no path separators).</summary>
    [MaxLength(255)] string? FileName,
    bool CreateDirectory,
    bool Run,
    /// <summary>The services to write, in order. The first seeds the file; the
    /// rest are appended. At least one is required.</summary>
    [Required, MinLength(1)] IReadOnlyList<ComposeServiceCreateRequest> Services);

/// <summary>V7.4.1 — result of a project bootstrap. <see cref="Started"/> reflects
/// the optional <c>up -d</c> (<c>false</c> when <c>Run</c> was off or the run
/// failed); <see cref="StartError"/> carries the failure when it ran but did not
/// succeed (the file is still written).</summary>
public sealed record ComposeProjectCreateResponse(
    string ProjectName,
    string FileName,
    string Directory,
    bool Started,
    string? StartError);

// ── V7.4 — raw file editor ───────────────────────────────────────────────────

/// <summary>V7.4 — the project's Compose file as raw text, for the "Raw YAML"
/// tab (<c>GET …/compose/{project}/file</c>). Lets the user write or paste a
/// whole file (e.g. several services at once) by hand.</summary>
public sealed record ComposeFileResponse(
    string FileName,
    string ProjectPath,
    string Content);

/// <summary>V7.4 — replace the whole Compose file with hand-edited text
/// (<c>PUT …/compose/{project}/file</c>). The content is validated by
/// <c>docker compose config -q</c> and renamed over the original atomically —
/// 422 with the raw stderr when validation fails.</summary>
public sealed record ComposeFileSaveRequest(
    [Required] string Content);

/// <summary>V7.4 — result of a raw-file save. <see cref="Changed"/> is
/// <c>false</c> when the submitted text already matched the file on disk
/// (nothing was written). The client re-fetches the parsed project on
/// success.</summary>
public sealed record ComposeFileSaveResponse(bool Changed);

// ── V7.4 — bring the project up ──────────────────────────────────────────────

/// <summary>V7.4 — result of the "Save and run" CTA's <c>docker compose up -d</c>
/// (<c>POST …/compose/{project}/up</c>). On failure <see cref="Error"/> carries
/// the actionable message; <see cref="Output"/> is the captured CLI output.</summary>
public sealed record ComposeUpResponse(
    bool Success,
    string? Output,
    string? Error);

// ── V7.3 — top-level resource edit ───────────────────────────────────────────

/// <summary>
/// V7.3 — desired final state of one top-level resource entry (network / volume /
/// secret / config). The kind and the entry name (key) travel in the route; the
/// shape here is a superset across kinds and the backend writes only the fields
/// that apply (subnet/gateway → networks, file → secrets/configs). When
/// <see cref="External"/> is set, only <c>external: true</c> (+ optional
/// <see cref="NameOverride"/>) is written. <c>PUT …/compose/{project}/{kind}/{name}</c>.
/// </summary>
public sealed record ComposeResourceEditRequest(
    bool External,
    /// <summary>The <c>name:</c> override (real Docker resource name); for an
    /// external entry, the pre-existing resource it binds to.</summary>
    [MaxLength(255)] string? NameOverride,
    [MaxLength(100)] string? Driver,
    [MaxLength(64)] string? Subnet,
    [MaxLength(64)] string? Gateway,
    [MaxLength(1000)] string? File,
    IReadOnlyList<ComposeEnvVarResponse>? DriverOpts = null);

/// <summary>V7.3 — result of a top-level resource edit / delete.
/// <see cref="Changed"/> is <c>false</c> when the file already matched (add/edit)
/// or the entry was already absent (delete). <see cref="Project"/> is the
/// freshly re-parsed project so the UI refreshes without a second GET.</summary>
public sealed record ComposeResourceEditResponse(
    bool Changed,
    ComposeProjectResponse Project);

// ── V7.3 — host enrichment ───────────────────────────────────────────────────

/// <summary>V7.3 — one network already defined on the connection's Docker host,
/// for the network editor's subnet-overlap warning
/// (<c>GET …/compose/{project}/host-networks</c>).</summary>
public sealed record ComposeHostNetworkResponse(
    string Name,
    string? Driver,
    IReadOnlyList<string> Subnets);

/// <summary>V7.3 — on-disk usage of one named volume from the host's
/// <c>/system/df</c> (<c>GET …/compose/{project}/volume-usage</c>).
/// <see cref="SizeBytes"/> is <c>null</c> when the daemon did not report a size
/// (older API, or the lookup failed) — the UI then just omits the size.</summary>
public sealed record ComposeVolumeUsageResponse(
    string Name,
    long? SizeBytes,
    int? RefCount);
