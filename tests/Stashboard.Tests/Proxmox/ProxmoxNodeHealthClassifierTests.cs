using Stashboard.Core.Enums;
using Stashboard.Core.Proxmox;

namespace Stashboard.Tests.Proxmox;

/// <summary>
/// V6.8.1 — boundary-value tests for the pure node-health classifier. The
/// <c>&gt;=</c> boundaries must match the frontend's <c>proxmox-node-health.ts</c>
/// exactly so the card colour and the fired alert never disagree.
/// </summary>
public class ProxmoxNodeHealthClassifierTests
{
    // ── percentage metrics (CPU / RAM / storage) ──────────────────────────────

    [Theory]
    [InlineData(null, HealthLevel.Ok)]   // unknown ratio is never an alert
    [InlineData(0.0, HealthLevel.Ok)]
    [InlineData(79.99, HealthLevel.Ok)]
    [InlineData(80.0, HealthLevel.Warn)]   // inclusive warn boundary
    [InlineData(94.99, HealthLevel.Warn)]
    [InlineData(95.0, HealthLevel.Crit)]   // inclusive crit boundary
    [InlineData(100.0, HealthLevel.Crit)]
    public void LevelForPercent_Cpu_BoundaryValues(double? pct, HealthLevel expected) =>
        Assert.Equal(expected, ProxmoxNodeHealthClassifier.LevelForPercent(pct, 80, 95));

    [Theory]
    [InlineData(84.99, HealthLevel.Ok)]
    [InlineData(85, HealthLevel.Warn)]
    [InlineData(95, HealthLevel.Crit)]
    public void LevelForPercent_MemoryAndStorage_BoundaryValues(double pct, HealthLevel expected) =>
        Assert.Equal(expected, ProxmoxNodeHealthClassifier.LevelForPercent(pct, 85, 95));

    [Theory]
    [InlineData(50.0, 100.0, 50.0)]
    [InlineData(0.0, 0.0, null)]      // zero total → unknown, not 0%
    [InlineData(10.0, null, null)]
    [InlineData(null, 100.0, null)]
    public void Percent_HandlesMissingTotals(double? used, double? total, double? expected) =>
        Assert.Equal(expected, ProxmoxNodeHealthClassifier.Percent(used, total));

    // ── SMART (health string + wearout) ───────────────────────────────────────

    [Theory]
    [InlineData("PASSED", null, HealthLevel.Ok)]
    [InlineData("passed", null, HealthLevel.Ok)]   // case-insensitive
    [InlineData("OK", null, HealthLevel.Ok)]
    [InlineData("FAILED", null, HealthLevel.Crit)]
    [InlineData("FAILED!", null, HealthLevel.Crit)]
    [InlineData(null, null, HealthLevel.Ok)]       // no data → not an alert
    public void DiskLevel_HealthString(string? health, int? wearout, HealthLevel expected) =>
        Assert.Equal(expected, ProxmoxNodeHealthClassifier.DiskLevel(health, wearout, 20, 10));

    [Theory]
    [InlineData(21, HealthLevel.Ok)]
    [InlineData(20, HealthLevel.Warn)]   // inclusive warn boundary (≤ 20)
    [InlineData(11, HealthLevel.Warn)]
    [InlineData(10, HealthLevel.Crit)]   // inclusive crit boundary (≤ 10)
    [InlineData(0, HealthLevel.Crit)]
    public void DiskLevel_Wearout(int wearout, HealthLevel expected) =>
        Assert.Equal(expected, ProxmoxNodeHealthClassifier.DiskLevel("PASSED", wearout, 20, 10));

    // ── thermal (chip thresholds with default fallback) ───────────────────────

    [Theory]
    [InlineData(70, 80, 90, HealthLevel.Ok)]
    [InlineData(80, 80, 90, HealthLevel.Warn)]   // chip's own high
    [InlineData(90, 80, 90, HealthLevel.Crit)]   // chip's own crit
    public void TempLevel_UsesChipThresholds(double temp, double high, double crit, HealthLevel expected) =>
        Assert.Equal(expected, ProxmoxNodeHealthClassifier.TempLevel(temp, high, crit, 80, 90));

    [Theory]
    [InlineData(79, HealthLevel.Ok)]
    [InlineData(80, HealthLevel.Warn)]   // falls back to default warn
    [InlineData(90, HealthLevel.Crit)]   // falls back to default crit
    public void TempLevel_FallsBackToDefaults_WhenChipExposesNone(double temp, HealthLevel expected) =>
        Assert.Equal(expected, ProxmoxNodeHealthClassifier.TempLevel(temp, null, null, 80, 90));

    [Fact]
    public void TempLevel_NullTemp_IsOk() =>
        Assert.Equal(HealthLevel.Ok, ProxmoxNodeHealthClassifier.TempLevel(null, 80, 90, 80, 90));

    // ── NIC error/drop spike ──────────────────────────────────────────────────

    [Theory]
    [InlineData(9, HealthLevel.Ok)]
    [InlineData(10, HealthLevel.Warn)]
    [InlineData(99, HealthLevel.Warn)]
    [InlineData(100, HealthLevel.Crit)]
    public void NicLevel_BoundaryValues(long delta, HealthLevel expected) =>
        Assert.Equal(expected, ProxmoxNodeHealthClassifier.NicLevel(delta, 10, 100));

    // ── per-node override resolution vs global defaults ───────────────────────

    [Fact]
    public void Resolve_AllNull_ReturnsGlobalDefaults()
    {
        var t = ProxmoxNodeAlertThresholds.Resolve(null, null, null, null, null, null, null, null);
        Assert.Equal(ProxmoxNodeAlertThresholds.Defaults, t);
        Assert.Equal(80, t.CpuWarn);
        Assert.Equal(95, t.CpuCrit);
        Assert.Equal(85, t.MemWarn);
    }

    [Fact]
    public void Resolve_PartialOverride_MergesOverDefaults()
    {
        // A deliberately hot node: CPU warns later, everything else default.
        var t = ProxmoxNodeAlertThresholds.Resolve(
            cpuWarn: 90, cpuCrit: 98, memWarn: null, memCrit: null,
            storageWarn: null, storageCrit: null, tempWarn: null, tempCrit: null);

        Assert.Equal(90, t.CpuWarn);
        Assert.Equal(98, t.CpuCrit);
        // Untouched pairs keep the global baseline.
        Assert.Equal(85, t.MemWarn);
        Assert.Equal(95, t.MemCrit);
        Assert.Equal(85, t.StorageWarn);
        // Non-overridable thresholds always keep defaults.
        Assert.Equal(20, t.WearoutWarn);
        Assert.Equal(10, t.WearoutCrit);
        Assert.Equal(10, t.NicWarnDelta);
    }
}



