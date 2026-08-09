using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using Stashboard.Api.Contracts;
using Stashboard.Core.Enums;

namespace Stashboard.Tests.Controllers.WebResources;

/// <summary>
/// Tests verifying that favicon / logo images are persisted as base64 in the database
/// for all four flows: Create, Update (URL changed), RefreshFavicon, and UploadLogo.
/// </summary>
public class FaviconBase64PersistenceTests : WebResourcesControllerTestBase
{
    // ── Create ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_StoresFaviconBase64_InDatabase()
    {
        var ctrl = BuildController();

        var result = await ctrl.Create(DefaultRequest("Service1", "https://example.com"), CancellationToken.None);

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        var dto = Assert.IsType<WebResourceResponse>(created.Value);

        var dbRow = await _dbContext.WebResources.AsNoTracking().SingleAsync(s => s.Id == dto.Id);
        Assert.NotNull(dbRow.LogoBase64);
        Assert.StartsWith("data:image/png;base64,", dbRow.LogoBase64);
    }

    [Fact]
    public async Task Create_WithCustomLogoSource_DoesNotStoreFaviconBase64()
    {
        var ctrl = BuildController();
        var request = new WebResourceUpsertRequest(
            "Service2", "https://example.com", true, null, true, null,
            Stashboard.Core.Enums.HealthCheckMethod.Get, null, null, null,
            LogoSource.Custom, "/some/path.png", [], []);

        var result = await ctrl.Create(request, CancellationToken.None);

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        var dto = Assert.IsType<WebResourceResponse>(created.Value);

        var dbRow = await _dbContext.WebResources.AsNoTracking().SingleAsync(s => s.Id == dto.Id);
        Assert.Null(dbRow.LogoBase64);

        _faviconMock.Verify(f => f.DownloadAsBase64Async(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Update ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Update_WhenUrlChanges_RefetchesFaviconBase64_InDatabase()
    {
        var svc = await _dataFactory.ServiceAsync(mainUrl: "https://old.example.com");

        // Seed an old base64 value
        var editable = await _dbContext.WebResources.SingleAsync(s => s.Id == svc.Id);
        editable.LogoBase64 = "data:image/png;base64,OLD==";
        await _dbContext.SaveChangesAsync();

        _dbContext.ChangeTracker.Clear();

        var ctrl = BuildController();
        var request = new WebResourceUpsertRequest(
            svc.Name, "https://new.example.com", true, null, true, null,
            Stashboard.Core.Enums.HealthCheckMethod.Get, null, null, null,
            LogoSource.AutoFavicon, null, [], []);

        await ctrl.Update(svc.Id, request, CancellationToken.None);

        var dbRow = await _dbContext.WebResources.AsNoTracking().SingleAsync(s => s.Id == svc.Id);
        Assert.NotNull(dbRow.LogoBase64);
        Assert.NotEqual("data:image/png;base64,OLD==", dbRow.LogoBase64);
    }

    [Fact]
    public async Task Update_WhenUrlUnchanged_DoesNotRefetchFaviconBase64()
    {
        var svc = await _dataFactory.ServiceAsync(mainUrl: "https://example.com");

        var editable = await _dbContext.WebResources.SingleAsync(s => s.Id == svc.Id);
        editable.LogoBase64 = "data:image/png;base64,KEEP==";
        await _dbContext.SaveChangesAsync();

        _dbContext.ChangeTracker.Clear();

        var ctrl = BuildController();
        var request = new WebResourceUpsertRequest(
            "Updated Name", "https://example.com", true, null, true, null,
            Stashboard.Core.Enums.HealthCheckMethod.Get, null, null, null,
            LogoSource.AutoFavicon, null, [], []);

        await ctrl.Update(svc.Id, request, CancellationToken.None);

        var dbRow = await _dbContext.WebResources.AsNoTracking().SingleAsync(s => s.Id == svc.Id);
        Assert.Equal("data:image/png;base64,KEEP==", dbRow.LogoBase64);
    }

    // ── RefreshFavicon ────────────────────────────────────────────────────────

    [Fact]
    public async Task RefreshFavicon_StoresNewFaviconBase64_InDatabase()
    {
        var svc = await _dataFactory.ServiceAsync();
        var editable = await _dbContext.WebResources.SingleAsync(s => s.Id == svc.Id);
        editable.LogoBase64 = "data:image/png;base64,OLD==";
        editable.CustomLogoPath = null;
        editable.LogoSource = LogoSource.AutoFavicon;
        await _dbContext.SaveChangesAsync();

        _dbContext.ChangeTracker.Clear();

        var ctrl = BuildController();
        await ctrl.RefreshFavicon(svc.Id, CancellationToken.None);

        var dbRow = await _dbContext.WebResources.AsNoTracking().SingleAsync(s => s.Id == svc.Id);
        Assert.NotNull(dbRow.LogoBase64);
        Assert.NotEqual("data:image/png;base64,OLD==", dbRow.LogoBase64);
        Assert.StartsWith("data:image/png;base64,", dbRow.LogoBase64);
    }

    [Fact]
    public async Task RefreshFavicon_ClearsOldBase64_WhenFaviconResolveReturnsNull()
    {
        var svc = await _dataFactory.ServiceAsync();
        var editable = await _dbContext.WebResources.SingleAsync(s => s.Id == svc.Id);
        editable.LogoBase64 = "data:image/png;base64,OLD==";
        await _dbContext.SaveChangesAsync();

        _dbContext.ChangeTracker.Clear();

        _faviconMock
            .Setup(f => f.ResolveFaviconUrlAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        var ctrl = BuildController();
        await ctrl.RefreshFavicon(svc.Id, CancellationToken.None);

        var dbRow = await _dbContext.WebResources.AsNoTracking().SingleAsync(s => s.Id == svc.Id);
        Assert.Null(dbRow.LogoBase64);
    }

    // ── UploadLogo ────────────────────────────────────────────────────────────

    [Fact]
    public async Task UploadLogo_StoresBase64_InDatabase()
    {
        var svc = await _dataFactory.ServiceAsync();
        var ctrl = BuildController();

        var imageBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47 }; // PNG header bytes
        var dataUri = $"data:image/png;base64,{Convert.ToBase64String(imageBytes)}";

        await ctrl.UploadLogo(svc.Id, new ContainerIconUploadRequest(dataUri), CancellationToken.None);

        var dbRow = await _dbContext.WebResources.AsNoTracking().SingleAsync(s => s.Id == svc.Id);
        Assert.Equal(dataUri, dbRow.LogoBase64);
    }

    [Fact]
    public async Task UploadLogo_Base64ContainsCorrectMimeType_InDatabase()
    {
        var svc = await _dataFactory.ServiceAsync();
        var ctrl = BuildController();

        var dataUri = $"data:image/gif;base64,{Convert.ToBase64String(new byte[] { 0x47, 0x49, 0x46 })}";

        await ctrl.UploadLogo(svc.Id, new ContainerIconUploadRequest(dataUri), CancellationToken.None);

        var dbRow = await _dbContext.WebResources.AsNoTracking().SingleAsync(s => s.Id == svc.Id);
        Assert.NotNull(dbRow.LogoBase64);
        Assert.StartsWith("data:image/gif;base64,", dbRow.LogoBase64);
    }

    // ── Mapper output ─────────────────────────────────────────────────────────

    [Fact]
    public async Task GetService_ReturnsFaviconUrlFromBase64_WhenStoredInDatabase()
    {
        var svc = await _dataFactory.ServiceAsync();
        var editable = await _dbContext.WebResources.SingleAsync(s => s.Id == svc.Id);
        editable.LogoBase64 = "data:image/png;base64,STORED==";
        editable.LogoSource = LogoSource.AutoFavicon;
        await _dbContext.SaveChangesAsync();

        _dbContext.ChangeTracker.Clear();

        var ctrl = BuildController();
        var result = await ctrl.Get(svc.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<WebResourceResponse>(ok.Value);
        Assert.Equal("data:image/png;base64,STORED==", dto.FaviconUrl);

        // Should NOT call live favicon resolution when base64 is stored
        _faviconMock.Verify(f => f.ResolveFaviconUrlAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetService_ReturnsCustomLogoPathFromBase64_WhenCustomLogoStored()
    {
        var svc = await _dataFactory.ServiceAsync();
        var editable = await _dbContext.WebResources.SingleAsync(s => s.Id == svc.Id);
        editable.LogoBase64 = "data:image/png;base64,CUSTOM==";
        editable.LogoSource = LogoSource.Custom;
        editable.CustomLogoPath = "/uploads/logos/old.png";
        await _dbContext.SaveChangesAsync();

        _dbContext.ChangeTracker.Clear();

        var ctrl = BuildController();
        var result = await ctrl.Get(svc.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<WebResourceResponse>(ok.Value);
        Assert.Equal("data:image/png;base64,CUSTOM==", dto.CustomLogoPath);
        Assert.Null(dto.FaviconUrl);
    }
}



