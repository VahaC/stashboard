using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Stashboard.Api.Contracts;
using Stashboard.Api.Data;
using Stashboard.Core.Options;

namespace Stashboard.Api.Services.ImagePrune;

/// <inheritdoc cref="IImagePruneSettingsService"/>
public sealed class ImagePruneSettingsService(
    ApplicationDbContext db,
    IOptions<ImagePruneOptions> seedDefaults,
    TimeProvider time) : IImagePruneSettingsService
{
    /// <summary>Hard lower bound on the schedule interval. One hour is the
    /// sweet spot — anything shorter just thrashes the daemon for no extra
    /// reclaim, since dangling images appear at recreate time, not on a clock.</summary>
    public const int MinimumIntervalHours = 1;

    public async Task<ImagePruneSettingsResponse> GetAsync(CancellationToken cancellationToken = default)
    {
        var entity = await GetOrCreateAsync(cancellationToken);
        return new ImagePruneSettingsResponse(entity.Enabled, entity.IntervalHours);
    }

    public async Task UpdateAsync(UpdateImagePruneSettingsRequest request, CancellationToken cancellationToken = default)
    {
        var entity = await GetOrCreateAsync(cancellationToken);
        entity.Enabled = request.Enabled;
        entity.IntervalHours = Math.Max(MinimumIntervalHours, request.IntervalHours);
        entity.UpdatedUtc = time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<ImagePruneSettingsEntity> GetOrCreateAsync(CancellationToken cancellationToken)
    {
        var entity = await db.ImagePruneSettings
            .FirstOrDefaultAsync(e => e.Id == ImagePruneSettingsEntity.SingletonId, cancellationToken);
        if (entity is not null) return entity;

        var now = time.GetUtcNow().UtcDateTime;
        entity = new ImagePruneSettingsEntity
        {
            Id = ImagePruneSettingsEntity.SingletonId,
            Enabled = seedDefaults.Value.Enabled,
            IntervalHours = Math.Max(MinimumIntervalHours, seedDefaults.Value.IntervalHours),
            CreatedUtc = now,
            UpdatedUtc = now,
        };
        db.ImagePruneSettings.Add(entity);
        await db.SaveChangesAsync(cancellationToken);
        return entity;
    }
}
