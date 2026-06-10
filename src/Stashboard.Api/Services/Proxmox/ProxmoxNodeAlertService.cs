using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Stashboard.Api.Auth;
using Stashboard.Api.Data;
using Stashboard.Api.Mapping;
using Stashboard.Api.Notifications;
using Stashboard.Core.Abstractions;
using Stashboard.Core.Entities;
using Stashboard.Core.Enums;
using Stashboard.Core.Options;
using Stashboard.Core.Proxmox;

namespace Stashboard.Api.Services.Proxmox;

/// <summary>
/// V6.8.1 — evaluates one Proxmox host's node-health metrics against its alert
/// thresholds, advances the per-category debounce/hysteresis state machine, and
/// fires email/Telegram notifications when the active-alert set changes. Reads
/// the very same API/SSH sources the V6.8 node card uses (node status, storage
/// pools, disks/SMART, sensors, NIC counters) so the card colour and the fired
/// alert always agree.
/// <para>Folded into <see cref="ProxmoxUpdateBackgroundService"/>'s tick (every
/// ~5 minutes, independent of the per-host update schedule) and exposed behind
/// an interface so it can be driven deterministically from tests.</para>
/// </summary>
public interface IProxmoxNodeAlertService
{
    /// <summary>Evaluates one connection's node health and persists the outcome.
    /// A no-op when the host has no alert settings row or it's disabled. The
    /// <paramref name="connection"/> need not be tracked — this method manages
    /// its own tracked reads/writes.</summary>
    Task EvaluateConnectionAsync(ProxmoxConnectionEntity connection, CancellationToken cancellationToken = default);
}

public sealed class ProxmoxNodeAlertService(
    ApplicationDbContext db,
    IProxmoxConnectionMapper mapper,
    IProxmoxApiClient apiClient,
    IProxmoxGuestInspector guestInspector,
    IProxmoxNodeAlertNotificationService notifier,
    IUserService userService,
    IOptionsMonitor<ProxmoxUpdateOptions> options,
    ILogger<ProxmoxNodeAlertService> logger) : IProxmoxNodeAlertService
{
    public async Task EvaluateConnectionAsync(ProxmoxConnectionEntity connection, CancellationToken cancellationToken = default)
    {
        var settings = await db.ProxmoxNodeAlertSettings.AsTracking()
            .FirstOrDefaultAsync(s => s.ProxmoxConnectionId == connection.Id, cancellationToken);

        // Opted out (or never configured) — never touch the Proxmox host.
        if (settings is null || !settings.Enabled) return;

        var thresholds = ProxmoxNodeAlertThresholds.Resolve(
            settings.CpuWarn, settings.CpuCrit,
            settings.MemWarn, settings.MemCrit,
            settings.StorageWarn, settings.StorageCrit,
            settings.TempWarn, settings.TempCrit);

        var profile = mapper.BuildProfile(connection);

        var states = await db.ProxmoxNodeAlertStates.AsTracking()
            .Where(s => s.ProxmoxConnectionId == connection.Id)
            .ToListAsync(cancellationToken);
        var byCategory = states.ToDictionary(s => s.Category);

        var observations = await GatherObservationsAsync(profile, settings.CategoryMask, thresholds, byCategory, cancellationToken);

        var n = options.CurrentValue.AlertConsecutiveBreaches;
        var now = DateTime.UtcNow;

        foreach (var obs in observations)
        {
            if (!byCategory.TryGetValue(obs.Category, out var entity))
            {
                entity = new ProxmoxNodeAlertStateEntity
                {
                    Id = Guid.NewGuid(),
                    ProxmoxConnectionId = connection.Id,
                    Category = obs.Category,
                };
                db.ProxmoxNodeAlertStates.Add(entity);
                byCategory[obs.Category] = entity;
            }

            var prev = ToEvalState(entity);
            var result = ProxmoxNodeAlertEvaluator.Step(prev, obs, n, now);
            ApplyEvalState(entity, result.State, now);
        }

        // Active = any category currently firing. Drives both the Alerts tab and
        // the notification digest.
        var active = byCategory.Values
            .Where(s => s.ActiveLevel != HealthLevel.Ok)
            .OrderBy(s => s.Category)
            .ToList();
        var signature = BuildSignature(active);

        var user = await userService.FindByIdAsync(connection.UserId, cancellationToken);
        if (user is not null)
            await notifier.NotifyIfNeededAsync(user, connection, settings, active, signature, cancellationToken);

        settings.UpdatedUtc = now;
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Stable signature of the active-alert set — sorted
    /// "category:level" tuples. Deliberately excludes the metric value: an alert
    /// is "the same" while its category and severity are unchanged, so a steady
    /// deviation whose value wiggles every tick (e.g. CPU 96↔97 %, or the NIC
    /// error delta) never re-pings. Only a severity change (warn↔crit), a new
    /// category, or a clear changes the signature and re-notifies (mirrors the
    /// update notifier's signature throttle).</summary>
    public static string BuildSignature(IEnumerable<ProxmoxNodeAlertStateEntity> active) =>
        string.Join("|", active
            .OrderBy(s => s.Category)
            .Select(s => $"{s.Category}:{s.ActiveLevel}"));

    // ── observation gathering ─────────────────────────────────────────────────

    private async Task<List<NodeAlertObservation>> GatherObservationsAsync(
        ProxmoxConnectionProfile profile,
        ProxmoxAlertCategory mask,
        ProxmoxNodeAlertThresholds t,
        IReadOnlyDictionary<ProxmoxAlertCategory, ProxmoxNodeAlertStateEntity> byCategory,
        CancellationToken cancellationToken)
    {
        var observations = new List<NodeAlertObservation>();

        // Node status backs CPU, Memory, and (with storage pools) Storage.
        ProxmoxNodeStatus? status = null;
        try { status = await apiClient.GetNodeStatusAsync(profile, cancellationToken); }
        catch (Exception ex) { logger.LogDebug(ex, "Alert eval: node status unavailable for {Node}", profile.NodeName); }

        if (mask.HasFlag(ProxmoxAlertCategory.Cpu))
        {
            var pct = status?.CpuFraction is { } f ? f * 100 : (double?)null;
            observations.Add(PercentObservation(
                ProxmoxAlertCategory.Cpu, status is not null, "CPU", pct, t.CpuWarn, t.CpuCrit));
        }

        if (mask.HasFlag(ProxmoxAlertCategory.Memory))
        {
            var pct = ProxmoxNodeHealthClassifier.Percent(status?.MemUsed, status?.MemTotal);
            observations.Add(PercentObservation(
                ProxmoxAlertCategory.Memory, status is not null, "RAM", pct, t.MemWarn, t.MemCrit));
        }

        if (mask.HasFlag(ProxmoxAlertCategory.Storage))
            observations.Add(await StorageObservationAsync(profile, status, t, cancellationToken));

        if (mask.HasFlag(ProxmoxAlertCategory.Smart))
            observations.Add(await SmartObservationAsync(profile, t, cancellationToken));

        if (mask.HasFlag(ProxmoxAlertCategory.Thermal))
            observations.Add(await ThermalObservationAsync(profile, t, cancellationToken));

        if (mask.HasFlag(ProxmoxAlertCategory.Network))
            observations.Add(await NetworkObservationAsync(
                profile, t, byCategory.GetValueOrDefault(ProxmoxAlertCategory.Network), cancellationToken));

        return observations;
    }

    private static NodeAlertObservation PercentObservation(
        ProxmoxAlertCategory category, bool available, string metric, double? pct, int warn, int crit)
    {
        var level = ProxmoxNodeHealthClassifier.LevelForPercent(pct, warn, crit);
        var threshold = level == HealthLevel.Crit ? crit : warn;
        return new NodeAlertObservation(category, available, level, metric, pct, threshold);
    }

    private async Task<NodeAlertObservation> StorageObservationAsync(
        ProxmoxConnectionProfile profile, ProxmoxNodeStatus? status, ProxmoxNodeAlertThresholds t, CancellationToken ct)
    {
        var worstPct = ProxmoxNodeHealthClassifier.Percent(status?.RootUsed, status?.RootTotal);
        var worstLabel = worstPct is null ? null : "root";
        var anySource = status is not null;

        try
        {
            var pools = await apiClient.GetNodeStoragesAsync(profile, ct);
            anySource = true;
            foreach (var p in pools.Where(p => p.Active))
            {
                var pct = ProxmoxNodeHealthClassifier.Percent(p.Used, p.Total);
                if (pct is { } v && (worstPct is null || v > worstPct))
                {
                    worstPct = v;
                    worstLabel = p.Storage;
                }
            }
        }
        catch (Exception ex) { logger.LogDebug(ex, "Alert eval: storage pools unavailable for {Node}", profile.NodeName); }

        var level = ProxmoxNodeHealthClassifier.LevelForPercent(worstPct, t.StorageWarn, t.StorageCrit);
        var threshold = level == HealthLevel.Crit ? t.StorageCrit : t.StorageWarn;
        return new NodeAlertObservation(
            ProxmoxAlertCategory.Storage, anySource, level, worstLabel ?? "storage", worstPct, threshold);
    }

    private async Task<NodeAlertObservation> SmartObservationAsync(
        ProxmoxConnectionProfile profile, ProxmoxNodeAlertThresholds t, CancellationToken ct)
    {
        try
        {
            var disks = await apiClient.GetNodeDisksAsync(profile, ct);
            var worst = HealthLevel.Ok;
            string? worstLabel = null;
            double? worstValue = null;
            foreach (var d in disks)
            {
                var level = ProxmoxNodeHealthClassifier.DiskLevel(d.Health, d.WearoutPercent, t.WearoutWarn, t.WearoutCrit);
                if (worstLabel is null || level > worst)
                {
                    worst = level;
                    worstLabel = d.DevPath;
                    worstValue = d.WearoutPercent;
                }
            }
            var threshold = worst == HealthLevel.Crit ? t.WearoutCrit : t.WearoutWarn;
            return new NodeAlertObservation(
                ProxmoxAlertCategory.Smart, true, worst, worstLabel ?? "disk", worstValue, threshold);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Alert eval: disks/SMART unavailable for {Node}", profile.NodeName);
            return new NodeAlertObservation(ProxmoxAlertCategory.Smart, false, HealthLevel.Ok, "disk", null, null);
        }
    }

    private async Task<NodeAlertObservation> ThermalObservationAsync(
        ProxmoxConnectionProfile profile, ProxmoxNodeAlertThresholds t, CancellationToken ct)
    {
        var sensors = await guestInspector.ReadNodeSensorsAsync(profile, ct);
        // SSH/lm-sensors missing ⇒ n/a, never an alert.
        if (!sensors.Available)
            return new NodeAlertObservation(ProxmoxAlertCategory.Thermal, false, HealthLevel.Ok, "temperature", null, null);

        var temps = sensors.Readings.Where(r => r.TempC is not null).ToList();
        if (temps.Count == 0)
            return new NodeAlertObservation(ProxmoxAlertCategory.Thermal, false, HealthLevel.Ok, "temperature", null, null);

        var worst = HealthLevel.Ok;
        ProxmoxSensorReading? worstReading = null;
        foreach (var r in temps)
        {
            var level = ProxmoxNodeHealthClassifier.TempLevel(r.TempC, r.HighC, r.CritC, t.TempWarn, t.TempCrit);
            if (level >= worst) { worst = level; worstReading = r; }
        }

        var threshold = worst == HealthLevel.Crit
            ? worstReading?.CritC ?? t.TempCrit
            : worstReading?.HighC ?? t.TempWarn;
        return new NodeAlertObservation(
            ProxmoxAlertCategory.Thermal, true, worst, worstReading?.Label ?? "temperature", worstReading?.TempC, threshold);
    }

    private async Task<NodeAlertObservation> NetworkObservationAsync(
        ProxmoxConnectionProfile profile, ProxmoxNodeAlertThresholds t,
        ProxmoxNodeAlertStateEntity? state, CancellationToken ct)
    {
        var errs = await guestInspector.ReadNodeNetworkErrorsAsync(profile, ct);
        if (!errs.Available)
            return new NodeAlertObservation(ProxmoxAlertCategory.Network, false, HealthLevel.Ok, "NIC errors", null, null);

        var baseline = state?.NicCounterBaseline;
        // First reading establishes the baseline (no delta yet); persist it so the
        // next tick can measure the rise. A counter reset (reboot) reads lower
        // than the baseline — clamp the delta to 0 rather than report a spike.
        var delta = baseline is { } b ? Math.Max(0, errs.TotalErrors - b) : 0;
        if (state is not null) state.NicCounterBaseline = errs.TotalErrors;

        var level = baseline is null
            ? HealthLevel.Ok
            : ProxmoxNodeHealthClassifier.NicLevel(delta, t.NicWarnDelta, t.NicCritDelta);
        var threshold = level == HealthLevel.Crit ? t.NicCritDelta : t.NicWarnDelta;
        return new NodeAlertObservation(
            ProxmoxAlertCategory.Network, true, level, "NIC errors", delta, threshold);
    }

    // ── state <-> entity mapping ──────────────────────────────────────────────

    private static NodeAlertEvalState ToEvalState(ProxmoxNodeAlertStateEntity e) =>
        new(e.ActiveLevel, e.PendingLevel, e.PendingCount, e.FirstSeenUtc, e.Metric, e.Value, e.Threshold);

    private static void ApplyEvalState(ProxmoxNodeAlertStateEntity e, NodeAlertEvalState s, DateTime now)
    {
        e.ActiveLevel = s.ActiveLevel;
        e.PendingLevel = s.PendingLevel;
        e.PendingCount = s.PendingCount;
        e.FirstSeenUtc = s.FirstSeenUtc;
        e.Metric = s.Metric;
        e.Value = s.Value;
        e.Threshold = s.Threshold;
        e.UpdatedUtc = now;
    }
}
