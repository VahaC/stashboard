using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Stashboard.Api.Contracts;
using Stashboard.Api.Data;
using Stashboard.Core.Options;

namespace Stashboard.Api.Services.Proxmox;

/// <inheritdoc cref="IProxmoxCloneSettingsService"/>
public sealed class ProxmoxCloneSettingsService(
    ApplicationDbContext db,
    IOptions<StashboardOptions> seedDefaults,
    TimeProvider time) : IProxmoxCloneSettingsService
{
    public async Task<bool> IsEnabledAsync(CancellationToken cancellationToken = default)
    {
        var entity = await GetOrCreateAsync(cancellationToken);
        return entity.Enabled;
    }

    public async Task<ProxmoxCloneSettingsResponse> GetAsync(CancellationToken cancellationToken = default)
    {
        var entity = await GetOrCreateAsync(cancellationToken);
        return new ProxmoxCloneSettingsResponse(entity.Enabled);
    }

    public async Task UpdateAsync(UpdateProxmoxCloneSettingsRequest request, CancellationToken cancellationToken = default)
    {
        var entity = await GetOrCreateAsync(cancellationToken);
        entity.Enabled = request.Enabled;
        entity.UpdatedUtc = time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<ProxmoxCloneSettingsEntity> GetOrCreateAsync(CancellationToken cancellationToken)
    {
        var entity = await db.ProxmoxCloneSettings
            .FirstOrDefaultAsync(e => e.Id == ProxmoxCloneSettingsEntity.SingletonId, cancellationToken);
        if (entity is not null) return entity;

        var now = time.GetUtcNow().UtcDateTime;
        entity = new ProxmoxCloneSettingsEntity
        {
            Id = ProxmoxCloneSettingsEntity.SingletonId,
            // Seed from the optional config flag so an operator who set it on
            // first run keeps that value until they change it in the UI.
            Enabled = seedDefaults.Value.AllowProxmoxClone,
            CreatedUtc = now,
            UpdatedUtc = now,
        };
        db.ProxmoxCloneSettings.Add(entity);
        await db.SaveChangesAsync(cancellationToken);
        return entity;
    }
}
