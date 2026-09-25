using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Stashboard.Api.Auth;
using Stashboard.Api.Contracts;
using Stashboard.Api.Controllers;
using Stashboard.Tests.Infrastructure;

namespace Stashboard.Tests.Controllers;

/// <summary>
/// V10.6 — the dashboard Custom-order endpoints. Each rewrites dense 0..N indices for
/// its slice, ignores ids the caller doesn't own, and keeps the two card orders and the
/// group order independent.
/// </summary>
public class DashboardControllerTests : DatabaseTestBase
{
    private DataFactory _factory = default!;
    private Guid _userId;
    private Guid _otherUserId;
    private DashboardController _ctrl = default!;

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        _factory = new DataFactory(_dbContext, new NoopEncryption(), new Pbkdf2PasswordHasher(), Guid.Empty);
        _userId = (await _factory.UserAsync("owner@x")).Id;
        _otherUserId = (await _factory.UserAsync("other@x")).Id;
        _ctrl = new DashboardController(_dbContext) { ControllerContext = BuildContext(_userId) };
    }

    [Fact]
    public async Task SetServiceOrder_RewritesDenseIndices_ForOwnedServicesOnly()
    {
        var a = await _factory.ServiceAsync(_userId, "A");
        var b = await _factory.ServiceAsync(_userId, "B");
        var c = await _factory.ServiceAsync(_userId, "C");
        var foreign = await _factory.ServiceAsync(_otherUserId, "X");

        var result = await _ctrl.SetServiceOrder(new ServiceOrderRequest([c.Id, a.Id, b.Id]), default);

        Assert.IsType<NoContentResult>(result);
        using var verify = CreateDbContext();
        Assert.Equal(0, (await verify.WebResources.FindAsync(c.Id))!.SortOrder);
        Assert.Equal(1, (await verify.WebResources.FindAsync(a.Id))!.SortOrder);
        Assert.Equal(2, (await verify.WebResources.FindAsync(b.Id))!.SortOrder);
        Assert.Equal(0, (await verify.WebResources.FindAsync(foreign.Id))!.SortOrder); // untouched
    }

    [Fact]
    public async Task SetServiceOrderInCategory_OnlyReordersThatCategory()
    {
        var cat = await _factory.CategoryAsync(_userId, "Media");
        var s1 = await _factory.ServiceAsync(_userId, "s1", categoryId: cat.Id);
        var s2 = await _factory.ServiceAsync(_userId, "s2", categoryId: cat.Id);
        var uncategorized = await _factory.ServiceAsync(_userId, "s3");

        var result = await _ctrl.SetServiceOrderInCategory(
            new CategoryServiceOrderRequest(cat.Id, [s2.Id, s1.Id]), default);

        Assert.IsType<NoContentResult>(result);
        using var verify = CreateDbContext();
        Assert.Equal(0, (await verify.WebResources.FindAsync(s2.Id))!.SortOrderInCategory);
        Assert.Equal(1, (await verify.WebResources.FindAsync(s1.Id))!.SortOrderInCategory);
        Assert.Equal(0, (await verify.WebResources.FindAsync(uncategorized.Id))!.SortOrderInCategory);
    }

    [Fact]
    public async Task SetCategoryOrder_RewritesGroupOrder()
    {
        var c1 = await _factory.CategoryAsync(_userId, "Alpha");
        var c2 = await _factory.CategoryAsync(_userId, "Beta");
        var c3 = await _factory.CategoryAsync(_userId, "Gamma");

        var result = await _ctrl.SetCategoryOrder(new CategoryOrderRequest([c3.Id, c1.Id, c2.Id]), default);

        Assert.IsType<NoContentResult>(result);
        using var verify = CreateDbContext();
        Assert.Equal(0, (await verify.Categories.FindAsync(c3.Id))!.SortOrder);
        Assert.Equal(1, (await verify.Categories.FindAsync(c1.Id))!.SortOrder);
        Assert.Equal(2, (await verify.Categories.FindAsync(c2.Id))!.SortOrder);
    }

    private static ControllerContext BuildContext(Guid userId)
    {
        var identity = new ClaimsIdentity(new[] { new Claim(StashboardClaims.UserId, userId.ToString()) }, "Test");
        return new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) } };
    }

    private sealed class NoopEncryption : Stashboard.Core.Abstractions.IEncryptionService
    {
        public string Encrypt(string plain) => plain;
        public string Decrypt(string cipher) => cipher;
    }
}
