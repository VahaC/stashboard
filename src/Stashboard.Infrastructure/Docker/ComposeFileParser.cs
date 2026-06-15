using Stashboard.Core.Abstractions;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Stashboard.Infrastructure.Docker;

/// <summary>
/// V7.0 — production <see cref="IComposeFileParser"/>. Parses a Compose file
/// with YamlDotNet's representation model into the read-only viewer subset
/// (services, top-level networks / volumes / secrets / configs,
/// <c>deploy.resources</c>). Pure: no file system, no Docker.
/// </summary>
/// <remarks>
/// <para>YamlDotNet resolves plain anchor/alias pairs while loading, so simple
/// aliases (<c>restart: *common-restart</c>) just work. Merge keys
/// (<c>&lt;&lt;: *base</c>) are <em>not</em> merged by the representation model —
/// the key survives as a literal <c>&lt;&lt;</c> — so the parser reports them
/// (together with <c>x-*</c> extension fields and <c>extends</c>) as
/// unsupported features instead of silently dropping the merged values.</para>
/// </remarks>
public sealed class ComposeFileParser : IComposeFileParser
{
    public ComposeParseResult Parse(string yamlText)
    {
        YamlMappingNode root;
        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(yamlText));
            if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode mapping)
                return new ComposeParseResult(null, "The Compose file is empty or its root is not a YAML mapping.");
            root = mapping;
        }
        catch (YamlException ex)
        {
            return new ComposeParseResult(null, $"YAML parse error: {ex.Message}");
        }

        var unsupported = new List<string>();

        if (!TryGetMap(root, "services", out var servicesNode))
            return new ComposeParseResult(null, "The Compose file has no 'services' map.");

        // Top-level x-* extension fields (commonly anchor definitions).
        foreach (var key in Keys(root).Where(k => k.StartsWith("x-", StringComparison.Ordinal)))
            unsupported.Add($"extension field '{key}'");

        var services = new List<ComposeServiceModel>();
        foreach (var (name, node) in Entries(servicesNode))
        {
            if (node is not YamlMappingNode serviceMap)
            {
                services.Add(EmptyService(name));
                continue;
            }
            services.Add(ParseService(name, serviceMap, unsupported));
        }

        return new ComposeParseResult(
            new ComposeProjectModel(
                ProjectName: GetScalar(root, "name"),
                Services: services,
                Networks: ParseTopLevelNetworks(root, unsupported),
                Volumes: ParseTopLevelVolumes(root),
                Secrets: ParseFileResources(root, "secrets"),
                Configs: ParseFileResources(root, "configs"),
                UnsupportedFeatures: unsupported.Distinct().ToList()),
            Error: null);
    }

    private static ComposeServiceModel ParseService(string name, YamlMappingNode map, List<string> unsupported)
    {
        foreach (var key in Keys(map))
        {
            if (key == "<<") unsupported.Add($"YAML merge key (<<) in service '{name}'");
            else if (key == "extends") unsupported.Add($"'extends' in service '{name}'");
            else if (key.StartsWith("x-", StringComparison.Ordinal)) unsupported.Add($"extension field '{key}' in service '{name}'");
        }

        return new ComposeServiceModel(
            Name: name,
            Image: GetScalar(map, "image"),
            ContainerName: GetScalar(map, "container_name"),
            Restart: GetScalar(map, "restart"),
            Ports: ParsePorts(map),
            Volumes: ParseVolumes(map),
            Environment: ParseNameValuePairs(map, "environment"),
            EnvFiles: ScalarOrList(map, "env_file"),
            DependsOn: ParseDependsOn(map),
            Networks: ParseServiceNetworks(map),
            Resources: ParseResources(name, map, unsupported),
            Labels: ParseNameValuePairs(map, "labels"),
            Command: ParseCommandLike(map, "command"),
            Entrypoint: ParseCommandLike(map, "entrypoint"),
            User: GetScalar(map, "user"),
            WorkingDir: GetScalar(map, "working_dir"));
    }

    /// <summary>V7.2 — normalises a service's resource constraints across the
    /// two Compose conventions into the flat <see cref="ComposeResourceConstraints"/>
    /// shape. Convention is detected per service: an existing
    /// <c>deploy.resources</c> wins; else any legacy top-level cpu/mem/pids key
    /// makes it legacy; else the modern <c>deploy</c> default. GPU device
    /// reservations (<c>deploy.resources.reservations.devices</c>) are reported
    /// as unsupported so the editor refuses the file rather than dropping them.</summary>
    private static ComposeResourceConstraints ParseResources(
        string serviceName, YamlMappingNode map, List<string> unsupported)
    {
        string? cpuLimit = null, cpuReservation = null, memLimit = null, memReservation = null, pidsLimit = null;

        var hasDeploy = false;
        if (TryGetMap(map, "deploy", out var deploy) && TryGetMap(deploy, "resources", out var resources))
        {
            hasDeploy = true;
            if (TryGetMap(resources, "limits", out var lim))
            {
                cpuLimit = GetScalar(lim, "cpus");
                memLimit = GetScalar(lim, "memory");
                pidsLimit = GetScalar(lim, "pids");
            }
            if (TryGetMap(resources, "reservations", out var res))
            {
                cpuReservation = GetScalar(res, "cpus");
                memReservation = GetScalar(res, "memory");
                if (res.Children.ContainsKey(new YamlScalarNode("devices")))
                    unsupported.Add($"GPU device reservations (deploy.resources.reservations.devices) in service '{serviceName}'");
            }
        }

        var convention = hasDeploy ? "deploy" : "legacy";
        if (!hasDeploy)
        {
            cpuLimit = GetScalar(map, "cpus");
            memLimit = GetScalar(map, "mem_limit");
            memReservation = GetScalar(map, "mem_reservation");
            pidsLimit = GetScalar(map, "pids_limit");
            // No legacy cpu/mem/pids key either → nothing declared; default to
            // the modern form so a first edit lands in deploy.resources.
            if (cpuLimit is null && memLimit is null && memReservation is null && pidsLimit is null)
                convention = "deploy";
        }

        return new ComposeResourceConstraints(
            Convention: convention,
            CpuLimit: cpuLimit,
            CpuReservation: cpuReservation,
            MemLimit: memLimit,
            MemReservation: memReservation,
            PidsLimit: pidsLimit,
            CpuShares: ParseLong(GetScalar(map, "cpu_shares")),
            OomKillDisable: ParseBool(GetScalar(map, "oom_kill_disable")),
            OomScoreAdj: ParseLong(GetScalar(map, "oom_score_adj")),
            ShmSize: GetScalar(map, "shm_size"),
            Ulimits: ParseUlimits(map));
    }

    /// <summary><c>ulimits:</c> is a mapping of name → scalar (symmetric soft=hard)
    /// or → <c>{ soft, hard }</c>.</summary>
    private static IReadOnlyList<ComposeUlimit> ParseUlimits(YamlMappingNode map)
    {
        if (!TryGetMap(map, "ulimits", out var ulimits)) return Array.Empty<ComposeUlimit>();
        var result = new List<ComposeUlimit>();
        foreach (var (name, node) in Entries(ulimits))
        {
            if (node is YamlScalarNode s)
            {
                var v = ParseLong(s.Value);
                result.Add(new ComposeUlimit(name, v, v));
            }
            else if (node is YamlMappingNode m)
            {
                result.Add(new ComposeUlimit(name, ParseLong(GetScalar(m, "soft")), ParseLong(GetScalar(m, "hard"))));
            }
        }
        return result;
    }

    private static long? ParseLong(string? value) =>
        long.TryParse(value, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : null;

    private static bool? ParseBool(string? value) => value?.ToLowerInvariant() switch
    {
        "true" or "yes" or "on" or "1" => true,
        "false" or "no" or "off" or "0" => false,
        _ => null,
    };

    // ── per-field normalisers ───────────────────────────────────────────────

    /// <summary>Short syntax passes through; long syntax (<c>target</c> /
    /// <c>published</c> / <c>protocol</c>) is folded back into
    /// <c>published:target/protocol</c> so the UI renders one shape.</summary>
    private static IReadOnlyList<string> ParsePorts(YamlMappingNode map)
    {
        var result = new List<string>();
        if (!TryGetSeq(map, "ports", out var seq)) return result;
        foreach (var item in seq)
        {
            if (item is YamlScalarNode s)
            {
                if (!string.IsNullOrEmpty(s.Value)) result.Add(s.Value);
            }
            else if (item is YamlMappingNode m)
            {
                var target = GetScalar(m, "target");
                if (target is null) continue;
                var published = GetScalar(m, "published");
                var protocol = GetScalar(m, "protocol");
                var text = published is null ? target : $"{published}:{target}";
                if (protocol is not null) text += $"/{protocol}";
                result.Add(text);
            }
        }
        return result;
    }

    /// <summary>Short syntax passes through; long syntax is folded into
    /// <c>source:target[:ro]</c> (or <c>type:target</c> for source-less mounts
    /// like tmpfs).</summary>
    private static IReadOnlyList<string> ParseVolumes(YamlMappingNode map)
    {
        var result = new List<string>();
        if (!TryGetSeq(map, "volumes", out var seq)) return result;
        foreach (var item in seq)
        {
            if (item is YamlScalarNode s)
            {
                if (!string.IsNullOrEmpty(s.Value)) result.Add(s.Value);
            }
            else if (item is YamlMappingNode m)
            {
                var target = GetScalar(m, "target");
                if (target is null) continue;
                var source = GetScalar(m, "source") ?? GetScalar(m, "type");
                var text = source is null ? target : $"{source}:{target}";
                if (GetScalar(m, "read_only") is "true") text += ":ro";
                result.Add(text);
            }
        }
        return result;
    }

    /// <summary>Accepts both the mapping form (<c>KEY: value</c>) and the list
    /// form (<c>- KEY=value</c> / pass-through <c>- KEY</c>). Shared by
    /// <c>environment</c> and (V7.1) <c>labels</c>, which use the same dual shape.</summary>
    private static IReadOnlyList<ComposeEnvVar> ParseNameValuePairs(YamlMappingNode map, string key)
    {
        var result = new List<ComposeEnvVar>();
        if (!map.Children.TryGetValue(new YamlScalarNode(key), out var node)) return result;
        if (node is YamlMappingNode envMap)
        {
            foreach (var (name, value) in Entries(envMap))
                result.Add(new ComposeEnvVar(name, (value as YamlScalarNode)?.Value));
        }
        else if (node is YamlSequenceNode envSeq)
        {
            foreach (var item in envSeq.OfType<YamlScalarNode>())
            {
                var raw = item.Value;
                if (string.IsNullOrEmpty(raw)) continue;
                var idx = raw.IndexOf('=');
                result.Add(idx < 0
                    ? new ComposeEnvVar(raw, null)
                    : new ComposeEnvVar(raw[..idx], raw[(idx + 1)..]));
            }
        }
        return result;
    }

    /// <summary>V7.1 — <c>command:</c> / <c>entrypoint:</c>. The string (shell)
    /// form passes through; the exec (list) form is folded into a JSON-style
    /// flow string so one text field shows / edits both forms.</summary>
    private static string? ParseCommandLike(YamlMappingNode map, string key)
    {
        if (!map.Children.TryGetValue(new YamlScalarNode(key), out var node)) return null;
        return node switch
        {
            YamlScalarNode s when !string.IsNullOrEmpty(s.Value) => s.Value,
            YamlSequenceNode seq => "[" + string.Join(", ", seq.OfType<YamlScalarNode>()
                .Select(s => "\"" + (s.Value ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"")) + "]",
            _ => null,
        };
    }

    /// <summary>Accepts both the list form and the conditional mapping form
    /// (<c>db: { condition: service_healthy }</c>); only the names matter here.</summary>
    private static IReadOnlyList<string> ParseDependsOn(YamlMappingNode map)
    {
        if (!map.Children.TryGetValue(new YamlScalarNode("depends_on"), out var node))
            return Array.Empty<string>();
        return node switch
        {
            YamlSequenceNode seq => seq.OfType<YamlScalarNode>()
                .Select(s => s.Value).Where(v => !string.IsNullOrEmpty(v)).Select(v => v!).ToList(),
            YamlMappingNode m => Keys(m).ToList(),
            _ => Array.Empty<string>(),
        };
    }

    /// <summary>Accepts both the list form and the per-network options mapping
    /// form; only the names matter on the viewer.</summary>
    private static IReadOnlyList<string> ParseServiceNetworks(YamlMappingNode map)
    {
        if (!map.Children.TryGetValue(new YamlScalarNode("networks"), out var node))
            return Array.Empty<string>();
        return node switch
        {
            YamlSequenceNode seq => seq.OfType<YamlScalarNode>()
                .Select(s => s.Value).Where(v => !string.IsNullOrEmpty(v)).Select(v => v!).ToList(),
            YamlMappingNode m => Keys(m).ToList(),
            _ => Array.Empty<string>(),
        };
    }

    private static IReadOnlyList<string> ScalarOrList(YamlMappingNode map, string key)
    {
        if (!map.Children.TryGetValue(new YamlScalarNode(key), out var node))
            return Array.Empty<string>();
        return node switch
        {
            YamlScalarNode s when !string.IsNullOrEmpty(s.Value) => new[] { s.Value },
            YamlSequenceNode seq => seq.OfType<YamlScalarNode>()
                .Select(s => s.Value).Where(v => !string.IsNullOrEmpty(v)).Select(v => v!).ToList(),
            _ => Array.Empty<string>(),
        };
    }

    // ── V7.3 — top-level resource maps with editable options ─────────────────

    /// <summary>Parses top-level <c>networks:</c> into name + driver / driver_opts
    /// / ipam (first subnet+gateway) / external / name. A network with more than
    /// one <c>ipam.config</c> entry is reported as unsupported so the editor
    /// refuses the file rather than dropping the extra subnets.</summary>
    private static IReadOnlyList<ComposeNetworkModel> ParseTopLevelNetworks(
        YamlMappingNode root, List<string> unsupported)
    {
        if (!TryGetMap(root, "networks", out var map)) return Array.Empty<ComposeNetworkModel>();
        var result = new List<ComposeNetworkModel>();
        foreach (var (name, node) in Entries(map))
        {
            var body = node as YamlMappingNode;
            var (external, nameOverride) = ParseExternal(body);
            string? subnet = null, gateway = null;
            if (body is not null && TryGetMap(body, "ipam", out var ipam)
                && ipam.Children.TryGetValue(new YamlScalarNode("config"), out var cfgNode)
                && cfgNode is YamlSequenceNode cfgSeq)
            {
                if (cfgSeq.Children.Count > 1)
                    unsupported.Add($"multiple ipam.config entries in network '{name}'");
                if (cfgSeq.Children.FirstOrDefault() is YamlMappingNode first)
                {
                    subnet = GetScalar(first, "subnet");
                    gateway = GetScalar(first, "gateway");
                }
            }
            result.Add(new ComposeNetworkModel(
                Name: name,
                External: external,
                NameOverride: nameOverride,
                Driver: body is null ? null : GetScalar(body, "driver"),
                Subnet: subnet,
                Gateway: gateway,
                DriverOpts: ParseDriverOpts(body)));
        }
        return result;
    }

    /// <summary>Parses top-level <c>volumes:</c> into name + driver / driver_opts
    /// / external / name.</summary>
    private static IReadOnlyList<ComposeVolumeModel> ParseTopLevelVolumes(YamlMappingNode root)
    {
        if (!TryGetMap(root, "volumes", out var map)) return Array.Empty<ComposeVolumeModel>();
        var result = new List<ComposeVolumeModel>();
        foreach (var (name, node) in Entries(map))
        {
            var body = node as YamlMappingNode;
            var (external, nameOverride) = ParseExternal(body);
            result.Add(new ComposeVolumeModel(
                Name: name,
                External: external,
                NameOverride: nameOverride,
                Driver: body is null ? null : GetScalar(body, "driver"),
                DriverOpts: ParseDriverOpts(body)));
        }
        return result;
    }

    /// <summary>Parses top-level <c>secrets:</c> / <c>configs:</c> (same shape):
    /// name + external/name or a host <c>file:</c> path.</summary>
    private static IReadOnlyList<ComposeFileResourceModel> ParseFileResources(YamlMappingNode root, string key)
    {
        if (!TryGetMap(root, key, out var map)) return Array.Empty<ComposeFileResourceModel>();
        var result = new List<ComposeFileResourceModel>();
        foreach (var (name, node) in Entries(map))
        {
            var body = node as YamlMappingNode;
            var (external, nameOverride) = ParseExternal(body);
            result.Add(new ComposeFileResourceModel(
                Name: name,
                External: external,
                NameOverride: nameOverride,
                File: body is null ? null : GetScalar(body, "file")));
        }
        return result;
    }

    /// <summary><c>external:</c> is either a bool scalar or (legacy) a mapping
    /// carrying <c>name:</c>; a sibling top-level <c>name:</c> also overrides the
    /// real resource name. Returns (isExternal, nameOverride).</summary>
    private static (bool External, string? NameOverride) ParseExternal(YamlMappingNode? body)
    {
        if (body is null) return (false, null);
        var nameOverride = GetScalar(body, "name");
        if (!body.Children.TryGetValue(new YamlScalarNode("external"), out var ext))
            return (false, nameOverride);
        if (ext is YamlScalarNode s)
            return (ParseBool(s.Value) == true, nameOverride);
        if (ext is YamlMappingNode m)
            return (true, nameOverride ?? GetScalar(m, "name"));
        return (false, nameOverride);
    }

    /// <summary><c>driver_opts:</c> mapping (key → scalar) as name/value pairs.</summary>
    private static IReadOnlyList<ComposeEnvVar> ParseDriverOpts(YamlMappingNode? body)
    {
        if (body is null || !TryGetMap(body, "driver_opts", out var opts))
            return Array.Empty<ComposeEnvVar>();
        var result = new List<ComposeEnvVar>();
        foreach (var (name, value) in Entries(opts))
            result.Add(new ComposeEnvVar(name, (value as YamlScalarNode)?.Value));
        return result;
    }

    private static ComposeServiceModel EmptyService(string name) => new(
        name, Image: null, ContainerName: null, Restart: null,
        Ports: Array.Empty<string>(), Volumes: Array.Empty<string>(),
        Environment: Array.Empty<ComposeEnvVar>(), EnvFiles: Array.Empty<string>(),
        DependsOn: Array.Empty<string>(), Networks: Array.Empty<string>(),
        Resources: ComposeResourceConstraints.Empty,
        Labels: Array.Empty<ComposeEnvVar>(), Command: null, Entrypoint: null,
        User: null, WorkingDir: null);

    // ── node helpers ────────────────────────────────────────────────────────

    private static IEnumerable<string> Keys(YamlMappingNode map) =>
        map.Children.Keys.OfType<YamlScalarNode>()
            .Select(k => k.Value).Where(v => !string.IsNullOrEmpty(v)).Select(v => v!);

    private static IEnumerable<(string Key, YamlNode Value)> Entries(YamlMappingNode map)
    {
        foreach (var child in map.Children)
        {
            if (child.Key is YamlScalarNode { Value: { Length: > 0 } key })
                yield return (key, child.Value);
        }
    }

    private static string? GetScalar(YamlMappingNode map, string key) =>
        map.Children.TryGetValue(new YamlScalarNode(key), out var node) && node is YamlScalarNode s
            ? s.Value
            : null;

    private static bool TryGetMap(YamlMappingNode map, string key, out YamlMappingNode result)
    {
        if (map.Children.TryGetValue(new YamlScalarNode(key), out var node) && node is YamlMappingNode m)
        {
            result = m;
            return true;
        }
        result = default!;
        return false;
    }

    private static bool TryGetSeq(YamlMappingNode map, string key, out YamlSequenceNode result)
    {
        if (map.Children.TryGetValue(new YamlScalarNode(key), out var node) && node is YamlSequenceNode s)
        {
            result = s;
            return true;
        }
        result = default!;
        return false;
    }
}
