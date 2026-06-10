namespace Stashboard.Core.Proxmox;

/// <summary>
/// V6.8.1 — the fully-resolved warn/crit thresholds an evaluation uses, after a
/// node's optional per-node overrides have been merged over the global defaults.
/// The percentage metrics (CPU / memory / storage) reuse the V6.8 card defaults
/// verbatim so the card colour and the fired alert agree. Thermal defaults are
/// the fallback used only when the chip doesn't expose its own high/crit;
/// wearout and NIC thresholds are fixed (not surfaced for per-node override this
/// phase) but live here so the evaluator reads them from one place.
/// </summary>
/// <param name="CpuWarn">CPU utilisation % at/above which CPU warns.</param>
/// <param name="CpuCrit">CPU utilisation % at/above which CPU is critical.</param>
/// <param name="MemWarn">Memory used % at/above which memory warns.</param>
/// <param name="MemCrit">Memory used % at/above which memory is critical.</param>
/// <param name="StorageWarn">Storage used % at/above which storage warns.</param>
/// <param name="StorageCrit">Storage used % at/above which storage is critical.</param>
/// <param name="TempWarn">Fallback °C warn threshold when the chip exposes none.</param>
/// <param name="TempCrit">Fallback °C crit threshold when the chip exposes none.</param>
/// <param name="WearoutWarn">SSD media-life-remaining % at/below which it warns.</param>
/// <param name="WearoutCrit">SSD media-life-remaining % at/below which it's critical.</param>
/// <param name="NicWarnDelta">Rise in NIC error+drop count between evaluations
/// at/above which the NIC warns.</param>
/// <param name="NicCritDelta">Rise in NIC error+drop count between evaluations
/// at/above which the NIC is critical.</param>
public sealed record ProxmoxNodeAlertThresholds(
    int CpuWarn,
    int CpuCrit,
    int MemWarn,
    int MemCrit,
    int StorageWarn,
    int StorageCrit,
    int TempWarn,
    int TempCrit,
    int WearoutWarn,
    int WearoutCrit,
    long NicWarnDelta,
    long NicCritDelta)
{
    /// <summary>The global baseline — the V6.8 card defaults for the percentage
    /// metrics, plus conservative thermal/wearout/NIC fallbacks.</summary>
    public static readonly ProxmoxNodeAlertThresholds Defaults = new(
        CpuWarn: 80, CpuCrit: 95,
        MemWarn: 85, MemCrit: 95,
        StorageWarn: 85, StorageCrit: 95,
        TempWarn: 80, TempCrit: 90,
        WearoutWarn: 20, WearoutCrit: 10,
        NicWarnDelta: 10, NicCritDelta: 100);

    /// <summary>
    /// Merges the per-node overrides (any <c>null</c> falls back to the matching
    /// global default) into a resolved set. Only the four user-tunable pairs are
    /// overridable; wearout and NIC keep the defaults.
    /// </summary>
    public static ProxmoxNodeAlertThresholds Resolve(
        int? cpuWarn, int? cpuCrit,
        int? memWarn, int? memCrit,
        int? storageWarn, int? storageCrit,
        int? tempWarn, int? tempCrit) =>
        Defaults with
        {
            CpuWarn = cpuWarn ?? Defaults.CpuWarn,
            CpuCrit = cpuCrit ?? Defaults.CpuCrit,
            MemWarn = memWarn ?? Defaults.MemWarn,
            MemCrit = memCrit ?? Defaults.MemCrit,
            StorageWarn = storageWarn ?? Defaults.StorageWarn,
            StorageCrit = storageCrit ?? Defaults.StorageCrit,
            TempWarn = tempWarn ?? Defaults.TempWarn,
            TempCrit = tempCrit ?? Defaults.TempCrit,
        };
}
