using Stashboard.Core.Abstractions;

namespace Stashboard.Core.Proxmox;

/// <summary>
/// V8.1 / V8.3 — locally-checkable guards for a guest <strong>restore</strong> from a
/// vzdump backup archive, run before the request reaches the Proxmox host so the
/// user gets a clean message instead of a raw Proxmox 400. Restore reuses the
/// <see cref="ProxmoxLxcCreate"/> spec (with <see cref="ProxmoxLxcCreate.Restore"/>
/// set), but its rules differ from a template create: the archive carries the
/// rootfs sizes and the guest's own config, so there is no password / template
/// / size to validate — only the target vmid, the backup volid, and the optional
/// default-storage override. The <c>qemu</c> flag selects which archive kind the
/// volid must be (<c>vzdump-qemu-*</c> for a VM, <c>vzdump-lxc-*</c> for a container) —
/// they restore through different endpoints. Proxmox stays authoritative for
/// everything that needs the host (storage / archive existence, whether the volid is
/// really restorable).
/// </summary>
public static class ProxmoxLxcRestoreValidator
{
    /// <summary>Returns one human-readable message per problem; empty ⇒ valid.
    /// <paramref name="qemu"/> selects the expected archive kind (VM vs LXC).</summary>
    public static IReadOnlyList<string> Validate(ProxmoxLxcCreate spec, bool qemu = false)
    {
        var errors = new List<string>();

        if (spec.VmId < ProxmoxLxcCreateValidator.MinVmId || spec.VmId > ProxmoxLxcCreateValidator.MaxVmId)
            errors.Add($"VMID must be between {ProxmoxLxcCreateValidator.MinVmId} and {ProxmoxLxcCreateValidator.MaxVmId}.");

        var (marker, kindLabel) = qemu ? ("vzdump-qemu-", "VM") : ("vzdump-lxc-", "LXC");
        if (string.IsNullOrWhiteSpace(spec.OsTemplate))
            errors.Add("A backup archive is required.");
        else if (!spec.OsTemplate.Contains(marker, StringComparison.OrdinalIgnoreCase))
            errors.Add($"The selected volume is not a {kindLabel} backup archive (expected a {marker}… volume).");

        if (spec.Cores is { } cores && (cores < 1 || cores > 8192))
            errors.Add("Cores must be between 1 and 8192.");

        if (spec.MemoryMib is { } mem && mem < 16)
            errors.Add("Memory must be at least 16 MiB.");

        if (spec.SwapMib is { } swap && swap < 0)
            errors.Add("Swap must be 0 MiB or more.");

        return errors;
    }
}
