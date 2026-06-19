using Stashboard.Core.Abstractions;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Stashboard.Infrastructure.Docker;

/// <summary>
/// V7.7 — production <see cref="IComposeLinter"/>. Runs the V7.7 rule set over the
/// raw Compose YAML with YamlDotNet's representation model:
/// <list type="number">
/// <item><b>port-collision</b> (error) — two services publish the same host
/// port/protocol on the same interface.</item>
/// <item><b>depends-on-cycle</b> (error) — a cycle in the <c>depends_on</c>
/// graph.</item>
/// <item><b>missing-healthcheck</b> (error) — a service is depended on with
/// <c>condition: service_healthy</c> but declares no (enabled) healthcheck.</item>
/// <item><b>bind-mount-escape</b> (warning) — a relative bind-mount source that
/// climbs above the project root (or a <c>~</c> home path).</item>
/// <item><b>deprecated-key</b> (warning) — <c>links</c> / <c>volumes_from</c> on a
/// service, or a top-level <c>version:</c>.</item>
/// <item><b>latest-tag</b> (warning) — an image pinned to <c>latest</c> or with no
/// tag at all.</item>
/// </list>
/// Pure: no file system, no Docker. Reads the raw text (not the parsed
/// <see cref="ComposeProjectModel"/>) so it sees the <c>depends_on</c> conditions,
/// <c>healthcheck</c> blocks and deprecated keys the viewer model drops.
/// </summary>
public sealed class ComposeFileLinter : IComposeLinter
{
    public IReadOnlyList<ComposeLintFinding> Lint(string yamlText, string? projectPath = null)
    {
        YamlMappingNode root;
        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(yamlText));
            if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode mapping)
                return Array.Empty<ComposeLintFinding>();
            root = mapping;
        }
        catch (YamlException)
        {
            // A file that doesn't parse can't be linted; the parser already
            // surfaces the parse error to the UI.
            return Array.Empty<ComposeLintFinding>();
        }

        var findings = new List<ComposeLintFinding>();

        // Service map: name → its mapping node (skip non-mapping / null entries).
        var services = new List<(string Name, YamlMappingNode Map)>();
        if (TryGetMap(root, "services", out var servicesNode))
        {
            foreach (var (name, node) in Entries(servicesNode))
                if (node is YamlMappingNode svc)
                    services.Add((name, svc));
        }

        CheckPortCollisions(services, findings);
        CheckDependsOnCycles(services, findings);
        CheckMissingHealthchecks(services, findings);
        CheckBindMountEscapes(services, projectPath, findings);
        CheckDeprecatedKeys(root, services, findings);
        CheckLatestTags(services, findings);

        return findings;
    }

    // ── port-collision ───────────────────────────────────────────────────────

    private static void CheckPortCollisions(
        IReadOnlyList<(string Name, YamlMappingNode Map)> services, List<ComposeLintFinding> findings)
    {
        // (port, protocol) → list of (service, hostIp). A published port omitting
        // the host part (random host port) or carrying a ${VAR} is skipped.
        var byPort = new Dictionary<(int Port, string Proto), List<(string Service, string HostIp)>>();
        foreach (var (name, map) in services)
        {
            foreach (var published in ParsePublishedPorts(map))
            {
                var key = (published.Port, published.Protocol);
                if (!byPort.TryGetValue(key, out var list))
                    byPort[key] = list = new List<(string, string)>();
                list.Add((name, published.HostIp));
            }
        }

        foreach (var ((port, proto), entries) in byPort)
        {
            if (entries.Count < 2) continue;

            // Distinct specific host IPs on the same port don't clash; a wildcard
            // bind (no IP / 0.0.0.0 / ::) clashes with anything else on that port.
            var hasWildcard = entries.Any(e => IsWildcardIp(e.HostIp));
            var hasDuplicateSpecificIp = entries
                .Where(e => !IsWildcardIp(e.HostIp))
                .GroupBy(e => e.HostIp, StringComparer.Ordinal)
                .Any(g => g.Count() > 1);
            if (!hasWildcard && !hasDuplicateSpecificIp) continue;

            var clashing = entries.Select(e => e.Service).Distinct(StringComparer.Ordinal)
                .OrderBy(s => s, StringComparer.Ordinal).ToList();
            if (clashing.Count < 2) continue; // same service twice is its own concern; skip

            var protoSuffix = proto == "tcp" ? "" : $"/{proto}";
            var others = string.Join(", ", clashing);
            foreach (var svc in clashing)
            {
                findings.Add(new ComposeLintFinding(
                    "port-collision",
                    ComposeLintSeverity.Error,
                    $"Host port {port}{protoSuffix} is published by multiple services ({others}).",
                    svc));
            }
        }
    }

    private readonly record struct PublishedPort(int Port, string Protocol, string HostIp);

    /// <summary>Pulls the published host ports from a service's <c>ports:</c>,
    /// handling both the short string form and the long mapping form. Container-only
    /// (no host port), range and <c>${VAR}</c> mappings are skipped (no single
    /// host port to clash on).</summary>
    private static IEnumerable<PublishedPort> ParsePublishedPorts(YamlMappingNode map)
    {
        if (!TryGetSeq(map, "ports", out var seq)) yield break;
        foreach (var item in seq)
        {
            if (item is YamlScalarNode s && !string.IsNullOrWhiteSpace(s.Value))
            {
                if (TryParseShortPort(s.Value!, out var p)) yield return p;
            }
            else if (item is YamlMappingNode m)
            {
                var publishedRaw = GetScalar(m, "published");
                if (publishedRaw is null) continue; // no host port published
                var protocol = (GetScalar(m, "protocol") ?? "tcp").ToLowerInvariant();
                var hostIp = GetScalar(m, "host_ip") ?? "";
                if (TryParsePortNumber(publishedRaw, out var port))
                    yield return new PublishedPort(port, protocol, hostIp);
            }
        }
    }

    /// <summary>Parses the short port form <c>[hostIp:]host:container[/proto]</c>.
    /// A bare <c>container</c> (random host port), a range or a <c>${VAR}</c> in the
    /// host part returns <c>false</c>.</summary>
    private static bool TryParseShortPort(string raw, out PublishedPort result)
    {
        result = default;
        var value = raw.Trim();

        var protocol = "tcp";
        var slash = value.LastIndexOf('/');
        if (slash >= 0)
        {
            protocol = value[(slash + 1)..].Trim().ToLowerInvariant();
            value = value[..slash];
        }

        var parts = value.Split(':');
        string hostIp = "", hostPort;
        switch (parts.Length)
        {
            case 1:
                return false; // container port only → ephemeral host port, no clash
            case 2:
                hostPort = parts[0];
                break;
            case 3:
                hostIp = parts[0];
                hostPort = parts[1];
                break;
            default:
                return false;
        }

        if (!TryParsePortNumber(hostPort, out var port)) return false;
        result = new PublishedPort(port, protocol, hostIp.Trim());
        return true;
    }

    private static bool TryParsePortNumber(string raw, out int port) =>
        int.TryParse(raw.Trim().Trim('"'), out port) && port is > 0 and <= 65535;

    private static bool IsWildcardIp(string ip) =>
        ip is "" or "0.0.0.0" or "::" or "*" or "[::]";

    // ── depends-on-cycle ───────────────────────────────────────────────────────

    private static void CheckDependsOnCycles(
        IReadOnlyList<(string Name, YamlMappingNode Map)> services, List<ComposeLintFinding> findings)
    {
        var names = new HashSet<string>(services.Select(s => s.Name), StringComparer.Ordinal);
        var edges = services.ToDictionary(
            s => s.Name,
            s => ParseDependencies(s.Map).Keys.Where(names.Contains).Distinct(StringComparer.Ordinal).ToList(),
            StringComparer.Ordinal);

        // DFS with a recursion stack; on a back edge, slice out the cycle path.
        var state = new Dictionary<string, int>(StringComparer.Ordinal); // 0 unvisited, 1 in-stack, 2 done
        var stack = new List<string>();
        var reportedCycles = new HashSet<string>(StringComparer.Ordinal);

        void Visit(string node)
        {
            state[node] = 1;
            stack.Add(node);
            foreach (var next in edges[node])
            {
                if (!state.TryGetValue(next, out var st) || st == 0)
                {
                    Visit(next);
                }
                else if (st == 1)
                {
                    var start = stack.IndexOf(next);
                    if (start < 0) continue;
                    var cycle = stack.Skip(start).ToList();
                    ReportCycle(cycle, reportedCycles, findings);
                }
            }
            stack.RemoveAt(stack.Count - 1);
            state[node] = 2;
        }

        foreach (var (name, _) in services)
            if (!state.TryGetValue(name, out var st) || st == 0)
                Visit(name);
    }

    private static void ReportCycle(
        List<string> cycle, HashSet<string> reported, List<ComposeLintFinding> findings)
    {
        // Dedup by the cycle's node set (rotation-independent), so a 2-node cycle
        // reached from either end is reported once.
        var fingerprint = string.Join("|", cycle.OrderBy(n => n, StringComparer.Ordinal));
        if (!reported.Add(fingerprint)) return;

        var path = string.Join(" → ", cycle.Append(cycle[0]));
        foreach (var node in cycle)
        {
            findings.Add(new ComposeLintFinding(
                "depends-on-cycle",
                ComposeLintSeverity.Error,
                $"depends_on cycle: {path}.",
                node));
        }
    }

    // ── missing-healthcheck ────────────────────────────────────────────────────

    private static void CheckMissingHealthchecks(
        IReadOnlyList<(string Name, YamlMappingNode Map)> services, List<ComposeLintFinding> findings)
    {
        var byName = services.ToDictionary(s => s.Name, s => s.Map, StringComparer.Ordinal);

        // target service → the dependents that wait on it being healthy.
        var waiters = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (name, map) in services)
        {
            foreach (var (dep, condition) in ParseDependencies(map))
            {
                if (condition != "service_healthy") continue;
                if (!byName.ContainsKey(dep)) continue; // unknown dep is a different problem
                if (!waiters.TryGetValue(dep, out var list))
                    waiters[dep] = list = new List<string>();
                list.Add(name);
            }
        }

        foreach (var (target, dependents) in waiters)
        {
            if (HasEnabledHealthcheck(byName[target])) continue;

            var who = string.Join(", ", dependents.Distinct(StringComparer.Ordinal)
                .OrderBy(n => n, StringComparer.Ordinal));
            findings.Add(new ComposeLintFinding(
                "missing-healthcheck",
                ComposeLintSeverity.Error,
                $"'{target}' has no healthcheck, but {who} wait(s) on it with condition: service_healthy "
                    + "— compose rejects this unless the image defines a HEALTHCHECK.",
                target));
        }
    }

    /// <summary>A service has an enabled healthcheck when it declares a
    /// <c>healthcheck:</c> mapping that is neither <c>disable: true</c> nor a
    /// <c>test: NONE</c>.</summary>
    private static bool HasEnabledHealthcheck(YamlMappingNode map)
    {
        if (!TryGetMap(map, "healthcheck", out var hc)) return false;
        if (ParseBool(GetScalar(hc, "disable")) == true) return false;

        if (hc.Children.TryGetValue(new YamlScalarNode("test"), out var test))
        {
            if (test is YamlScalarNode ts && string.Equals(ts.Value, "NONE", StringComparison.Ordinal))
                return false;
            if (test is YamlSequenceNode tseq
                && tseq.Children.Count == 1
                && tseq.Children[0] is YamlScalarNode { Value: "NONE" })
                return false;
        }
        return true;
    }

    // ── bind-mount-escape ──────────────────────────────────────────────────────

    private static void CheckBindMountEscapes(
        IReadOnlyList<(string Name, YamlMappingNode Map)> services, string? projectPath,
        List<ComposeLintFinding> findings)
    {
        foreach (var (name, map) in services)
        {
            foreach (var source in ParseBindSources(map))
            {
                if (!EscapesProjectRoot(source)) continue;
                findings.Add(new ComposeLintFinding(
                    "bind-mount-escape",
                    ComposeLintSeverity.Warning,
                    $"Bind mount '{source}' points outside the project root.",
                    name));
            }
        }
    }

    /// <summary>The bind-mount sources of a service (the host side of a bind),
    /// from both the short string form and the long mapping form
    /// (<c>type: bind</c>). Named/anonymous volumes are excluded.</summary>
    private static IEnumerable<string> ParseBindSources(YamlMappingNode map)
    {
        if (!TryGetSeq(map, "volumes", out var seq)) yield break;
        foreach (var item in seq)
        {
            if (item is YamlScalarNode s && !string.IsNullOrWhiteSpace(s.Value))
            {
                var source = ShortVolumeSource(s.Value!);
                if (source is not null && LooksLikePath(source)) yield return source;
            }
            else if (item is YamlMappingNode m)
            {
                var type = GetScalar(m, "type");
                var source = GetScalar(m, "source");
                if (source is null) continue;
                if (type is null or "bind" && LooksLikePath(source)) yield return source;
            }
        }
    }

    /// <summary>The host side of a short-syntax volume (<c>source:target[:ro]</c>),
    /// or <c>null</c> for an anonymous volume (<c>/data</c> with no source).</summary>
    private static string? ShortVolumeSource(string raw)
    {
        var value = raw.Trim();
        var idx = value.IndexOf(':');
        if (idx <= 0) return null; // no source side (anonymous volume) — nothing to check
        return value[..idx];
    }

    /// <summary>True for a bind source (a filesystem path) rather than a named
    /// volume. Named volumes match <c>^[a-zA-Z0-9._-]+$</c>; anything with a path
    /// shape (leading <c>/</c>, <c>.</c>, <c>~</c>) is a bind.</summary>
    private static bool LooksLikePath(string source) =>
        source.StartsWith('/') || source.StartsWith('.') || source.StartsWith('~');

    /// <summary>Whether a relative bind source climbs above the project root. Only
    /// relative <c>..</c> escapes (and <c>~</c> home paths) are flagged: absolute
    /// paths are treated as deliberate system mounts (<c>/etc/localtime</c>,
    /// <c>/var/run/docker.sock</c>, …) and left alone to keep the rule low-noise.</summary>
    private static bool EscapesProjectRoot(string source)
    {
        if (source.StartsWith('~')) return true;       // home dir — outside the project tree
        if (source.StartsWith('/')) return false;      // absolute — intentional, not flagged
        if (source.Contains("${")) return false;       // variable — can't resolve, skip

        var depth = 0;
        foreach (var seg in source.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (seg == ".") continue;
            if (seg == "..")
            {
                if (--depth < 0) return true;
            }
            else depth++;
        }
        return false;
    }

    // ── deprecated-key ─────────────────────────────────────────────────────────

    private static void CheckDeprecatedKeys(
        YamlMappingNode root, IReadOnlyList<(string Name, YamlMappingNode Map)> services,
        List<ComposeLintFinding> findings)
    {
        if (root.Children.ContainsKey(new YamlScalarNode("version")))
        {
            findings.Add(new ComposeLintFinding(
                "deprecated-key",
                ComposeLintSeverity.Warning,
                "Top-level 'version:' is obsolete in the Compose Specification and is ignored — remove it.",
                null));
        }

        foreach (var (name, map) in services)
        {
            if (map.Children.ContainsKey(new YamlScalarNode("links")))
                findings.Add(new ComposeLintFinding(
                    "deprecated-key",
                    ComposeLintSeverity.Warning,
                    "Deprecated key 'links:' — services on a shared network reach each other by name; drop it.",
                    name));
            if (map.Children.ContainsKey(new YamlScalarNode("volumes_from")))
                findings.Add(new ComposeLintFinding(
                    "deprecated-key",
                    ComposeLintSeverity.Warning,
                    "Deprecated key 'volumes_from:' — declare the shared volume under top-level volumes: instead.",
                    name));
        }
    }

    // ── latest-tag ─────────────────────────────────────────────────────────────

    private static void CheckLatestTags(
        IReadOnlyList<(string Name, YamlMappingNode Map)> services, List<ComposeLintFinding> findings)
    {
        foreach (var (name, map) in services)
        {
            var image = GetScalar(map, "image");
            if (string.IsNullOrWhiteSpace(image)) continue;
            if (image.Contains('@')) continue;        // digest-pinned — explicitly fine

            var tag = ImageTag(image);
            if (tag is null)
            {
                // A bare `${VAR}` for the whole reference is unknown, not "no tag".
                if (image.Contains("${")) continue;
                findings.Add(new ComposeLintFinding(
                    "latest-tag",
                    ComposeLintSeverity.Warning,
                    $"Image '{image}' has no tag (defaults to :latest) — pin a version.",
                    name));
                continue;
            }

            // A `${VAR:-default}` tag resolves to its default when the variable is
            // unset; check that default. A bare `${VAR}` (no default) is genuinely
            // unknown, so it's skipped to avoid false positives.
            var fromVar = tag.Contains("${");
            var effective = ResolveTagDefault(tag);
            if (effective is null || effective.Length == 0) continue;
            if (effective != "latest") continue;

            findings.Add(new ComposeLintFinding(
                "latest-tag",
                ComposeLintSeverity.Warning,
                fromVar
                    ? $"Image '{image}' defaults to :latest when its variable is unset — pin a version "
                        + "(intentional for some homelab setups)."
                    : $"Image '{image}' is pinned to :latest — pin a version (intentional for some homelab setups).",
                name));
        }
    }

    /// <summary>The tag of an image reference, or <c>null</c> when none is set
    /// (implicit <c>:latest</c>). Only the colon in the final path component is a
    /// tag — a registry host port (<c>registry:5000/img</c>) is not.</summary>
    private static string? ImageTag(string image)
    {
        var lastSlash = image.LastIndexOf('/');
        var lastComponent = lastSlash >= 0 ? image[(lastSlash + 1)..] : image;
        var colon = lastComponent.IndexOf(':');
        return colon >= 0 ? lastComponent[(colon + 1)..] : null;
    }

    /// <summary>Resolves a tag that may be a Compose variable expression to the
    /// value the linter should judge. A literal tag is returned as-is. A
    /// <c>${VAR:-default}</c> / <c>${VAR-default}</c> returns its default (so a
    /// <c>:-latest</c> default is still caught, a <c>:-1.27</c> default is not). A
    /// bare <c>${VAR}</c>, a <c>:?</c> / <c>:+</c> form, or any mixed text+variable
    /// returns <c>null</c> — genuinely unknown, so the caller skips it.</summary>
    private static string? ResolveTagDefault(string tag)
    {
        if (!tag.Contains("${")) return tag;

        var t = tag.Trim();
        if (!t.StartsWith("${", StringComparison.Ordinal) || !t.EndsWith("}", StringComparison.Ordinal))
            return null; // mixed literal + variable — can't tell

        var inner = t[2..^1];                                  // e.g. "VAR:-latest"
        if (inner.Contains(":?") || inner.Contains(":+")) return null; // required / alternate

        var i = inner.IndexOf(":-", StringComparison.Ordinal);
        if (i >= 0) return inner[(i + 2)..];
        i = inner.IndexOf('-');                                // ${VAR-default}; var names carry no '-'
        if (i >= 0) return inner[(i + 1)..];
        return null;                                           // bare ${VAR}: no default
    }

    // ── shared parse helpers ───────────────────────────────────────────────────

    /// <summary><c>depends_on</c> as name → condition. The list form
    /// (<c>- db</c>) carries a <c>null</c> condition; the mapping form
    /// (<c>db: { condition: service_healthy }</c>) carries the condition.</summary>
    private static IReadOnlyDictionary<string, string?> ParseDependencies(YamlMappingNode map)
    {
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (!map.Children.TryGetValue(new YamlScalarNode("depends_on"), out var node)) return result;
        switch (node)
        {
            case YamlSequenceNode seq:
                foreach (var s in seq.OfType<YamlScalarNode>())
                    if (!string.IsNullOrEmpty(s.Value)) result[s.Value!] = null;
                break;
            case YamlMappingNode m:
                foreach (var (dep, body) in Entries(m))
                    result[dep] = body is YamlMappingNode bm ? GetScalar(bm, "condition") : null;
                break;
        }
        return result;
    }

    private static bool? ParseBool(string? value) => value?.ToLowerInvariant() switch
    {
        "true" or "yes" or "on" or "1" => true,
        "false" or "no" or "off" or "0" => false,
        _ => null,
    };

    // ── node helpers (mirror ComposeFileParser) ────────────────────────────────

    private static IEnumerable<(string Key, YamlNode Value)> Entries(YamlMappingNode map)
    {
        foreach (var child in map.Children)
            if (child.Key is YamlScalarNode { Value: { Length: > 0 } key })
                yield return (key, child.Value);
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
