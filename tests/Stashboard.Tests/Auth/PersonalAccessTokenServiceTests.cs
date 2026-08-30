using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Stashboard.Api.Auth;
using Stashboard.Api.Auth.PersonalAccessTokens;
using Stashboard.Api.Data;
using Stashboard.Tests.Infrastructure;

namespace Stashboard.Tests.Auth;

public class PersonalAccessTokenServiceTests : DatabaseTestBase
{
    private readonly TestTimeProvider _time = new();
    private readonly JwtOptions _opt = new() { Secret = "test-secret-test-secret-test-secret-test-secret" };
    private PersonalAccessTokenService _sut = default!;

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        _sut = new PersonalAccessTokenService(_dbContext, Options.Create(_opt), _time);
    }

    private async Task<UserEntity> AddUserAsync(string email = "owner@test.local")
    {
        var user = new UserEntity { Email = email, NormalizedEmail = email.ToUpperInvariant(), PasswordHash = "x" };
        _dbContext.Users.Add(user);
        await _dbContext.SaveChangesAsync();
        return user;
    }

    private Task<PersonalAccessTokenEntity?> RowAsync(Guid id) =>
        _dbContext.PersonalAccessTokens.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id);

    [Fact]
    public async Task Create_ReturnsPlaintextOnce_AndStoresOnlyHash()
    {
        var user = await AddUserAsync();

        var result = await _sut.CreateAsync(user.Id, "Grafana", PersonalAccessTokenScope.Full, null);

        Assert.StartsWith("sb_pat_", result.PlaintextSecret);
        // The stored hash is a 64-char hex digest and is not the plaintext.
        Assert.Equal(64, result.Token.TokenHash.Length);
        Assert.NotEqual(result.PlaintextSecret, result.Token.TokenHash);
        Assert.DoesNotContain(result.PlaintextSecret, result.Token.TokenHash);
        // Display hint is the non-secret prefix + first 4 chars of the secret body.
        Assert.Equal("sb_pat_" + result.PlaintextSecret.Substring(7, 4), result.Token.DisplayHint);

        var row = await RowAsync(result.Token.Id);
        Assert.NotNull(row);
        Assert.Equal(result.Token.TokenHash, row!.TokenHash);
    }

    [Fact]
    public async Task Validate_ActiveToken_ResolvesOwnerAndScope()
    {
        var user = await AddUserAsync();
        var created = await _sut.CreateAsync(user.Id, "t", PersonalAccessTokenScope.Read, null);

        var principal = await _sut.ValidateAsync(created.PlaintextSecret);

        Assert.NotNull(principal);
        Assert.Equal(user.Id, principal!.UserId);
        Assert.Equal(PersonalAccessTokenScope.Read, principal.Scope);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-pat")]
    [InlineData("sb_pat_deadbeefdeadbeef")]
    public async Task Validate_UnknownOrMalformed_ReturnsNull(string token)
    {
        await AddUserAsync();
        Assert.Null(await _sut.ValidateAsync(token));
    }

    [Fact]
    public async Task Validate_RevokedToken_ReturnsNull()
    {
        var user = await AddUserAsync();
        var created = await _sut.CreateAsync(user.Id, "t", PersonalAccessTokenScope.Full, null);

        Assert.True(await _sut.RevokeAsync(user.Id, created.Token.Id));

        Assert.Null(await _sut.ValidateAsync(created.PlaintextSecret));
    }

    [Fact]
    public async Task Validate_ExpiredToken_ReturnsNull()
    {
        var user = await AddUserAsync();
        var created = await _sut.CreateAsync(
            user.Id, "t", PersonalAccessTokenScope.Full, _time.GetUtcNow().UtcDateTime.AddMinutes(5));

        _time.Advance(TimeSpan.FromMinutes(6));

        Assert.Null(await _sut.ValidateAsync(created.PlaintextSecret));
    }

    [Fact]
    public async Task Validate_StampsLastUsed_OnFirstUse()
    {
        var user = await AddUserAsync();
        var created = await _sut.CreateAsync(user.Id, "t", PersonalAccessTokenScope.Full, null);
        Assert.Null((await RowAsync(created.Token.Id))!.LastUsedUtc);

        await _sut.ValidateAsync(created.PlaintextSecret);

        Assert.Equal(_time.GetUtcNow().UtcDateTime, (await RowAsync(created.Token.Id))!.LastUsedUtc);
    }

    [Fact]
    public async Task Validate_ThrottlesLastUsed_WithinWindow()
    {
        var user = await AddUserAsync();
        var created = await _sut.CreateAsync(user.Id, "t", PersonalAccessTokenScope.Full, null);

        await _sut.ValidateAsync(created.PlaintextSecret);
        var firstStamp = (await RowAsync(created.Token.Id))!.LastUsedUtc;

        // Within the 60s throttle window — the stamp must not move.
        _time.Advance(TimeSpan.FromSeconds(30));
        await _sut.ValidateAsync(created.PlaintextSecret);
        Assert.Equal(firstStamp, (await RowAsync(created.Token.Id))!.LastUsedUtc);

        // Past the window — it advances.
        _time.Advance(TimeSpan.FromSeconds(40));
        await _sut.ValidateAsync(created.PlaintextSecret);
        Assert.Equal(_time.GetUtcNow().UtcDateTime, (await RowAsync(created.Token.Id))!.LastUsedUtc);
    }

    [Fact]
    public async Task Revoke_OtherUsersToken_ReturnsFalse_AndLeavesItActive()
    {
        var owner = await AddUserAsync("owner@test.local");
        var other = await AddUserAsync("other@test.local");
        var created = await _sut.CreateAsync(owner.Id, "t", PersonalAccessTokenScope.Full, null);

        Assert.False(await _sut.RevokeAsync(other.Id, created.Token.Id));
        Assert.Null((await RowAsync(created.Token.Id))!.RevokedUtc);
    }

    [Fact]
    public async Task Delete_RemovesRow()
    {
        var user = await AddUserAsync();
        var created = await _sut.CreateAsync(user.Id, "t", PersonalAccessTokenScope.Full, null);

        Assert.True(await _sut.DeleteAsync(user.Id, created.Token.Id));
        Assert.Null(await RowAsync(created.Token.Id));
    }

    [Fact]
    public async Task List_ReturnsOnlyOwnersTokens_NewestFirst()
    {
        var owner = await AddUserAsync("owner@test.local");
        var other = await AddUserAsync("other@test.local");
        var first = await _sut.CreateAsync(owner.Id, "first", PersonalAccessTokenScope.Full, null);
        _time.Advance(TimeSpan.FromMinutes(1));
        var second = await _sut.CreateAsync(owner.Id, "second", PersonalAccessTokenScope.Read, null);
        await _sut.CreateAsync(other.Id, "theirs", PersonalAccessTokenScope.Full, null);

        var list = await _sut.ListAsync(owner.Id);

        Assert.Equal(new[] { second.Token.Id, first.Token.Id }, list.Select(t => t.Id));
    }

    [Fact]
    public async Task DeletingUser_CascadeRemovesTokens()
    {
        var user = await AddUserAsync();
        await _sut.CreateAsync(user.Id, "t", PersonalAccessTokenScope.Full, null);

        var tracked = await _dbContext.Users.FirstAsync(u => u.Id == user.Id);
        _dbContext.Users.Remove(tracked);
        await _dbContext.SaveChangesAsync();

        Assert.Equal(0, await _dbContext.PersonalAccessTokens.CountAsync());
    }
}
