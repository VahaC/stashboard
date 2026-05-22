using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Stashboard.Tests.Controllers.WebResources;

public class DeleteEndpointTests : WebResourcesControllerTestBase
{
    [Fact]
    public async Task Delete_ReturnsNoContent_AndRowIsGoneFromDatabase()
    {
        var svc = await _dataFactory.ServiceAsync();
        var ctrl = BuildController();

        var result = await ctrl.Delete(svc.Id, CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        Assert.False(await _dbContext.WebResources.AnyAsync(s => s.Id == svc.Id));
    }

    [Fact]
    public async Task Delete_DatabaseContainsZeroServices_AfterDeletingTheOnlyOne()
    {
        var svc = await _dataFactory.ServiceAsync();
        var ctrl = BuildController();

        await ctrl.Delete(svc.Id, CancellationToken.None);

        Assert.Equal(0, await _dbContext.WebResources.CountAsync());
    }

    [Fact]
    public async Task Delete_RemovesCredentialRows_FromDatabase()
    {
        var svc = await _dataFactory.ServiceAsync();
        await _dataFactory.AttachCredentialAsync(svc, "KEY1", "val1");
        await _dataFactory.AttachCredentialAsync(svc, "KEY2", "val2");
        Assert.Equal(2, await _dbContext.Credentials.CountAsync(c => c.WebResourceId == svc.Id));

        var ctrl = BuildController();
        await ctrl.Delete(svc.Id, CancellationToken.None);

        Assert.Equal(0, await _dbContext.Credentials.CountAsync(c => c.WebResourceId == svc.Id));
    }

    [Fact]
    public async Task Delete_RemovesServiceTagRows_FromDatabase()
    {
        var svc = await _dataFactory.ServiceAsync();
        var t1 = await _dataFactory.TagAsync("tag1");
        var t2 = await _dataFactory.TagAsync("tag2");
        await _dataFactory.AttachTagAsync(svc, t1);
        await _dataFactory.AttachTagAsync(svc, t2);
        Assert.Equal(2, await _dbContext.WebResourceTags.CountAsync(st => st.WebResourceId == svc.Id));

        var ctrl = BuildController();
        await ctrl.Delete(svc.Id, CancellationToken.None);

        Assert.Equal(0, await _dbContext.WebResourceTags.CountAsync(st => st.WebResourceId == svc.Id));
        // Tag definitions themselves are kept
        Assert.True(await _dbContext.Tags.AnyAsync(t => t.Name == "tag1"));
        Assert.True(await _dbContext.Tags.AnyAsync(t => t.Name == "tag2"));
    }

    [Fact]
    public async Task Delete_OnlyDeletesOwnService_LeavesOtherUserRowIntact()
    {
        var other = await _dataFactory.ServiceAsync(userId: _otherUserId, name: "Other");
        var mine = await _dataFactory.ServiceAsync(name: "Mine");
        var ctrl = BuildController();

        await ctrl.Delete(mine.Id, CancellationToken.None);

        Assert.False(await _dbContext.WebResources.AnyAsync(s => s.Id == mine.Id));
        Assert.True(await _dbContext.WebResources.AnyAsync(s => s.Id == other.Id));
        var otherRow = await _dbContext.WebResources.AsNoTracking().SingleAsync(s => s.Id == other.Id);
        Assert.Equal(_otherUserId, otherRow.UserId);
    }

    [Fact]
    public async Task Delete_ReturnsNotFound_WhenServiceDoesNotExistInDatabase()
    {
        var nonExistentId = Guid.NewGuid();
        var ctrl = BuildController();

        var result = await ctrl.Delete(nonExistentId, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
        Assert.Equal(0, await _dbContext.WebResources.CountAsync());
    }

    [Fact]
    public async Task Delete_ReturnsNotFound_AndRowRemainsInDatabase_WhenOwnedByAnotherUser()
    {
        var svc = await _dataFactory.ServiceAsync(userId: _otherUserId);
        var ctrl = BuildController();

        var result = await ctrl.Delete(svc.Id, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
        // Row still exists and still belongs to other user
        var dbRow = await _dbContext.WebResources.AsNoTracking().SingleAsync(s => s.Id == svc.Id);
        Assert.Equal(_otherUserId, dbRow.UserId);
    }

    [Fact]
    public async Task Delete_ReturnsNotFound_ForEmptyGuid_AndDatabaseIsUnchanged()
    {
        await _dataFactory.ServiceAsync(name: "Safe");
        var ctrl = BuildController();

        var result = await ctrl.Delete(Guid.Empty, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
        Assert.Equal(1, await _dbContext.WebResources.CountAsync());
    }
}
