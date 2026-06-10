using Stashboard.Core.Enums;

namespace Stashboard.Core.Proxmox;

/// <summary>
/// V6.8.1 — pure classification of a single node-health metric into a
/// <see cref="HealthLevel"/>. The backend port of the frontend's
/// <c>proxmox-node-health.ts</c> helpers, kept byte-for-byte equivalent
/// (inclusive <c>&gt;=</c> boundaries) so the colour-coded card and the fired
/// alert never disagree. Stateless and side-effect free — the debounce /
/// hysteresis logic lives in <see cref="ProxmoxNodeAlertEvaluator"/>.
/// </summary>
public static class ProxmoxNodeHealthClassifier
{
    /// <summary>used/total as a 0..100 percentage, or <c>null</c> when total is
    /// missing or non-positive (an unknown ratio, not zero).</summary>
    public static double? Percent(double? used, double? total)
    {
        if (used is null || total is null || total <= 0) return null;
        return used.Value / total.Value * 100.0;
    }

    /// <summary>Classify a 0..100 percentage against a warn/crit pair. A
    /// <c>null</c> percentage is unknown, never an alert.</summary>
    public static HealthLevel LevelForPercent(double? pct, int warn, int crit)
    {
        if (pct is null) return HealthLevel.Ok;
        if (pct.Value >= crit) return HealthLevel.Crit;
        if (pct.Value >= warn) return HealthLevel.Warn;
        return HealthLevel.Ok;
    }

    /// <summary>Classify a disk's SMART health string + SSD wearout. A health
    /// string that isn't PASSED/OK is critical; a low wearout warns/crits.</summary>
    public static HealthLevel DiskLevel(string? health, int? wearoutPercent, int wearoutWarn, int wearoutCrit)
    {
        if (!string.IsNullOrWhiteSpace(health))
        {
            var h = health.Trim().ToUpperInvariant();
            if (h is not ("PASSED" or "OK")) return HealthLevel.Crit;
        }

        if (wearoutPercent is not null)
        {
            if (wearoutPercent.Value <= wearoutCrit) return HealthLevel.Crit;
            if (wearoutPercent.Value <= wearoutWarn) return HealthLevel.Warn;
        }

        return HealthLevel.Ok;
    }

    /// <summary>Classify a temperature against the chip's own high/crit, falling
    /// back to the supplied defaults when the chip doesn't expose them.</summary>
    public static HealthLevel TempLevel(double? tempC, double? highC, double? critC, int defWarn, int defCrit)
    {
        if (tempC is null) return HealthLevel.Ok;
        var crit = critC ?? defCrit;
        var warn = highC ?? defWarn;
        if (tempC.Value >= crit) return HealthLevel.Crit;
        if (tempC.Value >= warn) return HealthLevel.Warn;
        return HealthLevel.Ok;
    }

    /// <summary>Classify a NIC error/drop spike: the rise in error+drop counters
    /// since the previous evaluation against a warn/crit delta pair.</summary>
    public static HealthLevel NicLevel(long delta, long warnDelta, long critDelta)
    {
        if (delta >= critDelta) return HealthLevel.Crit;
        if (delta >= warnDelta) return HealthLevel.Warn;
        return HealthLevel.Ok;
    }
}
