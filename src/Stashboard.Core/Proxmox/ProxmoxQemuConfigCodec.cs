using System.Globalization;
using Stashboard.Core.Abstractions;

namespace Stashboard.Core.Proxmox;

/// <summary>
/// V8.5 — parses and formats the compound Proxmox QEMU config lines the V8.5 edit
/// surface touches: <c>net&lt;n&gt;</c> (NICs) and the disk lines
/// (<c>scsi&lt;n&gt;</c> / <c>virtio&lt;n&gt;</c> / <c>sata&lt;n&gt;</c> / …). The
/// QEMU analogue of <see cref="ProxmoxLxcConfigCodec"/>, kept alongside it rather than
/// merged because a VM line is shaped differently from an LXC one: a NIC's first token
/// is the device <em>model</em> carrying the MAC as its value
/// (<c>virtio=AA:BB:CC:DD:EE:FF,bridge=vmbr0,tag=10</c>), and a VM has no in-config IP.
///
/// <para>As with the LXC codec it is the single source of truth for the on-wire shape
/// (the write path formats the structured change models back into the exact option
/// string), options it does not model are preserved verbatim in <c>Extra</c> so a
/// guided edit is never lossy, and the parse direction backs round-trip tests.</para>
/// </summary>
public static class ProxmoxQemuConfigCodec
{
    /// <summary>The QEMU NIC device models — the recognised first-token keys on a
    /// <c>net&lt;n&gt;</c> line. Anything here is the model; the rest are options.</summary>
    public static readonly string[] NetModels =
        ["virtio", "e1000", "e1000e", "rtl8139", "vmxnet3", "ne2k_pci", "pcnet", "i82551", "i82557b", "i82559er", "ne2k_isa"];

    // ── net<n> ────────────────────────────────────────────────────────────────

    private static readonly string[] NetKnownKeys =
        ["bridge", "tag", "firewall", "rate", "mtu", "queues", "link_down"];

    public static ProxmoxQemuNetChange ParseNet(string key, string raw)
    {
        string? model = null, mac = null;
        var pairs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var extra = new List<string>();
        var known = new HashSet<string>(NetKnownKeys, StringComparer.OrdinalIgnoreCase);
        var models = new HashSet<string>(NetModels, StringComparer.OrdinalIgnoreCase);

        foreach (var token in (raw ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = token.IndexOf('=');
            if (eq < 0)
            {
                // A bare token that names a model is the device; otherwise preserve it.
                if (model is null && models.Contains(token)) model = token;
                else extra.Add(token);
                continue;
            }
            var k = token[..eq].Trim();
            var v = token[(eq + 1)..].Trim();
            if (model is null && models.Contains(k)) { model = k; mac = v; }
            else if (known.Contains(k)) pairs[k] = v;
            else extra.Add($"{k}={v}");
        }

        return new ProxmoxQemuNetChange(
            Key: key,
            Model: model,
            MacAddr: mac,
            Bridge: Get(pairs, "bridge"),
            Tag: GetInt(pairs, "tag"),
            Firewall: GetBool(pairs, "firewall"),
            Rate: GetInt(pairs, "rate"),
            Mtu: GetInt(pairs, "mtu"),
            Queues: GetInt(pairs, "queues"),
            LinkDown: GetBool(pairs, "link_down"),
            Extra: extra.Count == 0 ? null : string.Join(',', extra));
    }

    /// <summary>Formats the value after <c>net&lt;n&gt;=</c>. <see cref="ProxmoxQemuNetChange.Raw"/>
    /// wins verbatim; otherwise the model (defaulting to virtio) leads, carrying the MAC
    /// as its value, then the structured options in a stable order.</summary>
    public static string FormatNet(ProxmoxQemuNetChange c)
    {
        if (!string.IsNullOrWhiteSpace(c.Raw)) return c.Raw.Trim();
        var b = new OptionBuilder();
        var model = string.IsNullOrWhiteSpace(c.Model) ? "virtio" : c.Model.Trim();
        b.AddPositional(string.IsNullOrWhiteSpace(c.MacAddr) ? model : $"{model}={c.MacAddr.Trim()}");
        b.Add("bridge", c.Bridge);
        b.AddInt("tag", c.Tag);
        b.AddBool("firewall", c.Firewall);
        b.AddInt("rate", c.Rate);
        b.AddInt("mtu", c.Mtu);
        b.AddInt("queues", c.Queues);
        b.AddBool("link_down", c.LinkDown);
        b.AddExtra(c.Extra);
        return b.Build();
    }

    // ── disk (scsi<n> / virtio<n> / sata<n> / ide<n> / …) ─────────────────────

    private static readonly string[] DiskKnownKeys = ["size", "ssd", "discard", "cache"];

    public static ProxmoxQemuDiskChange ParseDisk(string key, string raw)
    {
        var pairs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var extra = new List<string>();
        var known = new HashSet<string>(DiskKnownKeys, StringComparer.OrdinalIgnoreCase);
        string? volume = null;
        var first = true;

        foreach (var token in (raw ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = token.IndexOf('=');
            if (eq < 0)
            {
                if (first && volume is null) volume = token;
                else extra.Add(token);
            }
            else
            {
                var k = token[..eq].Trim();
                var v = token[(eq + 1)..].Trim();
                if (known.Contains(k)) pairs[k] = v;
                else extra.Add($"{k}={v}");
            }
            first = false;
        }

        return new ProxmoxQemuDiskChange(
            Key: key,
            Volume: volume,
            Size: Get(pairs, "size"),
            // discard is on/ignore (not 1/0); treat "on" as enabled.
            Discard: pairs.TryGetValue("discard", out var d) ? string.Equals(d, "on", StringComparison.OrdinalIgnoreCase) : null,
            Ssd: GetBool(pairs, "ssd"),
            Cache: Get(pairs, "cache"),
            Extra: extra.Count == 0 ? null : string.Join(',', extra));
    }

    /// <summary>Formats the value after <c>scsi&lt;n&gt;=</c> (etc). The positional
    /// volume leads, then size, then the safe flags. <see cref="ProxmoxQemuDiskChange.Raw"/>
    /// wins verbatim. A disabled flag is simply omitted (Proxmox's default).</summary>
    public static string FormatDisk(ProxmoxQemuDiskChange c)
    {
        if (!string.IsNullOrWhiteSpace(c.Raw)) return c.Raw.Trim();
        var b = new OptionBuilder();
        b.AddPositional(c.Volume);
        b.Add("size", c.Size);
        if (c.Discard is true) b.AddPositional("discard=on");
        if (c.Ssd is true) b.AddPositional("ssd=1");
        b.Add("cache", c.Cache);
        b.AddExtra(c.Extra);
        return b.Build();
    }

    // ── shared helpers (kept local so the LXC codec stays untouched) ──────────

    private static string? Get(Dictionary<string, string> pairs, string key) =>
        pairs.TryGetValue(key, out var v) ? v : null;

    private static int? GetInt(Dictionary<string, string> pairs, string key) =>
        pairs.TryGetValue(key, out var v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            ? n : null;

    private static bool? GetBool(Dictionary<string, string> pairs, string key) =>
        pairs.TryGetValue(key, out var v) ? v.Trim() == "1" : null;

    private sealed class OptionBuilder
    {
        private readonly List<string> _parts = [];

        public void AddPositional(string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) _parts.Add(value.Trim());
        }

        public void Add(string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) _parts.Add($"{key}={value.Trim()}");
        }

        public void AddInt(string key, int? value)
        {
            if (value is { } n) _parts.Add($"{key}={n.ToString(CultureInfo.InvariantCulture)}");
        }

        public void AddBool(string key, bool? value)
        {
            if (value is { } b) _parts.Add($"{key}={(b ? "1" : "0")}");
        }

        public void AddExtra(string? extra)
        {
            if (string.IsNullOrWhiteSpace(extra)) return;
            foreach (var token in extra.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                _parts.Add(token);
        }

        public string Build() => string.Join(',', _parts);
    }
}
