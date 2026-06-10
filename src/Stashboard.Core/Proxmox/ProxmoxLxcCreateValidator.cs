using Stashboard.Core.Abstractions;

namespace Stashboard.Core.Proxmox;

/// <summary>
/// V6.13.1 — locally-checkable guards for an LXC <strong>create</strong>, run
/// before the request reaches the Proxmox host so the user gets a clean message
/// instead of a raw Proxmox 400. It mirrors the V6.9 edit validator's posture: it
/// only flags what it can verify without the host (it can't know which storages
/// or templates exist — Proxmox stays authoritative and relays any rejection
/// verbatim). The single <c>net0</c> interface is validated through the shared
/// <see cref="ProxmoxLxcConfigValidator"/> so the create and edit paths apply the
/// exact same CIDR / MAC / VLAN rules.
/// </summary>
public static class ProxmoxLxcCreateValidator
{
    /// <summary>Proxmox guest id range. The controller additionally rejects a
    /// vmid already present in the host's guest list (which needs the DB).</summary>
    public const int MinVmId = 100;
    public const int MaxVmId = 999_999_999;

    /// <summary>Returns one human-readable message per problem; empty ⇒ valid.</summary>
    public static IReadOnlyList<string> Validate(ProxmoxLxcCreate spec)
    {
        var errors = new List<string>();

        if (spec.VmId < MinVmId || spec.VmId > MaxVmId)
            errors.Add($"VMID must be between {MinVmId} and {MaxVmId}.");

        if (string.IsNullOrWhiteSpace(spec.OsTemplate))
            errors.Add("A template is required.");

        if (string.IsNullOrWhiteSpace(spec.RootfsStorage))
            errors.Add("A root filesystem storage is required.");

        if (spec.RootfsSizeGib < 1)
            errors.Add("Root filesystem size must be at least 1 GiB.");

        if (spec.Cores is { } cores && (cores < 1 || cores > 8192))
            errors.Add("Cores must be between 1 and 8192.");

        if (spec.MemoryMib is { } mem && mem < 16)
            errors.Add("Memory must be at least 16 MiB.");

        if (spec.SwapMib is { } swap && swap < 0)
            errors.Add("Swap must be 0 MiB or more.");

        // Either a root password or an SSH key is needed to log in afterwards;
        // Proxmox itself allows neither, but a container you can't reach is a
        // footgun, so we nudge the user to set one.
        if (string.IsNullOrWhiteSpace(spec.Password) && string.IsNullOrWhiteSpace(spec.SshPublicKeys))
            errors.Add("Set a root password or an SSH public key so you can log in to the container.");

        // Reuse the edit validator for the interface so net rules never drift.
        if (spec.Net0 is { } net)
            errors.AddRange(ProxmoxLxcConfigValidator.Validate(new ProxmoxLxcConfigUpdate(Networks: [net])));

        return errors;
    }
}
