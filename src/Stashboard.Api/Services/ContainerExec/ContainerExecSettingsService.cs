using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Stashboard.Api.Contracts;
using Stashboard.Api.Data;
using Stashboard.Core.Options;

namespace Stashboard.Api.Services.ContainerExec;

/// <inheritdoc cref="IContainerExecSettingsService"/>
public sealed class ContainerExecSettingsService(
    ApplicationDbContext db,
    IOptions<StashboardOptions> seedDefaults,
    TimeProvider time) : IContainerExecSettingsService
{
    public async Task<bool> IsEnabledAsync(CancellationToken cancellationToken = default)
    {
        var entity = await GetOrCreateAsync(cancellationToken);
        return entity.Enabled;
    }

    public async Task<ContainerExecSettingsResponse> GetAsync(CancellationToken cancellationToken = default)
    {
        var entity = await GetOrCreateAsync(cancellationToken);
        return new ContainerExecSettingsResponse(entity.Enabled);
    }

    public async Task UpdateAsync(UpdateContainerExecSettingsRequest request, CancellationToken cancellationToken = default)
    {
        var entity = await GetOrCreateAsync(cancellationToken);
        entity.Enabled = request.Enabled;
        entity.UpdatedUtc = time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<ContainerExecSettingsEntity> GetOrCreateAsync(CancellationToken cancellationToken)
    {
        var entity = await db.ContainerExecSettings
            .FirstOrDefaultAsync(e => e.Id == ContainerExecSettingsEntity.SingletonId, cancellationToken);
        if (entity is not null) return entity;

        var now = time.GetUtcNow().UtcDateTime;
        entity = new ContainerExecSettingsEntity
        {
            Id = ContainerExecSettingsEntity.SingletonId,
            // Seed from the optional config flag so an operator who set it on
            // first run keeps that value until they change it in the UI.
            Enabled = seedDefaults.Value.AllowContainerExec,
            CreatedUtc = now,
            UpdatedUtc = now,
        };
        db.ContainerExecSettings.Add(entity);
        await db.SaveChangesAsync(cancellationToken);
        return entity;
    }
}
