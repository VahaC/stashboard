using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OtpNet;
using Stashboard.Api.Auth;
using Stashboard.Api.Data;
using Stashboard.Tests.Infrastructure;

namespace Stashboard.Tests.Auth;

/// <summary>
/// Unit tests for <see cref="TwoFactorService"/>: the TOTP engine (window + replay guard),
/// recovery-code lifecycle, and the security-sensitive disable/regenerate paths. All assertions
/// run against real SQLite state.
/// </summary>
public class TwoFactorServiceTests : DatabaseTestBase
{
    private readonly TestTimeProvider _time = new();
    private readonly Pbkdf2PasswordHasher _hasher = new();
    private readonly PrefixEncryption _encryption = new();
    private readonly JwtOptions _opt = new()
    {
        Secret = "test-secret-test-secret-test-secret-test-secret",
        MaxFailedAccessAttempts = 3,
        LockoutMinutes = 10,
    };

    private UserService _users = default!;
    private TwoFactorService _sut = default!;

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        _users = new UserService(_dbContext, _hasher, _encryption, Options.Create(_opt), _time);
        _sut = new TwoFactorService(_dbContext, _hasher, _encryption, Options.Create(_opt), _time);
    }

    private sealed class PrefixEncryption : Stashboard.Core.Abstractions.IEncryptionService
    {
        public string Encrypt(string plaintext) => "enc:" + plaintext;
        public string Decrypt(string ciphertext) => ciphertext.StartsWith("enc:") ? ciphertext[4..] : ciphertext;
    }

    private async Task<UserEntity> Register(string email = "u@x", string password = "P@ssword1")
        => (await _users.RegisterAsync(email, password)).User!;

    private static string SecretOf(TwoFactorEnrollment e) => e.ManualKey.Replace(" ", "");

    /// <summary>The TOTP code for a Base32 secret, optionally offset in seconds to target an adjacent step.</summary>
    private static string Code(string base32, int offsetSeconds = 0)
        => new Totp(Base32Encoding.ToBytes(base32)).ComputeTotp(DateTime.UtcNow.AddSeconds(offsetSeconds));

    /// <summary>Enrolls and enables a user, returning the secret for code generation.</summary>
    private async Task<(UserEntity user, string secret)> EnableUser(string email = "u@x")
    {
        var user = await Register(email);
        var enrollment = await _sut.BeginEnrollAsync(user.Id);
        var secret = SecretOf(enrollment!);
        await _sut.EnableAsync(user.Id, Code(secret));
        return (user, secret);
    }

    // ── Enrollment ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task BeginEnroll_StoresEncryptedSecret_StillDisabled()
    {
        var u = await Register();

        var enrollment = await _sut.BeginEnrollAsync(u.Id);

        Assert.NotNull(enrollment);
        Assert.StartsWith("otpauth://totp/", enrollment!.OtpauthUri);
        Assert.Contains("issuer=Stashboard", enrollment.OtpauthUri);

        var stored = await _dbContext.Users.AsNoTracking().FirstAsync(x => x.Id == u.Id);
        Assert.False(stored.TwoFactorEnabled);
        // Encrypted at rest — the stored value is never the raw Base32 secret.
        Assert.NotNull(stored.TwoFactorSecretEncrypted);
        Assert.StartsWith("enc:", stored.TwoFactorSecretEncrypted!);
    }

    [Fact]
    public async Task Enable_RequiresValidFirstCode()
    {
        var u = await Register();
        await _sut.BeginEnrollAsync(u.Id);

        var result = await _sut.EnableAsync(u.Id, "000000");

        Assert.False(result.Succeeded);
        var stored = await _dbContext.Users.AsNoTracking().FirstAsync(x => x.Id == u.Id);
        Assert.False(stored.TwoFactorEnabled);
    }

    [Fact]
    public async Task Enable_WithValidCode_EnablesAndReturnsTenRecoveryCodes()
    {
        var u = await Register();
        var enrollment = await _sut.BeginEnrollAsync(u.Id);

        var result = await _sut.EnableAsync(u.Id, Code(SecretOf(enrollment!)));

        Assert.True(result.Succeeded);
        Assert.Equal(10, result.Codes!.Count);
        var stored = await _dbContext.Users.AsNoTracking().FirstAsync(x => x.Id == u.Id);
        Assert.True(stored.TwoFactorEnabled);
        Assert.Equal(10, await _dbContext.TwoFactorRecoveryCodes.CountAsync(c => c.UserId == u.Id));
    }

    // ── Login verification (window + replay) ─────────────────────────────────────

    [Fact]
    public async Task CompleteLogin_ValidCode_Succeeds()
    {
        var (user, secret) = await EnableUser();
        // The enable step consumed the current step; advance the clock past it so the
        // freshly-computed code maps to a newer step and isn't rejected as a replay.
        var result = await _sut.CompleteLoginAsync(user.Id, Code(secret, offsetSeconds: 30));

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task CompleteLogin_ReplayedCode_Rejected()
    {
        var user = await Register();
        var enrollment = await _sut.BeginEnrollAsync(user.Id);
        var code = Code(SecretOf(enrollment!));
        await _sut.EnableAsync(user.Id, code); // consumes this step

        var result = await _sut.CompleteLoginAsync(user.Id, code);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task CompleteLogin_AdjacentStepWithinWindow_Accepted()
    {
        var (user, secret) = await EnableUser();
        // Clear the replay marker so the window behaviour is what's under test.
        var tracked = await _dbContext.Users.FirstAsync(x => x.Id == user.Id);
        tracked.TwoFactorLastUsedStep = null;
        await _dbContext.SaveChangesAsync();

        var previousStep = await _sut.CompleteLoginAsync(user.Id, Code(secret, offsetSeconds: -30));

        Assert.True(previousStep.Succeeded);
    }

    [Fact]
    public async Task CompleteLogin_CodeOutsideWindow_Rejected()
    {
        var (user, secret) = await EnableUser();
        var tracked = await _dbContext.Users.FirstAsync(x => x.Id == user.Id);
        tracked.TwoFactorLastUsedStep = null;
        await _dbContext.SaveChangesAsync();

        // Three steps away (~90s) is well outside the ±1 window.
        var result = await _sut.CompleteLoginAsync(user.Id, Code(secret, offsetSeconds: -90));

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task CompleteLogin_WrongCodes_IncrementLockout()
    {
        var (user, _) = await EnableUser();

        await _sut.CompleteLoginAsync(user.Id, "000000");
        await _sut.CompleteLoginAsync(user.Id, "000000");
        var third = await _sut.CompleteLoginAsync(user.Id, "000000");

        Assert.False(third.Succeeded);
        Assert.Equal(AuthFailureReason.AccountLocked, third.Failure!.Reason);
        var stored = await _dbContext.Users.AsNoTracking().FirstAsync(x => x.Id == user.Id);
        Assert.NotNull(stored.LockoutEndUtc);
    }

    // ── Recovery codes ───────────────────────────────────────────────────────────

    [Fact]
    public async Task CompleteLogin_RecoveryCode_WorksOnceThenFails()
    {
        var (user, _) = await EnableUser();
        _dbContext.TwoFactorRecoveryCodes.Add(new TwoFactorRecoveryCodeEntity
        {
            UserId = user.Id, CodeHash = _hasher.Hash("ABCDEFGHIJ"),
        });
        await _dbContext.SaveChangesAsync();

        var first = await _sut.CompleteLoginAsync(user.Id, "abcde-fghij"); // accepts dashes + lowercase
        Assert.True(first.Succeeded);

        var second = await _sut.CompleteLoginAsync(user.Id, "ABCDE-FGHIJ");
        Assert.False(second.Succeeded);
    }

    // ── Disable / regenerate (security-sensitive) ────────────────────────────────

    [Fact]
    public async Task Disable_WrongPassword_LeavesTwoFactorEnabled()
    {
        var (user, _) = await EnableUser();

        var result = await _sut.DisableAsync(user.Id, "wrong");

        Assert.False(result.Succeeded);
        var stored = await _dbContext.Users.AsNoTracking().FirstAsync(x => x.Id == user.Id);
        Assert.True(stored.TwoFactorEnabled);
    }

    [Fact]
    public async Task Disable_CorrectPassword_ClearsStateRotatesStampAndRevokesSessions()
    {
        var (user, _) = await EnableUser();
        var oldStamp = (await _dbContext.Users.AsNoTracking().FirstAsync(x => x.Id == user.Id)).SecurityStamp;

        var now = _time.GetUtcNow().UtcDateTime;
        _dbContext.RefreshTokens.Add(new RefreshTokenEntity
        {
            UserId = user.Id, TokenHash = "h", FamilyId = Guid.NewGuid(),
            CreatedUtc = now, ExpiresUtc = now.AddDays(30), SessionExpiresUtc = now.AddDays(90),
        });
        await _dbContext.SaveChangesAsync();

        var result = await _sut.DisableAsync(user.Id, "P@ssword1");

        Assert.True(result.Succeeded);
        var stored = await _dbContext.Users.AsNoTracking().FirstAsync(x => x.Id == user.Id);
        Assert.False(stored.TwoFactorEnabled);
        Assert.Null(stored.TwoFactorSecretEncrypted);
        Assert.Null(stored.TwoFactorLastUsedStep);
        Assert.NotEqual(oldStamp, stored.SecurityStamp);
        Assert.Equal(0, await _dbContext.TwoFactorRecoveryCodes.CountAsync(c => c.UserId == user.Id));
        Assert.NotNull((await _dbContext.RefreshTokens.AsNoTracking().SingleAsync()).RevokedUtc);
    }

    [Fact]
    public async Task RegenerateRecoveryCodes_ReplacesCodesAndRotatesStamp()
    {
        var (user, _) = await EnableUser();
        var oldStamp = (await _dbContext.Users.AsNoTracking().FirstAsync(x => x.Id == user.Id)).SecurityStamp;
        var oldHashes = await _dbContext.TwoFactorRecoveryCodes.AsNoTracking()
            .Where(c => c.UserId == user.Id).Select(c => c.CodeHash).ToListAsync();

        var result = await _sut.RegenerateRecoveryCodesAsync(user.Id, "P@ssword1");

        Assert.True(result.Succeeded);
        Assert.Equal(10, result.Codes!.Count);
        var newHashes = await _dbContext.TwoFactorRecoveryCodes.AsNoTracking()
            .Where(c => c.UserId == user.Id).Select(c => c.CodeHash).ToListAsync();
        Assert.Equal(10, newHashes.Count);
        Assert.Empty(oldHashes.Intersect(newHashes)); // old set fully replaced
        var stored = await _dbContext.Users.AsNoTracking().FirstAsync(x => x.Id == user.Id);
        Assert.NotEqual(oldStamp, stored.SecurityStamp);
    }

    [Fact]
    public async Task RegenerateRecoveryCodes_WrongPassword_Fails()
    {
        var (user, _) = await EnableUser();

        var result = await _sut.RegenerateRecoveryCodesAsync(user.Id, "wrong");

        Assert.False(result.Succeeded);
    }
}
