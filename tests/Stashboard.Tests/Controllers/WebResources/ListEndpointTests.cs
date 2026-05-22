using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Stashboard.Api.Contracts;

namespace Stashboard.Tests.Controllers.WebResources;

public class ListEndpointTests : WebResourcesControllerTestBase
{
    [Fact]
    public async Task List_ReturnsOk_AndEmptyList_WhenDatabaseHasNoServices()
    {
        var ctrl = BuildController();

        var result = await ctrl.List(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var list = Assert.IsType<List<WebResourceResponse>>(ok.Value);
        Assert.Empty(list);
        Assert.Equal(0, await _dbContext.WebResources.CountAsync());
    }

    [Fact]
    public async Task List_ReturnsOnlyOwnServices_WhenOtherUserHasServices()
    {
        await _dataFactory.ServiceAsync(userId: _otherUserId, name: "Alien Service");
        await _dataFactory.ServiceAsync(name: "My Service");

        var ctrl = BuildController();
        var result = await ctrl.List(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var list = Assert.IsType<List<WebResourceResponse>>(ok.Value);

        // Response contains only owned service
        Assert.Single(list);
        Assert.Equal("My Service", list[0].Name);

        // Both records still exist in DB — we only filtered, not deleted
        Assert.Equal(2, await _dbContext.WebResources.CountAsync());
        Assert.True(await _dbContext.WebResources.AnyAsync(s => s.UserId == _otherUserId));
    }

    [Fact]
    public async Task List_ReturnsServices_OrderedByNameAscending()
    {
        await _dataFactory.ServiceAsync(name: "Zebra");
        await _dataFactory.ServiceAsync(name: "Alpha");
        await _dataFactory.ServiceAsync(name: "Mu");

        var ctrl = BuildController();
        var result = await ctrl.List(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var list = Assert.IsType<List<WebResourceResponse>>(ok.Value);

        Assert.Equal(3, list.Count);
        Assert.Equal(["Alpha", "Mu", "Zebra"], list.Select(s => s.Name).ToList());
        Assert.Equal(3, await _dbContext.WebResources.CountAsync(s => s.UserId == _userId));
    }

    [Fact]
    public async Task List_IncludesTags_StoredInDatabase()
    {
        var svc = await _dataFactory.ServiceAsync();
        var tag = await _dataFactory.TagAsync("backend");
        await _dataFactory.AttachTagAsync(svc, tag);

        var ctrl = BuildController();
        var result = await ctrl.List(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var list = Assert.IsType<List<WebResourceResponse>>(ok.Value);

        Assert.Contains("backend", list[0].Tags);
        Assert.True(await _dbContext.WebResourceTags.AnyAsync(st => st.WebResourceId == svc.Id && st.TagId == tag.Id));
    }

    [Fact]
    public async Task List_TagsAreReturnedAlphabetically()
    {
        var svc = await _dataFactory.ServiceAsync();
        var t1 = await _dataFactory.TagAsync("zebra");
        var t2 = await _dataFactory.TagAsync("apple");
        await _dataFactory.AttachTagAsync(svc, t1);
        await _dataFactory.AttachTagAsync(svc, t2);

        var ctrl = BuildController();
        var result = await ctrl.List(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var list = Assert.IsType<List<WebResourceResponse>>(ok.Value);

        Assert.Equal(["apple", "zebra"], list[0].Tags);
        Assert.Equal(2, await _dbContext.WebResourceTags.CountAsync(st => st.WebResourceId == svc.Id));
    }

    [Fact]
    public async Task List_IncludesDecryptedCredentials_WhileDatabaseStoresEncryptedValues()
    {
        var svc = await _dataFactory.ServiceAsync();
        await _dataFactory.AttachCredentialAsync(svc, "API_KEY", "secret123");

        var ctrl = BuildController();
        var result = await ctrl.List(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var list = Assert.IsType<List<WebResourceResponse>>(ok.Value);
        var cred = Assert.Single(list[0].Credentials);

        // Response is decrypted
        Assert.Equal("API_KEY", cred.Key);
        Assert.Equal("secret123", cred.Value);

        // DB still holds the encrypted form
        var dbCred = await _dbContext.Credentials.SingleAsync(c => c.WebResourceId == svc.Id);
        Assert.Equal("enc:secret123", dbCred.EncryptedValue);
    }

    [Fact]
    public async Task List_IncludesCategoryInfo_StoredInDatabase()
    {
        var cat = await _dataFactory.CategoryAsync(name: "Infra");
        await _dataFactory.ServiceAsync(categoryId: cat.Id);

        var ctrl = BuildController();
        var result = await ctrl.List(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var list = Assert.IsType<List<WebResourceResponse>>(ok.Value);

        Assert.Equal(cat.Id, list[0].CategoryId);
        Assert.Equal("Infra", list[0].CategoryName);
        Assert.True(await _dbContext.Categories.AnyAsync(c => c.Id == cat.Id));
    }
}
