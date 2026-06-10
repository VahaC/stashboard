using Stashboard.Api.Contracts;
using Stashboard.Core.Entities;
using Stashboard.Core.Enums;
using Stashboard.Core.Proxmox;

namespace Stashboard.Api.Mapping;

/// <summary>
/// V6.8.1 — maps between the node-alert entities and their API contracts. Pure /
/// static (no secrets, no DI): converts the persisted <see cref="ProxmoxAlertCategory"/>
/// mask to/from plain booleans, exposes the global thresholds as placeholders,
/// and renders active alert-state rows for the Alerts tab.
/// </summary>
public static class ProxmoxNodeAlertMapper
{
    /// <summary>Response for a host's settings. <paramref name="settings"/> is
    /// <c>null</c> when the node has never been configured — the user then sees
    /// the muted, all-categories, no-override default without a row being
    /// persisted.</summary>
    public static ProxmoxNodeAlertSettingsResponse ToResponse(ProxmoxNodeAlertSettingsEntity? settings)
    {
        var mask = settings?.CategoryMask ?? ProxmoxAlertCategory.All;
        return new ProxmoxNodeAlertSettingsResponse(
            Enabled: settings?.Enabled ?? false,
            Categories: ToToggles(mask),
            Thresholds: new ProxmoxNodeAlertThresholdValues(
                settings?.CpuWarn, settings?.CpuCrit,
                settings?.MemWarn, settings?.MemCrit,
                settings?.StorageWarn, settings?.StorageCrit,
                settings?.TempWarn, settings?.TempCrit),
            Defaults: DefaultThresholdValues,
            LastNotificationSentUtc: settings?.LastNotificationSentUtc);
    }

    /// <summary>Applies an upsert onto a (new or existing) settings row.</summary>
    public static void ApplyUpdate(ProxmoxNodeAlertSettingsEntity entity, ProxmoxNodeAlertSettingsUpdateRequest request)
    {
        entity.Enabled = request.Enabled;
        entity.CategoryMask = ToMask(request.Categories);

        var t = request.Thresholds;
        entity.CpuWarn = t.CpuWarn;
        entity.CpuCrit = t.CpuCrit;
        entity.MemWarn = t.MemWarn;
        entity.MemCrit = t.MemCrit;
        entity.StorageWarn = t.StorageWarn;
        entity.StorageCrit = t.StorageCrit;
        entity.TempWarn = t.TempWarn;
        entity.TempCrit = t.TempCrit;

        entity.UpdatedUtc = DateTime.UtcNow;
    }

    /// <summary>One active alert-state row → the Alerts-tab response.</summary>
    public static ProxmoxNodeAlertResponse ToAlertResponse(ProxmoxNodeAlertStateEntity s) =>
        new(
            Category: CategoryName(s.Category),
            Severity: s.ActiveLevel == HealthLevel.Crit ? "crit" : "warn",
            Metric: s.Metric,
            Value: s.Value,
            Threshold: s.Threshold,
            FirstSeenUtc: s.FirstSeenUtc);

    // ── mask <-> toggles ──────────────────────────────────────────────────────

    public static ProxmoxAlertCategoryToggles ToToggles(ProxmoxAlertCategory mask) =>
        new(
            Cpu: mask.HasFlag(ProxmoxAlertCategory.Cpu),
            Memory: mask.HasFlag(ProxmoxAlertCategory.Memory),
            Storage: mask.HasFlag(ProxmoxAlertCategory.Storage),
            Thermal: mask.HasFlag(ProxmoxAlertCategory.Thermal),
            Smart: mask.HasFlag(ProxmoxAlertCategory.Smart),
            Network: mask.HasFlag(ProxmoxAlertCategory.Network));

    public static ProxmoxAlertCategory ToMask(ProxmoxAlertCategoryToggles t)
    {
        var mask = ProxmoxAlertCategory.None;
        if (t.Cpu) mask |= ProxmoxAlertCategory.Cpu;
        if (t.Memory) mask |= ProxmoxAlertCategory.Memory;
        if (t.Storage) mask |= ProxmoxAlertCategory.Storage;
        if (t.Thermal) mask |= ProxmoxAlertCategory.Thermal;
        if (t.Smart) mask |= ProxmoxAlertCategory.Smart;
        if (t.Network) mask |= ProxmoxAlertCategory.Network;
        return mask;
    }

    private static string CategoryName(ProxmoxAlertCategory c) => c switch
    {
        ProxmoxAlertCategory.Cpu => "cpu",
        ProxmoxAlertCategory.Memory => "memory",
        ProxmoxAlertCategory.Storage => "storage",
        ProxmoxAlertCategory.Thermal => "thermal",
        ProxmoxAlertCategory.Smart => "smart",
        ProxmoxAlertCategory.Network => "network",
        _ => c.ToString().ToLowerInvariant(),
    };

    private static readonly ProxmoxNodeAlertThresholdValues DefaultThresholdValues = new(
        ProxmoxNodeAlertThresholds.Defaults.CpuWarn, ProxmoxNodeAlertThresholds.Defaults.CpuCrit,
        ProxmoxNodeAlertThresholds.Defaults.MemWarn, ProxmoxNodeAlertThresholds.Defaults.MemCrit,
        ProxmoxNodeAlertThresholds.Defaults.StorageWarn, ProxmoxNodeAlertThresholds.Defaults.StorageCrit,
        ProxmoxNodeAlertThresholds.Defaults.TempWarn, ProxmoxNodeAlertThresholds.Defaults.TempCrit);
}
