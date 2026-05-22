using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using Stashboard.Api.Contracts;
using Stashboard.Core.Enums;

namespace Stashboard.Tests.Controllers.WebResources;

public class UploadLogoEndpointTests : WebResourcesControllerTestBase
{
    private static IFormFile MakeFormFile(
        string content = "fake-image-data",
        string fileName = "logo.png",
        string contentType = "image/png")
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(content);
        var stream = new MemoryStream(bytes);
        var mock = new Mock<IFormFile>();
        mock.Setup(f => f.FileName).Returns(fileName);
        mock.Setup(f => f.Length).Returns(bytes.Length);
        mock.Setup(f => f.ContentType).Returns(contentType);
        mock.Setup(f => f.CopyToAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .Callback<Stream, CancellationToken>((s, _) => stream.CopyTo(s))
            .Returns(Task.CompletedTask);
        return mock.Object;
    }

    [Fact]
    public async Task UploadLogo_ReturnsOk_AndDatabaseHasCustomLogoPath()
    {
        var svc = await _dataFactory.ServiceAsync();
        var ctrl = BuildController();

        var result = await ctrl.UploadLogo(svc.Id, MakeFormFile(), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var path = ok.Value!.GetType().GetProperty("path")!.GetValue(ok.Value) as string;
        Assert.NotNull(path);
        Assert.StartsWith("/uploads/logos/", path);
        Assert.EndsWith(".png", path);

        // Verify DB was updated
        var dbRow = await _dbContext.WebResources.AsNoTracking().SingleAsync(s => s.Id == svc.Id);
        Assert.Equal(path, dbRow.CustomLogoPath);
    }

    [Fact]
    public async Task UploadLogo_SetsLogoSourceToCustom_InDatabase()
    {
        var svc = await _dataFactory.ServiceAsync();
        // Initially AutoFavicon
        Assert.Equal(LogoSource.AutoFavicon,
            (await _dbContext.WebResources.AsNoTracking().SingleAsync(s => s.Id == svc.Id)).LogoSource);

        var ctrl = BuildController();
        await ctrl.UploadLogo(svc.Id, MakeFormFile(), CancellationToken.None);

        var dbRow = await _dbContext.WebResources.AsNoTracking().SingleAsync(s => s.Id == svc.Id);
        Assert.Equal(LogoSource.Custom, dbRow.LogoSource);
    }

    [Fact]
    public async Task UploadLogo_PreservesFileExtension_InDatabasePath()
    {
        var svc = await _dataFactory.ServiceAsync();
        var ctrl = BuildController();

        await ctrl.UploadLogo(svc.Id, MakeFormFile(fileName: "icon.svg", contentType: "image/svg+xml"), CancellationToken.None);

        var dbRow = await _dbContext.WebResources.AsNoTracking().SingleAsync(s => s.Id == svc.Id);
        Assert.True(dbRow.CustomLogoPath!.EndsWith(".svg"));
    }

    [Theory]
    [InlineData("image/png", "logo.png")]
    [InlineData("image/jpeg", "logo.jpg")]
    [InlineData("image/svg+xml", "logo.svg")]
    [InlineData("image/webp", "logo.webp")]
    public async Task UploadLogo_AcceptsImageContentType_AndSavesPathInDatabase(string contentType, string fileName)
    {
        var svc = await _dataFactory.ServiceAsync();
        var ctrl = BuildController();

        var result = await ctrl.UploadLogo(svc.Id, MakeFormFile(fileName: fileName, contentType: contentType), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        var dbRow = await _dbContext.WebResources.AsNoTracking().SingleAsync(s => s.Id == svc.Id);
        Assert.NotNull(dbRow.CustomLogoPath);
        Assert.Equal(LogoSource.Custom, dbRow.LogoSource);
    }

    [Fact]
    public async Task UploadLogo_TwoUploads_GenerateUniquePaths_InDatabase()
    {
        var svc1 = await _dataFactory.ServiceAsync(name: "S1");
        var svc2 = await _dataFactory.ServiceAsync(name: "S2");
        var ctrl = BuildController();

        await ctrl.UploadLogo(svc1.Id, MakeFormFile(), CancellationToken.None);
        await ctrl.UploadLogo(svc2.Id, MakeFormFile(), CancellationToken.None);

        var path1 = (await _dbContext.WebResources.AsNoTracking().SingleAsync(s => s.Id == svc1.Id)).CustomLogoPath;
        var path2 = (await _dbContext.WebResources.AsNoTracking().SingleAsync(s => s.Id == svc2.Id)).CustomLogoPath;
        Assert.NotEqual(path1, path2);
    }

    [Fact]
    public async Task UploadLogo_ReturnsBadRequest_WhenFileIsNull_AndDatabaseIsUnchanged()
    {
        var svc = await _dataFactory.ServiceAsync();
        var ctrl = BuildController();

        var result = await ctrl.UploadLogo(svc.Id, null!, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        var dbRow = await _dbContext.WebResources.AsNoTracking().SingleAsync(s => s.Id == svc.Id);
        Assert.Null(dbRow.CustomLogoPath);
        Assert.Equal(LogoSource.AutoFavicon, dbRow.LogoSource);
    }

    [Fact]
    public async Task UploadLogo_ReturnsBadRequest_WhenFileIsEmpty_AndDatabaseIsUnchanged()
    {
        var svc = await _dataFactory.ServiceAsync();
        var ctrl = BuildController();
        var emptyFile = new Mock<IFormFile>();
        emptyFile.Setup(f => f.Length).Returns(0);
        emptyFile.Setup(f => f.ContentType).Returns("image/png");

        var result = await ctrl.UploadLogo(svc.Id, emptyFile.Object, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        var dbRow = await _dbContext.WebResources.AsNoTracking().SingleAsync(s => s.Id == svc.Id);
        Assert.Null(dbRow.CustomLogoPath);
    }

    [Theory]
    [InlineData("application/pdf", "doc.pdf")]
    [InlineData("text/html", "page.html")]
    [InlineData("application/octet-stream", "binary.bin")]
    [InlineData("video/mp4", "video.mp4")]
    public async Task UploadLogo_ReturnsBadRequest_WhenNotAnImage_AndDatabaseIsUnchanged(string contentType, string fileName)
    {
        var svc = await _dataFactory.ServiceAsync();
        var ctrl = BuildController();

        var result = await ctrl.UploadLogo(svc.Id, MakeFormFile(fileName: fileName, contentType: contentType), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        var dbRow = await _dbContext.WebResources.AsNoTracking().SingleAsync(s => s.Id == svc.Id);
        Assert.Null(dbRow.CustomLogoPath);
        Assert.Equal(LogoSource.AutoFavicon, dbRow.LogoSource);
    }

    [Fact]
    public async Task UploadLogo_ReturnsNotFound_WhenServiceDoesNotExistInDatabase()
    {
        var nonExistentId = Guid.NewGuid();
        var ctrl = BuildController();

        var result = await ctrl.UploadLogo(nonExistentId, MakeFormFile(), CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
        Assert.False(await _dbContext.WebResources.AnyAsync(s => s.Id == nonExistentId));
    }

    [Fact]
    public async Task UploadLogo_ReturnsNotFound_AndLeavesOtherUsersRowUntouched()
    {
        var svc = await _dataFactory.ServiceAsync(userId: _otherUserId);
        var ctrl = BuildController();

        var result = await ctrl.UploadLogo(svc.Id, MakeFormFile(), CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);

        // Other user's row unchanged in DB
        var dbRow = await _dbContext.WebResources.AsNoTracking().SingleAsync(s => s.Id == svc.Id);
        Assert.Null(dbRow.CustomLogoPath);
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
