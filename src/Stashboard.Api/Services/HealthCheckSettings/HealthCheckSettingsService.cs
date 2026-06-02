using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Stashboard.Api.Contracts;
using Stashboard.Api.Data;
using Stashboard.Core.Options;

namespace Stashboard.Api.Services.HealthCheckSettings;

/// <inheritdoc cref="IHealthCheckSettingsService"/>
public sealed class HealthCheckSettingsService(
    ApplicationDbContext db,
    IOptions<HealthCheckOptions> seedDefaults,
    TimeProvider time) : IHealthCheckSettingsService
{
    /// <summary>A service must fail at least one scan to ever be marked Down,
    /// so the threshold floor is 1 (notify on the first failure).</summary>
    public const int MinimumFailureThreshold = 1;

    /// <summary>The background loop never scans more often than every 10 s, so
    /// the interval is floored to match (mirrors the <c>Math.Max(10, …)</c> in
    /// <c>HealthCheckBackgroundService</c>).</summary>
    public const int MinimumIntervalSeconds = 10;

    public async Task<HealthCheckSettingsResponse> GetAsync(CancellationToken cancellationToken = default)
    {
        var entity = await GetOrCreateAsync(cancellationToken);
        return new HealthCheckSettingsResponse(entity.IntervalSeconds, entity.FailureThreshold, entity.RetryCount, entity.RetryDelayMs);
    }

    public async Task UpdateAsync(UpdateHealthCheckSettingsRequest request, CancellationToken cancellationToken = default)
    {
        var entity = await GetOrCreateAsync(cancellationToken);
        entity.IntervalSeconds = Math.Max(MinimumIntervalSeconds, request.IntervalSeconds);
        entity.FailureThreshold = Math.Max(MinimumFailureThreshold, request.FailureThreshold);
        entity.RetryCount = Math.Max(0, request.RetryCount);
        entity.RetryDelayMs = Math.Max(0, request.RetryDelayMs);
        entity.UpdatedUtc = time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<HealthCheckSettingsEntity> GetOrCreateAsync(CancellationToken cancellationToken)
    {
        var entity = await db.HealthCheckSettings
            .FirstOrDefaultAsync(e => e.Id == HealthCheckSettingsEntity.SingletonId, cancellationToken);
        if (entity is not null) return entity;

        var now = time.GetUtcNow().UtcDateTime;
        entity = new HealthCheckSettingsEntity
        {
            Id = HealthCheckSettingsEntity.SingletonId,
            IntervalSeconds = Math.Max(MinimumIntervalSeconds, seedDefaults.Value.IntervalSeconds),
            FailureThreshold = Math.Max(MinimumFailureThreshold, seedDefaults.Value.FailureThreshold),
            RetryCount = Math.Max(0, seedDefaults.Value.RetryCount),
            RetryDelayMs = Math.Max(0, seedDefaults.Value.RetryDelayMs),
            CreatedUtc = now,
            UpdatedUtc = now,
        };
        db.HealthCheckSettings.Add(entity);
        await db.SaveChangesAsync(cancellationToken);
        return entity;
    }
}
