using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Stashboard.Api.Auth;
using Stashboard.Api.Contracts;
using Stashboard.Api.Controllers;
using Stashboard.Tests.Infrastructure;

namespace Stashboard.Tests.Controllers.StatusPages;

/// <summary>
/// V10.2 — owner-scoped CRUD for public status pages: create with auto / explicit slug,
/// slug uniqueness, rejecting a foreign service, owner isolation and publish state.
/// </summary>
public class StatusPagesControllerTests : DatabaseTestBase
{
    private Guid _userId;
    private Guid _otherUserId;
    private DataFactory _factory = default!;
    private StatusPagesController _ctrl = default!;

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        await _dbContext.StatusPageItems.ExecuteDeleteAsync();
        await _dbContext.StatusPages.ExecuteDeleteAsync();

        var hasher = new Pbkdf2PasswordHasher();
        _factory = new DataFactory(_dbContext, new NoopEncryption(), hasher, Guid.Empty);
        _userId = (await _factory.UserAsync("owner@x")).Id;
        _otherUserId = (await _factory.UserAsync("other@x")).Id;
        _ctrl = new StatusPagesController(_dbContext) { ControllerContext = BuildContext(_userId) };
    }

    [Fact]
    public async Task Create_DerivesSlugFromTitle_WhenBlank()
    {
        var svc = await _factory.ServiceAsync(_userId, "Jellyfin");

        var result = await _ctrl.Create(
            new StatusPageUpsertRequest("My Homelab", null, null, false,
                [new StatusPageItemUpsert(svc.Id, null)]), default);

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        var body = Assert.IsType<StatusPageResponse>(created.Value);
        Assert.Equal("my-homelab", body.Slug);
        Assert.False(body.IsPublished);
        Assert.Single(body.Items);
        Assert.Equal(svc.Id, body.Items[0].WebResourceId);
    }

    [Fact]
    public async Task Create_RejectsForeignService()
    {
        var theirs = await _factory.ServiceAsync(_otherUserId, "Not Yours");

        var result = await _ctrl.Create(
            new StatusPageUpsertRequest("Page", null, "page", true,
                [new StatusPageItemUpsert(theirs.Id, null)]), default);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task Create_RejectsDuplicateSlug()
    {
        await _ctrl.Create(new StatusPageUpsertRequest("First", null, "shared", false, []), default);

        var result = await _ctrl.Create(
            new StatusPageUpsertRequest("Second", null, "shared", false, []), default);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task List_OnlyReturnsCurrentUserPages()
    {
        await _ctrl.Create(new StatusPageUpsertRequest("Mine", null, "mine", false, []), default);
        // A page owned by the other user must not appear.
        var otherCtrl = new StatusPagesController(_dbContext) { ControllerContext = BuildContext(_otherUserId) };
        await otherCtrl.Create(new StatusPageUpsertRequest("Theirs", null, "theirs", false, []), default);

        var result = await _ctrl.List(default);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var list = Assert.IsType<List<StatusPageResponse>>(ok.Value);
        Assert.Single(list);
        Assert.Equal("Mine", list[0].Title);
    }

    [Fact]
    public async Task Update_TogglesPublishAndReplacesItems()
    {
        var a = await _factory.ServiceAsync(_userId, "A");
        var b = await _factory.ServiceAsync(_userId, "B");
        var created = (StatusPageResponse)((CreatedAtActionResult)(await _ctrl.Create(
            new StatusPageUpsertRequest("Page", null, "page", false,
                [new StatusPageItemUpsert(a.Id, "Alpha")]), default)).Result!).Value!;

        var result = await _ctrl.Update(created.Id,
            new StatusPageUpsertRequest("Page", "now public", "page", true,
                [new StatusPageItemUpsert(b.Id, null)]), default);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var body = Assert.IsType<StatusPageResponse>(ok.Value);
        Assert.True(body.IsPublished);
        Assert.Equal("now public", body.Description);
        var item = Assert.Single(body.Items);
        Assert.Equal(b.Id, item.WebResourceId);
    }

    [Fact]
    public async Task Update_OtherUserPage_Returns404()
    {
        var otherCtrl = new StatusPagesController(_dbContext) { ControllerContext = BuildContext(_otherUserId) };
        var theirs = (StatusPageResponse)((CreatedAtActionResult)(await otherCtrl.Create(
            new StatusPageUpsertRequest("Theirs", null, "theirs", false, []), default)).Result!).Value!;

        var result = await _ctrl.Update(theirs.Id,
            new StatusPageUpsertRequest("Hacked", null, "hacked", true, []), default);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task Delete_OwnPage_RemovesRow()
    {
        var created = (StatusPageResponse)((CreatedAtActionResult)(await _ctrl.Create(
            new StatusPageUpsertRequest("Page", null, "page", false, []), default)).Result!).Value!;

        var result = await _ctrl.Delete(created.Id, default);

        Assert.IsType<NoContentResult>(result);
        Assert.False(await _dbContext.StatusPages.AnyAsync(p => p.Id == created.Id));
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
