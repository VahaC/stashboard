using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Stashboard.Api.Contracts;
using Stashboard.Api.Data;
using Stashboard.Core.Options;

namespace Stashboard.Api.Services.ProxmoxConsole;

/// <inheritdoc cref="IProxmoxConsoleSettingsService"/>
public sealed class ProxmoxConsoleSettingsService(
    ApplicationDbContext db,
    IOptions<StashboardOptions> seedDefaults,
    TimeProvider time) : IProxmoxConsoleSettingsService
{
    public async Task<bool> IsEnabledAsync(CancellationToken cancellationToken = default)
    {
        var entity = await GetOrCreateAsync(cancellationToken);
        return entity.Enabled;
    }

    public async Task<ProxmoxConsoleSettingsResponse> GetAsync(CancellationToken cancellationToken = default)
    {
        var entity = await GetOrCreateAsync(cancellationToken);
        return new ProxmoxConsoleSettingsResponse(entity.Enabled);
    }

    public async Task UpdateAsync(UpdateProxmoxConsoleSettingsRequest request, CancellationToken cancellationToken = default)
    {
        var entity = await GetOrCreateAsync(cancellationToken);
        entity.Enabled = request.Enabled;
        entity.UpdatedUtc = time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<ProxmoxConsoleSettingsEntity> GetOrCreateAsync(CancellationToken cancellationToken)
    {
        var entity = await db.ProxmoxConsoleSettings
            .FirstOrDefaultAsync(e => e.Id == ProxmoxConsoleSettingsEntity.SingletonId, cancellationToken);
        if (entity is not null) return entity;

        var now = time.GetUtcNow().UtcDateTime;
        entity = new ProxmoxConsoleSettingsEntity
        {
            Id = ProxmoxConsoleSettingsEntity.SingletonId,
            // Seed from the optional config flag so an operator who set it on
            // first run keeps that value until they change it in the UI.
            Enabled = seedDefaults.Value.AllowProxmoxConsole,
            CreatedUtc = now,
            UpdatedUtc = now,
        };
        db.ProxmoxConsoleSettings.Add(entity);
        await db.SaveChangesAsync(cancellationToken);
        return entity;
    }
}
