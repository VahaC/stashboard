using System.Text.RegularExpressions;
using Stashboard.Core.Abstractions;

namespace Stashboard.Core.Proxmox;

/// <summary>
/// V8.5 — locally-checkable safety guards for a structured QEMU config write, run
/// before the request reaches the Proxmox host. The VM analogue of
/// <see cref="ProxmoxLxcConfigValidator"/>: it rejects the things it can verify without
/// the host (malformed MAC / VLAN / size, an unknown NIC model, impossible
/// cores/sockets/memory, a balloon above the memory ceiling) so the user gets a clean
/// message instead of a raw Proxmox 400. It is deliberately conservative — it never
/// inspects the advanced <c>Raw</c> escape hatch (that path is the user's explicit
/// choice; any rejection there is surfaced verbatim from Proxmox) and it can't know
/// which storages / ISOs exist (Proxmox stays authoritative for those).
/// </summary>
public static class ProxmoxQemuConfigValidator
{
    private static readonly Regex MacRegex =
        new("^([0-9A-Fa-f]{2}:){5}[0-9A-Fa-f]{2}$", RegexOptions.Compiled);

    // A grow increment: a leading + then a number with an optional T/G/M/K suffix.
    private static readonly Regex GrowRegex =
        new("^\\+[0-9]+(\\.[0-9]+)?[TGMK]?$", RegexOptions.Compiled);

    private static readonly HashSet<string> KnownModels =
        new(ProxmoxQemuConfigCodec.NetModels, StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> KnownOsTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "l26", "l24", "win11", "win10", "win8", "win7", "wvista", "wxp", "w2k", "w2k3", "w2k8", "solaris", "other",
    };

    /// <summary>Returns one human-readable message per problem; empty ⇒ valid.</summary>
    public static IReadOnlyList<string> Validate(ProxmoxQemuConfigUpdate update)
    {
        var errors = new List<string>();

        if (update.Cores is { } cores && (cores < 1 || cores > 8192))
            errors.Add("Cores must be between 1 and 8192.");
        if (update.Sockets is { } sockets && (sockets < 1 || sockets > 4))
            errors.Add("Sockets must be between 1 and 4.");
        if (update.MemoryMib is { } mem && mem < 16)
            errors.Add("Memory must be at least 16 MiB.");
        if (update.BalloonMib is { } balloon && balloon < 0)
            errors.Add("Balloon minimum cannot be negative.");
        // Balloon is the floor; it must not exceed the memory ceiling when both are set.
        if (update.BalloonMib is { } b && b > 0 && update.MemoryMib is { } m && b > m)
            errors.Add("Balloon minimum cannot exceed the memory ceiling.");
        if (update.OsType is { } ostype && !string.IsNullOrWhiteSpace(ostype) && !KnownOsTypes.Contains(ostype.Trim()))
            errors.Add($"'{ostype}' is not a known OS type.");

        ValidateNetworks(update.Networks, errors);
        return errors;
    }

    private static void ValidateNetworks(IReadOnlyList<ProxmoxQemuNetChange>? changes, List<string> errors)
    {
        if (changes is null) return;
        foreach (var c in changes)
        {
            if (c.Remove)
            {
                if (string.IsNullOrWhiteSpace(c.Key)) errors.Add("A network interface removal is missing its key.");
                continue;
            }
            if (!string.IsNullOrWhiteSpace(c.Raw)) continue;   // advanced mode — host validates

            if (!string.IsNullOrWhiteSpace(c.Model) && !KnownModels.Contains(c.Model!.Trim()))
                errors.Add($"Interface {Label(c.Key)}: '{c.Model}' is not a known NIC model.");
            if (c.MacAddr is { } mac && !string.IsNullOrWhiteSpace(mac) && !MacRegex.IsMatch(mac.Trim()))
                errors.Add($"Interface {Label(c.Key)}: '{mac}' is not a valid MAC address (aa:bb:cc:dd:ee:ff).");
            if (c.Tag is { } tag && (tag < 1 || tag > 4094))
                errors.Add($"Interface {Label(c.Key)}: VLAN tag must be between 1 and 4094.");
            if (c.Mtu is { } mtu && (mtu < 64 || mtu > 65520))
                errors.Add($"Interface {Label(c.Key)}: MTU must be between 64 and 65520.");
            if (c.Rate is { } rate && rate < 0)
                errors.Add($"Interface {Label(c.Key)}: rate limit cannot be negative.");
            if (c.Queues is { } q && (q < 1 || q > 64))
                errors.Add($"Interface {Label(c.Key)}: queues must be between 1 and 64.");
        }
    }

    /// <summary>Validates a disk grow request: the disk key is present and the size is
    /// a positive grow increment (<c>+8G</c>). Grow-only is structural — a bare or
    /// negative size is rejected here so a shrink can never reach the host.</summary>
    public static IReadOnlyList<string> ValidateResize(string? disk, string? size)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(disk)) errors.Add("A disk key is required.");
        if (string.IsNullOrWhiteSpace(size) || !GrowRegex.IsMatch(size.Trim()))
            errors.Add("Grow amount must be a positive increment like +8G (resize is grow-only).");
        return errors;
    }

    private static string Label(string key) => string.IsNullOrWhiteSpace(key) ? "(new)" : key;
}
