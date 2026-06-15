using System.Globalization;
using Stashboard.Core.Abstractions;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace Stashboard.Infrastructure.Docker;

/// <summary>
/// V7.1 — production <see cref="IComposeFileEditor"/>. Locates the exact text
/// spans of the edited service keys by walking YamlDotNet's low-level event
/// stream (whose marks point at the literal token positions, unlike the
/// representation model where an alias inherits its anchor's marks) and splices
/// the raw file text only there. Everything outside the edited keys — comments,
/// key order, quoting style, blank lines — survives byte-for-byte.
/// </summary>
/// <remarks>
/// <para>Granularity is per service key: editing one env var rewrites the whole
/// <c>environment:</c> block (its in-block comments are lost), but a field whose
/// desired state equals the file's current state is never touched. See
/// docs/adr/0001-compose-yaml-round-trip.md for why this beats a full
/// load-and-redump with either YamlDotNet or SharpYaml.</para>
/// <para>Safety refusals (typed errors, never silent data loss): flow-style
/// service bodies, merge keys in the service, anchors declared on or inside an
/// edited value (splicing those would break <c>*alias</c> references elsewhere).</para>
/// </remarks>
public sealed class ComposeFileEditor(IComposeFileParser parser) : IComposeFileEditor
{
    public ComposeEditResult ApplyServiceEdit(string yamlText, string serviceName, ComposeServiceEdit edit)
    {
        // 1) Current normalised state via the V7.0 parser — the diff baseline.
        var parsed = parser.Parse(yamlText);
        if (parsed.Project is null)
            return ComposeEditResult.Failed(parsed.Error!);
        var current = parsed.Project.Services.FirstOrDefault(s => s.Name == serviceName);
        if (current is null)
            return ComposeEditResult.Failed($"Service '{serviceName}' was not found in the Compose file.");

        if (ValidateEdit(edit) is { } inputError)
            return ComposeEditResult.Failed(inputError);

        // 2) Exact token spans for the service's keys via the event stream.
        ServiceCapture svc;
        try
        {
            var captured = CaptureService(yamlText, serviceName);
            if (captured is null)
                return ComposeEditResult.Failed($"Service '{serviceName}' was not found in the Compose file.");
            svc = captured;
        }
        catch (YamlException ex)
        {
            return ComposeEditResult.Failed($"YAML parse error: {ex.Message}");
        }

        if (svc.FlowBody)
            return ComposeEditResult.Failed(
                $"Service '{serviceName}' uses a flow-style ({{…}}) body — edit the file manually.");
        if (svc.HasMergeKey)
            return ComposeEditResult.Failed(
                $"Service '{serviceName}' uses a YAML merge key (<<) — edit the file manually.");

        var eol = yamlText.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var keyIndent = new string(' ', svc.BodyIndent);
        var itemIndent = keyIndent + "  ";

        // 3) Build splice operations only for fields whose value actually changed.
        var ops = new List<SpliceOp>();
        var additions = new List<string>();

        string? fieldError = null;
        void Scalar(string key, string? currentValue, string? desired, bool allowFlowList = false, bool raw = false) =>
            fieldError ??= BuildScalarOp(svc, yamlText, eol, keyIndent, ops, additions,
                key, currentValue, desired, allowFlowList, raw);
        void Block(string key, bool changed, bool desiredEmpty, Func<NodeKind, string> render) =>
            fieldError ??= BuildBlockOp(svc, yamlText, eol, ops, additions, key, changed, desiredEmpty, render);

        Scalar("image", current.Image, edit.Image);
        Scalar("restart", current.Restart, edit.Restart);
        Scalar("command", current.Command, edit.Command, allowFlowList: true);
        Scalar("entrypoint", current.Entrypoint, edit.Entrypoint, allowFlowList: true);
        Scalar("user", current.User, edit.User);
        Scalar("working_dir", current.WorkingDir, edit.WorkingDir);

        Block("ports",
            changed: !SequenceEqual(current.Ports, edit.Ports),
            desiredEmpty: edit.Ports.Count == 0,
            render: _ => "ports:" + string.Concat(edit.Ports.Select(p => eol + itemIndent + "- " + Quote(p))));
        Block("volumes",
            changed: !SequenceEqual(current.Volumes, edit.Volumes),
            desiredEmpty: edit.Volumes.Count == 0,
            render: _ => "volumes:" + string.Concat(edit.Volumes.Select(v => eol + itemIndent + "- " + RenderScalar(v))));
        Block("environment",
            changed: !PairsEqual(current.Environment, edit.Environment),
            desiredEmpty: edit.Environment.Count == 0,
            render: kind => RenderPairsBlock("environment", edit.Environment, kind, eol, itemIndent));
        Block("labels",
            changed: !PairsEqual(current.Labels, edit.Labels),
            desiredEmpty: edit.Labels.Count == 0,
            render: kind => RenderPairsBlock("labels", edit.Labels, kind, eol, itemIndent));

        // V7.2 — resource constraints. cpu_shares / oom_* / shm_size / ulimits
        // are always top-level; cpu/mem/pids follow the file's convention.
        var curR = current.Resources;
        var edR = edit.Resources;
        Scalar("cpu_shares", LongStr(curR.CpuShares), LongStr(edR.CpuShares), raw: true);
        Scalar("oom_kill_disable", BoolStr(curR.OomKillDisable), BoolStr(edR.OomKillDisable), raw: true);
        Scalar("oom_score_adj", LongStr(curR.OomScoreAdj), LongStr(edR.OomScoreAdj), raw: true);
        Scalar("shm_size", curR.ShmSize, edR.ShmSize);
        Block("ulimits",
            changed: !UlimitsEqual(curR.Ulimits, edR.Ulimits),
            desiredEmpty: edR.Ulimits.Count == 0,
            render: _ => RenderUlimitsBlock(edR.Ulimits, eol, itemIndent));

        if (edR.Convention == "legacy")
        {
            Scalar("cpus", curR.CpuLimit, edR.CpuLimit, raw: true);
            Scalar("mem_limit", curR.MemLimit, edR.MemLimit);
            Scalar("mem_reservation", curR.MemReservation, edR.MemReservation);
            Scalar("pids_limit", curR.PidsLimit, edR.PidsLimit, raw: true);
        }
        else
        {
            fieldError ??= BuildDeployResourcesOp(svc, yamlText, eol, keyIndent, ops, additions, curR, edR);
        }

        if (fieldError is not null)
            return ComposeEditResult.Failed(fieldError);

        if (additions.Count > 0)
        {
            // All new keys land together after the service's last existing line.
            var insertAt = EndOfLine(yamlText, svc.LastContentEnd);
            var text = string.Concat(additions.Select(block => eol + keyIndent + block));
            ops.Add(new SpliceOp(insertAt, insertAt, text));
        }

        if (ops.Count == 0)
            return ComposeEditResult.Unchanged(yamlText);

        var result = yamlText;
        foreach (var op in ops.OrderByDescending(o => o.Start))
            result = result[..(int)op.Start] + op.Replacement + result[(int)op.End..];
        return ComposeEditResult.Edited(result);
    }

    // ── V7.4 — add a brand-new service ────────────────────────────────────────

    private static readonly System.Text.RegularExpressions.Regex ServiceNameRegex =
        new("^[a-zA-Z0-9._-]+$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Compose project names must be lowercase and start with an
    /// alphanumeric (the daemon lowercases anything else).</summary>
    private static readonly System.Text.RegularExpressions.Regex ProjectNameRegex =
        new("^[a-z0-9][a-z0-9_-]*$", System.Text.RegularExpressions.RegexOptions.Compiled);

    public ComposeEditResult CreateFile(string projectName, string serviceName, ComposeServiceEdit edit)
    {
        if (string.IsNullOrWhiteSpace(projectName) || !ProjectNameRegex.IsMatch(projectName))
            return ComposeEditResult.Failed(
                $"Project name '{projectName}' must match ^[a-z0-9][a-z0-9_-]*$ (lowercase).");
        if (string.IsNullOrWhiteSpace(serviceName) || !ServiceNameRegex.IsMatch(serviceName))
            return ComposeEditResult.Failed(
                $"Service name '{serviceName}' must match ^[a-zA-Z0-9._-]+$.");
        if (string.IsNullOrWhiteSpace(edit.Image))
            return ComposeEditResult.Failed("A new service must set an image.");
        if (ValidateEdit(edit) is { } inputError)
            return ComposeEditResult.Failed(inputError);
        foreach (var (label, value) in new[] { ("command", edit.Command), ("entrypoint", edit.Entrypoint) })
            if (value is not null && value.TrimStart().StartsWith('[') && !IsValidFlowSequence(value))
                return ComposeEditResult.Failed(
                    $"'{label}' looks like an exec-form list but is not valid YAML: {value}");

        const string eol = "\n";
        var body = RenderServiceBody(edit, eol, "    ");
        var content = $"name: {RenderScalar(projectName)}{eol}services:{eol}  {RenderScalar(serviceName)}:{body}{eol}";
        return ComposeEditResult.Edited(content);
    }

    public ComposeEditResult AddService(string yamlText, string serviceName, ComposeServiceEdit edit)
    {
        var parsed = parser.Parse(yamlText);
        if (parsed.Project is null)
            return ComposeEditResult.Failed(parsed.Error!);

        if (string.IsNullOrWhiteSpace(serviceName) || !ServiceNameRegex.IsMatch(serviceName))
            return ComposeEditResult.Failed(
                $"Service name '{serviceName}' must match ^[a-zA-Z0-9._-]+$.");
        if (parsed.Project.Services.Any(s => s.Name == serviceName))
            return ComposeEditResult.Failed(
                $"A service named '{serviceName}' already exists in this Compose file.");
        if (string.IsNullOrWhiteSpace(edit.Image))
            return ComposeEditResult.Failed("A new service must set an image.");
        if (ValidateEdit(edit) is { } inputError)
            return ComposeEditResult.Failed(inputError);

        // command / entrypoint exec-form (`["sh", "-c", …]`) must be valid YAML.
        foreach (var (label, value) in new[] { ("command", edit.Command), ("entrypoint", edit.Entrypoint) })
            if (value is not null && value.TrimStart().StartsWith('[') && !IsValidFlowSequence(value))
                return ComposeEditResult.Failed(
                    $"'{label}' looks like an exec-form list but is not valid YAML: {value}");

        SectionCapture services;
        try
        {
            services = CaptureSection(yamlText, "services");
        }
        catch (YamlException ex)
        {
            return ComposeEditResult.Failed($"YAML parse error: {ex.Message}");
        }

        if (services.Exists && (services.FlowOrNonMapping || services.Anchored))
            return ComposeEditResult.Failed(
                "The top-level 'services:' uses a style the editor can't extend safely "
                + "(flow style, anchors, or a non-mapping body) — edit the file manually.");

        var eol = yamlText.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        // New service sits at the existing services' column (honours 2- vs
        // 4-space) with a 2-space body increment — the same shape the service
        // editor uses when appending keys.
        var entryCols = services.Exists && services.EntryIndent >= 0
            ? services.EntryIndent
            : services.RootIndent + 2;
        var entryIndent = new string(' ', entryCols);
        var bodyIndent = new string(' ', entryCols + 2);

        var block = RenderScalar(serviceName) + ":" + RenderServiceBody(edit, eol, bodyIndent);

        SpliceOp op;
        if (!services.Exists)
        {
            var leading = yamlText.Length > 0 && !yamlText.EndsWith('\n') ? eol : "";
            var section = "services:" + eol + entryIndent + block;
            op = new SpliceOp(yamlText.Length, yamlText.Length, leading + section);
        }
        else
        {
            var insertAt = EndOfLine(yamlText, services.SectionValueEnd);
            op = new SpliceOp(insertAt, insertAt, eol + entryIndent + block);
        }

        return ComposeEditResult.Edited(yamlText[..(int)op.Start] + op.Replacement + yamlText[(int)op.End..]);
    }

    /// <summary>Renders the body of a new service (every set field) at
    /// <paramref name="ind"/>, each key on its own line. <c>image</c> is always
    /// present (the caller guarantees it); the resource fields follow the chosen
    /// convention, matching <see cref="ApplyServiceEdit"/>'s write shape.</summary>
    private static string RenderServiceBody(ComposeServiceEdit edit, string eol, string ind)
    {
        var item = ind + "  ";
        var parts = new List<string> { $"image: {RenderScalar(edit.Image!)}" };

        if (edit.Restart is not null) parts.Add($"restart: {RenderScalar(edit.Restart)}");
        if (edit.Command is not null) parts.Add($"command: {RenderCommandValue(edit.Command)}");
        if (edit.Entrypoint is not null) parts.Add($"entrypoint: {RenderCommandValue(edit.Entrypoint)}");
        if (edit.User is not null) parts.Add($"user: {RenderScalar(edit.User)}");
        if (edit.WorkingDir is not null) parts.Add($"working_dir: {RenderScalar(edit.WorkingDir)}");

        if (edit.Ports.Count > 0)
            parts.Add("ports:" + string.Concat(edit.Ports.Select(p => eol + item + "- " + Quote(p))));
        if (edit.Volumes.Count > 0)
            parts.Add("volumes:" + string.Concat(edit.Volumes.Select(v => eol + item + "- " + RenderScalar(v))));
        if (edit.Environment.Count > 0)
            parts.Add(RenderPairsBlock("environment", edit.Environment, NodeKind.Mapping, eol, item));
        if (edit.Labels.Count > 0)
            parts.Add(RenderPairsBlock("labels", edit.Labels, NodeKind.Mapping, eol, item));

        var r = edit.Resources;
        if (r.CpuShares is not null) parts.Add($"cpu_shares: {r.CpuShares}");
        if (r.OomKillDisable is not null) parts.Add($"oom_kill_disable: {(r.OomKillDisable.Value ? "true" : "false")}");
        if (r.OomScoreAdj is not null) parts.Add($"oom_score_adj: {r.OomScoreAdj}");
        if (r.ShmSize is not null) parts.Add($"shm_size: {RenderScalar(r.ShmSize)}");
        if (r.Ulimits.Count > 0) parts.Add(RenderUlimitsBlock(r.Ulimits, eol, item));

        if (r.Convention == "legacy")
        {
            if (r.CpuLimit is not null) parts.Add($"cpus: {r.CpuLimit.Trim()}");
            if (r.MemLimit is not null) parts.Add($"mem_limit: {RenderScalar(r.MemLimit)}");
            if (r.MemReservation is not null) parts.Add($"mem_reservation: {RenderScalar(r.MemReservation)}");
            if (r.PidsLimit is not null) parts.Add($"pids_limit: {r.PidsLimit.Trim()}");
        }
        else if (r.CpuLimit is not null || r.CpuReservation is not null || r.MemLimit is not null
                 || r.MemReservation is not null || r.PidsLimit is not null)
        {
            parts.Add("deploy:" + eol + item + RenderResources(r, eol, item));
        }

        return string.Concat(parts.Select(p => eol + ind + p));
    }

    /// <summary>command / entrypoint render: an exec-form list (already
    /// validated) stays as-is; anything else goes through the scalar quoter.</summary>
    private static string RenderCommandValue(string desired) =>
        desired.TrimStart().StartsWith('[') ? desired.Trim() : RenderScalar(desired);

    // ── per-field op builders ───────────────────────────────────────────────

    /// <summary>Scalar-valued key (image / restart / user / working_dir, plus
    /// command / entrypoint where the desired value may be a <c>["exec"]</c>
    /// flow list). Returns an error string, or <c>null</c> on success.</summary>
    private static string? BuildScalarOp(
        ServiceCapture svc, string yamlText, string eol, string keyIndent,
        List<SpliceOp> ops, List<string> additions,
        string key, string? currentValue, string? desired, bool allowFlowList = false, bool raw = false)
    {
        if (string.Equals(currentValue, desired, StringComparison.Ordinal)) return null;

        string? rendered = null;
        if (desired is not null)
        {
            if (allowFlowList && desired.TrimStart().StartsWith('['))
            {
                if (!IsValidFlowSequence(desired))
                    return $"'{key}' looks like an exec-form list but is not valid YAML: {desired}";
                rendered = desired.Trim();
            }
            else if (raw)
            {
                // V7.2 — numeric / boolean resource values render unquoted so
                // integer-typed keys (pids_limit, cpu_shares) and the deploy
                // `cpus` float stay valid; the value is already normalised
                // upstream (numeric inputs + ParseLong/ParseBool round-trip).
                rendered = desired.Trim();
            }
            else
            {
                rendered = RenderScalar(desired);
            }
        }

        if (svc.Fields.TryGetValue(key, out var field))
        {
            if (field.Value.Anchored)
                return $"'{key}' carries a YAML anchor (&…) that other parts of the file may reference — edit the file manually.";

            if (rendered is null)
                ops.Add(RemoveKeyOp(yamlText, field));
            else if (field.Value.Kind is NodeKind.Scalar or NodeKind.Alias)
                ops.Add(new SpliceOp(field.Value.Start, field.Value.End, rendered));
            else
                // Block list (e.g. exec-form command) or empty value: rewrite key + value.
                ops.Add(new SpliceOp(field.Key.Start.Index, field.Value.End, $"{key}: {rendered}"));
        }
        else if (rendered is not null)
        {
            additions.Add($"{key}: {rendered}");
        }
        return null;
    }

    /// <summary>List/map-valued key (ports / volumes / environment / labels).
    /// <paramref name="render"/> receives the existing node kind so name/value
    /// pairs can keep the file's mapping-vs-list style.</summary>
    private static string? BuildBlockOp(
        ServiceCapture svc, string yamlText, string eol,
        List<SpliceOp> ops, List<string> additions,
        string key, bool changed, bool desiredEmpty, Func<NodeKind, string> render)
    {
        if (!changed) return null;

        if (svc.Fields.TryGetValue(key, out var field))
        {
            if (field.Value.Anchored)
                return $"'{key}' contains a YAML anchor (&…) that other parts of the file may reference — edit the file manually.";

            if (desiredEmpty)
                ops.Add(RemoveKeyOp(yamlText, field));
            else
                ops.Add(new SpliceOp(field.Key.Start.Index, field.Value.End, render(field.Value.Kind)));
        }
        else if (!desiredEmpty)
        {
            additions.Add(render(NodeKind.Mapping));
        }
        return null;
    }

    /// <summary>Removes the whole key line(s). When the key starts its own line
    /// (the normal case) whole lines go; otherwise just the key+value span.</summary>
    private static SpliceOp RemoveKeyOp(string yamlText, FieldCapture field)
    {
        var keyStart = field.Key.Start.Index;
        var lineStart = yamlText.LastIndexOf('\n', (int)Math.Max(0, keyStart - 1)) + 1;
        var onlyEntryOnLine = yamlText[(int)lineStart..(int)keyStart].All(char.IsWhiteSpace);
        if (!onlyEntryOnLine)
            return new SpliceOp(keyStart, field.Value.End, "");
        var lineEnd = yamlText.IndexOf('\n', (int)field.Value.End);
        return new SpliceOp(lineStart, lineEnd < 0 ? yamlText.Length : lineEnd + 1, "");
    }

    private static long EndOfLine(string text, long index)
    {
        var nl = text.IndexOf('\n', (int)Math.Min(index, text.Length));
        if (nl < 0) return text.Length;
        // Insert before the \r of a \r\n pair so spliced text keeps clean CRLFs.
        return nl > 0 && text[nl - 1] == '\r' ? nl - 1 : nl;
    }

    // ── rendering ───────────────────────────────────────────────────────────

    private static string RenderPairsBlock(
        string key, IReadOnlyList<ComposeEnvVar> pairs, NodeKind existingKind, string eol, string itemIndent)
    {
        // Keep the file's existing style; new keys default to the mapping form.
        var listStyle = existingKind == NodeKind.Sequence;
        return key + ":" + string.Concat(pairs.Select(p => eol + itemIndent + (listStyle
            ? "- " + RenderScalar(p.Value is null ? p.Name : $"{p.Name}={p.Value}")
            : RenderScalar(p.Name) + ":" + (p.Value is null ? "" : " " + RenderScalar(p.Value)))));
    }

    private static string RenderScalar(string value) => NeedsQuoting(value) ? Quote(value) : value;

    private static string Quote(string value) =>
        "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    /// <summary>Conservative plain-scalar check: when in doubt, quote. Covers
    /// YAML indicators, comment/mapping ambiguity, and bool / null / number
    /// lookalikes (so an env value like <c>1.5</c> or <c>no</c> stays a string).</summary>
    private static bool NeedsQuoting(string v)
    {
        if (v.Length == 0) return true;
        if (char.IsWhiteSpace(v[0]) || char.IsWhiteSpace(v[^1])) return true;
        if ("!&*?{}[]#,%@`\"'>|".Contains(v[0])) return true;
        if (v is "-" or ":" || v.StartsWith("- ", StringComparison.Ordinal) || v.StartsWith(": ", StringComparison.Ordinal)) return true;
        if (v.Contains(": ", StringComparison.Ordinal) || v.EndsWith(':')) return true;
        if (v.Contains(" #", StringComparison.Ordinal)) return true;
        if (v.Contains('\n') || v.Contains('\t')) return true;
        var lower = v.ToLowerInvariant();
        if (lower is "true" or "false" or "yes" or "no" or "on" or "off" or "null" or "~") return true;
        if (double.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out _)) return true;
        if (v.All(c => char.IsDigit(c) || c is ':' or '_')) return true; // YAML 1.1 sexagesimal trap (ports)
        return false;
    }

    private static bool IsValidFlowSequence(string text)
    {
        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader("x: " + text.Trim()));
            return stream.Documents.Count == 1
                && stream.Documents[0].RootNode is YamlMappingNode m
                && m.Children.First().Value is YamlSequenceNode;
        }
        catch (YamlException)
        {
            return false;
        }
    }

    // ── comparisons ─────────────────────────────────────────────────────────

    private static bool SequenceEqual(IReadOnlyList<string> a, IReadOnlyList<string> b) =>
        a.Count == b.Count && a.Zip(b).All(p => string.Equals(p.First, p.Second, StringComparison.Ordinal));

    private static bool PairsEqual(IReadOnlyList<ComposeEnvVar> a, IReadOnlyList<ComposeEnvVar> b) =>
        a.Count == b.Count && a.Zip(b).All(p =>
            string.Equals(p.First.Name, p.Second.Name, StringComparison.Ordinal)
            && string.Equals(p.First.Value, p.Second.Value, StringComparison.Ordinal));

    private static bool UlimitsEqual(IReadOnlyList<ComposeUlimit> a, IReadOnlyList<ComposeUlimit> b) =>
        a.Count == b.Count && a.Zip(b).All(p =>
            string.Equals(p.First.Name, p.Second.Name, StringComparison.Ordinal)
            && p.First.Soft == p.Second.Soft && p.First.Hard == p.Second.Hard);

    // ── V7.2 resource rendering ──────────────────────────────────────────────

    private static string? LongStr(long? v) => v?.ToString(CultureInfo.InvariantCulture);

    private static string? BoolStr(bool? v) => v is null ? null : (v.Value ? "true" : "false");

    private static string RenderUlimitsBlock(IReadOnlyList<ComposeUlimit> ulimits, string eol, string itemIndent)
    {
        var text = "ulimits:";
        foreach (var u in ulimits)
        {
            if (u.Soft is not null && u.Soft == u.Hard)
            {
                text += eol + itemIndent + $"{RenderScalar(u.Name)}: {u.Soft}";
            }
            else
            {
                text += eol + itemIndent + $"{RenderScalar(u.Name)}:";
                if (u.Soft is not null) text += eol + itemIndent + "  " + $"soft: {u.Soft}";
                if (u.Hard is not null) text += eol + itemIndent + "  " + $"hard: {u.Hard}";
            }
        }
        return text;
    }

    private static bool ResourcesChanged(ComposeResourceConstraints a, ComposeResourceConstraints b)
    {
        static bool Diff(string? x, string? y) => !string.Equals(x, y, StringComparison.Ordinal);
        return Diff(a.CpuLimit, b.CpuLimit) || Diff(a.CpuReservation, b.CpuReservation)
            || Diff(a.MemLimit, b.MemLimit) || Diff(a.MemReservation, b.MemReservation)
            || Diff(a.PidsLimit, b.PidsLimit);
    }

    /// <summary>Renders the <c>resources:</c>-rooted block for the deploy
    /// convention. <paramref name="ind"/> is the indent of the <c>resources:</c>
    /// line; children are nested two spaces deeper per level. Numeric leaves
    /// (cpus/pids) render unquoted; memory goes through the quote-if-needed
    /// scalar renderer.</summary>
    private static string RenderResources(ComposeResourceConstraints ed, string eol, string ind)
    {
        var inner = ind + "  ";   // limits: / reservations:
        var leaf = inner + "  ";  // cpus: / memory: / pids:
        var text = "resources:";

        var limits = new List<string>();
        if (ed.CpuLimit is not null) limits.Add($"cpus: {ed.CpuLimit.Trim()}");
        if (ed.MemLimit is not null) limits.Add($"memory: {RenderScalar(ed.MemLimit)}");
        if (ed.PidsLimit is not null) limits.Add($"pids: {ed.PidsLimit.Trim()}");
        if (limits.Count > 0)
            text += eol + inner + "limits:" + string.Concat(limits.Select(l => eol + leaf + l));

        var reservations = new List<string>();
        if (ed.CpuReservation is not null) reservations.Add($"cpus: {ed.CpuReservation.Trim()}");
        if (ed.MemReservation is not null) reservations.Add($"memory: {RenderScalar(ed.MemReservation)}");
        if (reservations.Count > 0)
            text += eol + inner + "reservations:" + string.Concat(reservations.Select(l => eol + leaf + l));

        return text;
    }

    /// <summary>Builds the splice op(s) for the deploy-convention cpu/mem/pids
    /// fields by rewriting the whole <c>deploy.resources</c> subtree as a unit
    /// (sibling <c>deploy</c> keys — replicas / placement / … — are preserved
    /// byte for byte). Inserts <c>resources:</c> under an existing <c>deploy:</c>
    /// or adds a whole <c>deploy:</c> block when absent. Returns a typed error
    /// when the block can't be edited safely.</summary>
    private static string? BuildDeployResourcesOp(
        ServiceCapture svc, string yamlText, string eol, string keyIndent,
        List<SpliceOp> ops, List<string> additions,
        ComposeResourceConstraints cur, ComposeResourceConstraints ed)
    {
        if (!ResourcesChanged(cur, ed)) return null;

        var deploy = svc.Deploy;
        if (deploy is { Editable: false })
            return "Service 'deploy:' uses a style the editor can't edit safely (flow style or non-mapping) — edit the file manually.";
        if (deploy?.Anchored == true || deploy?.ResourcesAnchored == true)
            return "'deploy' carries a YAML anchor (&…) that other parts of the file may reference — edit the file manually.";

        var desiredEmpty = ed.CpuLimit is null && ed.CpuReservation is null
            && ed.MemLimit is null && ed.MemReservation is null && ed.PidsLimit is null;

        if (deploy is null)
        {
            // No deploy key — add a whole deploy/resources block (only reached
            // when something is set, since an all-null desired == no change).
            var ind = keyIndent + "  ";
            additions.Add("deploy:" + eol + ind + RenderResources(ed, eol, ind));
            return null;
        }

        if (!deploy.ResourcesExists)
        {
            var ind = new string(' ', deploy.BodyIndent >= 0 ? deploy.BodyIndent : svc.BodyIndent + 2);
            var insertAt = EndOfLine(yamlText, deploy.ValueEnd);
            ops.Add(new SpliceOp(insertAt, insertAt, eol + ind + RenderResources(ed, eol, ind)));
            return null;
        }

        if (desiredEmpty)
        {
            // Remove the whole deploy block when resources was its only child,
            // otherwise just the resources lines.
            if (deploy.ChildCount <= 1 && svc.Fields.TryGetValue("deploy", out var deployField))
                ops.Add(RemoveKeyOp(yamlText, deployField));
            else
                ops.Add(RemoveLinesOp(yamlText, deploy.ResKeyStart, deploy.ResValueEnd));
            return null;
        }

        var resInd = IndentBefore(yamlText, deploy.ResKeyStart);
        ops.Add(new SpliceOp(deploy.ResKeyStart, deploy.ResValueEnd, RenderResources(ed, eol, resInd)));
        return null;
    }

    /// <summary>Removes whole lines spanning <paramref name="keyStart"/> (a key
    /// that starts its own line) through the line holding <paramref name="valueEnd"/>.</summary>
    private static SpliceOp RemoveLinesOp(string yamlText, long keyStart, long valueEnd)
    {
        var lineStart = yamlText.LastIndexOf('\n', (int)Math.Max(0, keyStart - 1)) + 1;
        var lineEnd = yamlText.IndexOf('\n', (int)valueEnd);
        return new SpliceOp(lineStart, lineEnd < 0 ? yamlText.Length : lineEnd + 1, "");
    }

    /// <summary>The whitespace indent preceding a key on its own line.</summary>
    private static string IndentBefore(string yamlText, long keyStart)
    {
        var lineStart = yamlText.LastIndexOf('\n', (int)Math.Max(0, keyStart - 1)) + 1;
        return yamlText[lineStart..(int)keyStart];
    }

    // ── input validation ────────────────────────────────────────────────────

    private static string? ValidateEdit(ComposeServiceEdit edit)
    {
        if (edit.Ports.Any(string.IsNullOrWhiteSpace)) return "Ports must not contain empty entries.";
        if (edit.Volumes.Any(string.IsNullOrWhiteSpace)) return "Volumes must not contain empty entries.";
        foreach (var (pairs, what) in new[] { (edit.Environment, "Environment variable"), (edit.Labels, "Label") })
        {
            foreach (var p in pairs)
            {
                if (string.IsNullOrWhiteSpace(p.Name)) return $"{what} names must not be empty.";
                if (p.Name.Contains('=')) return $"{what} name '{p.Name}' must not contain '='.";
                if (p.Name.Contains('\n') || (p.Value?.Contains('\n') ?? false))
                    return $"{what} entries must not contain line breaks.";
            }
        }
        foreach (var u in edit.Resources.Ulimits)
        {
            if (string.IsNullOrWhiteSpace(u.Name)) return "ulimit names must not be empty.";
            if (u.Soft is null && u.Hard is null) return $"ulimit '{u.Name}' must set a soft and/or hard value.";
        }
        return null;
    }

    // ── event-stream capture ────────────────────────────────────────────────

    private enum NodeKind { Scalar, Alias, Sequence, Mapping, Empty }

    /// <summary>One captured value node: its literal text span, kind, and
    /// whether any anchor is declared on or inside it. <see cref="End"/> is the
    /// max end of <em>content</em> tokens — block-style SequenceEnd/MappingEnd
    /// marks point at the next token and must not be trusted.</summary>
    private sealed record NodeSpan(long Start, long End, NodeKind Kind, bool Anchored);

    private sealed record FieldCapture(Scalar Key, NodeSpan Value);

    /// <summary>V7.2 — the service's <c>deploy:</c> block, captured one level
    /// deep so the <c>resources</c> child can be rewritten in place while its
    /// siblings (replicas / placement / …) survive untouched. <see cref="Editable"/>
    /// is false for flow-style or non-mapping deploy values.</summary>
    private sealed record DeployInfo(
        bool Editable, bool Anchored, int BodyIndent, long ValueEnd, int ChildCount,
        bool ResourcesExists, long ResKeyStart, long ResValueEnd, bool ResourcesAnchored);

    private sealed class ServiceCapture
    {
        public bool FlowBody;
        public bool HasMergeKey;
        public Dictionary<string, FieldCapture> Fields { get; } = new(StringComparer.Ordinal);
        public long LastContentEnd;
        public int BodyIndent;
        /// <summary>The service's <c>deploy:</c> block when present; <c>null</c> otherwise.</summary>
        public DeployInfo? Deploy;
    }

    /// <summary>Walks the event stream to the target service and records the
    /// span of every direct key/value in its body. Returns <c>null</c> when the
    /// service (or the <c>services</c> map) is absent.</summary>
    private static ServiceCapture? CaptureService(string yamlText, string serviceName)
    {
        var p = new Parser(new StringReader(yamlText));
        p.Consume<StreamStart>();
        if (!p.TryConsume<DocumentStart>(out _)) return null;
        if (!p.TryConsume<MappingStart>(out _)) return null;

        while (!p.Accept<MappingEnd>(out _))
        {
            if (!p.TryConsume<Scalar>(out var rootKey))
            {
                SkipNode(p); // non-scalar key
                SkipNode(p);
                continue;
            }
            if (rootKey.Value != "services")
            {
                SkipNode(p);
                continue;
            }

            if (!p.TryConsume<MappingStart>(out _)) return null;
            while (!p.Accept<MappingEnd>(out _))
            {
                if (!p.TryConsume<Scalar>(out var svcKey))
                {
                    SkipNode(p);
                    SkipNode(p);
                    continue;
                }
                if (svcKey.Value != serviceName)
                {
                    SkipNode(p);
                    continue;
                }
                return CaptureServiceBody(p, svcKey, yamlText);
            }
            return null;
        }
        return null;
    }

    private static ServiceCapture CaptureServiceBody(IParser p, Scalar svcKey, string yamlText)
    {
        var svc = new ServiceCapture
        {
            BodyIndent = (int)svcKey.Start.Column - 1 + 2,
            LastContentEnd = svcKey.End.Index,
        };

        if (p.TryConsume<Scalar>(out var nullBody))
        {
            // `placeholder:` with no body — additions go right after the key line.
            svc.LastContentEnd = Math.Max(svc.LastContentEnd, nullBody.End.Index);
            return svc;
        }
        if (!p.TryConsume<MappingStart>(out var body))
        {
            SkipNode(p); // sequence / alias body — not editable, fall through to flow refusal
            svc.FlowBody = true;
            return svc;
        }
        if (body.Style == MappingStyle.Flow)
        {
            ConsumeUntilMappingEnd(p);
            svc.FlowBody = true;
            return svc;
        }

        var firstKey = true;
        while (!p.TryConsume<MappingEnd>(out _))
        {
            if (!p.TryConsume<Scalar>(out var key))
            {
                var k = CaptureNode(p);
                var v = CaptureNode(p);
                svc.LastContentEnd = Math.Max(svc.LastContentEnd, Math.Max(k.End, v.End));
                continue;
            }
            if (firstKey)
            {
                svc.BodyIndent = (int)key.Start.Column - 1;
                firstKey = false;
            }
            if (key.Value == "<<") svc.HasMergeKey = true;

            var value = key.Value == "deploy" ? CaptureDeploy(p, svc) : CaptureNode(p);
            if (value.Kind == NodeKind.Scalar && value.Start >= value.End)
            {
                // Implicit empty value (`labels:` with no body): YamlDotNet puts
                // the synthesised null scalar's marks at the NEXT token, which
                // would make every span calculation swallow the following key.
                // Clamp the span to just past the key's colon instead.
                var colon = yamlText.IndexOf(':', (int)key.End.Index);
                value = new NodeSpan(colon + 1, colon + 1, NodeKind.Empty, false);
            }
            svc.Fields[key.Value] = new FieldCapture(key, value);
            svc.LastContentEnd = Math.Max(svc.LastContentEnd, Math.Max(key.End.Index, value.End));
        }
        return svc;
    }

    /// <summary>V7.2 — consumes the service's <c>deploy:</c> value (returning its
    /// span for the generic <see cref="ServiceCapture.Fields"/> entry) and, when
    /// it is a block mapping, records its <c>resources</c> child span on
    /// <see cref="ServiceCapture.Deploy"/> so the resources subtree can be
    /// rewritten without disturbing sibling keys.</summary>
    private static NodeSpan CaptureDeploy(IParser p, ServiceCapture svc)
    {
        if (!p.Accept<MappingStart>(out var peek) || peek.Style == MappingStyle.Flow)
        {
            var node = CaptureNode(p);
            svc.Deploy = new DeployInfo(
                Editable: false, Anchored: node.Anchored, BodyIndent: -1,
                ValueEnd: node.End, ChildCount: 0,
                ResourcesExists: false, ResKeyStart: 0, ResValueEnd: 0, ResourcesAnchored: false);
            return node;
        }

        var ms = p.Consume<MappingStart>();
        var anchored = !ms.Anchor.IsEmpty;
        var end = ms.End.Index;
        var bodyIndent = -1;
        var childCount = 0;
        var resExists = false;
        long resKeyStart = 0, resValueEnd = 0;
        var resAnchored = false;

        while (!p.TryConsume<MappingEnd>(out _))
        {
            if (!p.TryConsume<Scalar>(out var dkey))
            {
                var k = CaptureNode(p);
                var v = CaptureNode(p);
                end = Math.Max(end, Math.Max(k.End, v.End));
                continue;
            }
            if (bodyIndent < 0) bodyIndent = (int)dkey.Start.Column - 1;
            childCount++;
            var dval = CaptureNode(p);
            end = Math.Max(end, Math.Max(dkey.End.Index, dval.End));
            if (dkey.Value == "resources")
            {
                resExists = true;
                resKeyStart = dkey.Start.Index;
                resValueEnd = dval.End;
                resAnchored = dval.Anchored;
            }
        }

        svc.Deploy = new DeployInfo(
            Editable: true, Anchored: anchored, BodyIndent: bodyIndent,
            ValueEnd: end, ChildCount: childCount,
            ResourcesExists: resExists, ResKeyStart: resKeyStart, ResValueEnd: resValueEnd,
            ResourcesAnchored: resAnchored);
        return new NodeSpan(ms.Start.Index, end, NodeKind.Mapping, anchored);
    }

    /// <summary>Consumes one complete node, tracking the max end index of its
    /// content tokens (block-end events are position-unreliable; flow-end
    /// events are real <c>]</c>/<c>}</c> tokens and are included).</summary>
    private static NodeSpan CaptureNode(IParser p)
    {
        if (p.TryConsume<Scalar>(out var s))
            return new NodeSpan(s.Start.Index, s.End.Index, NodeKind.Scalar, !s.Anchor.IsEmpty);
        if (p.TryConsume<AnchorAlias>(out var a))
            return new NodeSpan(a.Start.Index, a.End.Index, NodeKind.Alias, false);

        if (p.TryConsume<SequenceStart>(out var ss))
        {
            var anchored = !ss.Anchor.IsEmpty;
            var end = ss.End.Index;
            while (!p.Accept<SequenceEnd>(out _))
            {
                var child = CaptureNode(p);
                end = Math.Max(end, child.End);
                anchored |= child.Anchored;
            }
            var se = p.Consume<SequenceEnd>();
            if (ss.Style == SequenceStyle.Flow) end = Math.Max(end, se.End.Index);
            return new NodeSpan(ss.Start.Index, end, NodeKind.Sequence, anchored);
        }

        var ms = p.Consume<MappingStart>();
        var manchored = !ms.Anchor.IsEmpty;
        var mend = ms.End.Index;
        while (!p.Accept<MappingEnd>(out _))
        {
            var key = CaptureNode(p);
            var value = CaptureNode(p);
            mend = Math.Max(mend, Math.Max(key.End, value.End));
            manchored |= key.Anchored || value.Anchored;
        }
        var me = p.Consume<MappingEnd>();
        if (ms.Style == MappingStyle.Flow) mend = Math.Max(mend, me.End.Index);
        return new NodeSpan(ms.Start.Index, mend, NodeKind.Mapping, manchored);
    }

    // ── V7.3 — top-level resource (networks / volumes / secrets / configs) ───

    public ComposeEditResult ApplyResourceEdit(string yamlText, ComposeResourceEdit edit)
    {
        var parsed = parser.Parse(yamlText);
        if (parsed.Project is null)
            return ComposeEditResult.Failed(parsed.Error!);

        if (string.IsNullOrWhiteSpace(edit.Name))
            return ComposeEditResult.Failed("Resource name must not be empty.");
        if (ValidateResourceEdit(edit) is { } inputError)
            return ComposeEditResult.Failed(inputError);

        // No-op when the file already holds exactly this entry (entry-level
        // granularity — editing one resource never rewrites its siblings).
        if (CurrentResourceMatches(parsed.Project, edit))
            return ComposeEditResult.Unchanged(yamlText);

        var sectionKey = SectionKeyName(edit.Kind);
        SectionCapture section;
        try
        {
            section = CaptureSection(yamlText, sectionKey);
        }
        catch (YamlException ex)
        {
            return ComposeEditResult.Failed($"YAML parse error: {ex.Message}");
        }

        if (section.Exists && (section.FlowOrNonMapping || section.Anchored))
            return ComposeEditResult.Failed(SectionRefusal(sectionKey, section));
        if (section.Entries.TryGetValue(edit.Name, out var existing) && existing.Value.Anchored)
            return ComposeEditResult.Failed(
                $"'{edit.Name}' under '{sectionKey}:' carries a YAML anchor (&…) that other parts of the file may reference — edit the file manually.");

        var eol = yamlText.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        SpliceOp op;

        if (!section.Exists)
        {
            // Whole new top-level section appended to the file.
            var entryIndent = new string(' ', section.RootIndent + 2);
            var block = sectionKey + ":" + eol + entryIndent + RenderEntry(edit, eol, entryIndent + "  ");
            var leading = yamlText.Length > 0 && !yamlText.EndsWith('\n') ? eol : "";
            op = new SpliceOp(yamlText.Length, yamlText.Length, leading + block);
        }
        else if (section.Entries.TryGetValue(edit.Name, out var entry))
        {
            // Rewrite the entry's key + value subtree in place.
            var entryIndent = IndentBefore(yamlText, entry.Key.Start.Index);
            op = new SpliceOp(entry.Key.Start.Index, entry.Value.End, RenderEntry(edit, eol, entryIndent + "  "));
        }
        else
        {
            // Insert a new entry under the existing section.
            var cols = section.EntryIndent >= 0 ? section.EntryIndent : section.RootIndent + 2;
            var entryIndent = new string(' ', cols);
            var insertAt = EndOfLine(yamlText, section.SectionValueEnd);
            op = new SpliceOp(insertAt, insertAt, eol + entryIndent + RenderEntry(edit, eol, entryIndent + "  "));
        }

        return ComposeEditResult.Edited(yamlText[..(int)op.Start] + op.Replacement + yamlText[(int)op.End..]);
    }

    public ComposeEditResult RemoveResource(string yamlText, ComposeResourceKind kind, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return ComposeEditResult.Failed("Resource name must not be empty.");

        var sectionKey = SectionKeyName(kind);
        SectionCapture section;
        try
        {
            section = CaptureSection(yamlText, sectionKey);
        }
        catch (YamlException ex)
        {
            return ComposeEditResult.Failed($"YAML parse error: {ex.Message}");
        }

        if (!section.Exists || !section.Entries.TryGetValue(name, out var entry))
            return ComposeEditResult.Unchanged(yamlText);
        if (section.FlowOrNonMapping || section.Anchored)
            return ComposeEditResult.Failed(SectionRefusal(sectionKey, section));
        if (entry.Value.Anchored)
            return ComposeEditResult.Failed(
                $"'{name}' under '{sectionKey}:' carries a YAML anchor (&…) that other parts of the file may reference — edit the file manually.");

        // Removing the section's only entry takes the now-empty section key with
        // it (an empty `networks:` map is not worth leaving behind).
        var op = section.ChildCount <= 1 && section.SectionKey is not null
            ? RemoveKeyOp(yamlText, new FieldCapture(
                section.SectionKey,
                new NodeSpan(section.SectionKey.Start.Index, section.SectionValueEnd, NodeKind.Mapping, false)))
            : RemoveKeyOp(yamlText, entry);

        return ComposeEditResult.Edited(yamlText[..(int)op.Start] + op.Replacement + yamlText[(int)op.End..]);
    }

    private static string SectionRefusal(string sectionKey, SectionCapture section) =>
        section.Anchored
            ? $"The top-level '{sectionKey}:' carries a YAML anchor (&…) that other parts of the file may reference — edit the file manually."
            : $"The top-level '{sectionKey}:' uses a style the editor can't edit safely (flow style or non-mapping) — edit the file manually.";

    private static string SectionKeyName(ComposeResourceKind kind) => kind switch
    {
        ComposeResourceKind.Network => "networks",
        ComposeResourceKind.Volume => "volumes",
        ComposeResourceKind.Secret => "secrets",
        ComposeResourceKind.Config => "configs",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    private static string? ValidateResourceEdit(ComposeResourceEdit edit)
    {
        if (edit.External) return null; // only `external: true` (+ optional name) is written
        if (edit.Kind is ComposeResourceKind.Network or ComposeResourceKind.Volume)
        {
            foreach (var o in edit.DriverOpts)
            {
                if (string.IsNullOrWhiteSpace(o.Name)) return "driver_opts keys must not be empty.";
                if (o.Name.Contains('\n') || (o.Value?.Contains('\n') ?? false))
                    return "driver_opts entries must not contain line breaks.";
            }
        }
        if (edit.Kind is ComposeResourceKind.Secret or ComposeResourceKind.Config
            && string.IsNullOrWhiteSpace(edit.File))
            return "A non-external secret/config must set a file path.";
        if (edit.Kind == ComposeResourceKind.Network
            && string.IsNullOrWhiteSpace(edit.Subnet) && !string.IsNullOrWhiteSpace(edit.Gateway))
            return "A gateway requires a subnet.";
        return null;
    }

    /// <summary>Renders one entry: its quoted name key plus the body lines.</summary>
    private static string RenderEntry(ComposeResourceEdit edit, string eol, string optIndent) =>
        RenderScalar(edit.Name) + ":" + RenderEntryBody(edit, eol, optIndent);

    /// <summary>The body lines under an entry, each already prefixed with
    /// <paramref name="ind"/>. Empty for a bare default network/volume (a valid
    /// <c>name:</c>-only entry with no options).</summary>
    private static string RenderEntryBody(ComposeResourceEdit edit, string eol, string ind)
    {
        var nameOverride = NullIfBlank(edit.NameOverride);
        if (edit.External)
        {
            var ext = eol + ind + "external: true";
            if (nameOverride is not null) ext += eol + ind + $"name: {RenderScalar(nameOverride)}";
            return ext;
        }

        var text = "";
        if (edit.Kind is ComposeResourceKind.Secret or ComposeResourceKind.Config)
        {
            var file = NullIfBlank(edit.File);
            if (file is not null) text += eol + ind + $"file: {RenderScalar(file)}";
            if (nameOverride is not null) text += eol + ind + $"name: {RenderScalar(nameOverride)}";
            return text;
        }

        var driver = NullIfBlank(edit.Driver);
        if (driver is not null) text += eol + ind + $"driver: {RenderScalar(driver)}";
        var opts = edit.DriverOpts.Where(o => !string.IsNullOrWhiteSpace(o.Name)).ToList();
        if (opts.Count > 0)
        {
            text += eol + ind + "driver_opts:";
            foreach (var o in opts)
                text += eol + ind + "  " + $"{RenderScalar(o.Name.Trim())}: {RenderScalar(o.Value ?? "")}";
        }

        if (edit.Kind == ComposeResourceKind.Network)
        {
            var subnet = NullIfBlank(edit.Subnet);
            var gateway = NullIfBlank(edit.Gateway);
            if (subnet is not null)
            {
                text += eol + ind + "ipam:";
                text += eol + ind + "  config:";
                text += eol + ind + "    - subnet: " + RenderScalar(subnet);
                if (gateway is not null) text += eol + ind + "      gateway: " + RenderScalar(gateway);
            }
        }

        if (nameOverride is not null) text += eol + ind + $"name: {RenderScalar(nameOverride)}";
        return text;
    }

    private static string? NullIfBlank(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();

    private static bool ResourceFieldsEqual(string? a, string? b) =>
        string.Equals(NullIfBlank(a), NullIfBlank(b), StringComparison.Ordinal);

    /// <summary>True when the file's current normalised entry already equals the
    /// desired edit — the save is then a no-op. External entries compare only the
    /// fields they actually carry (external flag + name override).</summary>
    private static bool CurrentResourceMatches(ComposeProjectModel project, ComposeResourceEdit edit)
    {
        switch (edit.Kind)
        {
            case ComposeResourceKind.Network:
            {
                var n = project.Networks.FirstOrDefault(x => x.Name == edit.Name);
                if (n is null || n.External != edit.External) return false;
                if (!ResourceFieldsEqual(n.NameOverride, edit.NameOverride)) return false;
                if (edit.External) return true;
                return ResourceFieldsEqual(n.Driver, edit.Driver)
                    && ResourceFieldsEqual(n.Subnet, edit.Subnet)
                    && ResourceFieldsEqual(n.Gateway, edit.Gateway)
                    && PairsEqual(n.DriverOpts, edit.DriverOpts);
            }
            case ComposeResourceKind.Volume:
            {
                var v = project.Volumes.FirstOrDefault(x => x.Name == edit.Name);
                if (v is null || v.External != edit.External) return false;
                if (!ResourceFieldsEqual(v.NameOverride, edit.NameOverride)) return false;
                if (edit.External) return true;
                return ResourceFieldsEqual(v.Driver, edit.Driver)
                    && PairsEqual(v.DriverOpts, edit.DriverOpts);
            }
            default:
            {
                var list = edit.Kind == ComposeResourceKind.Secret ? project.Secrets : project.Configs;
                var f = list.FirstOrDefault(x => x.Name == edit.Name);
                if (f is null || f.External != edit.External) return false;
                if (!ResourceFieldsEqual(f.NameOverride, edit.NameOverride)) return false;
                return edit.External || ResourceFieldsEqual(f.File, edit.File);
            }
        }
    }

    /// <summary>One captured top-level section (<c>networks:</c> …) and the spans
    /// of its direct entries, mirroring <see cref="ServiceCapture"/> one level up.</summary>
    private sealed class SectionCapture
    {
        public bool Exists;
        public bool FlowOrNonMapping;
        public bool Anchored;
        public int RootIndent;
        public int EntryIndent = -1;
        public long SectionValueEnd;
        public int ChildCount;
        public Scalar? SectionKey;
        public Dictionary<string, FieldCapture> Entries { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>Walks the event stream to a top-level section and records the span
    /// of every direct entry's key/value. Returns a capture with
    /// <see cref="SectionCapture.Exists"/> false when the section is absent.</summary>
    private static SectionCapture CaptureSection(string yamlText, string sectionKey)
    {
        var cap = new SectionCapture();
        var p = new Parser(new StringReader(yamlText));
        p.Consume<StreamStart>();
        if (!p.TryConsume<DocumentStart>(out _)) return cap;
        if (!p.TryConsume<MappingStart>(out _)) return cap;

        var firstRootKey = true;
        while (!p.Accept<MappingEnd>(out _))
        {
            if (!p.TryConsume<Scalar>(out var rootKey))
            {
                SkipNode(p);
                SkipNode(p);
                continue;
            }
            if (firstRootKey)
            {
                cap.RootIndent = (int)rootKey.Start.Column - 1;
                firstRootKey = false;
            }
            if (rootKey.Value != sectionKey)
            {
                SkipNode(p);
                continue;
            }

            cap.Exists = true;
            cap.SectionKey = rootKey;
            cap.SectionValueEnd = rootKey.End.Index;

            if (p.TryConsume<Scalar>(out var nullBody))
            {
                // `networks:` with a null / empty body.
                cap.SectionValueEnd = Math.Max(cap.SectionValueEnd, nullBody.End.Index);
                return cap;
            }
            if (!p.TryConsume<MappingStart>(out var body))
            {
                var node = CaptureNode(p); // sequence / alias body — not editable
                cap.FlowOrNonMapping = true;
                cap.Anchored = node.Anchored;
                cap.SectionValueEnd = node.End;
                return cap;
            }
            if (body.Style == MappingStyle.Flow)
            {
                cap.FlowOrNonMapping = true;
                cap.Anchored = !body.Anchor.IsEmpty;
                var end = body.End.Index;
                while (!p.Accept<MappingEnd>(out _))
                {
                    var k = CaptureNode(p);
                    var v = CaptureNode(p);
                    end = Math.Max(end, Math.Max(k.End, v.End));
                }
                var me = p.Consume<MappingEnd>();
                cap.SectionValueEnd = Math.Max(end, me.End.Index);
                return cap;
            }

            cap.Anchored = !body.Anchor.IsEmpty;
            var contentEnd = body.End.Index;
            var firstEntry = true;
            while (!p.TryConsume<MappingEnd>(out _))
            {
                if (!p.TryConsume<Scalar>(out var entryKey))
                {
                    var k = CaptureNode(p);
                    var v = CaptureNode(p);
                    contentEnd = Math.Max(contentEnd, Math.Max(k.End, v.End));
                    continue;
                }
                if (firstEntry)
                {
                    cap.EntryIndent = (int)entryKey.Start.Column - 1;
                    firstEntry = false;
                }
                cap.ChildCount++;
                var value = CaptureNode(p);
                if (value.Kind == NodeKind.Scalar && value.Start >= value.End)
                {
                    // Implicit empty value (`frontend:` with no body) — clamp the
                    // span to just past the colon (see CaptureServiceBody).
                    var colon = yamlText.IndexOf(':', (int)entryKey.End.Index);
                    value = new NodeSpan(colon + 1, colon + 1, NodeKind.Empty, false);
                }
                cap.Entries[entryKey.Value] = new FieldCapture(entryKey, value);
                contentEnd = Math.Max(contentEnd, Math.Max(entryKey.End.Index, value.End));
            }
            cap.SectionValueEnd = contentEnd;
            return cap;
        }
        return cap;
    }

    private static void SkipNode(IParser p) => CaptureNode(p);

    private static void ConsumeUntilMappingEnd(IParser p)
    {
        while (!p.TryConsume<MappingEnd>(out _))
        {
            CaptureNode(p); // key
            CaptureNode(p); // value
        }
    }

    private sealed record SpliceOp(long Start, long End, string Replacement);
}
