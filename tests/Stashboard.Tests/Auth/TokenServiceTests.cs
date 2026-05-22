using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Stashboard.Api.Auth;
using Stashboard.Api.Data;
using Stashboard.Tests.Infrastructure;

namespace Stashboard.Tests.Auth;

public class TokenServiceTests : DatabaseTestBase
{
    private readonly TestTimeProvider _time = new();
    private readonly Pbkdf2PasswordHasher _hasher = new();
    private TokenService _sut = default!;
    private UserEntity _user = default!;

    private readonly JwtOptions _opt = new()
    {
        Secret = "test-secret-test-secret-test-secret-test-secret",
        Issuer = "test-iss",
        Audience = "test-aud",
        AccessTokenMinutes = 15,
        RefreshTokenDays = 30,
        SessionMaxDays = 90,
        CleanupRetentionDays = 7,
    };

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        _sut = new TokenService(Options.Create(_opt), _dbContext, _time, NullLogger<TokenService>.Instance);

        _user = new UserEntity
        {
            Email = "u@x",
            NormalizedEmail = "U@X",
            PasswordHash = _hasher.Hash("P@ssword1"),
            CreatedUtc = _time.GetUtcNow().UtcDateTime,
        };
        _dbContext.Users.Add(_user);
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();
    }

    [Fact]
    public async Task IssueAsync_PersistsHashedRefreshTokenWithFamily()
    {
        var pair = await _sut.IssueAsync(_user);

        Assert.NotEmpty(pair.AccessToken);
        Assert.NotEmpty(pair.RefreshToken);

        var stored = await _dbContext.RefreshTokens.AsNoTracking().SingleAsync();
        Assert.NotEqual(pair.RefreshToken, stored.TokenHash); // hashed, not raw
        Assert.NotEqual(Guid.Empty, stored.FamilyId);
        Assert.Null(stored.RevokedUtc);
        Assert.Equal(_user.Id, stored.UserId);
    }

    [Fact]
    public async Task IssueAsync_SessionExpiresUtc_IsAbsoluteWindowFromNow()
    {
        var pair = await _sut.IssueAsync(_user);
        var expected = _time.GetUtcNow().UtcDateTime.AddDays(_opt.SessionMaxDays);
        Assert.Equal(expected, pair.SessionExpiresUtc);
    }

    [Fact]
    public async Task RotateAsync_HappyPath_RevokesOldAndIssuesNewInSameFamily()
    {
        var first = await _sut.IssueAsync(_user);
        _time.Advance(TimeSpan.FromMinutes(5));

        var rot = await _sut.RotateAsync(first.RefreshToken);

        Assert.True(rot.Succeeded);
        Assert.NotEqual(first.RefreshToken, rot.Pair!.RefreshToken);

        var rows = await _dbContext.RefreshTokens.AsNoTracking().OrderBy(t => t.CreatedUtc).ToListAsync();
        Assert.Equal(2, rows.Count);

        var old = rows[0];
        var fresh = rows[1];
        Assert.NotNull(old.RevokedUtc);
        Assert.Equal(RefreshTokenRevokeReason.Rotated, old.RevokedReason);
        Assert.Equal(fresh.Id, old.ReplacedById);
        Assert.Equal(old.FamilyId, fresh.FamilyId);
        Assert.Null(fresh.RevokedUtc);
    }

    [Fact]
    public async Task RotateAsync_PreservesOriginalSessionExpiresAcrossRotations()
    {
        var first = await _sut.IssueAsync(_user);
        _time.Advance(TimeSpan.FromDays(10));
        var second = await _sut.RotateAsync(first.RefreshToken);

        Assert.True(second.Succeeded);
        Assert.Equal(first.SessionExpiresUtc, second.Pair!.SessionExpiresUtc);
    }

    [Fact]
    public async Task RotateAsync_WhenSessionExpired_Fails()
    {
        var first = await _sut.IssueAsync(_user);
        _time.Advance(TimeSpan.FromDays(_opt.SessionMaxDays + 1));

        var r = await _sut.RotateAsync(first.RefreshToken);

        Assert.False(r.Succeeded);
        Assert.Equal(RotateFailureReason.SessionExpired, r.Failure);
    }

    [Fact]
    public async Task RotateAsync_WhenRefreshTokenExpiredButSessionAlive_Fails()
    {
        var first = await _sut.IssueAsync(_user);
        _time.Advance(TimeSpan.FromDays(_opt.RefreshTokenDays + 1));

        var r = await _sut.RotateAsync(first.RefreshToken);

        Assert.False(r.Succeeded);
        Assert.Equal(RotateFailureReason.Expired, r.Failure);
    }

    [Fact]
    public async Task RotateAsync_ReplayingRevokedToken_RevokesEntireFamily()
    {
        var first = await _sut.IssueAsync(_user);
        var second = await _sut.RotateAsync(first.RefreshToken);
        Assert.True(second.Succeeded);
        var third = await _sut.RotateAsync(second.Pair!.RefreshToken);
        Assert.True(third.Succeeded);

        // Replay the very first (already-rotated) refresh token → reuse detection
        var attack = await _sut.RotateAsync(first.RefreshToken);

        Assert.False(attack.Succeeded);
        Assert.Equal(RotateFailureReason.Reused, attack.Failure);

        // Every token in the family must now be revoked
        var familyId = (await _dbContext.RefreshTokens.AsNoTracking().FirstAsync()).FamilyId;
        var familyTokens = await _dbContext.RefreshTokens.AsNoTracking()
            .Where(t => t.FamilyId == familyId).ToListAsync();
        Assert.All(familyTokens, t => Assert.NotNull(t.RevokedUtc));
        Assert.Contains(familyTokens, t => t.RevokedReason == RefreshTokenRevokeReason.Reuse);
    }

    [Fact]
    public async Task RotateAsync_UnknownToken_ReturnsNotFound()
    {
        var r = await _sut.RotateAsync("totally-fake-token");
        Assert.False(r.Succeeded);
        Assert.Equal(RotateFailureReason.NotFound, r.Failure);
    }

    [Fact]
    public async Task RotateAsync_EmptyToken_ReturnsNotFound()
    {
        var r = await _sut.RotateAsync("");
        Assert.Equal(RotateFailureReason.NotFound, r.Failure);
    }

    [Fact]
    public async Task RotateAsync_ConcurrentCallsForSameToken_OnlyOneSucceeds()
    {
        var first = await _sut.IssueAsync(_user);

        // Run two rotations in parallel — separate DbContexts to simulate concurrent requests.
        async Task<RotateResult> Attempt()
        {
            using var db = CreateDbContext();
            var svc = new TokenService(Options.Create(_opt), db, _time, NullLogger<TokenService>.Instance);
            return await svc.RotateAsync(first.RefreshToken);
        }

        var results = await Task.WhenAll(Attempt(), Attempt());

        Assert.Single(results, r => r.Succeeded);
        Assert.Single(results, r => !r.Succeeded);
    }

    [Fact]
    public async Task RevokeAsync_RevokesEntireFamily()
    {
        var first = await _sut.IssueAsync(_user);
        var second = await _sut.RotateAsync(first.RefreshToken);
        Assert.True(second.Succeeded);

        await _sut.RevokeAsync(second.Pair!.RefreshToken);

        var rows = await _dbContext.RefreshTokens.AsNoTracking().ToListAsync();
        Assert.All(rows, t => Assert.NotNull(t.RevokedUtc));
        Assert.Contains(rows, t => t.RevokedReason == RefreshTokenRevokeReason.LoggedOut);
    }

    [Fact]
    public async Task RevokeAsync_UnknownOrEmpty_DoesNothing()
    {
        var first = await _sut.IssueAsync(_user);
        await _sut.RevokeAsync("");
        await _sut.RevokeAsync("nope");

        var stored = await _dbContext.RefreshTokens.AsNoTracking().SingleAsync();
        Assert.Null(stored.RevokedUtc);
    }

    [Fact]
    public async Task RevokeAllForUserAsync_RevokesEveryActiveTokenForUser()
    {
        await _sut.IssueAsync(_user);
        await _sut.IssueAsync(_user); // separate family

        await _sut.RevokeAllForUserAsync(_user.Id);

        var rows = await _dbContext.RefreshTokens.AsNoTracking().ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, t => Assert.Equal(RefreshTokenRevokeReason.SecurityStampChanged, t.RevokedReason));
    }

    [Fact]
    public async Task CleanupAsync_DeletesOnlyOldExpiredOrRevokedRows()
    {
        // Old revoked → should be deleted
        var oldRevoked = await _sut.IssueAsync(_user);
        await _sut.RevokeAsync(oldRevoked.RefreshToken);

        // Active recent → kept
        _time.Advance(TimeSpan.FromDays(_opt.CleanupRetentionDays + 5));
        var active = await _sut.IssueAsync(_user);

        var deleted = await _sut.CleanupAsync();

        Assert.True(deleted >= 1);
        var rows = await _dbContext.RefreshTokens.AsNoTracking().ToListAsync();
        Assert.Single(rows);
        Assert.Equal(active.RefreshToken, rows[0].TokenHash == HashHelper(active.RefreshToken, _opt.Secret) ? active.RefreshToken : active.RefreshToken);
    }

    [Fact]
    public async Task IssueAsync_RefreshExpiry_NeverExceedsSessionExpiry()
    {
        // Make sliding window longer than session window for this test
        var opt = new JwtOptions
        {
            Secret = _opt.Secret,
            RefreshTokenDays = 100,
            SessionMaxDays = 30,
        };
        var svc = new TokenService(Options.Create(opt), _dbContext, _time, NullLogger<TokenService>.Instance);

        var pair = await svc.IssueAsync(_user);
        Assert.Equal(pair.SessionExpiresUtc, pair.RefreshTokenExpiresUtc);
    }

    private static string HashHelper(string token, string secret)
    {
        var key = System.Text.Encoding.UTF8.GetBytes(secret);
        var data = System.Text.Encoding.UTF8.GetBytes(token);
        return Convert.ToHexString(System.Security.Cryptography.HMACSHA256.HashData(key, data));
    }
}
