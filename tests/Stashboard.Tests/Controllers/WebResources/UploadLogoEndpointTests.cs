using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using Stashboard.Api.Contracts;
using Stashboard.Core.Enums;

namespace Stashboard.Tests.Controllers.WebResources;

public class UploadLogoEndpointTests : WebResourcesControllerTestBase
{
    // A 3-byte payload base64-encodes to "AQID" — a minimal valid image data URI
    // for the endpoint's ImageDataUri validation.
    private static ContainerIconUploadRequest MakeRequest(string mime = "image/png")
        => new($"data:{mime};base64,AQID");

    [Fact]
    public async Task UploadLogo_ReturnsOk_AndStoresBase64InDatabase_WithNoFilePath()
    {
        var svc = await _dataFactory.ServiceAsync();
        var ctrl = BuildController();
        var request = MakeRequest();

        var result = await ctrl.UploadLogo(svc.Id, request, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dataUri = ok.Value!.GetType().GetProperty("dataUri")!.GetValue(ok.Value) as string;
        Assert.Equal(request.DataUri, dataUri);

        // The image lives purely in the database — base64 set, no file reference.
        var dbRow = await _dbContext.WebResources.AsNoTracking().SingleAsync(s => s.Id == svc.Id);
        Assert.Equal(request.DataUri, dbRow.LogoBase64);
        Assert.Null(dbRow.CustomLogoPath);
    }

    [Fact]
    public async Task UploadLogo_SetsLogoSourceToCustom_InDatabase()
    {
        var svc = await _dataFactory.ServiceAsync();
        // Initially AutoFavicon
        Assert.Equal(LogoSource.AutoFavicon,
            (await _dbContext.WebResources.AsNoTracking().SingleAsync(s => s.Id == svc.Id)).LogoSource);

        var ctrl = BuildController();
        await ctrl.UploadLogo(svc.Id, MakeRequest(), CancellationToken.None);

        var dbRow = await _dbContext.WebResources.AsNoTracking().SingleAsync(s => s.Id == svc.Id);
        Assert.Equal(LogoSource.Custom, dbRow.LogoSource);
    }

    [Fact]
    public async Task UploadLogo_ClearsLegacyFilePath_OnReupload()
    {
        var svc = await _dataFactory.ServiceAsync();
        var editable = await _dbContext.WebResources.SingleAsync(s => s.Id == svc.Id);
        editable.CustomLogoPath = "/uploads/logos/legacy.png";
        editable.LogoSource = LogoSource.Custom;
        await _dbContext.SaveChangesAsync();

        var ctrl = BuildController();
        await ctrl.UploadLogo(svc.Id, MakeRequest(), CancellationToken.None);

        var dbRow = await _dbContext.WebResources.AsNoTracking().SingleAsync(s => s.Id == svc.Id);
        Assert.Null(dbRow.CustomLogoPath);
        Assert.NotNull(dbRow.LogoBase64);
    }

    [Theory]
    [InlineData("image/png")]
    [InlineData("image/jpeg")]
    [InlineData("image/svg+xml")]
    [InlineData("image/webp")]
    public async Task UploadLogo_AcceptsImageDataUri_AndStoresInDatabase(string mime)
    {
        var svc = await _dataFactory.ServiceAsync();
        var ctrl = BuildController();

        var result = await ctrl.UploadLogo(svc.Id, MakeRequest(mime), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        var dbRow = await _dbContext.WebResources.AsNoTracking().SingleAsync(s => s.Id == svc.Id);
        Assert.NotNull(dbRow.LogoBase64);
        Assert.Equal(LogoSource.Custom, dbRow.LogoSource);
    }

    [Fact]
    public async Task UploadLogo_ReturnsBadRequest_WhenRequestIsNull_AndDatabaseIsUnchanged()
    {
        var svc = await _dataFactory.ServiceAsync();
        var ctrl = BuildController();

        var result = await ctrl.UploadLogo(svc.Id, null!, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        var dbRow = await _dbContext.WebResources.AsNoTracking().SingleAsync(s => s.Id == svc.Id);
        Assert.Null(dbRow.LogoBase64);
        Assert.Equal(LogoSource.AutoFavicon, dbRow.LogoSource);
    }

    [Fact]
    public async Task UploadLogo_ReturnsBadRequest_WhenDataUriIsEmpty_AndDatabaseIsUnchanged()
    {
        var svc = await _dataFactory.ServiceAsync();
        var ctrl = BuildController();

        var result = await ctrl.UploadLogo(svc.Id, new ContainerIconUploadRequest(""), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        var dbRow = await _dbContext.WebResources.AsNoTracking().SingleAsync(s => s.Id == svc.Id);
        Assert.Null(dbRow.LogoBase64);
    }

    [Theory]
    [InlineData("data:application/pdf;base64,AQID")]
    [InlineData("data:text/html;base64,AQID")]
    [InlineData("data:image/png;base64,not-valid-base64!!")]
    [InlineData("https://example.com/logo.png")]
    public async Task UploadLogo_ReturnsBadRequest_WhenNotAValidImageDataUri_AndDatabaseIsUnchanged(string dataUri)
    {
        var svc = await _dataFactory.ServiceAsync();
        var ctrl = BuildController();

        var result = await ctrl.UploadLogo(svc.Id, new ContainerIconUploadRequest(dataUri), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        var dbRow = await _dbContext.WebResources.AsNoTracking().SingleAsync(s => s.Id == svc.Id);
        Assert.Null(dbRow.LogoBase64);
        Assert.Equal(LogoSource.AutoFavicon, dbRow.LogoSource);
    }

    [Fact]
    public async Task UploadLogo_ReturnsNotFound_WhenServiceDoesNotExistInDatabase()
    {
        var nonExistentId = Guid.NewGuid();
        var ctrl = BuildController();

        var result = await ctrl.UploadLogo(nonExistentId, MakeRequest(), CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
        Assert.False(await _dbContext.WebResources.AnyAsync(s => s.Id == nonExistentId));
    }

    [Fact]
    public async Task UploadLogo_ReturnsNotFound_AndLeavesOtherUsersRowUntouched()
    {
        var svc = await _dataFactory.ServiceAsync(userId: _otherUserId);
        var ctrl = BuildController();

        var result = await ctrl.UploadLogo(svc.Id, MakeRequest(), CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);

        // Other user's row unchanged in DB
        var dbRow = await _dbContext.WebResources.AsNoTracking().SingleAsync(s => s.Id == svc.Id);
        Assert.Null(dbRow.LogoBase64);
        Assert.Equal(LogoSource.AutoFavicon, dbRow.LogoSource);
        Assert.Equal(_otherUserId, dbRow.UserId);
    }

    [Fact]
    public async Task RefreshFavicon_ReturnsOk_ClearsCustomLogoAndSetsAutoFavicon_InDatabase()
    {
        var service = await _dataFactory.ServiceAsync();
        var editable = await _dbContext.WebResources.SingleAsync(s => s.Id == service.Id);
        editable.CustomLogoPath = "/uploads/logos/custom-logo.png";
        editable.LogoSource = LogoSource.Custom;
        await _dbContext.SaveChangesAsync();

        var controller = BuildController();
        var result = await controller.RefreshFavicon(service.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<WebResourceResponse>(ok.Value);
        Assert.Equal(LogoSource.AutoFavicon, response.LogoSource);
        Assert.Null(response.CustomLogoPath);
        Assert.NotNull(response.FaviconUrl);

        var databaseRow = await _dbContext.WebResources.AsNoTracking().SingleAsync(s => s.Id == service.Id);
        Assert.Equal(LogoSource.AutoFavicon, databaseRow.LogoSource);
        Assert.Null(databaseRow.CustomLogoPath);

        _faviconMock.Verify(f => f.InvalidateSiteFaviconCache(databaseRow.MainUrl), Times.Once);
    }

    [Fact]
    public async Task RefreshFavicon_DeletesUploadedLogoFile_WhenCustomPathPointsToUploads()
    {
        var service = await _dataFactory.ServiceAsync();
        var webRoot = Path.Combine(Path.GetTempPath(), "wwwroot");
        var uploadsDirectory = Path.Combine(webRoot, "uploads", "logos");
        Directory.CreateDirectory(uploadsDirectory);

        var fileName = $"{Guid.NewGuid():N}.png";
        var fullFilePath = Path.Combine(uploadsDirectory, fileName);
        await File.WriteAllTextAsync(fullFilePath, "stub-image");

        var editable = await _dbContext.WebResources.SingleAsync(s => s.Id == service.Id);
        editable.CustomLogoPath = $"/uploads/logos/{fileName}";
        editable.LogoSource = LogoSource.Custom;
        await _dbContext.SaveChangesAsync();

        var controller = BuildController();
        await controller.RefreshFavicon(service.Id, CancellationToken.None);

        Assert.False(File.Exists(fullFilePath));
    }

    [Fact]
    public async Task RefreshFavicon_ReturnsNotFound_AndDoesNotChangeOtherUsersService()
    {
        var service = await _dataFactory.ServiceAsync(userId: _otherUserId);
        var editable = await _dbContext.WebResources.SingleAsync(s => s.Id == service.Id);
        editable.CustomLogoPath = "/uploads/logos/other-user.png";
        editable.LogoSource = LogoSource.Custom;
        await _dbContext.SaveChangesAsync();

        var controller = BuildController();
        var result = await controller.RefreshFavicon(service.Id, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);

        var databaseRow = await _dbContext.WebResources.AsNoTracking().SingleAsync(s => s.Id == service.Id);
        Assert.Equal(LogoSource.Custom, databaseRow.LogoSource);
        Assert.Equal("/uploads/logos/other-user.png", databaseRow.CustomLogoPath);
        _faviconMock.Verify(f => f.InvalidateSiteFaviconCache(It.IsAny<string>()), Times.Never);
    }
}



