using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Stashboard.Core.Abstractions;

namespace Stashboard.Core.Proxmox;

/// <summary>
/// V6.9 — locally-checkable safety guards for a structured LXC config write,
/// run before the request ever reaches the Proxmox host. It rejects the illegal
/// operations the spec calls out (removing <c>rootfs</c>, duplicate interface
/// names / mount paths, malformed IP/CIDR, invalid gateways, impossible sizes)
/// so the user gets a clean message instead of a raw Proxmox 400.
///
/// <para>It is deliberately conservative: it only flags things it can verify
/// without the host (it can't know which storages exist), and it never inspects
/// the advanced <c>Raw</c> escape hatch — that path is the user's explicit choice
/// and any rejection there is surfaced verbatim from Proxmox.</para>
/// </summary>
public static class ProxmoxLxcConfigValidator
{
    private static readonly Regex MacRegex =
        new("^([0-9A-Fa-f]{2}:){5}[0-9A-Fa-f]{2}$", RegexOptions.Compiled);

    // A Proxmox disk size: a number with an optional T/G/M/K suffix (e.g. 8G).
    private static readonly Regex SizeRegex =
        new("^[0-9]+(\\.[0-9]+)?[TGMK]?$", RegexOptions.Compiled);

    /// <summary>Returns one human-readable message per problem; empty ⇒ valid.</summary>
    public static IReadOnlyList<string> Validate(ProxmoxLxcConfigUpdate update)
    {
        var errors = new List<string>();
        ValidateNetworks(update.Networks, errors);
        ValidateMounts(update.Mounts, errors);
        ValidateRootfs(update.Rootfs, errors);
        return errors;
    }

    private static void ValidateNetworks(IReadOnlyList<ProxmoxLxcNetChange>? changes, List<string> errors)
    {
        if (changes is null) return;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var c in changes)
        {
            if (c.Remove)
            {
                if (string.IsNullOrWhiteSpace(c.Key)) errors.Add("A network interface removal is missing its key.");
                continue;
            }
            if (!string.IsNullOrWhiteSpace(c.Raw)) continue;   // advanced mode — host validates

            if (!string.IsNullOrWhiteSpace(c.Name) && !names.Add(c.Name!.Trim()))
                errors.Add($"Duplicate interface name '{c.Name}'.");

            if (c.Ip is { } ip && !IsValidIpMode(ip, ipv6: false))
                errors.Add($"Interface {Label(c.Key)}: '{ip}' is not 'dhcp', 'manual', or a valid IPv4 CIDR (x.x.x.x/nn).");
            if (c.Gw is { } gw && !string.IsNullOrWhiteSpace(gw) && !IsValidIp(gw, ipv6: false))
                errors.Add($"Interface {Label(c.Key)}: gateway '{gw}' is not a valid IPv4 address.");
            if (c.Ip6 is { } ip6 && !IsValidIpMode(ip6, ipv6: true))
                errors.Add($"Interface {Label(c.Key)}: '{ip6}' is not 'dhcp', 'auto', 'manual', or a valid IPv6 CIDR.");
            if (c.Gw6 is { } gw6 && !string.IsNullOrWhiteSpace(gw6) && !IsValidIp(gw6, ipv6: true))
                errors.Add($"Interface {Label(c.Key)}: IPv6 gateway '{gw6}' is not a valid IPv6 address.");
            if (c.Hwaddr is { } mac && !string.IsNullOrWhiteSpace(mac) && !MacRegex.IsMatch(mac.Trim()))
                errors.Add($"Interface {Label(c.Key)}: '{mac}' is not a valid MAC address (aa:bb:cc:dd:ee:ff).");
            if (c.Tag is { } tag && (tag < 1 || tag > 4094))
                errors.Add($"Interface {Label(c.Key)}: VLAN tag must be between 1 and 4094.");
            if (c.Mtu is { } mtu && (mtu < 64 || mtu > 65520))
                errors.Add($"Interface {Label(c.Key)}: MTU must be between 64 and 65520.");
            if (c.Rate is { } rate && rate < 0)
                errors.Add($"Interface {Label(c.Key)}: rate limit cannot be negative.");
        }
    }

    private static void ValidateMounts(IReadOnlyList<ProxmoxLxcMountChange>? changes, List<string> errors)
    {
        if (changes is null) return;
        var paths = new HashSet<string>(StringComparer.Ordinal);

        foreach (var c in changes)
        {
            if (c.Remove)
            {
                if (string.IsNullOrWhiteSpace(c.Key))
                    errors.Add("A mount point removal is missing its key.");
                else if (string.Equals(c.Key.Trim(), "rootfs", StringComparison.OrdinalIgnoreCase))
                    errors.Add("rootfs cannot be removed — only modified.");
                continue;
            }
            if (!string.IsNullOrWhiteSpace(c.Raw)) continue;   // advanced mode — host validates

            if (string.IsNullOrWhiteSpace(c.MountPoint))
                errors.Add($"Mount {Label(c.Key)}: a container mount path is required.");
            else if (!c.MountPoint.Trim().StartsWith('/'))
                errors.Add($"Mount {Label(c.Key)}: mount path '{c.MountPoint}' must be absolute (start with '/').");
            else if (!paths.Add(c.MountPoint.Trim()))
                errors.Add($"Duplicate mount path '{c.MountPoint}'.");

            if (c.Size is { } size && !string.IsNullOrWhiteSpace(size) && !SizeRegex.IsMatch(size.Trim()))
                errors.Add($"Mount {Label(c.Key)}: '{size}' is not a valid size (e.g. 8G, 512M).");
        }
    }

    private static void ValidateRootfs(ProxmoxLxcRootfsChange? rootfs, List<string> errors)
    {
        if (rootfs is null || !string.IsNullOrWhiteSpace(rootfs.Raw)) return;
        if (rootfs.Size is { } size && !string.IsNullOrWhiteSpace(size) && !SizeRegex.IsMatch(size.Trim()))
            errors.Add($"rootfs: '{size}' is not a valid size (e.g. 8G).");
    }

    private static string Label(string key) => string.IsNullOrWhiteSpace(key) ? "(new)" : key;

    /// <summary>Accepts the symbolic modes plus a literal CIDR for the family.</summary>
    private static bool IsValidIpMode(string value, bool ipv6)
    {
        var v = value.Trim();
        if (v.Length == 0) return true;   // empty ⇒ field cleared, not invalid
        if (v.Equals("dhcp", StringComparison.OrdinalIgnoreCase)
            || v.Equals("manual", StringComparison.OrdinalIgnoreCase)
            || (ipv6 && v.Equals("auto", StringComparison.OrdinalIgnoreCase)))
            return true;
        return IsValidCidr(v, ipv6);
    }

    private static bool IsValidCidr(string value, bool ipv6)
    {
        var slash = value.IndexOf('/');
        if (slash < 0) return false;                 // a static address needs a prefix length
        var addr = value[..slash];
        if (!IsValidIp(addr, ipv6)) return false;
        if (!int.TryParse(value[(slash + 1)..], out var prefix)) return false;
        return ipv6 ? prefix is >= 0 and <= 128 : prefix is >= 0 and <= 32;
    }

    private static bool IsValidIp(string value, bool ipv6)
    {
        if (!IPAddress.TryParse(value.Trim(), out var ip)) return false;
        return ip.AddressFamily == (ipv6 ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork);
    }
}
