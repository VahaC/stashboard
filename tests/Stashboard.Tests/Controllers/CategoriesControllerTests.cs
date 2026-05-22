using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Stashboard.Api.Auth;
using Stashboard.Api.Contracts;
using Stashboard.Api.Controllers;
using Stashboard.Api.Data;
using Stashboard.Api.Auth;
using Stashboard.Tests.Infrastructure;

namespace Stashboard.Tests.Controllers;

public class CategoriesControllerTests : DatabaseTestBase
{
    private Guid _userId;
    private Guid _otherUserId;
    private CategoriesController _ctrl = default!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        var hasher = new Pbkdf2PasswordHasher();
        var factory = new DataFactory(_dbContext, new NoopEncryption(), hasher, Guid.Empty);
        _userId = (await factory.UserAsync("owner@x")).Id;
        _otherUserId = (await factory.UserAsync("other@x")).Id;
        _ctrl = new CategoriesController(_dbContext, TestMapperFactory.Create()) { ControllerContext = BuildContext(_userId) };
    }

    [Fact]
    public async Task Create_PersistsCategoryWithUserId()
    {
        var result = await _ctrl.Create(new CategoryUpsertRequest("Web", "#fff"), default);

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        var body = Assert.IsType<CategoryResponse>(created.Value);
        Assert.Equal("Web", body.Name);
    }

    [Fact]
    public async Task List_OnlyReturnsCurrentUserCategories()
    {
        var hasher = new Pbkdf2PasswordHasher();
        var factory = new DataFactory(_dbContext, new NoopEncryption(), hasher, _userId);
        await factory.CategoryAsync(_userId, "Mine");
        await factory.CategoryAsync(_otherUserId, "Theirs");

        var result = await _ctrl.List(default);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var list = Assert.IsType<List<CategoryResponse>>(ok.Value);
        Assert.Single(list);
        Assert.Equal("Mine", list[0].Name);
    }

    [Fact]
    public async Task Update_OtherUserCategory_Returns404()
    {
        var hasher = new Pbkdf2PasswordHasher();
        var factory = new DataFactory(_dbContext, new NoopEncryption(), hasher, _otherUserId);
        var theirs = await factory.CategoryAsync(_otherUserId, "Theirs");

        var result = await _ctrl.Update(theirs.Id, new CategoryUpsertRequest("Hacked", "#000"), default);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task Update_OwnCategory_UpdatesNameAndColor()
    {
        var hasher = new Pbkdf2PasswordHasher();
        var factory = new DataFactory(_dbContext, new NoopEncryption(), hasher, _userId);
        var mine = await factory.CategoryAsync(_userId, "Old", "#aaa");

        var result = await _ctrl.Update(mine.Id, new CategoryUpsertRequest("New", "#bbb"), default);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var body = Assert.IsType<CategoryResponse>(ok.Value);
        Assert.Equal("New", body.Name);
        Assert.Equal("#bbb", body.Color);

        var persisted = await _dbContext.Categories.AsNoTracking().SingleAsync(c => c.Id == mine.Id);
        Assert.Equal("New", persisted.Name);
        Assert.Equal("#bbb", persisted.Color);
    }

    [Fact]
    public async Task Delete_OwnCategory_RemovesRow()
    {
        var hasher = new Pbkdf2PasswordHasher();
        var factory = new DataFactory(_dbContext, new NoopEncryption(), hasher, _userId);
        var mine = await factory.CategoryAsync(_userId);

        var result = await _ctrl.Delete(mine.Id, default);

        Assert.IsType<NoContentResult>(result);
        Assert.False(_dbContext.Categories.Any(c => c.Id == mine.Id));
    }

    [Fact]
    public async Task Delete_OtherUserCategory_Returns404()
    {
        var hasher = new Pbkdf2PasswordHasher();
        var factory = new DataFactory(_dbContext, new NoopEncryption(), hasher, _otherUserId);
        var theirs = await factory.CategoryAsync(_otherUserId);

        var result = await _ctrl.Delete(theirs.Id, default);

        Assert.IsType<NotFoundResult>(result);
    }

    private static ControllerContext BuildContext(Guid userId)
    {
        var identity = new ClaimsIdentity(
            new[] { new Claim(StashboardClaims.UserId, userId.ToString()) }, "Test");
        return new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };
    }

    private sealed class NoopEncryption : Stashboard.Core.Abstractions.IEncryptionService
    {
        public string Encrypt(string plain) => plain;
        public string Decrypt(string cipher) => cipher;
    }
}
