using Stashboard.Core.Enums;

namespace Stashboard.Core.Abstractions;

/// <summary>
/// V7.1 — optional host→container path mapping for <see cref="DockerHostType.LocalSocket"/>
/// connections. The compose project directory advertised by a container's
/// <c>com.docker.compose.project.working_dir</c> label is a path on the
/// <em>host</em>; inside the Stashboard container the same directory is only
/// visible through a bind mount. When the operator mounts the stacks root at a
/// different path (host <c>/opt/stacks</c> → container <c>/compose</c>) this
/// mapping translates the prefix. When both sides are mounted at the same path
/// no mapping is needed and this stays <c>null</c>.
/// </summary>
public sealed record ComposePathMapping(string HostPrefix, string ContainerPrefix);

/// <summary>
/// V7.1 — resolves the directory Stashboard should read/write/run
/// <c>docker compose</c> in for one Compose project, from the project's
/// <c>working_dir</c> label. Replaces the V5.2 single
/// <c>ComposeProjectPath</c>-per-connection model, which broke on hosts running
/// more than one Compose project.
/// </summary>
public static class ComposeProjectPaths
{
    /// <summary>Standard Compose label carrying the absolute host path of the
    /// directory <c>docker compose up</c> ran in.</summary>
    public const string WorkingDirLabel = "com.docker.compose.project.working_dir";

    /// <summary>Standard Compose label carrying the project name.</summary>
    public const string ProjectLabel = "com.docker.compose.project";

    /// <summary>
    /// Returns the path usable by this connection's transport, or <c>null</c>
    /// when there is none (no label, or a TcpTls host — no file access).
    /// SSH uses the host path as-is (commands run on the host); LocalSocket
    /// applies the prefix mapping when one is configured and matches.
    /// </summary>
    public static string? Resolve(DockerHostType hostType, ComposePathMapping? mapping, string? workingDir)
    {
        if (string.IsNullOrWhiteSpace(workingDir)) return null;
        var path = workingDir.Trim();
        return hostType switch
        {
            DockerHostType.Ssh => path,
            DockerHostType.LocalSocket => ApplyMapping(mapping, path),
            _ => null,
        };
    }

    private static string ApplyMapping(ComposePathMapping? mapping, string workingDir)
    {
        if (mapping is null) return workingDir;
        var hostPrefix = TrimTrailingSlash(mapping.HostPrefix);
        if (hostPrefix.Length == 0) return workingDir;
        if (!workingDir.StartsWith(hostPrefix, StringComparison.Ordinal)) return workingDir;

        var remainder = workingDir[hostPrefix.Length..];
        // Prefix must match on a path boundary: `/opt/stacks` maps
        // `/opt/stacks/jellyfin` but not `/opt/stacksize`.
        if (remainder.Length > 0 && remainder[0] != '/') return workingDir;
        return TrimTrailingSlash(mapping.ContainerPrefix) + remainder;
    }

    private static string TrimTrailingSlash(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length > 1 ? trimmed.TrimEnd('/') : trimmed;
    }
}
