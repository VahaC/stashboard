namespace Stashboard.Core.Enums;

/// <summary>
/// V7.9 — the Proxmox analogue of <see cref="DockerUpdateStatus"/>: the
/// aggregated pending-update state a service derives from the Proxmox guests
/// linked to it, driving the dashboard card's update badge alongside the Docker
/// one. Mirrors the Docker enum's members (and integer values) so the same badge
/// component renders both.
/// </summary>
public enum ProxmoxUpdateStatus
{
    Unknown = 0,
    UpToDate = 1,
    UpdateAvailable = 2,
    Error = 3,
    Disabled = 4,
}
