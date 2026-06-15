namespace Stashboard.Core.Abstractions;

/// <summary>
/// V7.0 — categorical outcome of locating + reading a Compose file inside a
/// connection's bind-mounted project directory. Mirrors the "never throw,
/// branch on a typed status" contract the V5.2 compose runner uses.
/// </summary>
public enum ComposeProjectReadStatus
{
    /// <summary>The file was found and read; <see cref="ComposeProjectReadResult.Content"/> is set.</summary>
    Ok = 0,

    /// <summary>The configured Compose project directory does not exist inside
    /// the Stashboard container — almost always a missing / wrong bind mount.</summary>
    DirectoryNotFound = 1,

    /// <summary>The directory exists but holds none of the standard Compose
    /// file names (<c>compose.yaml</c> / <c>compose.yml</c> /
    /// <c>docker-compose.yaml</c> / <c>docker-compose.yml</c>).</summary>
    FileNotFound = 2,

    /// <summary>V7.0 SSH read only — the SSH connection or the remote probe
    /// command failed (host unreachable, auth failure, unexpected exit code).
    /// <see cref="ComposeProjectReadResult.Error"/> carries the detail.</summary>
    SshFailed = 3,
}

/// <summary>
/// V7.0 — result of <see cref="IComposeProjectReader.ReadAsync"/>.
/// <see cref="FileName"/> is the bare Compose file name that was matched
/// (e.g. <c>docker-compose.yml</c>); <see cref="Content"/> the raw YAML text.
/// </summary>
public sealed record ComposeProjectReadResult(
    ComposeProjectReadStatus Status,
    string? FileName,
    string? Content,
    string? Error);

/// <summary>
/// V7.0 — locates and reads a project's Compose file. Two transports, same
/// result shape: <see cref="ReadAsync"/> reads a V5.2 bind-mounted directory
/// inside the Stashboard container (LocalSocket connections);
/// <see cref="ReadOverSshAsync"/> reads a directory on the remote Docker host
/// over the connection's existing SSH credentials (Ssh connections). Read-only:
/// never writes, never shells out to <c>docker compose</c>.
/// </summary>
public interface IComposeProjectReader
{
    Task<ComposeProjectReadResult> ReadAsync(string projectPath, CancellationToken cancellationToken = default);

    /// <summary>Reads the Compose file from <paramref name="projectPath"/> on
    /// the remote host via SSH (one probe + <c>cat</c> round trip). Never
    /// throws — SSH failures come back as
    /// <see cref="ComposeProjectReadStatus.SshFailed"/>.</summary>
    Task<ComposeProjectReadResult> ReadOverSshAsync(
        DockerSshCredentials ssh, string projectPath, CancellationToken cancellationToken = default);
}

// ── parsed model ────────────────────────────────────────────────────────────

/// <summary>
/// V7.0 — result of <see cref="IComposeFileParser.Parse"/>. Exactly one of
/// <see cref="Project"/> / <see cref="Error"/> is set: a file that fails YAML
/// parsing (or has no <c>services</c> map) yields an error, never a partial model.
/// </summary>
public sealed record ComposeParseResult(
    ComposeProjectModel? Project,
    string? Error);

/// <summary>
/// V7.0 — the subset of the Compose v3.x spec Stashboard renders on the
/// read-only viewer: services plus the top-level resource name lists.
/// <see cref="UnsupportedFeatures"/> names every construct the V7 editor can't
/// round-trip yet (<c>x-*</c> extension fields, <c>extends</c>, YAML merge
/// keys) so the UI can show a "Read-only — file uses X" banner instead of
/// silently dropping data.
/// </summary>
public sealed record ComposeProjectModel(
    /// <summary>Top-level <c>name:</c> if the file sets one; otherwise <c>null</c>.</summary>
    string? ProjectName,
    IReadOnlyList<ComposeServiceModel> Services,
    /// <summary>V7.3 — top-level <c>networks:</c> with the options the editor
    /// surfaces (driver / driver_opts / ipam subnet+gateway / external / name).
    /// Was a bare name list through V7.2.</summary>
    IReadOnlyList<ComposeNetworkModel> Networks,
    /// <summary>V7.3 — top-level <c>volumes:</c> (driver / driver_opts /
    /// external / name).</summary>
    IReadOnlyList<ComposeVolumeModel> Volumes,
    /// <summary>V7.3 — top-level <c>secrets:</c> (external vs. file path).</summary>
    IReadOnlyList<ComposeFileResourceModel> Secrets,
    /// <summary>V7.3 — top-level <c>configs:</c> (external vs. file path).</summary>
    IReadOnlyList<ComposeFileResourceModel> Configs,
    IReadOnlyList<string> UnsupportedFeatures);

/// <summary>
/// V7.3 — one top-level <c>networks:</c> entry. <see cref="External"/> entries
/// reference a network created outside Compose; the driver / ipam / driver_opts
/// fields are then unused. <see cref="Subnet"/> / <see cref="Gateway"/> are read
/// from the first <c>ipam.config</c> item (the common single-subnet case the
/// editor round-trips); files with multiple ipam configs are reported as
/// unsupported rather than silently flattened.
/// </summary>
public sealed record ComposeNetworkModel(
    string Name,
    bool External,
    /// <summary><c>name:</c> override (the real Docker network name). For an
    /// external network this is the pre-existing network it binds to.</summary>
    string? NameOverride,
    string? Driver,
    string? Subnet,
    string? Gateway,
    IReadOnlyList<ComposeEnvVar> DriverOpts);

/// <summary>V7.3 — one top-level <c>volumes:</c> entry. Same external / name
/// semantics as <see cref="ComposeNetworkModel"/>, without the ipam block.</summary>
public sealed record ComposeVolumeModel(
    string Name,
    bool External,
    string? NameOverride,
    string? Driver,
    IReadOnlyList<ComposeEnvVar> DriverOpts);

/// <summary>
/// V7.3 — one top-level <c>secrets:</c> or <c>configs:</c> entry (identical
/// shape in the Compose spec). A non-external entry points at a host
/// <see cref="File"/> path; an external entry references a pre-created
/// secret/config (optionally under a different real <see cref="NameOverride"/>).
/// </summary>
public sealed record ComposeFileResourceModel(
    string Name,
    bool External,
    string? NameOverride,
    string? File);

/// <summary>
/// V7.0 — one service entry. List-shaped fields are normalised to the Compose
/// short syntax (<c>published:target/protocol</c> ports, <c>source:target[:ro]</c>
/// volumes) regardless of whether the file used short or long form, so the UI
/// renders one shape.
/// </summary>
public sealed record ComposeServiceModel(
    string Name,
    string? Image,
    string? ContainerName,
    /// <summary>Service-level <c>restart:</c> policy (e.g. <c>unless-stopped</c>).</summary>
    string? Restart,
    IReadOnlyList<string> Ports,
    IReadOnlyList<string> Volumes,
    IReadOnlyList<ComposeEnvVar> Environment,
    IReadOnlyList<string> EnvFiles,
    IReadOnlyList<string> DependsOn,
    IReadOnlyList<string> Networks,
    /// <summary>V7.2 — the service's CPU / memory / pids / ulimits / OOM
    /// constraints, normalised across the <c>deploy.resources</c> and the
    /// legacy top-level conventions. Never <c>null</c> (an empty constraints
    /// record when the service declares none).</summary>
    ComposeResourceConstraints Resources,
    /// <summary>V7.1 — <c>labels:</c> (mapping or <c>- k=v</c> list form), as
    /// name/value pairs in file order.</summary>
    IReadOnlyList<ComposeEnvVar> Labels,
    /// <summary>V7.1 — <c>command:</c>; the exec (list) form is folded into a
    /// JSON-style flow string (<c>["nginx", "-g", "daemon off;"]</c>) so one
    /// text field can show / edit both forms.</summary>
    string? Command,
    /// <summary>V7.1 — <c>entrypoint:</c>, same folding as <see cref="Command"/>.</summary>
    string? Entrypoint,
    /// <summary>V7.1 — <c>user:</c>.</summary>
    string? User,
    /// <summary>V7.1 — <c>working_dir:</c>.</summary>
    string? WorkingDir);

/// <summary>V7.0 — one environment variable. <see cref="Value"/> is <c>null</c>
/// for pass-through entries (<c>- KEY</c> with no value).</summary>
public sealed record ComposeEnvVar(string Name, string? Value);

/// <summary>
/// V7.2 — one <c>ulimits:</c> entry. A scalar form (<c>nproc: 65535</c>) yields
/// equal <see cref="Soft"/> / <see cref="Hard"/>; the mapping form
/// (<c>nofile: { soft: 20000, hard: 40000 }</c>) yields the two separately.
/// </summary>
public sealed record ComposeUlimit(string Name, long? Soft, long? Hard);

/// <summary>
/// V7.2 — a service's resource constraints, normalised across the two Compose
/// conventions into one flat shape the UI edits directly. <see cref="Convention"/>
/// records where cpu / mem / pids live in <em>this</em> file so the editor writes
/// them back into the same home (never mixing conventions in one document):
/// <list type="bullet">
/// <item><c>"deploy"</c> — <c>deploy.resources.limits</c> / <c>.reservations</c>
/// (modern, portable; the default for files that declare nothing yet).</item>
/// <item><c>"legacy"</c> — top-level <c>cpus</c> / <c>mem_limit</c> /
/// <c>mem_reservation</c> / <c>pids_limit</c> (older v2 files). The v2 form has
/// no CPU-reservation key, so <see cref="CpuReservation"/> is always <c>null</c>
/// in legacy mode.</item>
/// </list>
/// <see cref="CpuShares"/>, <see cref="OomKillDisable"/>,
/// <see cref="OomScoreAdj"/>, <see cref="ShmSize"/> and <see cref="Ulimits"/>
/// are always top-level (no <c>deploy</c> equivalent) regardless of convention.
/// </summary>
public sealed record ComposeResourceConstraints(
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
    IReadOnlyList<ComposeUlimit> Ulimits)
{
    /// <summary>The empty constraints a service that declares nothing carries;
    /// <c>"deploy"</c> so a first edit lands in the modern form.</summary>
    public static ComposeResourceConstraints Empty { get; } = new(
        "deploy", null, null, null, null, null, null, null, null, null,
        Array.Empty<ComposeUlimit>());
}

/// <summary>
/// V7.2 — runtime resources reserved by the containers already on a host,
/// summed from each container's <c>HostConfig</c> (<c>NanoCpus</c> /
/// <c>Memory</c>). Powers the editor's "already allocated by other containers"
/// capacity line.
/// </summary>
public sealed record DockerResourceAllocation(
    int ContainerCount,
    double ReservedCpus,
    long ReservedMemoryBytes);

/// <summary>
/// V7.0 — pure YAML-text → <see cref="ComposeProjectModel"/> parser. No file
/// or Docker access; the seam between it and <see cref="IComposeProjectReader"/>
/// keeps both trivially unit-testable.
/// </summary>
public interface IComposeFileParser
{
    ComposeParseResult Parse(string yamlText);
}
