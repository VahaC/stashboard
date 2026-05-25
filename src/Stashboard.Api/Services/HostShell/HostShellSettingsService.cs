using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Stashboard.Api.Contracts;
using Stashboard.Api.Data;
using Stashboard.Core.Options;

namespace Stashboard.Api.Services.HostShell;

/// <inheritdoc cref="IHostShellSettingsService"/>
public sealed class HostShellSettingsService(
    ApplicationDbContext db,
    IOptions<StashboardOptions> seedDefaults,
    TimeProvider time) : IHostShellSettingsService
{
    public async Task<bool> IsEnabledAsync(CancellationToken cancellationToken = default)
    {
        var entity = await GetOrCreateAsync(cancellationToken);
        return entity.Enabled;
    }

    public async Task<HostShellSettingsResponse> GetAsync(CancellationToken cancellationToken = default)
    {
        var entity = await GetOrCreateAsync(cancellationToken);
        return new HostShellSettingsResponse(entity.Enabled);
    }

    public async Task UpdateAsync(UpdateHostShellSettingsRequest request, CancellationToken cancellationToken = default)
    {
        var entity = await GetOrCreateAsync(cancellationToken);
        entity.Enabled = request.Enabled;
        entity.UpdatedUtc = time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<HostShellSettingsEntity> GetOrCreateAsync(CancellationToken cancellationToken)
    {
        var entity = await db.HostShellSettings
            .FirstOrDefaultAsync(e => e.Id == HostShellSettingsEntity.SingletonId, cancellationToken);
        if (entity is not null) return entity;

        var now = time.GetUtcNow().UtcDateTime;
        entity = new HostShellSettingsEntity
        {
            Id = HostShellSettingsEntity.SingletonId,
            // Seed from the optional config flag so an operator who set it on
            // first run keeps that value until they change it in the UI.
            Enabled = seedDefaults.Value.AllowHostShell,
            CreatedUtc = now,
            UpdatedUtc = now,
        };
        db.HostShellSettings.Add(entity);
        await db.SaveChangesAsync(cancellationToken);
        return entity;
    }
}
