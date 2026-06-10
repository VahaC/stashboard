using System.Globalization;
using System.Text;
using Stashboard.Core.Abstractions;

namespace Stashboard.Core.Proxmox;

/// <summary>
/// V6.9 — parses and formats the compound Proxmox LXC config lines
/// (<c>net&lt;n&gt;</c> / <c>mp&lt;n&gt;</c> / <c>rootfs</c>) that the V6.5 scalar
/// editor intentionally left alone. A line is a comma-separated option list,
/// e.g. <c>name=eth0,bridge=vmbr0,ip=dhcp</c>; for <c>mp</c>/<c>rootfs</c> the
/// first token is a <em>positional</em> volume/source with no <c>key=</c> prefix
/// (<c>local-lvm:vm-101-disk-1,mp=/data,size=20G</c>).
///
/// <para>The codec is the single source of truth for the on-wire shape: the
/// write path formats the structured change models back into the exact option
/// string, while the parse direction backs round-trip tests and lets the server
/// reason about an existing line. Options the structured model does not name are
/// preserved verbatim in <c>Extra</c> so a guided edit is never lossy.</para>
/// </summary>
public static class ProxmoxLxcConfigCodec
{
    // ── net<n> ────────────────────────────────────────────────────────────────

    private static readonly string[] NetKnownKeys =
        ["name", "bridge", "ip", "gw", "ip6", "gw6", "tag", "firewall", "rate", "mtu", "hwaddr", "link_down"];

    public static ProxmoxLxcNetChange ParseNet(string key, string raw)
    {
        var (_, pairs, extra) = Split(raw, hasPositional: false, NetKnownKeys);
        return new ProxmoxLxcNetChange(
            Key: key,
            Name: Get(pairs, "name"),
            Bridge: Get(pairs, "bridge"),
            Ip: Get(pairs, "ip"),
            Gw: Get(pairs, "gw"),
            Ip6: Get(pairs, "ip6"),
            Gw6: Get(pairs, "gw6"),
            Tag: GetInt(pairs, "tag"),
            Firewall: GetBool(pairs, "firewall"),
            Rate: GetInt(pairs, "rate"),
            Mtu: GetInt(pairs, "mtu"),
            Hwaddr: Get(pairs, "hwaddr"),
            LinkDown: GetBool(pairs, "link_down"),
            Extra: extra);
    }

    /// <summary>Formats the value that goes after <c>net&lt;n&gt;=</c>. When
    /// <see cref="ProxmoxLxcNetChange.Raw"/> is set it wins verbatim (advanced
    /// mode); otherwise the structured fields are emitted in a stable order with
    /// any preserved <c>Extra</c> appended.</summary>
    public static string FormatNet(ProxmoxLxcNetChange c)
    {
        if (!string.IsNullOrWhiteSpace(c.Raw)) return c.Raw.Trim();
        var b = new OptionBuilder();
        b.Add("name", c.Name);
        b.Add("hwaddr", c.Hwaddr);
        b.Add("bridge", c.Bridge);
        b.Add("ip", c.Ip);
        b.Add("gw", c.Gw);
        b.Add("ip6", c.Ip6);
        b.Add("gw6", c.Gw6);
        b.AddInt("tag", c.Tag);
        b.AddBool("firewall", c.Firewall);
        b.AddInt("rate", c.Rate);
        b.AddInt("mtu", c.Mtu);
        b.AddBool("link_down", c.LinkDown);
        b.AddExtra(c.Extra);
        return b.Build();
    }

    // ── mp<n> ─────────────────────────────────────────────────────────────────

    private static readonly string[] MountKnownKeys =
        ["mp", "size", "ro", "backup", "quota", "acl", "shared", "replicate", "mountoptions"];

    public static ProxmoxLxcMountChange ParseMount(string key, string raw)
    {
        var (volume, pairs, extra) = Split(raw, hasPositional: true, MountKnownKeys);
        return new ProxmoxLxcMountChange(
            Key: key,
            Volume: volume,
            MountPoint: Get(pairs, "mp"),
            Size: Get(pairs, "size"),
            ReadOnly: GetBool(pairs, "ro"),
            Backup: GetBool(pairs, "backup"),
            Quota: GetBool(pairs, "quota"),
            Acl: GetBool(pairs, "acl"),
            Shared: GetBool(pairs, "shared"),
            Replicate: GetBool(pairs, "replicate"),
            MountOptions: Get(pairs, "mountoptions"),
            Extra: extra);
    }

    public static string FormatMount(ProxmoxLxcMountChange c)
    {
        if (!string.IsNullOrWhiteSpace(c.Raw)) return c.Raw.Trim();
        var b = new OptionBuilder();
        b.AddPositional(c.Volume);
        b.Add("mp", c.MountPoint);
        b.Add("size", c.Size);
        b.Add("mountoptions", c.MountOptions);
        b.AddBool("ro", c.ReadOnly);
        b.AddBool("backup", c.Backup);
        b.AddBool("quota", c.Quota);
        b.AddBool("acl", c.Acl);
        b.AddBool("shared", c.Shared);
        b.AddBool("replicate", c.Replicate);
        b.AddExtra(c.Extra);
        return b.Build();
    }

    // ── rootfs ──────────────────────────────────────────────────────────────────

    private static readonly string[] RootfsKnownKeys =
        ["size", "ro", "quota", "acl", "replicate", "mountoptions"];

    public static ProxmoxLxcRootfsChange ParseRootfs(string raw)
    {
        var (volume, pairs, extra) = Split(raw, hasPositional: true, RootfsKnownKeys);
        return new ProxmoxLxcRootfsChange(
            Volume: volume,
            Size: Get(pairs, "size"),
            ReadOnly: GetBool(pairs, "ro"),
            Quota: GetBool(pairs, "quota"),
            Acl: GetBool(pairs, "acl"),
            Replicate: GetBool(pairs, "replicate"),
            MountOptions: Get(pairs, "mountoptions"),
            Extra: extra);
    }

    public static string FormatRootfs(ProxmoxLxcRootfsChange c)
    {
        if (!string.IsNullOrWhiteSpace(c.Raw)) return c.Raw.Trim();
        var b = new OptionBuilder();
        b.AddPositional(c.Volume);
        b.Add("size", c.Size);
        b.Add("mountoptions", c.MountOptions);
        b.AddBool("ro", c.ReadOnly);
        b.AddBool("quota", c.Quota);
        b.AddBool("acl", c.Acl);
        b.AddBool("replicate", c.Replicate);
        b.AddExtra(c.Extra);
        return b.Build();
    }

    // ── shared parsing helpers ──────────────────────────────────────────────────

    /// <summary>Splits a raw option line into its positional token (when the line
    /// type has one), the recognised key/value pairs, and an <c>Extra</c> string
    /// of every option not in <paramref name="knownKeys"/>, preserved verbatim.</summary>
    private static (string? Positional, Dictionary<string, string> Pairs, string? Extra) Split(
        string raw, bool hasPositional, string[] knownKeys)
    {
        var pairs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var extra = new List<string>();
        string? positional = null;
        var known = new HashSet<string>(knownKeys, StringComparer.OrdinalIgnoreCase);

        var first = true;
        foreach (var token in (raw ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var t = token.Trim();
            if (t.Length == 0) { first = false; continue; }

            var eq = t.IndexOf('=');
            if (eq < 0)
            {
                // A keyless token is the positional volume (only valid first).
                if (first && hasPositional) positional = t;
                else extra.Add(t);
            }
            else
            {
                var k = t[..eq].Trim();
                var v = t[(eq + 1)..].Trim();
                if (known.Contains(k)) pairs[k] = v;
                else extra.Add($"{k}={v}");
            }
            first = false;
        }

        return (positional, pairs, extra.Count == 0 ? null : string.Join(',', extra));
    }

    private static string? Get(Dictionary<string, string> pairs, string key) =>
        pairs.TryGetValue(key, out var v) ? v : null;

    private static int? GetInt(Dictionary<string, string> pairs, string key) =>
        pairs.TryGetValue(key, out var v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            ? n : null;

    /// <summary>A Proxmox flag is <c>1</c> / <c>0</c>; absent ⇒ <c>null</c>
    /// (leave default) so a round-trip never invents a flag that wasn't there.</summary>
    private static bool? GetBool(Dictionary<string, string> pairs, string key) =>
        pairs.TryGetValue(key, out var v) ? v.Trim() == "1" : null;

    /// <summary>Assembles a comma-joined option line in a stable field order.</summary>
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
