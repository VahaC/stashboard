using Microsoft.Extensions.Options;
using Stashboard.Api.Contracts;
using Stashboard.Api.Services.HealthCheckSettings;
using Stashboard.Core.Options;
using Stashboard.Tests.Infrastructure;

namespace Stashboard.Tests.Services.HealthCheckSettings;

/// <summary>
/// V5.6 — tests for <see cref="HealthCheckSettingsService"/>: the DB-backed
/// offline-alert tuning seeds from the bound <see cref="HealthCheckOptions"/>
/// config on first access, persists edits made from the Settings page, and
/// floors the values defensively.
/// </summary>
public class HealthCheckSettingsServiceTests : DatabaseTestBase
{
    private HealthCheckSettingsService Build(HealthCheckOptions? seed = null) =>
        new(_dbContext, Options.Create(seed ?? new HealthCheckOptions()), TimeProvider.System);

    [Fact]
    public async Task FirstAccess_SeedsFromConfigDefaults()
    {
        var service = Build(new HealthCheckOptions { IntervalSeconds = 30, FailureThreshold = 5, RetryCount = 4, RetryDelayMs = 250 });

        var settings = await service.GetAsync();

        Assert.Equal(30, settings.IntervalSeconds);
        Assert.Equal(5, settings.FailureThreshold);
        Assert.Equal(4, settings.RetryCount);
        Assert.Equal(250, settings.RetryDelayMs);
    }

    [Fact]
    public async Task Update_PersistsValues()
    {
        var service = Build();

        await service.UpdateAsync(new UpdateHealthCheckSettingsRequest(IntervalSeconds: 120, FailureThreshold: 7, RetryCount: 1, RetryDelayMs: 2000));

        var settings = await service.GetAsync();
        Assert.Equal(120, settings.IntervalSeconds);
        Assert.Equal(7, settings.FailureThreshold);
        Assert.Equal(1, settings.RetryCount);
        Assert.Equal(2000, settings.RetryDelayMs);
    }

    [Fact]
    public async Task Update_PersistsAcrossNewServiceInstances()
    {
        await Build().UpdateAsync(new UpdateHealthCheckSettingsRequest(IntervalSeconds: 300, FailureThreshold: 9, RetryCount: 0, RetryDelayMs: 0));

        // A fresh instance reads the persisted row, not the config seed.
        var settings = await Build().GetAsync();
        Assert.Equal(300, settings.IntervalSeconds);
        Assert.Equal(9, settings.FailureThreshold);
        Assert.Equal(0, settings.RetryCount);
        Assert.Equal(0, settings.RetryDelayMs);
    }

    [Fact]
    public async Task Update_FloorsValuesDefensively()
    {
        var service = Build();

        await service.UpdateAsync(new UpdateHealthCheckSettingsRequest(IntervalSeconds: 1, FailureThreshold: 0, RetryCount: -5, RetryDelayMs: -1));

        var settings = await service.GetAsync();
        Assert.Equal(10, settings.IntervalSeconds); // floor 10 — loop never scans more often
        Assert.Equal(1, settings.FailureThreshold); // floor 1 — must fail at least one scan
        Assert.Equal(0, settings.RetryCount);
        Assert.Equal(0, settings.RetryDelayMs);
    }

    [Fact]
    public async Task Update_PersistsAndFloors_HistorySettings()
    {
        var service = Build();

        await service.UpdateAsync(new UpdateHealthCheckSettingsRequest(
            IntervalSeconds: 60, FailureThreshold: 3, RetryCount: 2, RetryDelayMs: 1000,
            HistoryRetentionDays: 45, HistorySampleIntervalMinutes: 5));
        var persisted = await service.GetAsync();
        Assert.Equal(45, persisted.HistoryRetentionDays);
        Assert.Equal(5, persisted.HistorySampleIntervalMinutes);

        // Floors: at least 1 day of retention and a 1-minute sample cadence.
        await service.UpdateAsync(new UpdateHealthCheckSettingsRequest(
            IntervalSeconds: 60, FailureThreshold: 3, RetryCount: 2, RetryDelayMs: 1000,
            HistoryRetentionDays: 0, HistorySampleIntervalMinutes: 0));
        var floored = await service.GetAsync();
        Assert.Equal(1, floored.HistoryRetentionDays);
        Assert.Equal(1, floored.HistorySampleIntervalMinutes);
    }
}


