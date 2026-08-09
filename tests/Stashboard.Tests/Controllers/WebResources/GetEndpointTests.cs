using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Stashboard.Api.Contracts;
using Stashboard.Core.Enums;

namespace Stashboard.Tests.Controllers.WebResources;

public class GetEndpointTests : WebResourcesControllerTestBase
{
    [Fact]
    public async Task Get_ReturnsOk_WithDataMatchingDatabase()
    {
        var svc = await _dataFactory.ServiceAsync(name: "My API", mainUrl: "https://api.example.com");
        var ctrl = BuildController();

        var result = await ctrl.Get(svc.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<WebResourceResponse>(ok.Value);

        // Response matches what is in DB
        var dbRow = await _dbContext.WebResources.AsNoTracking().SingleAsync(s => s.Id == svc.Id);
        Assert.Equal(dbRow.Id, response.Id);
        Assert.Equal(dbRow.Name, response.Name);
        Assert.Equal(dbRow.MainUrl, response.MainUrl);
        Assert.Equal(dbRow.UserId, _userId);
    }

    [Fact]
    public async Task Get_IncludesTags_ThatAreStoredInDatabase()
    {
        var svc = await _dataFactory.ServiceAsync();
        var tag = await _dataFactory.TagAsync("monitoring");
        await _dataFactory.AttachTagAsync(svc, tag);
        var ctrl = BuildController();

        var result = await ctrl.Get(svc.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<WebResourceResponse>(ok.Value);
        Assert.Contains("monitoring", response.Tags);

        // ServiceTag row exists in DB
        Assert.True(await _dbContext.WebResourceTags.AnyAsync(st => st.WebResourceId == svc.Id && st.TagId == tag.Id));
    }

    [Fact]
    public async Task Get_IncludesDecryptedCredential_WhileDatabaseStoresEncryptedValue()
    {
        var svc = await _dataFactory.ServiceAsync();
        await _dataFactory.AttachCredentialAsync(svc, "TOKEN", "my-token", isSecret: true);
        var ctrl = BuildController();

        var result = await ctrl.Get(svc.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<WebResourceResponse>(ok.Value);
        var cred = Assert.Single(response.Credentials);

        Assert.Equal("TOKEN", cred.Key);
        Assert.Equal("my-token", cred.Value);  // decrypted in response
        Assert.True(cred.IsSecret);

        // DB holds the encrypted form
        var dbCred = await _dbContext.Credentials.SingleAsync(c => c.WebResourceId == svc.Id);
        Assert.Equal("enc:my-token", dbCred.EncryptedValue);
    }

    [Fact]
    public async Task Get_IncludesCategoryInfo_ThatIsStoredInDatabase()
    {
        var cat = await _dataFactory.CategoryAsync(name: "Databases");
        var svc = await _dataFactory.ServiceAsync(categoryId: cat.Id);
        var ctrl = BuildController();

        var result = await ctrl.Get(svc.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<WebResourceResponse>(ok.Value);

        Assert.Equal(cat.Id, response.CategoryId);
        Assert.Equal("Databases", response.CategoryName);

        // FK is stored correctly in DB
        var dbRow = await _dbContext.WebResources.AsNoTracking().SingleAsync(s => s.Id == svc.Id);
        Assert.Equal(cat.Id, dbRow.CategoryId);
    }

    [Fact]
    public async Task Get_ReturnsFaviconUrl_WhenLogoSourceIsAutoFavicon()
    {
        var svc = await _dataFactory.ServiceAsync(mainUrl: "https://github.com");
        // Verify DB has AutoFavicon as default
        var dbRow = await _dbContext.WebResources.AsNoTracking().SingleAsync(s => s.Id == svc.Id);
        Assert.Equal(LogoSource.AutoFavicon, dbRow.LogoSource);

        var ctrl = BuildController();
        var result = await ctrl.Get(svc.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<WebResourceResponse>(ok.Value);
        Assert.NotNull(response.FaviconUrl);
        Assert.Contains("github.com", response.FaviconUrl);
    }

    [Fact]
    public async Task Get_ReturnsNotFound_WhenServiceDoesNotExistInDatabase()
    {
        var nonExistentId = Guid.NewGuid();
        var ctrl = BuildController();

        var result = await ctrl.Get(nonExistentId, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
        Assert.False(await _dbContext.WebResources.AnyAsync(s => s.Id == nonExistentId));
    }

    [Fact]
    public async Task Get_ReturnsNotFound_WhenServiceBelongsToAnotherUser_AndLeavesItInDatabase()
    {
        var svc = await _dataFactory.ServiceAsync(userId: _otherUserId);
        var ctrl = BuildController(); // authenticated as UserId

        var result = await ctrl.Get(svc.Id, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
        // The record was NOT deleted — it still belongs to the other user
        var dbRow = await _dbContext.WebResources.AsNoTracking().SingleAsync(s => s.Id == svc.Id);
        Assert.Equal(_otherUserId, dbRow.UserId);
    }

    [Fact]
    public async Task Get_ReturnsNotFound_ForEmptyGuid()
    {
        var ctrl = BuildController();

        var result = await ctrl.Get(Guid.Empty, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
    }
}


