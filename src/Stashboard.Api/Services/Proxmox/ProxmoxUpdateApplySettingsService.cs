using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Stashboard.Api.Contracts;
using Stashboard.Api.Data;
using Stashboard.Core.Options;

namespace Stashboard.Api.Services.Proxmox;

/// <inheritdoc cref="IProxmoxUpdateApplySettingsService"/>
public sealed class ProxmoxUpdateApplySettingsService(
    ApplicationDbContext db,
    IOptions<StashboardOptions> seedDefaults,
    TimeProvider time) : IProxmoxUpdateApplySettingsService
{
    public async Task<bool> IsEnabledAsync(CancellationToken cancellationToken = default)
    {
        var entity = await GetOrCreateAsync(cancellationToken);
        return entity.Enabled;
    }

    public async Task<ProxmoxUpdateApplySettingsResponse> GetAsync(CancellationToken cancellationToken = default)
    {
        var entity = await GetOrCreateAsync(cancellationToken);
        return new ProxmoxUpdateApplySettingsResponse(entity.Enabled);
    }

    public async Task UpdateAsync(UpdateProxmoxUpdateApplySettingsRequest request, CancellationToken cancellationToken = default)
    {
        var entity = await GetOrCreateAsync(cancellationToken);
        entity.Enabled = request.Enabled;
        entity.UpdatedUtc = time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<ProxmoxUpdateApplySettingsEntity> GetOrCreateAsync(CancellationToken cancellationToken)
    {
        var entity = await db.ProxmoxUpdateApplySettings
            .FirstOrDefaultAsync(e => e.Id == ProxmoxUpdateApplySettingsEntity.SingletonId, cancellationToken);
        if (entity is not null) return entity;

        var now = time.GetUtcNow().UtcDateTime;
        entity = new ProxmoxUpdateApplySettingsEntity
        {
            Id = ProxmoxUpdateApplySettingsEntity.SingletonId,
            // Seed from the optional config flag so an operator who set it on
            // first run keeps that value until they change it in the UI.
            Enabled = seedDefaults.Value.AllowProxmoxUpdates,
            CreatedUtc = now,
            UpdatedUtc = now,
        };
        db.ProxmoxUpdateApplySettings.Add(entity);
        await db.SaveChangesAsync(cancellationToken);
        return entity;
    }
}
