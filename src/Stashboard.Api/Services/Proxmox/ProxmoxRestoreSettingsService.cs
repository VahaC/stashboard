using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Stashboard.Api.Contracts;
using Stashboard.Api.Data;
using Stashboard.Core.Options;

namespace Stashboard.Api.Services.Proxmox;

/// <inheritdoc cref="IProxmoxRestoreSettingsService"/>
public sealed class ProxmoxRestoreSettingsService(
    ApplicationDbContext db,
    IOptions<StashboardOptions> seedDefaults,
    TimeProvider time) : IProxmoxRestoreSettingsService
{
    public async Task<bool> IsEnabledAsync(CancellationToken cancellationToken = default)
    {
        var entity = await GetOrCreateAsync(cancellationToken);
        return entity.Enabled;
    }

    public async Task<ProxmoxRestoreSettingsResponse> GetAsync(CancellationToken cancellationToken = default)
    {
        var entity = await GetOrCreateAsync(cancellationToken);
        return new ProxmoxRestoreSettingsResponse(entity.Enabled);
    }

    public async Task UpdateAsync(UpdateProxmoxRestoreSettingsRequest request, CancellationToken cancellationToken = default)
    {
        var entity = await GetOrCreateAsync(cancellationToken);
        entity.Enabled = request.Enabled;
        entity.UpdatedUtc = time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<ProxmoxRestoreSettingsEntity> GetOrCreateAsync(CancellationToken cancellationToken)
    {
        var entity = await db.ProxmoxRestoreSettings
            .FirstOrDefaultAsync(e => e.Id == ProxmoxRestoreSettingsEntity.SingletonId, cancellationToken);
        if (entity is not null) return entity;

        var now = time.GetUtcNow().UtcDateTime;
        entity = new ProxmoxRestoreSettingsEntity
        {
            Id = ProxmoxRestoreSettingsEntity.SingletonId,
            // Seed from the optional config flag so an operator who set it on
            // first run keeps that value until they change it in the UI.
            Enabled = seedDefaults.Value.AllowProxmoxRestore,
            CreatedUtc = now,
            UpdatedUtc = now,
        };
        db.ProxmoxRestoreSettings.Add(entity);
        await db.SaveChangesAsync(cancellationToken);
        return entity;
    }
}
