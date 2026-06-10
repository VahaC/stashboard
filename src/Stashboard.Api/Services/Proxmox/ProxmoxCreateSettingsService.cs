using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Stashboard.Api.Contracts;
using Stashboard.Api.Data;
using Stashboard.Core.Options;

namespace Stashboard.Api.Services.Proxmox;

/// <inheritdoc cref="IProxmoxCreateSettingsService"/>
public sealed class ProxmoxCreateSettingsService(
    ApplicationDbContext db,
    IOptions<StashboardOptions> seedDefaults,
    TimeProvider time) : IProxmoxCreateSettingsService
{
    public async Task<bool> IsEnabledAsync(CancellationToken cancellationToken = default)
    {
        var entity = await GetOrCreateAsync(cancellationToken);
        return entity.Enabled;
    }

    public async Task<ProxmoxCreateSettingsResponse> GetAsync(CancellationToken cancellationToken = default)
    {
        var entity = await GetOrCreateAsync(cancellationToken);
        return new ProxmoxCreateSettingsResponse(entity.Enabled);
    }

    public async Task UpdateAsync(UpdateProxmoxCreateSettingsRequest request, CancellationToken cancellationToken = default)
    {
        var entity = await GetOrCreateAsync(cancellationToken);
        entity.Enabled = request.Enabled;
        entity.UpdatedUtc = time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<ProxmoxCreateSettingsEntity> GetOrCreateAsync(CancellationToken cancellationToken)
    {
        var entity = await db.ProxmoxCreateSettings
            .FirstOrDefaultAsync(e => e.Id == ProxmoxCreateSettingsEntity.SingletonId, cancellationToken);
        if (entity is not null) return entity;

        var now = time.GetUtcNow().UtcDateTime;
        entity = new ProxmoxCreateSettingsEntity
        {
            Id = ProxmoxCreateSettingsEntity.SingletonId,
            // Seed from the optional config flag so an operator who set it on
            // first run keeps that value until they change it in the UI.
            Enabled = seedDefaults.Value.AllowProxmoxCreate,
            CreatedUtc = now,
            UpdatedUtc = now,
        };
        db.ProxmoxCreateSettings.Add(entity);
        await db.SaveChangesAsync(cancellationToken);
        return entity;
    }
}
