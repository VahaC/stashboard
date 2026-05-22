using System.Globalization;
using Docker.DotNet.Models;
using Stashboard.Core.Abstractions;

namespace Stashboard.Infrastructure.Docker;

/// <summary>
/// V3.1 — translates Docker.DotNet's <see cref="ContainerInspectResponse"/>
/// into the slimmed <see cref="DockerContainerInspect"/> DTO surfaced by the
/// V3.1 inspect viewer. Owns two concerns the host client would otherwise
/// inline: shaping the Engine response into a small wire contract, and
/// masking env values whose key matches the secret heuristic so the API
/// never leaks <c>POSTGRES_PASSWORD</c> / <c>API_TOKEN</c> / etc. into the
/// browser.
/// </summary>
public static class DockerInspectMapper
{
    /// <summary>Substrings (case-insensitive) that trigger value masking on
    /// an env entry. Conservative on purpose — the user can still see the
    /// key, just not the value. Matches common conventions across
    /// docker-compose templates, Helm charts, and 12-factor apps.</summary>
    private static readonly string[] SecretKeyHints =
    [
        "PASSWORD", "PASSWD", "SECRET", "TOKEN", "API_KEY", "APIKEY",
        "PRIVATE_KEY", "PRIVATEKEY", "AUTH", "CREDENTIAL", "ACCESS_KEY",
        "ACCESSKEY",
    ];

    public static DockerContainerInspect Map(ContainerInspectResponse inspect, IReadOnlyList<string> repoDigests) =>
        new(
            Id: inspect.ID ?? string.Empty,
            Name: (inspect.Name ?? string.Empty).TrimStart('/'),
            Image: inspect.Config?.Image ?? string.Empty,
            ImageId: inspect.Image ?? string.Empty,
            ImageRepoDigests: repoDigests,
            CreatedUtc: NormalizeUtc(inspect.Created),
            RestartCount: (int)inspect.RestartCount,
            Platform: inspect.Platform,
            Driver: inspect.Driver,
            State: MapState(inspect.State),
            Config: MapConfig(inspect.Config),
            HostConfig: MapHostConfig(inspect.HostConfig),
            NetworkSettings: MapNetworkSettings(inspect.NetworkSettings),
            Mounts: MapMounts(inspect.Mounts));

    private static DockerInspectState MapState(ContainerState? state)
    {
        if (state is null)
            return new DockerInspectState(
                Status: "unknown",
                Running: false, Restarting: false, Paused: false,
                OomKilled: false, Dead: false, ExitCode: 0,
                Error: null, StartedUtc: null, FinishedUtc: null, Health: null);

        return new DockerInspectState(
            Status: state.Status ?? "unknown",
            Running: state.Running,
            Restarting: state.Restarting,
            Paused: state.Paused,
            OomKilled: state.OOMKilled,
            Dead: state.Dead,
            ExitCode: (int)state.ExitCode,
            Error: NullIfEmpty(state.Error),
            StartedUtc: ParseDateTimeOrNull(state.StartedAt),
            FinishedUtc: ParseDateTimeOrNull(state.FinishedAt),
            Health: MapHealth(state.Health));
    }

    private static DockerInspectHealth? MapHealth(Health? health)
    {
        if (health is null) return null;
        var log = health.Log is { Count: > 0 }
            ? health.Log.Select(l => new DockerInspectHealthLog(
                StartUtc: l.Start == default ? null : DateTime.SpecifyKind(l.Start, DateTimeKind.Utc),
                EndUtc: l.End == default ? null : DateTime.SpecifyKind(l.End, DateTimeKind.Utc),
                ExitCode: (int)l.ExitCode,
                Output: NullIfEmpty(l.Output))).ToArray()
            : Array.Empty<DockerInspectHealthLog>();
        return new DockerInspectHealth(
            Status: health.Status ?? "none",
            FailingStreak: (int)health.FailingStreak,
            Log: log);
    }

    private static DockerInspectConfig MapConfig(Config? config)
    {
        if (config is null)
            return new DockerInspectConfig(
                Hostname: null, User: null, WorkingDir: null, Image: null,
                Entrypoint: Array.Empty<string>(), Cmd: Array.Empty<string>(),
                Env: Array.Empty<DockerInspectEnvVar>(),
                Labels: new Dictionary<string, string>(),
                ExposedPorts: Array.Empty<string>());

        var env = config.Env is { Count: > 0 }
            ? config.Env.Select(MapEnvVar).ToArray()
            : Array.Empty<DockerInspectEnvVar>();

        return new DockerInspectConfig(
            Hostname: NullIfEmpty(config.Hostname),
            User: NullIfEmpty(config.User),
            WorkingDir: NullIfEmpty(config.WorkingDir),
            Image: NullIfEmpty(config.Image),
            Entrypoint: config.Entrypoint is { Count: > 0 } ? config.Entrypoint.ToArray() : Array.Empty<string>(),
            Cmd: config.Cmd is { Count: > 0 } ? config.Cmd.ToArray() : Array.Empty<string>(),
            Env: env,
            Labels: config.Labels is { Count: > 0 }
                ? new Dictionary<string, string>(config.Labels)
                : new Dictionary<string, string>(),
            ExposedPorts: config.ExposedPorts is { Count: > 0 }
                ? config.ExposedPorts.Keys.ToArray()
                : Array.Empty<string>());
    }

    /// <summary>
    /// Splits an env entry on the first <c>=</c> and replaces the value with
    /// an empty string + <c>Masked = true</c> when the key trips the secret
    /// heuristic. The split-on-first-= rule matters because env values often
    /// contain <c>=</c> themselves (base64, query strings).
    /// </summary>
    public static DockerInspectEnvVar MapEnvVar(string raw)
    {
        if (string.IsNullOrEmpty(raw))
            return new DockerInspectEnvVar(string.Empty, string.Empty, Masked: false);

        var eq = raw.IndexOf('=');
        var key = eq >= 0 ? raw[..eq] : raw;
        var value = eq >= 0 ? raw[(eq + 1)..] : string.Empty;
        var masked = IsSecretKey(key);
        return new DockerInspectEnvVar(key, masked ? null : value, masked);
    }

    public static bool IsSecretKey(string key)
    {
        if (string.IsNullOrEmpty(key)) return false;
        foreach (var hint in SecretKeyHints)
        {
            if (key.Contains(hint, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static DockerInspectHostConfig MapHostConfig(HostConfig? hostConfig)
    {
        if (hostConfig is null)
            return new DockerInspectHostConfig(
                NetworkMode: null, RestartPolicy: null,
                MemoryBytes: null, CpuShares: null,
                Privileged: false, ReadonlyRootfs: false, AutoRemove: false,
                PortBindings: Array.Empty<DockerInspectPortBinding>());

        var portBindings = new List<DockerInspectPortBinding>();
        if (hostConfig.PortBindings is { Count: > 0 })
        {
            foreach (var (containerPort, bindings) in hostConfig.PortBindings)
            {
                if (bindings is null || bindings.Count == 0)
                {
                    portBindings.Add(new DockerInspectPortBinding(containerPort, HostIp: null, HostPort: null));
                    continue;
                }
                foreach (var b in bindings)
                {
                    portBindings.Add(new DockerInspectPortBinding(
                        ContainerPort: containerPort,
                        HostIp: NullIfEmpty(b.HostIP),
                        HostPort: NullIfEmpty(b.HostPort)));
                }
            }
        }

        return new DockerInspectHostConfig(
            NetworkMode: NullIfEmpty(hostConfig.NetworkMode),
            RestartPolicy: MapRestartPolicy(hostConfig.RestartPolicy),
            MemoryBytes: hostConfig.Memory > 0 ? hostConfig.Memory : null,
            CpuShares: hostConfig.CPUShares > 0 ? hostConfig.CPUShares : null,
            Privileged: hostConfig.Privileged,
            ReadonlyRootfs: hostConfig.ReadonlyRootfs,
            AutoRemove: hostConfig.AutoRemove,
            PortBindings: portBindings);
    }

    private static DockerInspectRestartPolicy? MapRestartPolicy(RestartPolicy? policy)
    {
        if (policy is null) return null;
        // Docker.DotNet's RestartPolicy.Name is a RestartPolicyKind enum
        // (None, Always, OnFailure, UnlessStopped). Treat the absence of a
        // policy (Name = None) as "no policy" so the UI doesn't show
        // "restart: none" when the container simply doesn't have one set.
        var name = policy.Name.ToString();
        if (string.IsNullOrEmpty(name) || string.Equals(name, "None", StringComparison.OrdinalIgnoreCase))
            return null;
        return new DockerInspectRestartPolicy(name, (int)policy.MaximumRetryCount);
    }

    private static DockerInspectNetworkSettings MapNetworkSettings(NetworkSettings? settings)
    {
        var networks = new Dictionary<string, DockerInspectNetwork>(StringComparer.Ordinal);
        if (settings?.Networks is { Count: > 0 })
        {
            foreach (var (name, endpoint) in settings.Networks)
            {
                networks[name] = new DockerInspectNetwork(
                    NetworkID: NullIfEmpty(endpoint.NetworkID),
                    EndpointID: NullIfEmpty(endpoint.EndpointID),
                    IPAddress: NullIfEmpty(endpoint.IPAddress),
                    Gateway: NullIfEmpty(endpoint.Gateway),
                    IPPrefixLen: endpoint.IPPrefixLen > 0 ? (int)endpoint.IPPrefixLen : null,
                    MacAddress: NullIfEmpty(endpoint.MacAddress),
                    Aliases: endpoint.Aliases is { Count: > 0 } ? endpoint.Aliases.ToArray() : Array.Empty<string>());
            }
        }
        return new DockerInspectNetworkSettings(networks);
    }

    private static IReadOnlyList<DockerInspectMount> MapMounts(IList<MountPoint>? mounts)
    {
        if (mounts is null || mounts.Count == 0) return Array.Empty<DockerInspectMount>();
        return mounts.Select(m => new DockerInspectMount(
            Type: m.Type ?? string.Empty,
            Name: NullIfEmpty(m.Name),
            Source: NullIfEmpty(m.Source),
            Destination: m.Destination ?? string.Empty,
            Driver: NullIfEmpty(m.Driver),
            Mode: m.Mode ?? string.Empty,
            ReadWrite: m.RW,
            Propagation: m.Propagation ?? string.Empty)).ToArray();
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    /// <summary>
    /// Docker.DotNet pre-parses RFC3339 timestamps into <see cref="DateTime"/>.
    /// The Engine uses <c>0001-01-01T00:00:00Z</c> as a sentinel for unset
    /// timestamps (e.g. <c>FinishedAt</c> on a still-running container);
    /// coerce that to <c>null</c> and stamp the result as UTC so JSON output
    /// carries the <c>Z</c> suffix the frontend expects.
    /// </summary>
    private static DateTime? NormalizeUtc(DateTime raw)
    {
        if (raw.Year <= 1) return null;
        return DateTime.SpecifyKind(raw, DateTimeKind.Utc);
    }

    private static DateTime? ParseDateTimeOrNull(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return null;
        if (!DateTime.TryParse(raw, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
            return null;
        if (parsed.Year <= 1) return null;
        return DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
    }
}
