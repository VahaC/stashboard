using System.Text.RegularExpressions;
using Stashboard.Core.Abstractions;

namespace Stashboard.Core.Proxmox;

/// <summary>
/// V8.4 — locally-checkable guards for a QEMU/KVM VM <strong>create</strong>, run
/// before the request reaches the Proxmox host so the user gets a clean message
/// instead of a raw Proxmox 400. The VM analogue of
/// <see cref="ProxmoxLxcCreateValidator"/>: it only flags what it can verify without
/// the host (it can't know which storages or ISO volumes exist — Proxmox stays
/// authoritative and relays any rejection verbatim). The vmid range is shared with
/// the LXC validator so the two create paths apply the exact same id rule.
/// </summary>
public static class ProxmoxQemuCreateValidator
{
    private static readonly Regex MacRegex =
        new("^([0-9A-Fa-f]{2}:){5}[0-9A-Fa-f]{2}$", RegexOptions.Compiled);

    /// <summary>Proxmox guest OS-type hints accepted by <c>POST …/qemu</c>.</summary>
    private static readonly HashSet<string> OsTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "other", "wxp", "w2k", "w2k3", "w2k8", "wvista", "win7", "win8", "win10", "win11",
        "l24", "l26", "solaris",
    };

    private static readonly HashSet<string> BiosTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "seabios", "ovmf",
    };

    private static readonly HashSet<string> Machines = new(StringComparer.OrdinalIgnoreCase)
    {
        "q35", "pc", "i440fx",
    };

    /// <summary>Returns one human-readable message per problem; empty ⇒ valid.</summary>
    public static IReadOnlyList<string> Validate(ProxmoxQemuCreate spec)
    {
        var errors = new List<string>();

        if (spec.VmId < ProxmoxLxcCreateValidator.MinVmId || spec.VmId > ProxmoxLxcCreateValidator.MaxVmId)
            errors.Add($"VMID must be between {ProxmoxLxcCreateValidator.MinVmId} and {ProxmoxLxcCreateValidator.MaxVmId}.");

        if (string.IsNullOrWhiteSpace(spec.DiskStorage))
            errors.Add("A disk storage is required.");

        if (spec.DiskSizeGib < 1)
            errors.Add("Disk size must be at least 1 GiB.");

        if (spec.Cores is { } cores && (cores < 1 || cores > 8192))
            errors.Add("Cores must be between 1 and 8192.");

        if (spec.Sockets is { } sockets && (sockets < 1 || sockets > 4))
            errors.Add("Sockets must be between 1 and 4.");

        if (spec.MemoryMib is { } mem && mem < 16)
            errors.Add("Memory must be at least 16 MiB.");

        if (string.IsNullOrWhiteSpace(spec.Bridge))
            errors.Add("A network bridge is required.");

        if (string.IsNullOrWhiteSpace(spec.OsType) || !OsTypes.Contains(spec.OsType.Trim()))
            errors.Add("Pick a known OS type (e.g. l26, win11).");

        if (string.IsNullOrWhiteSpace(spec.Bios) || !BiosTypes.Contains(spec.Bios.Trim()))
            errors.Add("BIOS must be SeaBIOS or OVMF.");

        if (!string.IsNullOrWhiteSpace(spec.Machine) && !Machines.Contains(spec.Machine.Trim()))
            errors.Add("Machine must be q35 or i440fx.");

        if (spec.VlanTag is { } tag && (tag < 1 || tag > 4094))
            errors.Add("VLAN tag must be between 1 and 4094.");

        if (!string.IsNullOrWhiteSpace(spec.MacAddr) && !MacRegex.IsMatch(spec.MacAddr.Trim()))
            errors.Add($"'{spec.MacAddr}' is not a valid MAC address (aa:bb:cc:dd:ee:ff).");

        // The install media is optional (a VM can boot from disk / PXE), but when set
        // it must look like an ISO volume — Proxmox stays authoritative for existence.
        if (!string.IsNullOrWhiteSpace(spec.IsoVolid) && !spec.IsoVolid.Contains("iso", StringComparison.OrdinalIgnoreCase))
            errors.Add("The selected install media is not an ISO image.");

        return errors;
    }
}
