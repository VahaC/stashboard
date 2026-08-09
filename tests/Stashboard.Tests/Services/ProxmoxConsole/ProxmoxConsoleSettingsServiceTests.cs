using Microsoft.Extensions.Options;
using Stashboard.Api.Contracts;
using Stashboard.Api.Services.ProxmoxConsole;
using Stashboard.Core.Options;
using Stashboard.Tests.Infrastructure;

namespace Stashboard.Tests.Services.ProxmoxConsole;

/// <summary>
/// V6.6 — tests for <see cref="ProxmoxConsoleSettingsService"/>: the DB-backed
/// master switch seeds from the optional config flag on first access and
/// persists edits made from the Settings page. Mirrors the container-exec variant.
/// </summary>
public class ProxmoxConsoleSettingsServiceTests : DatabaseTestBase
{
    private ProxmoxConsoleSettingsService Build(bool seed) =>
        new(_dbContext, Options.Create(new StashboardOptions { AllowProxmoxConsole = seed }), TimeProvider.System);

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

        await service.UpdateAsync(new UpdateProxmoxConsoleSettingsRequest(Enabled: true));
        Assert.True(await service.IsEnabledAsync());

        await service.UpdateAsync(new UpdateProxmoxConsoleSettingsRequest(Enabled: false));
        Assert.False(await service.IsEnabledAsync());
    }

    [Fact]
    public async Task Update_PersistsAcrossNewServiceInstances()
    {
        await Build(seed: false).UpdateAsync(new UpdateProxmoxConsoleSettingsRequest(Enabled: true));

        // A fresh instance reads the persisted row, not the (false) seed.
        Assert.True(await Build(seed: false).IsEnabledAsync());
    }
}



