using Microsoft.Extensions.Options;
using Stashboard.Api.Auth;
using Stashboard.Api.Data;
using Stashboard.Tests.Infrastructure;

namespace Stashboard.Tests.Auth;

public class UserServiceTests : DatabaseTestBase
{
    private readonly TestTimeProvider _time = new();
    private readonly Pbkdf2PasswordHasher _hasher = new();
    private UserService _sut = default!;

    private readonly JwtOptions _opt = new()
    {
        Secret = "test-secret-test-secret-test-secret-test-secret",
        MaxFailedAccessAttempts = 3,
        LockoutMinutes = 10,
    };

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        _sut = new UserService(_dbContext, _hasher, Options.Create(_opt), _time);
    }

    [Fact]
    public async Task Register_NewEmail_PersistsUserAndReturnsSuccess()
    {
        var result = await _sut.RegisterAsync("Alice@Test.LOCAL", "P@ssword1");

        Assert.True(result.Succeeded);
        Assert.NotNull(result.User);
        Assert.Equal("Alice@Test.LOCAL", result.User!.Email);
        Assert.Equal("ALICE@TEST.LOCAL", result.User.NormalizedEmail);
        Assert.True(_hasher.Verify("P@ssword1", result.User.PasswordHash));

        Assert.Equal(1, _dbContext.Users.Count());
    }

    [Fact]
    public async Task Register_DuplicateEmail_CaseInsensitive_ReturnsFailure()
    {
        await _sut.RegisterAsync("user@x.com", "P@ssword1");

        var result = await _sut.RegisterAsync("USER@x.com", "P@ssword1");

        Assert.False(result.Succeeded);
        Assert.Equal(AuthFailureReason.EmailAlreadyTaken, result.Failure!.Reason);
        Assert.Equal(1, _dbContext.Users.Count());
    }

    [Fact]
    public async Task ValidatePassword_WithCorrectCredentials_ReturnsUserAndUpdatesLastLogin()
    {
        await _sut.RegisterAsync("u@x", "P@ssword1");
        _time.Advance(TimeSpan.FromHours(2));

        var result = await _sut.ValidatePasswordAsync("u@x", "P@ssword1");

        Assert.True(result.Succeeded);
        Assert.NotNull(result.User!.LastLoginUtc);
        Assert.Equal(_time.GetUtcNow().UtcDateTime, result.User.LastLoginUtc);
        Assert.Equal(0, result.User.FailedAccessCount);
    }

    [Fact]
    public async Task ValidatePassword_WithMissingUser_ReturnsInvalidCredentials()
    {
        var result = await _sut.ValidatePasswordAsync("missing@x", "anything");

        Assert.False(result.Succeeded);
        Assert.Equal(AuthFailureReason.InvalidCredentials, result.Failure!.Reason);
    }

    [Fact]
    public async Task ValidatePassword_WithWrongPassword_IncrementsFailedCount()
    {
        await _sut.RegisterAsync("u@x", "P@ssword1");

        var r = await _sut.ValidatePasswordAsync("u@x", "wrong");

        Assert.False(r.Succeeded);
        var stored = _dbContext.Users.AsQueryable().Single();
        Assert.Equal(1, stored.FailedAccessCount);
        Assert.Null(stored.LockoutEndUtc);
    }

    [Fact]
    public async Task ValidatePassword_AfterMaxFailedAttempts_LocksAccount()
    {
        await _sut.RegisterAsync("u@x", "P@ssword1");

        await _sut.ValidatePasswordAsync("u@x", "wrong");
        await _sut.ValidatePasswordAsync("u@x", "wrong");
        var third = await _sut.ValidatePasswordAsync("u@x", "wrong");

        Assert.False(third.Succeeded);
        Assert.Equal(AuthFailureReason.AccountLocked, third.Failure!.Reason);

        var stored = _dbContext.Users.AsQueryable().Single();
        Assert.NotNull(stored.LockoutEndUtc);
        Assert.Equal(0, stored.FailedAccessCount); // counter reset on lockout
    }

    [Fact]
    public async Task ValidatePassword_WhileLocked_ReturnsLockedEvenWithCorrectPassword()
    {
        await _sut.RegisterAsync("u@x", "P@ssword1");
        // Lock the account
        for (int i = 0; i < 3; i++) await _sut.ValidatePasswordAsync("u@x", "wrong");

        var r = await _sut.ValidatePasswordAsync("u@x", "P@ssword1");

        Assert.False(r.Succeeded);
        Assert.Equal(AuthFailureReason.AccountLocked, r.Failure!.Reason);
    }

    [Fact]
    public async Task ValidatePassword_AfterLockoutExpires_AllowsLogin()
    {
        await _sut.RegisterAsync("u@x", "P@ssword1");
        for (int i = 0; i < 3; i++) await _sut.ValidatePasswordAsync("u@x", "wrong");

        _time.Advance(TimeSpan.FromMinutes(11));

        var r = await _sut.ValidatePasswordAsync("u@x", "P@ssword1");

        Assert.True(r.Succeeded);
        Assert.Null(r.User!.LockoutEndUtc);
    }

    [Fact]
    public async Task RotateSecurityStampAsync_ChangesStamp()
    {
        var reg = await _sut.RegisterAsync("u@x", "P@ssword1");
        var original = reg.User!.SecurityStamp;

        await _sut.RotateSecurityStampAsync(reg.User.Id);

        var reloaded = await _sut.FindByIdAsync(reg.User.Id);
        Assert.NotNull(reloaded);
        Assert.NotEqual(original, reloaded!.SecurityStamp);
    }

    [Fact]
    public async Task FindByEmail_IsCaseInsensitive()
    {
        await _sut.RegisterAsync("Alice@x", "P@ssword1");
        Assert.NotNull(await _sut.FindByEmailAsync("ALICE@X"));
    }

    [Fact]
    public async Task ValidatePassword_EmptyInputs_ReturnInvalidCredentials()
    {
        var a = await _sut.ValidatePasswordAsync("", "p");
        var b = await _sut.ValidatePasswordAsync("e", "");
        Assert.Equal(AuthFailureReason.InvalidCredentials, a.Failure!.Reason);
        Assert.Equal(AuthFailureReason.InvalidCredentials, b.Failure!.Reason);
    }
}
