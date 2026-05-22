using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Stashboard.Api.Auth;
using Stashboard.Api.Contracts;
using Stashboard.Api.Controllers;
using Stashboard.Tests.Infrastructure;

namespace Stashboard.Tests.Controllers;

public class TagsControllerTests : DatabaseTestBase
{
    private Guid _userId;
    private Guid _otherUserId;
    private TagsController _ctrl = default!;
    private DataFactory _factory = default!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        var hasher = new Pbkdf2PasswordHasher();
        var bootFactory = new DataFactory(_dbContext, new NoopEncryption(), hasher, Guid.Empty);
        _userId = (await bootFactory.UserAsync("owner@x")).Id;
        _otherUserId = (await bootFactory.UserAsync("other@x")).Id;
        _factory = new DataFactory(_dbContext, new NoopEncryption(), hasher, _userId);

        var identity = new ClaimsIdentity(
            new[] { new Claim(StashboardClaims.UserId, _userId.ToString()) }, "Test");
        _ctrl = new TagsController(_dbContext, TestMapperFactory.Create())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
            }
        };
    }

    [Fact]
    public async Task Create_PersistsWithCurrentUserId()
    {
        var result = await _ctrl.Create(new TagUpsertRequest("api"), default);

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        var body = Assert.IsType<TagResponse>(created.Value);
        Assert.Equal("api", body.Name);
    }

    [Fact]
    public async Task List_FiltersByOwner()
    {
        await _factory.TagAsync("mine", _userId);
        await _factory.TagAsync("theirs", _otherUserId);

        var result = await _ctrl.List(default);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var list = Assert.IsType<List<TagResponse>>(ok.Value);
        Assert.Single(list);
        Assert.Equal("mine", list[0].Name);
    }

    [Fact]
    public async Task Delete_OwnTag_RemovesRow()
    {
        var tag = await _factory.TagAsync("x", _userId);

        var result = await _ctrl.Delete(tag.Id, default);

        Assert.IsType<NoContentResult>(result);
    }

    [Fact]
    public async Task Delete_OtherUserTag_Returns404()
    {
        var tag = await _factory.TagAsync("x", _otherUserId);

        var result = await _ctrl.Delete(tag.Id, default);

        Assert.IsType<NotFoundResult>(result);
    }

    private sealed class NoopEncryption : Stashboard.Core.Abstractions.IEncryptionService
    {
        public string Encrypt(string plain) => plain;
        public string Decrypt(string cipher) => cipher;
    }
}
