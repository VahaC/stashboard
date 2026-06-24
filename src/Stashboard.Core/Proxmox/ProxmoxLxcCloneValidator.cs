using System.Text.RegularExpressions;
using Stashboard.Core.Abstractions;

namespace Stashboard.Core.Proxmox;

/// <summary>
/// V8.0 — locally-checkable guards for an LXC <strong>clone</strong> and for
/// <strong>snapshot</strong> names, run before the request reaches the Proxmox
/// host so the user gets a clean message instead of a raw Proxmox 400. Mirrors
/// <see cref="ProxmoxLxcCreateValidator"/>'s posture: it only flags what it can
/// verify without the host (it can't know which storages exist, whether a linked
/// clone is possible, or whether a snapshot name is already taken — Proxmox stays
/// authoritative and relays any rejection verbatim). The destination vmid reuses
/// the exact create range so clone and create never drift.
/// </summary>
public static class ProxmoxLxcCloneValidator
{
    /// <summary>Proxmox snapshot-name rule: starts with a letter, then letters /
    /// digits / underscore / hyphen, up to a sane length. <c>current</c> is
    /// reserved (it is the synthetic live-state pseudo-snapshot).</summary>
    private static readonly Regex SnapshotName = new(
        "^[A-Za-z][A-Za-z0-9_-]{0,39}$", RegexOptions.Compiled);

    /// <summary>V8.2 — the disk formats Proxmox accepts for a QEMU full clone. Only
    /// meaningful for a VM; an LXC clone ignores <see cref="ProxmoxLxcClone.Format"/>.</summary>
    private static readonly HashSet<string> CloneFormats =
        new(StringComparer.OrdinalIgnoreCase) { "raw", "qcow2", "vmdk" };

    /// <summary>Returns one human-readable message per problem; empty ⇒ valid. The
    /// controller additionally rejects a destination vmid already present in the
    /// host's guest list (which needs the DB).</summary>
    public static IReadOnlyList<string> Validate(ProxmoxLxcClone spec)
    {
        var errors = new List<string>();

        if (spec.NewVmId < ProxmoxLxcCreateValidator.MinVmId || spec.NewVmId > ProxmoxLxcCreateValidator.MaxVmId)
            errors.Add($"VMID must be between {ProxmoxLxcCreateValidator.MinVmId} and {ProxmoxLxcCreateValidator.MaxVmId}.");

        if (spec.SnapName is { } snap && !IsValidSnapshotName(snap))
            errors.Add("Source snapshot name is invalid.");

        if (spec.Format is { Length: > 0 } fmt && !CloneFormats.Contains(fmt))
            errors.Add("Disk format must be one of raw, qcow2 or vmdk.");

        return errors;
    }

    /// <summary>True when <paramref name="name"/> is a valid Proxmox snapshot name
    /// (and not the reserved <c>current</c>).</summary>
    public static bool IsValidSnapshotName(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && !string.Equals(name, "current", StringComparison.OrdinalIgnoreCase)
        && SnapshotName.IsMatch(name);
}
