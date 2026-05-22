using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Stashboard.Api.Auth;
using Stashboard.Api.Contracts;
using Stashboard.Api.Controllers;
using Stashboard.Tests.Infrastructure;

namespace Stashboard.Tests.Controllers;

/// <summary>
/// Integration tests that verify registration by asserting the actual database state,
/// not just the HTTP response.
/// </summary>
public class RegistrationPersistenceTests : DatabaseTestBase
{
    private readonly Pbkdf2PasswordHasher _hasher = new();
    private readonly TestTimeProvider _time = new();
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

    // ── User row ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Register_CreatesExactlyOneUserRow()
    {
        await _ctrl.Register(new RegisterRequest("alice@x.com", "P@ssword1"), default);

        Assert.Equal(1, await _dbContext.Users.CountAsync());
    }

    [Fact]
    public async Task Register_UserRow_HasCorrectEmail()
    {
        await _ctrl.Register(new RegisterRequest("alice@x.com", "P@ssword1"), default);

        var user = await _dbContext.Users.AsNoTracking().SingleAsync();
        Assert.Equal("alice@x.com", user.Email);
    }

    [Fact]
    public async Task Register_UserRow_HasUpperInvariantNormalizedEmail()
    {
        await _ctrl.Register(new RegisterRequest("Alice@X.COM", "P@ssword1"), default);

        var user = await _dbContext.Users.AsNoTracking().SingleAsync();
        Assert.Equal("ALICE@X.COM", user.NormalizedEmail);
    }

    [Fact]
    public async Task Register_UserRow_PasswordHash_IsNotPlaintext()
    {
        await _ctrl.Register(new RegisterRequest("u@x", "P@ssword1"), default);

        var user = await _dbContext.Users.AsNoTracking().SingleAsync();
        Assert.NotEqual("P@ssword1", user.PasswordHash);
        Assert.StartsWith("pbkdf2-sha256$", user.PasswordHash);
    }

    [Fact]
    public async Task Register_UserRow_PasswordHash_VerifiesAgainstOriginalPassword()
    {
        await _ctrl.Register(new RegisterRequest("u@x", "P@ssword1"), default);

        var user = await _dbContext.Users.AsNoTracking().SingleAsync();
        Assert.True(_hasher.Verify("P@ssword1", user.PasswordHash));
    }

    [Fact]
    public async Task Register_UserRow_HasNonEmptySecurityStamp()
    {
        await _ctrl.Register(new RegisterRequest("u@x", "P@ssword1"), default);

        var user = await _dbContext.Users.AsNoTracking().SingleAsync();
        Assert.NotEmpty(user.SecurityStamp);
    }

    [Fact]
    public async Task Register_UserRow_HasNonDefaultId()
    {
        await _ctrl.Register(new RegisterRequest("u@x", "P@ssword1"), default);

        var user = await _dbContext.Users.AsNoTracking().SingleAsync();
        Assert.NotEqual(Guid.Empty, user.Id);
    }

    [Fact]
    public async Task Register_UserRow_FailedAccessCount_IsZero()
    {
        await _ctrl.Register(new RegisterRequest("u@x", "P@ssword1"), default);

        var user = await _dbContext.Users.AsNoTracking().SingleAsync();
        Assert.Equal(0, user.FailedAccessCount);
    }

    [Fact]
    public async Task Register_UserRow_IsNotLocked()
    {
        await _ctrl.Register(new RegisterRequest("u@x", "P@ssword1"), default);

        var user = await _dbContext.Users.AsNoTracking().SingleAsync();
        Assert.Null(user.LockoutEndUtc);
    }

    [Fact]
    public async Task Register_UserRow_CreatedUtc_IsSetToCurrentTime()
    {
        var before = _time.GetUtcNow().UtcDateTime;
        await _ctrl.Register(new RegisterRequest("u@x", "P@ssword1"), default);

        var user = await _dbContext.Users.AsNoTracking().SingleAsync();
        Assert.True(user.CreatedUtc >= before);
        Assert.True(user.CreatedUtc <= _time.GetUtcNow().UtcDateTime);
    }

    [Fact]
    public async Task Register_UserRow_ResponseId_MatchesDatabaseId()
    {
        var result = await _ctrl.Register(new RegisterRequest("u@x", "P@ssword1"), default);

        var body = (AuthResponse)((OkObjectResult)result.Result!).Value!;
        var user = await _dbContext.Users.AsNoTracking().SingleAsync();
        Assert.Equal(user.Id, body.User.Id);
    }

    // ── RefreshToken row ──────────────────────────────────────────────────────

    [Fact]
    public async Task Register_CreatesExactlyOneRefreshTokenRow()
    {
        await _ctrl.Register(new RegisterRequest("u@x", "P@ssword1"), default);

        Assert.Equal(1, await _dbContext.RefreshTokens.CountAsync());
    }

    [Fact]
    public async Task Register_RefreshTokenRow_IsLinkedToNewUser()
    {
        await _ctrl.Register(new RegisterRequest("u@x", "P@ssword1"), default);

        var user = await _dbContext.Users.AsNoTracking().SingleAsync();
        var token = await _dbContext.RefreshTokens.AsNoTracking().SingleAsync();
        Assert.Equal(user.Id, token.UserId);
    }

    [Fact]
    public async Task Register_RefreshTokenRow_TokenHash_IsNotPlaintext()
    {
        var result = await _ctrl.Register(new RegisterRequest("u@x", "P@ssword1"), default);

        var body = (AuthResponse)((OkObjectResult)result.Result!).Value!;
        var token = await _dbContext.RefreshTokens.AsNoTracking().SingleAsync();
        Assert.NotEqual(body.RefreshToken, token.TokenHash);
    }

    [Fact]
    public async Task Register_RefreshTokenRow_IsNotRevoked()
    {
        await _ctrl.Register(new RegisterRequest("u@x", "P@ssword1"), default);

        var token = await _dbContext.RefreshTokens.AsNoTracking().SingleAsync();
        Assert.Null(token.RevokedUtc);
        Assert.Null(token.RevokedReason);
    }

    [Fact]
    public async Task Register_RefreshTokenRow_HasNonEmptyFamilyId()
    {
        await _ctrl.Register(new RegisterRequest("u@x", "P@ssword1"), default);

        var token = await _dbContext.RefreshTokens.AsNoTracking().SingleAsync();
        Assert.NotEqual(Guid.Empty, token.FamilyId);
    }

    [Fact]
    public async Task Register_RefreshTokenRow_ExpiresUtc_IsInTheFuture()
    {
        await _ctrl.Register(new RegisterRequest("u@x", "P@ssword1"), default);

        var token = await _dbContext.RefreshTokens.AsNoTracking().SingleAsync();
        Assert.True(token.ExpiresUtc > _time.GetUtcNow().UtcDateTime);
    }

    [Fact]
    public async Task Register_RefreshTokenRow_SessionExpiresUtc_IsInTheFuture()
    {
        await _ctrl.Register(new RegisterRequest("u@x", "P@ssword1"), default);

        var token = await _dbContext.RefreshTokens.AsNoTracking().SingleAsync();
        Assert.True(token.SessionExpiresUtc > _time.GetUtcNow().UtcDateTime);
    }

    [Fact]
    public async Task Register_RefreshTokenRow_SessionExpiresUtc_NeverExceedsConfiguredSessionWindow()
    {
        await _ctrl.Register(new RegisterRequest("u@x", "P@ssword1"), default);

        var token = await _dbContext.RefreshTokens.AsNoTracking().SingleAsync();
        var maxSession = _time.GetUtcNow().UtcDateTime.AddDays(_opt.SessionMaxDays);
        Assert.True(token.SessionExpiresUtc <= maxSession);
    }

    // ── Duplicate registration ────────────────────────────────────────────────

    [Fact]
    public async Task Register_DuplicateEmail_DoesNotCreateSecondUserRow()
    {
        await _ctrl.Register(new RegisterRequest("u@x", "P@ssword1"), default);
        await _ctrl.Register(new RegisterRequest("u@x", "Different1!"), default);

        Assert.Equal(1, await _dbContext.Users.CountAsync());
    }

    [Fact]
    public async Task Register_DuplicateEmail_CaseInsensitive_DoesNotCreateSecondUserRow()
    {
        await _ctrl.Register(new RegisterRequest("Alice@X.COM", "P@ssword1"), default);
        await _ctrl.Register(new RegisterRequest("alice@x.com", "P@ssword1"), default);

        Assert.Equal(1, await _dbContext.Users.CountAsync());
    }

    [Fact]
    public async Task Register_DuplicateEmail_DoesNotCreateAdditionalRefreshTokenRow()
    {
        await _ctrl.Register(new RegisterRequest("u@x", "P@ssword1"), default);
        await _ctrl.Register(new RegisterRequest("u@x", "P@ssword1"), default);

        Assert.Equal(1, await _dbContext.RefreshTokens.CountAsync());
    }

    // ── Two independent users ─────────────────────────────────────────────────

    [Fact]
    public async Task Register_TwoDistinctEmails_CreatesTwoUserRows()
    {
        await _ctrl.Register(new RegisterRequest("alice@x", "P@ssword1"), default);
        await _ctrl.Register(new RegisterRequest("bob@x", "P@ssword1"), default);

        Assert.Equal(2, await _dbContext.Users.CountAsync());
    }

    [Fact]
    public async Task Register_TwoDistinctEmails_EachHasOwnRefreshTokenRow()
    {
        await _ctrl.Register(new RegisterRequest("alice@x", "P@ssword1"), default);
        await _ctrl.Register(new RegisterRequest("bob@x", "P@ssword1"), default);

        var tokens = await _dbContext.RefreshTokens.AsNoTracking().ToListAsync();
        Assert.Equal(2, tokens.Count);
        Assert.Equal(2, tokens.Select(t => t.UserId).Distinct().Count());
    }

    [Fact]
    public async Task Register_TwoDistinctEmails_HaveDifferentIds()
    {
        await _ctrl.Register(new RegisterRequest("alice@x", "P@ssword1"), default);
        await _ctrl.Register(new RegisterRequest("bob@x", "P@ssword1"), default);

        var ids = await _dbContext.Users.AsNoTracking().Select(u => u.Id).ToListAsync();
        Assert.Equal(2, ids.Distinct().Count());
    }
}
