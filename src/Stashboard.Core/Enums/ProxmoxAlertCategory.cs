namespace Stashboard.Core.Enums;

/// <summary>
/// V6.8.1 — the node-health deviation categories that can fire an alert. Used
/// both as a single-bit value on a per-category alert-state row and, combined,
/// as the <see cref="All"/> mask persisted on the per-node alert settings (so a
/// user can opt individual categories out without muting the node). The single
/// bits double as the natural key alongside the connection id, mirroring how
/// <c>ProxmoxGuestEntity</c> keys on (connection, vmid).
/// </summary>
[Flags]
public enum ProxmoxAlertCategory
{
    None = 0,

    /// <summary>CPU saturation (utilisation % vs warn/crit).</summary>
    Cpu = 1,

    /// <summary>Memory pressure (used/total % vs warn/crit).</summary>
    Memory = 2,

    /// <summary>Storage fullness (worst pool used/total % vs warn/crit).</summary>
    Storage = 4,

    /// <summary>Thermal limit (temperature vs the chip's own high/crit, falling
    /// back to defaults).</summary>
    Thermal = 8,

    /// <summary>SMART degradation (health ≠ PASSED, or SSD wearout ≤ thresholds).</summary>
    Smart = 16,

    /// <summary>NIC error/drop spikes (rise in rx/tx error+drop counters between
    /// evaluations).</summary>
    Network = 32,

    /// <summary>Every category — the default mask for a freshly enabled node.</summary>
    All = Cpu | Memory | Storage | Thermal | Smart | Network,
}
