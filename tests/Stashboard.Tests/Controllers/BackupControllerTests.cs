using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Stashboard.Api.Auth;
using Stashboard.Api.Controllers;
using Stashboard.Core.Abstractions;

namespace Stashboard.Tests.Controllers;

public class BackupControllerTests
{
    private readonly Mock<IBackupService> _backup = new();
    private readonly BackupController _ctrl;
    private readonly Guid _userId = Guid.NewGuid();

    public BackupControllerTests()
    {
        _ctrl = new BackupController(_backup.Object);
        var identity = new ClaimsIdentity(
            new[] { new Claim(StashboardClaims.UserId, _userId.ToString()) }, "Test");
        _ctrl.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };
    }

    [Fact]
    public async Task Export_ReturnsFileResultWithBytesFromService()
    {
        var payload = Encoding.UTF8.GetBytes("{\"x\":1}");
        _backup.Setup(b => b.ExportAsync(_userId, It.IsAny<CancellationToken>())).ReturnsAsync(payload);

        var result = await _ctrl.Export(default);

        var file = Assert.IsType<FileContentResult>(result);
        Assert.Equal("application/json", file.ContentType);
        Assert.Equal(payload, file.FileContents);
    }

    [Fact]
    public async Task Import_NullFile_Returns400()
    {
        var result = await _ctrl.Import(null!, default);
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task Import_EmptyFile_Returns400()
    {
        var file = new Mock<IFormFile>();
        file.SetupGet(f => f.Length).Returns(0);

        var result = await _ctrl.Import(file.Object, default);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task Import_ValidFile_DelegatesToBackupServiceAndReturnsCount()
    {
        var bytes = Encoding.UTF8.GetBytes("{}");
        var file = new Mock<IFormFile>();
        file.SetupGet(f => f.Length).Returns(bytes.Length);
        file.Setup(f => f.OpenReadStream()).Returns(new MemoryStream(bytes));
        _backup.Setup(b => b.ImportAsync(_userId, It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(7);

        var result = await _ctrl.Import(file.Object, default);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(7, ok.Value!.GetType().GetProperty("imported")!.GetValue(ok.Value));
    }
}


