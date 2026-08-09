using Microsoft.Extensions.Options;
using Stashboard.Api.Contracts;
using Stashboard.Api.Services.Proxmox;
using Stashboard.Core.Options;
using Stashboard.Tests.Infrastructure;

namespace Stashboard.Tests.Services.Proxmox;

/// <summary>
/// V8.1 — tests for <see cref="ProxmoxRestoreSettingsService"/>: the DB-backed
/// restore-LXC master switch seeds from the optional config flag on first access and
/// persists edits made from the Settings page. Mirrors the create variant.
/// </summary>
public class ProxmoxRestoreSettingsServiceTests : DatabaseTestBase
{
    private ProxmoxRestoreSettingsService Build(bool seed) =>
        new(_dbContext, Options.Create(new StashboardOptions { AllowProxmoxRestore = seed }), TimeProvider.System);

    [Fact]
    public async Task FirstAccess_SeedsFromConfigFlag_False()
    {
        var service = Build(seed: false);

        Assert.False(await service.IsEnabledAsync());
        Assert.False((await service.GetAsync()).Enabled);
    }

    [Fact]
    public async Task FirstAccess_SeedsFromConfigFlag_True()
    {
        var service = Build(seed: true);

        Assert.True(await service.IsEnabledAsync());
    }

    [Fact]
    public async Task Update_PersistsToggle()
    {
        var service = Build(seed: false);

        await service.UpdateAsync(new UpdateProxmoxRestoreSettingsRequest(Enabled: true));
        Assert.True(await service.IsEnabledAsync());

        await service.UpdateAsync(new UpdateProxmoxRestoreSettingsRequest(Enabled: false));
        Assert.False(await service.IsEnabledAsync());
    }

    [Fact]
    public async Task Update_PersistsAcrossNewServiceInstances()
    {
        await Build(seed: false).UpdateAsync(new UpdateProxmoxRestoreSettingsRequest(Enabled: true));

        // A fresh instance reads the persisted row, not the (false) seed.
        Assert.True(await Build(seed: false).IsEnabledAsync());
    }
}



