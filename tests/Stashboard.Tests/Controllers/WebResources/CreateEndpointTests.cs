using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using Stashboard.Api.Contracts;
using Stashboard.Core.Enums;

namespace Stashboard.Tests.Controllers.WebResources;

public class CreateEndpointTests : WebResourcesControllerTestBase
{
    [Fact]
    public async Task Create_Returns201_AndRowAppearsInDatabase()
    {
        var ctrl = BuildController();
        var req = DefaultRequest("New Service", "https://new.example.com");

        var result = await ctrl.Create(req, CancellationToken.None);

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        Assert.Equal(201, created.StatusCode);
        var response = Assert.IsType<WebResourceResponse>(created.Value);

        // Verify the row really exists in DB with correct data
        var dbRow = await _dbContext.WebResources.AsNoTracking().SingleAsync(s => s.Id == response.Id);
        Assert.Equal("New Service", dbRow.Name);
        Assert.Equal("https://new.example.com", dbRow.MainUrl);
        Assert.Equal(_userId, dbRow.UserId);
    }

    [Fact]
    public async Task Create_DatabaseContainsExactlyOneService_AfterSingleCall()
    {
        var ctrl = BuildController();

        await ctrl.Create(DefaultRequest("Only One"), CancellationToken.None);

        Assert.Equal(1, await _dbContext.WebResources.CountAsync());
    }

    [Fact]
    public async Task Create_AssignsAuthenticatedUserId_ToNewRowInDatabase()
    {
        var anotherUser = await _dataFactory.UserAsync();
        var ctrl = BuildController(userId: anotherUser.Id);

        var result = await ctrl.Create(DefaultRequest(), CancellationToken.None);

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        var response = Assert.IsType<WebResourceResponse>(created.Value);
        var dbRow = await _dbContext.WebResources.AsNoTracking().SingleAsync(s => s.Id == response.Id);
        Assert.Equal(anotherUser.Id, dbRow.UserId);
    }

    [Fact]
    public async Task Create_WithTags_CreatesTagAndServiceTagRowsInDatabase()
    {
        var ctrl = BuildController();
        var req = new WebResourceUpsertRequest(
            "Tagged", "https://example.com", true, null, true, null, HealthCheckMethod.Get,
            null, null, null, LogoSource.AutoFavicon, null,
            ["api", "backend"], []);

        var result = await ctrl.Create(req, CancellationToken.None);

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        var response = Assert.IsType<WebResourceResponse>(created.Value);

        // Two Tag rows created for this user
        Assert.Equal(2, await _dbContext.Tags.CountAsync(t => t.UserId == _userId));
        // Two ServiceTag join rows linking service to tags
        Assert.Equal(2, await _dbContext.WebResourceTags.CountAsync(st => st.WebResourceId == response.Id));
        Assert.Contains("api", response.Tags);
        Assert.Contains("backend", response.Tags);
    }

    [Fact]
    public async Task Create_WithDuplicateTags_CreatesOnlyOneTagRowInDatabase()
    {
        var ctrl = BuildController();
        var req = new WebResourceUpsertRequest(
            "Service", "https://example.com", true, null, true, null, HealthCheckMethod.Get,
            null, null, null, LogoSource.AutoFavicon, null,
            ["api", "API", "Api"], []);

        var result = await ctrl.Create(req, CancellationToken.None);

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        var response = Assert.IsType<WebResourceResponse>(created.Value);

        Assert.Equal(1, await _dbContext.Tags.CountAsync(t => t.UserId == _userId));
        Assert.Equal(1, await _dbContext.WebResourceTags.CountAsync(st => st.WebResourceId == response.Id));
    }

    [Fact]
    public async Task Create_WithCredentials_SavesEncryptedValueInDatabase()
    {
        var ctrl = BuildController();
        var req = new WebResourceUpsertRequest(
            "Service", "https://example.com", true, null, true, null, HealthCheckMethod.Get,
            null, null, null, LogoSource.AutoFavicon, null,
            [], [new CredentialUpsert("MY_KEY", "my-secret", true)]);

        var result = await ctrl.Create(req, CancellationToken.None);

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        var response = Assert.IsType<WebResourceResponse>(created.Value);

        // One credential row in DB
        var dbCred = await _dbContext.Credentials.SingleAsync(c => c.WebResourceId == response.Id);
        Assert.Equal("MY_KEY", dbCred.Key);
        Assert.Equal("enc:my-secret", dbCred.EncryptedValue);  // stored encrypted
        Assert.True(dbCred.IsSecret);

        // But response exposes the decrypted value
        var respCred = Assert.Single(response.Credentials);
        Assert.Equal("my-secret", respCred.Value);

        _encryptionMock.Verify(e => e.Encrypt("my-secret"), Times.Once);
    }

    [Fact]
    public async Task Create_WithEmptyCredentialKey_DoesNotCreateCredentialRowInDatabase()
    {
        var ctrl = BuildController();
        var req = new WebResourceUpsertRequest(
            "Service", "https://example.com", true, null, true, null, HealthCheckMethod.Get,
            null, null, null, LogoSource.AutoFavicon, null,
            [], [new CredentialUpsert("", "value", false)]);

        var result = await ctrl.Create(req, CancellationToken.None);

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        var response = Assert.IsType<WebResourceResponse>(created.Value);

        Assert.Equal(0, await _dbContext.Credentials.CountAsync(c => c.WebResourceId == response.Id));
        Assert.Empty(response.Credentials);
    }

    [Fact]
    public async Task Create_WithWhitespaceOnlyTags_DoesNotCreateTagRowsInDatabase()
    {
        var ctrl = BuildController();
        var req = new WebResourceUpsertRequest(
            "Service", "https://example.com", true, null, true, null, HealthCheckMethod.Get,
            null, null, null, LogoSource.AutoFavicon, null,
            ["  ", "", "valid-tag"], []);

        var result = await ctrl.Create(req, CancellationToken.None);

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        var response = Assert.IsType<WebResourceResponse>(created.Value);

        // Only one valid tag row
        Assert.Equal(1, await _dbContext.Tags.CountAsync(t => t.UserId == _userId));
        Assert.Equal(1, await _dbContext.WebResourceTags.CountAsync(st => st.WebResourceId == response.Id));
        Assert.Single(response.Tags);
        Assert.Equal("valid-tag", response.Tags[0]);
    }

    [Fact]
    public async Task Create_WithExistingTag_ReusesTagRow_AndDoesNotCreateDuplicate()
    {
        await _dataFactory.TagAsync("existing-tag");
        var ctrl = BuildController();
        var req = new WebResourceUpsertRequest(
            "Service", "https://example.com", true, null, true, null, HealthCheckMethod.Get,
            null, null, null, LogoSource.AutoFavicon, null,
            ["existing-tag"], []);

        await ctrl.Create(req, CancellationToken.None);

        // Still only one Tag row for this name
        Assert.Equal(1, await _dbContext.Tags.CountAsync(t => t.Name == "existing-tag" && t.UserId == _userId));
    }

    [Fact]
    public async Task Create_WithCategory_StoresCategoryIdInDatabase()
    {
        var cat = await _dataFactory.CategoryAsync();
        var ctrl = BuildController();
        var req = new WebResourceUpsertRequest(
            "Categorised", "https://example.com", true, null, true, null, HealthCheckMethod.Get,
            null, null, cat.Id, LogoSource.AutoFavicon, null, [], []);

        var result = await ctrl.Create(req, CancellationToken.None);

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        var response = Assert.IsType<WebResourceResponse>(created.Value);

        var dbRow = await _dbContext.WebResources.AsNoTracking().SingleAsync(s => s.Id == response.Id);
        Assert.Equal(cat.Id, dbRow.CategoryId);
        Assert.Equal(cat.Id, response.CategoryId);
    }

    [Fact]
    public async Task Create_WithHealthCheckUrl_StoresItInDatabase()
    {
        var ctrl = BuildController();
        var req = new WebResourceUpsertRequest(
            "HC Service", "https://example.com", true, null, true, "https://example.com/health", HealthCheckMethod.Get,
            "200-299", null, null, LogoSource.AutoFavicon, null, [], []);

        var result = await ctrl.Create(req, CancellationToken.None);

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        var response = Assert.IsType<WebResourceResponse>(created.Value);

        var dbRow = await _dbContext.WebResources.AsNoTracking().SingleAsync(s => s.Id == response.Id);
        Assert.Equal("https://example.com/health", dbRow.HealthCheckUrl);
        Assert.Equal("200-299", dbRow.ExpectedStatusRange);
    }

    [Fact]
    public async Task Create_WithWhitespaceHealthCheckUrl_StoresNullInDatabase()
    {
        var ctrl = BuildController();
        var req = new WebResourceUpsertRequest(
            "Service", "https://example.com", true, null, true, "   ", HealthCheckMethod.Get,
            null, null, null, LogoSource.AutoFavicon, null, [], []);

        var result = await ctrl.Create(req, CancellationToken.None);

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        var response = Assert.IsType<WebResourceResponse>(created.Value);

        var dbRow = await _dbContext.WebResources.AsNoTracking().SingleAsync(s => s.Id == response.Id);
        Assert.Null(dbRow.HealthCheckUrl);
        Assert.Null(response.HealthCheckUrl);
    }

    [Fact]
    public async Task Create_WithAdditionalUrl_StoresItInDatabase()
    {
        var ctrl = BuildController();
        var req = new WebResourceUpsertRequest(
            "Service", "https://primary.example.com", true, "https://secondary.example.com", true, null, HealthCheckMethod.Get,
            null, null, null, LogoSource.AutoFavicon, null, [], []);

        var result = await ctrl.Create(req, CancellationToken.None);

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        var response = Assert.IsType<WebResourceResponse>(created.Value);

        var dbRow = await _dbContext.WebResources.AsNoTracking().SingleAsync(s => s.Id == response.Id);
        Assert.Equal("https://secondary.example.com", dbRow.AdditionalUrl);
        Assert.Equal("https://secondary.example.com", response.AdditionalUrl);
    }

    [Fact]
    public async Task Create_WithDisabledHealthChecks_PersistsFlagsAndClearsStatuses()
    {
        var ctrl = BuildController();
        var req = new WebResourceUpsertRequest(
            "Service", "https://primary.example.com", false, "https://secondary.example.com", false, null, HealthCheckMethod.Get,
            null, null, null, LogoSource.AutoFavicon, null, [], []);

        var result = await ctrl.Create(req, CancellationToken.None);

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        var response = Assert.IsType<WebResourceResponse>(created.Value);
        Assert.False(response.MainUrlHealthCheckEnabled);
        Assert.False(response.AdditionalUrlHealthCheckEnabled);
        Assert.Equal(ServiceStatus.Unknown, response.CurrentStatus);
        Assert.Equal(ServiceStatus.Unknown, response.AdditionalUrlStatus);

        var dbRow = await _dbContext.WebResources.AsNoTracking().SingleAsync(s => s.Id == response.Id);
        Assert.False(dbRow.MainUrlHealthCheckEnabled);
        Assert.False(dbRow.AdditionalUrlHealthCheckEnabled);
        Assert.Equal(ServiceStatus.Unknown, dbRow.CurrentStatus);
        Assert.Equal(ServiceStatus.Unknown, dbRow.AdditionalUrlStatus);
    }

    [Fact]
    public async Task Create_WhenAllChecksDisabledAndNoHealthCheckUrl_ForcesOfflineNotificationsDisabled()
    {
        var ctrl = BuildController();
        var req = new WebResourceUpsertRequest(
            "Service", "https://primary.example.com", false, "https://secondary.example.com", false, null, HealthCheckMethod.Get,
            null, null, null, LogoSource.AutoFavicon, null, [], [], true);

        var result = await ctrl.Create(req, CancellationToken.None);

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        var response = Assert.IsType<WebResourceResponse>(created.Value);
        Assert.False(response.OfflineNotificationsEnabled);

        var dbRow = await _dbContext.WebResources.AsNoTracking().SingleAsync(s => s.Id == response.Id);
        Assert.False(dbRow.OfflineNotificationsEnabled);
    }
}



