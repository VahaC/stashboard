using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Stashboard.Api.Contracts;
using Stashboard.Tests.Infrastructure;

namespace Stashboard.Tests.Controllers.DockerWatches;

/// <summary>
/// V3.6 — endpoint tests for the connection-scoped <c>DockerConnectionWatchesController</c>,
/// the primary surface the Docker page uses to track containers as first-class
/// objects (with an optional service link).
/// </summary>
public class ConnectionWatchEndpointTests : DockerWatchesControllerTestBase
{
    [Fact]
    public async Task Create_Standalone_PersistsWithNullService()
    {
        var conn = await SeedConnectionAsync(_userId);
        var ctrl = BuildConnectionWatchesController();

        var result = await ctrl.Create(conn.Id, CreateWatchEndpointTests.DefaultRequest(image: "nginx:latest"), CancellationToken.None);

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        var body = Assert.IsType<DockerWatchResponse>(created.Value);
        Assert.Equal(conn.Id, body.DockerConnectionId);
        Assert.Null(body.WebResourceId);

        var stored = await _dbContext.DockerWatches.AsNoTracking().SingleAsync(w => w.Id == body.Id);
        Assert.Equal(conn.Id, stored.DockerConnectionId);
        Assert.Null(stored.WebResourceId);
    }

    [Fact]
    public async Task Create_LinkedToService_SetsWebResourceId()
    {
        var conn = await SeedConnectionAsync(_userId);
        var svc = await _dataFactory.ServiceAsync(name: "Linked");
        var ctrl = BuildConnectionWatchesController();

        var request = CreateWatchEndpointTests.DefaultRequest(image: "nginx:latest") with { WebResourceId = svc.Id };
        var result = await ctrl.Create(conn.Id, request, CancellationToken.None);

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        var body = Assert.IsType<DockerWatchResponse>(created.Value);
        Assert.Equal(svc.Id, body.WebResourceId);
        Assert.Equal(conn.Id, body.DockerConnectionId);
    }

    [Fact]
    public async Task Create_DuplicateContainerOnConnection_ReturnsConflict()
    {
        var conn = await SeedConnectionAsync(_userId);
        var ctrl = BuildConnectionWatchesController();

        await ctrl.Create(conn.Id, CreateWatchEndpointTests.DefaultRequest(image: "nginx:latest", containerName: "web"), CancellationToken.None);
        var result = await ctrl.Create(conn.Id, CreateWatchEndpointTests.DefaultRequest(image: "nginx:1.27", containerName: "web"), CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(result.Result);
    }

    [Fact]
    public async Task Create_LinkToForeignService_ReturnsBadRequest()
    {
        var conn = await SeedConnectionAsync(_userId);
        var foreignSvc = await _dataFactory.ServiceAsync(userId: _otherUserId);
        var ctrl = BuildConnectionWatchesController();

        var request = CreateWatchEndpointTests.DefaultRequest(image: "nginx:latest") with { WebResourceId = foreignSvc.Id };
        var result = await ctrl.Create(conn.Id, request, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task Create_OnForeignConnection_ReturnsNotFound()
    {
        var foreignConn = await SeedConnectionAsync(_otherUserId);
        var ctrl = BuildConnectionWatchesController();

        var result = await ctrl.Create(foreignConn.Id, CreateWatchEndpointTests.DefaultRequest(image: "nginx:latest"), CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task List_ReturnsConnectionWatches()
    {
        var conn = await SeedConnectionAsync(_userId);
        var ctrl = BuildConnectionWatchesController();
        await ctrl.Create(conn.Id, CreateWatchEndpointTests.DefaultRequest(image: "nginx:latest", containerName: "a"), CancellationToken.None);
        await ctrl.Create(conn.Id, CreateWatchEndpointTests.DefaultRequest(image: "redis:7", containerName: "b"), CancellationToken.None);

        var result = await ctrl.List(conn.Id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var list = Assert.IsType<List<DockerWatchResponse>>(ok.Value);
        Assert.Equal(2, list.Count);
        Assert.All(list, w => Assert.Equal(conn.Id, w.DockerConnectionId));
    }

    [Fact]
    public async Task UnlinkService_ClearsWebResourceId()
    {
        var conn = await SeedConnectionAsync(_userId);
        var svc = await _dataFactory.ServiceAsync(name: "Linked");
        var ctrl = BuildConnectionWatchesController();

        var request = CreateWatchEndpointTests.DefaultRequest(image: "nginx:latest") with { WebResourceId = svc.Id };
        var created = (CreatedAtActionResult)(await ctrl.Create(conn.Id, request, CancellationToken.None)).Result!;
        var watchId = ((DockerWatchResponse)created.Value!).Id;

        var result = await ctrl.UnlinkService(conn.Id, watchId, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var body = Assert.IsType<DockerWatchResponse>(ok.Value);
        Assert.Null(body.WebResourceId);

        var stored = await _dbContext.DockerWatches.AsNoTracking().SingleAsync(w => w.Id == watchId);
        Assert.Null(stored.WebResourceId);
    }

    [Fact]
    public async Task UnlinkService_AlreadyStandalone_ReturnsOk()
    {
        var conn = await SeedConnectionAsync(_userId);
        var ctrl = BuildConnectionWatchesController();

        var created = (CreatedAtActionResult)(await ctrl.Create(
            conn.Id, CreateWatchEndpointTests.DefaultRequest(image: "nginx:latest"), CancellationToken.None)).Result!;
        var watchId = ((DockerWatchResponse)created.Value!).Id;

        var result = await ctrl.UnlinkService(conn.Id, watchId, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var body = Assert.IsType<DockerWatchResponse>(ok.Value);
        Assert.Null(body.WebResourceId);
    }

    [Fact]
    public async Task UnlinkService_ForeignConnection_ReturnsNotFound()
    {
        var foreignConn = await SeedConnectionAsync(_otherUserId);
        var ctrl = BuildConnectionWatchesController();

        var result = await ctrl.UnlinkService(foreignConn.Id, Guid.NewGuid(), CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task Delete_RemovesWatch()
    {
        var conn = await SeedConnectionAsync(_userId);
        var ctrl = BuildConnectionWatchesController();
        var created = (CreatedAtActionResult)(await ctrl.Create(
            conn.Id, CreateWatchEndpointTests.DefaultRequest(image: "nginx:latest"), CancellationToken.None)).Result!;
        var watchId = ((DockerWatchResponse)created.Value!).Id;

        var result = await ctrl.Delete(conn.Id, watchId, CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        Assert.False(await _dbContext.DockerWatches.AnyAsync(w => w.Id == watchId));
    }
}


