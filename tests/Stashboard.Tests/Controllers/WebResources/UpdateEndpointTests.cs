using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Stashboard.Api.Contracts;
using Stashboard.Core.Enums;

namespace Stashboard.Tests.Controllers.WebResources;

public class UpdateEndpointTests : WebResourcesControllerTestBase
{
    [Fact]
    public async Task Update_ReturnsOk_AndDatabaseRowReflectsNewValues()
    {
        var svc = await _dataFactory.ServiceAsync(name: "Old Name", mainUrl: "https://old.example.com");
        var ctrl = BuildController();

        var result = await ctrl.Update(svc.Id, DefaultRequest("New Name", "https://new.example.com"), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<WebResourceResponse>(ok.Value);
        Assert.Equal("New Name", response.Name);
        Assert.Equal("https://new.example.com", response.MainUrl);

        // Verify DB was updated
        var dbRow = await _dbContext.WebResources.AsNoTracking().SingleAsync(s => s.Id == svc.Id);
        Assert.Equal("New Name", dbRow.Name);
        Assert.Equal("https://new.example.com", dbRow.MainUrl);
    }

    [Fact]
    public async Task Update_UpdatedUtc_IsRefreshedInDatabase()
    {
        var svc = await _dataFactory.ServiceAsync();
        var originalUpdated = (await _dbContext.WebResources.AsNoTracking().SingleAsync(s => s.Id == svc.Id)).UpdatedUtc;

        await Task.Delay(5); // ensure clock advances
        var ctrl = BuildController();

        await ctrl.Update(svc.Id, DefaultRequest("Changed"), CancellationToken.None);

        var dbRow = await _dbContext.WebResources.AsNoTracking().SingleAsync(s => s.Id == svc.Id);
        Assert.True(dbRow.UpdatedUtc >= originalUpdated);
    }

    [Fact]
    public async Task Update_ReplacesAllCredentials_OldRowsDeletedNewRowsInserted()
    {
        var svc = await _dataFactory.ServiceAsync();
        await _dataFactory.AttachCredentialAsync(svc, "OLD_KEY", "old-value");
        var ctrl = BuildController();
        var req = new WebResourceUpsertRequest(
            "Service", "https://example.com", true, null, true, null, HealthCheckMethod.Get,
            null, null, null, LogoSource.AutoFavicon, null,
            [], [new CredentialUpsert("NEW_KEY", "new-value", false)]);

        await ctrl.Update(svc.Id, req, CancellationToken.None);

        var dbCreds = await _dbContext.Credentials.AsNoTracking().Where(c => c.WebResourceId == svc.Id).ToListAsync();
        Assert.Single(dbCreds);
        Assert.Equal("NEW_KEY", dbCreds[0].Key);
        Assert.Equal("enc:new-value", dbCreds[0].EncryptedValue);
        Assert.False(await _dbContext.Credentials.AnyAsync(c => c.Key == "OLD_KEY" && c.WebResourceId == svc.Id));
    }

    [Fact]
    public async Task Update_WithEmptyCredentialsList_DeletesAllCredentialRowsFromDatabase()
    {
        var svc = await _dataFactory.ServiceAsync();
        await _dataFactory.AttachCredentialAsync(svc, "KEY1", "val1");
        await _dataFactory.AttachCredentialAsync(svc, "KEY2", "val2");
        var ctrl = BuildController();
        var req = new WebResourceUpsertRequest(
            "Service", "https://example.com", true, null, true, null, HealthCheckMethod.Get,
            null, null, null, LogoSource.AutoFavicon, null, [], []);

        await ctrl.Update(svc.Id, req, CancellationToken.None);

        Assert.Equal(0, await _dbContext.Credentials.CountAsync(c => c.WebResourceId == svc.Id));
    }

    [Fact]
    public async Task Update_AddsTags_NewTagAndServiceTagRowsCreatedInDatabase()
    {
        var svc = await _dataFactory.ServiceAsync();
        var ctrl = BuildController();
        var req = new WebResourceUpsertRequest(
            "Service", "https://example.com", true, null, true, null, HealthCheckMethod.Get,
            null, null, null, LogoSource.AutoFavicon, null,
            ["new-tag"], []);

        await ctrl.Update(svc.Id, req, CancellationToken.None);

        Assert.Equal(1, await _dbContext.Tags.CountAsync(t => t.Name == "new-tag" && t.UserId == _userId));
        Assert.Equal(1, await _dbContext.WebResourceTags.CountAsync(st => st.WebResourceId == svc.Id));
    }

    [Fact]
    public async Task Update_RemovesTagsNotInRequest_ServiceTagRowDeletedTagRowKept()
    {
        var svc = await _dataFactory.ServiceAsync();
        var oldTag = await _dataFactory.TagAsync("old-tag");
        await _dataFactory.AttachTagAsync(svc, oldTag);
        var ctrl = BuildController();
        var req = new WebResourceUpsertRequest(
            "Service", "https://example.com", true, null, true, null, HealthCheckMethod.Get,
            null, null, null, LogoSource.AutoFavicon, null,
            ["new-tag"], []);

        await ctrl.Update(svc.Id, req, CancellationToken.None);

        // ServiceTag for old-tag is removed
        Assert.False(await _dbContext.WebResourceTags.AnyAsync(st => st.WebResourceId == svc.Id && st.TagId == oldTag.Id));
        // ServiceTag for new-tag is added
        Assert.True(await _dbContext.Tags.AnyAsync(t => t.Name == "new-tag" && t.UserId == _userId));
        Assert.Equal(1, await _dbContext.WebResourceTags.CountAsync(st => st.WebResourceId == svc.Id));
    }

    [Fact]
    public async Task Update_KeepsExistingTag_NoNewRowCreated_WhenTagStillInRequest()
    {
        var svc = await _dataFactory.ServiceAsync();
        var tag = await _dataFactory.TagAsync("keep-tag");
        await _dataFactory.AttachTagAsync(svc, tag);
        var ctrl = BuildController();
        var req = new WebResourceUpsertRequest(
            "Service", "https://example.com", true, null, true, null, HealthCheckMethod.Get,
            null, null, null, LogoSource.AutoFavicon, null,
            ["keep-tag"], []);

        await ctrl.Update(svc.Id, req, CancellationToken.None);

        // Still exactly one Tag row for this name
        Assert.Equal(1, await _dbContext.Tags.CountAsync(t => t.Name == "keep-tag" && t.UserId == _userId));
        // Still exactly one ServiceTag row
        Assert.Equal(1, await _dbContext.WebResourceTags.CountAsync(st => st.WebResourceId == svc.Id));
    }

    [Fact]
    public async Task Update_SetsCategoryId_InDatabase()
    {
        var svc = await _dataFactory.ServiceAsync();
        var cat = await _dataFactory.CategoryAsync();
        var ctrl = BuildController();
        var req = new WebResourceUpsertRequest(
            "Service", "https://example.com", true, null, true, null, HealthCheckMethod.Get,
            null, null, cat.Id, LogoSource.AutoFavicon, null, [], []);

        await ctrl.Update(svc.Id, req, CancellationToken.None);

        var dbRow = await _dbContext.WebResources.AsNoTracking().SingleAsync(s => s.Id == svc.Id);
        Assert.Equal(cat.Id, dbRow.CategoryId);
    }

    [Fact]
    public async Task Update_ClearsCategoryId_InDatabase_WhenNullPassed()
    {
        var cat = await _dataFactory.CategoryAsync();
        var svc = await _dataFactory.ServiceAsync(categoryId: cat.Id);
        var ctrl = BuildController();
        var req = new WebResourceUpsertRequest(
            "Service", "https://example.com", true, null, true, null, HealthCheckMethod.Get,
            null, null, null, LogoSource.AutoFavicon, null, [], []);

        await ctrl.Update(svc.Id, req, CancellationToken.None);

        var dbRow = await _dbContext.WebResources.AsNoTracking().SingleAsync(s => s.Id == svc.Id);
        Assert.Null(dbRow.CategoryId);
    }

    [Fact]
    public async Task Update_ReturnsNotFound_WhenServiceDoesNotExistInDatabase()
    {
        var ctrl = BuildController();

        var result = await ctrl.Update(Guid.NewGuid(), DefaultRequest(), CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
        Assert.Equal(0, await _dbContext.WebResources.CountAsync());
    }

    [Fact]
    public async Task Update_ReturnsNotFound_AndLeavesOtherUsersRowUntouched()
    {
        var svc = await _dataFactory.ServiceAsync(userId: _otherUserId, name: "Original Name");
        var ctrl = BuildController();

        var result = await ctrl.Update(svc.Id, DefaultRequest("Hacked Name"), CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);

        // DB row for the other user is unchanged
        var dbRow = await _dbContext.WebResources.AsNoTracking().SingleAsync(s => s.Id == svc.Id);
        Assert.Equal("Original Name", dbRow.Name);
        Assert.Equal(_otherUserId, dbRow.UserId);
    }

    [Fact]
    public async Task Update_WithAdditionalUrl_PersistsItInDatabase()
    {
        var svc = await _dataFactory.ServiceAsync();
        var ctrl = BuildController();
        var req = new WebResourceUpsertRequest(
            "Service", "https://example.com", true, "https://mirror.example.com", true, null, HealthCheckMethod.Get,
            null, null, null, LogoSource.AutoFavicon, null, [], []);

        var result = await ctrl.Update(svc.Id, req, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<WebResourceResponse>(ok.Value);
        Assert.Equal("https://mirror.example.com", response.AdditionalUrl);

        var dbRow = await _dbContext.WebResources.AsNoTracking().SingleAsync(s => s.Id == svc.Id);
        Assert.Equal("https://mirror.example.com", dbRow.AdditionalUrl);
    }

    [Fact]
    public async Task Update_ClearsAdditionalUrl_WhenNullPassed()
    {
        var svc = await _dataFactory.ServiceAsync();
        var ctrl = BuildController();

        // First set it
        var setReq = new WebResourceUpsertRequest(
            "Service", "https://example.com", true, "https://mirror.example.com", true, null, HealthCheckMethod.Get,
            null, null, null, LogoSource.AutoFavicon, null, [], []);
        await ctrl.Update(svc.Id, setReq, CancellationToken.None);

        // Then clear it
        var clearReq = new WebResourceUpsertRequest(
            "Service", "https://example.com", true, null, true, null, HealthCheckMethod.Get,
            null, null, null, LogoSource.AutoFavicon, null, [], []);
        var result = await ctrl.Update(svc.Id, clearReq, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<WebResourceResponse>(ok.Value);
        Assert.Null(response.AdditionalUrl);

        var dbRow = await _dbContext.WebResources.AsNoTracking().SingleAsync(s => s.Id == svc.Id);
        Assert.Null(dbRow.AdditionalUrl);
    }

    [Fact]
    public async Task Update_DisablingHealthChecks_ClearsPersistedStatuses()
    {
        var svc = await _dataFactory.ServiceAsync();
        svc.AdditionalUrl = "https://mirror.example.com";
        svc.CurrentStatus = ServiceStatus.Up;
        svc.LastCheckedUtc = DateTime.UtcNow;
        svc.LastResponseTimeMs = 25;
        svc.LastError = "old";
        svc.AdditionalUrlStatus = ServiceStatus.Down;
        svc.AdditionalUrlLastResponseTimeMs = 40;
        svc.AdditionalUrlLastError = "boom";
        _dbContext.Update(svc);
        await _dbContext.SaveChangesAsync();

        var ctrl = BuildController();
        var req = new WebResourceUpsertRequest(
            "Service", "https://example.com", false, "https://mirror.example.com", false, null, HealthCheckMethod.Get,
            null, null, null, LogoSource.AutoFavicon, null, [], []);

        var result = await ctrl.Update(svc.Id, req, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        _ = Assert.IsType<WebResourceResponse>(ok.Value);

        var dbRow = await _dbContext.WebResources.AsNoTracking().SingleAsync(s => s.Id == svc.Id);
        Assert.False(dbRow.MainUrlHealthCheckEnabled);
        Assert.False(dbRow.AdditionalUrlHealthCheckEnabled);
        Assert.Equal(ServiceStatus.Unknown, dbRow.CurrentStatus);
        Assert.Equal(ServiceStatus.Unknown, dbRow.AdditionalUrlStatus);
        Assert.Null(dbRow.LastCheckedUtc);
        Assert.Null(dbRow.LastResponseTimeMs);
        Assert.Null(dbRow.LastError);
        Assert.Null(dbRow.AdditionalUrlLastResponseTimeMs);
        Assert.Null(dbRow.AdditionalUrlLastError);
    }

    [Fact]
    public async Task Update_WhenAllChecksDisabledAndNoHealthCheckUrl_ForcesOfflineNotificationsDisabled()
    {
        var svc = await _dataFactory.ServiceAsync();
        var ctrl = BuildController();
        var req = new WebResourceUpsertRequest(
            "Service", "https://example.com", false, "https://mirror.example.com", false, null, HealthCheckMethod.Get,
            null, null, null, LogoSource.AutoFavicon, null, [], [], true);

        var result = await ctrl.Update(svc.Id, req, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<WebResourceResponse>(ok.Value);
        Assert.False(response.OfflineNotificationsEnabled);

        var dbRow = await _dbContext.WebResources.AsNoTracking().SingleAsync(s => s.Id == svc.Id);
        Assert.False(dbRow.OfflineNotificationsEnabled);
    }
}


