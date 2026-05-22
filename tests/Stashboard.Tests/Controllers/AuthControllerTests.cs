using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Stashboard.Api.Auth;
using Stashboard.Api.Contracts;
using Stashboard.Api.Controllers;
using Stashboard.Api.Data;
using Stashboard.Tests.Infrastructure;

namespace Stashboard.Tests.Controllers;

public class AuthControllerTests : DatabaseTestBase
{
    private readonly TestTimeProvider _time = new();
    private readonly Pbkdf2PasswordHasher _hasher = new();
    private readonly JwtOptions _opt = new()
    {
        Secret = "test-secret-test-secret-test-secret-test-secret",
        MaxFailedAccessAttempts = 3,
        LockoutMinutes = 10,
    };

    private AuthController _ctrl = default!;
    private UserService _users = default!;
    private TokenService _tokens = default!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        _users = new UserService(_dbContext, _hasher, Options.Create(_opt), _time);
        _tokens = new TokenService(Options.Create(_opt), _dbContext, _time, NullLogger<TokenService>.Instance);
        _ctrl = new AuthController(_users, _tokens, TestMapperFactory.Create(), Options.Create(_opt));
    }

    private void SignIn(Guid userId)
    {
        var identity = new ClaimsIdentity(
            new[] { new Claim(StashboardClaims.UserId, userId.ToString()) }, "Test");
        _ctrl.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };
    }

    [Fact]
    public async Task Register_NewEmail_Returns200WithTokens()
    {
        var result = await _ctrl.Register(new RegisterRequest("u@x", "P@ssword1"), default);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var body = Assert.IsType<AuthResponse>(ok.Value);
        Assert.Equal("u@x", body.User.Email);
        Assert.NotEmpty(body.AccessToken);
        Assert.NotEmpty(body.RefreshToken);
    }

    [Fact]
    public async Task Register_DuplicateEmail_Returns409()
    {
        await _ctrl.Register(new RegisterRequest("u@x", "P@ssword1"), default);

        var result = await _ctrl.Register(new RegisterRequest("u@x", "P@ssword1"), default);

        Assert.IsType<ConflictObjectResult>(result.Result);
    }

    [Fact]
    public async Task Login_Valid_Returns200()
    {
        await _ctrl.Register(new RegisterRequest("u@x", "P@ssword1"), default);

        var result = await _ctrl.Login(new LoginRequest("u@x", "P@ssword1"), default);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task Login_WrongPassword_Returns401()
    {
        await _ctrl.Register(new RegisterRequest("u@x", "P@ssword1"), default);

        var result = await _ctrl.Login(new LoginRequest("u@x", "wrong"), default);

        Assert.IsType<UnauthorizedObjectResult>(result.Result);
    }

    [Fact]
    public async Task Login_LockedAccount_Returns423()
    {
        await _ctrl.Register(new RegisterRequest("u@x", "P@ssword1"), default);
        for (int i = 0; i < 3; i++)
            await _ctrl.Login(new LoginRequest("u@x", "wrong"), default);

        var result = await _ctrl.Login(new LoginRequest("u@x", "P@ssword1"), default);

        var status = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status423Locked, status.StatusCode);
    }

    [Fact]
    public async Task Refresh_ValidToken_Returns200WithNewPair()
    {
        var reg = await _ctrl.Register(new RegisterRequest("u@x", "P@ssword1"), default);
        var registered = (AuthResponse)((OkObjectResult)reg.Result!).Value!;

        var result = await _ctrl.Refresh(new RefreshRequest(registered.RefreshToken), default);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var body = Assert.IsType<AuthResponse>(ok.Value);
        Assert.NotEqual(registered.RefreshToken, body.RefreshToken);
    }

    [Fact]
    public async Task Refresh_ReusedToken_Returns401()
    {
        var reg = await _ctrl.Register(new RegisterRequest("u@x", "P@ssword1"), default);
        var registered = (AuthResponse)((OkObjectResult)reg.Result!).Value!;
        await _ctrl.Refresh(new RefreshRequest(registered.RefreshToken), default);

        // Replay
        var result = await _ctrl.Refresh(new RefreshRequest(registered.RefreshToken), default);

        Assert.IsType<UnauthorizedObjectResult>(result.Result);
    }

    [Fact]
    public async Task Refresh_UnknownToken_Returns401()
    {
        var result = await _ctrl.Refresh(new RefreshRequest("nope"), default);
        Assert.IsType<UnauthorizedObjectResult>(result.Result);
    }

    [Fact]
    public async Task Logout_RevokesEntireFamily()
    {
        var reg = await _ctrl.Register(new RegisterRequest("u@x", "P@ssword1"), default);
        var registered = (AuthResponse)((OkObjectResult)reg.Result!).Value!;
        SignIn(registered.User.Id);

        var result = await _ctrl.Logout(new RefreshRequest(registered.RefreshToken), default);

        Assert.IsType<NoContentResult>(result);
        var rotated = await _ctrl.Refresh(new RefreshRequest(registered.RefreshToken), default);
        Assert.IsType<UnauthorizedObjectResult>(rotated.Result);
    }

    [Fact]
    public async Task LogoutAll_RotatesSecurityStampAndRevokesAllTokens()
    {
        var reg = await _ctrl.Register(new RegisterRequest("u@x", "P@ssword1"), default);
        var registered = (AuthResponse)((OkObjectResult)reg.Result!).Value!;
        var originalStamp = (await _users.FindByIdAsync(registered.User.Id))!.SecurityStamp;

        SignIn(registered.User.Id);
        var result = await _ctrl.LogoutAll(default);

        Assert.IsType<NoContentResult>(result);
        var reloaded = await _users.FindByIdAsync(registered.User.Id);
        Assert.NotEqual(originalStamp, reloaded!.SecurityStamp);

        var attempt = await _ctrl.Refresh(new RefreshRequest(registered.RefreshToken), default);
        Assert.IsType<UnauthorizedObjectResult>(attempt.Result);
    }

    [Fact]
    public async Task Me_Authenticated_ReturnsCurrentUser()
    {
        var reg = await _ctrl.Register(new RegisterRequest("u@x", "P@ssword1"), default);
        var registered = (AuthResponse)((OkObjectResult)reg.Result!).Value!;
        SignIn(registered.User.Id);

        var result = await _ctrl.Me(default);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var user = Assert.IsType<UserResponse>(ok.Value);
        Assert.Equal(registered.User.Id, user.Id);
        Assert.Equal("u@x", user.Email);
    }

    [Fact]
    public async Task Me_DeletedUser_ReturnsUnauthorized()
    {
        var fakeId = Guid.NewGuid();
        SignIn(fakeId);

        var result = await _ctrl.Me(default);

        Assert.IsType<UnauthorizedResult>(result.Result);
    }
}
