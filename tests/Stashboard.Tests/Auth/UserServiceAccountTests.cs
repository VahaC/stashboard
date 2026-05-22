using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Stashboard.Api.Auth;
using Stashboard.Api.Data;
using Stashboard.Tests.Infrastructure;

namespace Stashboard.Tests.Auth;

/// <summary>
/// Tests for the account-management methods added on top of the original UserService:
/// email confirmation, password reset, password change, email change, profile, deletion.
/// All tests assert against real DB state.
/// </summary>
public class UserServiceAccountTests : DatabaseTestBase
{
    private readonly TestTimeProvider _time = new();
    private readonly Pbkdf2PasswordHasher _hasher = new();
    private readonly PrefixEncryption _encryption = new();
    private UserService _sut = default!;

    private readonly JwtOptions _opt = new()
    {
        Secret = "test-secret-test-secret-test-secret-test-secret",
        EmailConfirmationTokenLifetimeHours = 24,
        PasswordResetTokenLifetimeHours = 2,
    };

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        _sut = new UserService(_dbContext, _hasher, _encryption, Options.Create(_opt), _time);
    }

    private sealed class PrefixEncryption : Stashboard.Core.Abstractions.IEncryptionService
    {
        public string Encrypt(string plaintext) => "enc:" + plaintext;
        public string Decrypt(string ciphertext) => ciphertext.StartsWith("enc:") ? ciphertext[4..] : ciphertext;
    }

    private async Task<UserEntity> Register(string email = "u@x", string password = "P@ssword1")
    {
        var r = await _sut.RegisterAsync(email, password);
        return r.User!;
    }

    // ── Email confirmation ──────────────────────────────────────────────────

    [Fact]
    public async Task CreateEmailConfirmationToken_PersistsHashAndExpiry()
    {
        var u = await Register();

        var t = await _sut.CreateEmailConfirmationTokenAsync(u.Id);

        Assert.True(t.Succeeded);
        Assert.NotNull(t.Token);
        var stored = await _sut.FindByIdAsync(u.Id);
        Assert.NotNull(stored!.EmailConfirmationTokenHash);
        Assert.NotEqual(t.Token, stored.EmailConfirmationTokenHash); // stored as hash, not plaintext
        Assert.NotNull(stored.EmailConfirmationTokenExpiresUtc);
    }

    [Fact]
    public async Task ConfirmEmail_WithValidToken_SetsConfirmedAndClearsToken()
    {
        var u = await Register();
        var t = await _sut.CreateEmailConfirmationTokenAsync(u.Id);

        var result = await _sut.ConfirmEmailAsync(u.Email, t.Token!);

        Assert.True(result.Succeeded);
        var stored = await _sut.FindByIdAsync(u.Id);
        Assert.True(stored!.EmailConfirmed);
        Assert.Null(stored.EmailConfirmationTokenHash);
        Assert.Null(stored.EmailConfirmationTokenExpiresUtc);
    }

    [Fact]
    public async Task ConfirmEmail_WithWrongToken_Fails()
    {
        var u = await Register();
        await _sut.CreateEmailConfirmationTokenAsync(u.Id);

        var result = await _sut.ConfirmEmailAsync(u.Email, "not-the-token");

        Assert.False(result.Succeeded);
        var stored = await _sut.FindByIdAsync(u.Id);
        Assert.False(stored!.EmailConfirmed);
    }

    [Fact]
    public async Task ConfirmEmail_AfterExpiry_Fails()
    {
        var u = await Register();
        var t = await _sut.CreateEmailConfirmationTokenAsync(u.Id);
        _time.Advance(TimeSpan.FromHours(_opt.EmailConfirmationTokenLifetimeHours + 1));

        var result = await _sut.ConfirmEmailAsync(u.Email, t.Token!);

        Assert.False(result.Succeeded);
        Assert.Equal(AuthFailureReason.InvalidToken, result.Failure!.Reason);
    }

    [Fact]
    public async Task CreateEmailConfirmationToken_WhenAlreadyConfirmed_Fails()
    {
        var u = await Register();
        var t = await _sut.CreateEmailConfirmationTokenAsync(u.Id);
        await _sut.ConfirmEmailAsync(u.Email, t.Token!);

        var second = await _sut.CreateEmailConfirmationTokenAsync(u.Id);

        Assert.False(second.Succeeded);
    }

    // ── Password reset ──────────────────────────────────────────────────────

    [Fact]
    public async Task ResetPassword_WithValidToken_ChangesHashAndRevokesAllRefreshTokens()
    {
        var u = await Register("u@x", "OldP@ss1");
        var t = await _sut.CreatePasswordResetTokenAsync(u.Email);
        var oldStamp = u.SecurityStamp;

        // Seed an active refresh token to verify it's revoked.
        _dbContext.RefreshTokens.Add(new RefreshTokenEntity
        {
            UserId = u.Id,
            TokenHash = "h",
            FamilyId = Guid.NewGuid(),
            CreatedUtc = _time.GetUtcNow().UtcDateTime,
            ExpiresUtc = _time.GetUtcNow().UtcDateTime.AddDays(30),
            SessionExpiresUtc = _time.GetUtcNow().UtcDateTime.AddDays(90),
        });
        await _dbContext.SaveChangesAsync();

        var result = await _sut.ResetPasswordAsync(u.Email, t.Token!, "NewP@ss1");

        Assert.True(result.Succeeded);
        var stored = await _sut.FindByIdAsync(u.Id);
        Assert.True(_hasher.Verify("NewP@ss1", stored!.PasswordHash));
        Assert.False(_hasher.Verify("OldP@ss1", stored.PasswordHash));
        Assert.NotEqual(oldStamp, stored.SecurityStamp);
        Assert.Null(stored.PasswordResetTokenHash);

        var rt = await _dbContext.RefreshTokens.AsNoTracking().SingleAsync();
        Assert.NotNull(rt.RevokedUtc);
    }

    [Fact]
    public async Task ResetPassword_WithUnknownEmail_Fails()
    {
        var result = await _sut.ResetPasswordAsync("missing@x", "any", "NewP@ss1");
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task ResetPassword_TokenIsSingleUse()
    {
        var u = await Register("u@x", "OldP@ss1");
        var t = await _sut.CreatePasswordResetTokenAsync(u.Email);

        Assert.True((await _sut.ResetPasswordAsync(u.Email, t.Token!, "NewP@ss1")).Succeeded);
        var second = await _sut.ResetPasswordAsync(u.Email, t.Token!, "OtherP@ss2");

        Assert.False(second.Succeeded);
    }

    // ── Change password ─────────────────────────────────────────────────────

    [Fact]
    public async Task ChangePassword_WithCorrectCurrent_RotatesStampAndRevokesSessions()
    {
        var u = await Register("u@x", "OldP@ss1");
        var oldStamp = u.SecurityStamp;

        var result = await _sut.ChangePasswordAsync(u.Id, "OldP@ss1", "NewP@ss1");

        Assert.True(result.Succeeded);
        var stored = await _sut.FindByIdAsync(u.Id);
        Assert.True(_hasher.Verify("NewP@ss1", stored!.PasswordHash));
        Assert.NotEqual(oldStamp, stored.SecurityStamp);
    }

    [Fact]
    public async Task ChangePassword_WithWrongCurrent_Fails()
    {
        var u = await Register("u@x", "OldP@ss1");

        var result = await _sut.ChangePasswordAsync(u.Id, "wrong", "NewP@ss1");

        Assert.False(result.Succeeded);
        Assert.Equal(AuthFailureReason.InvalidCredentials, result.Failure!.Reason);
        var stored = await _sut.FindByIdAsync(u.Id);
        Assert.True(_hasher.Verify("OldP@ss1", stored!.PasswordHash));
    }

    // ── Email change ────────────────────────────────────────────────────────

    [Fact]
    public async Task RequestEmailChange_StoresPendingEmailAndToken()
    {
        var u = await Register("old@x", "P@ssword1");

        var t = await _sut.RequestEmailChangeAsync(u.Id, "new@x", "P@ssword1");

        Assert.True(t.Succeeded);
        var stored = await _sut.FindByIdAsync(u.Id);
        Assert.Equal("new@x", stored!.PendingEmail);
        Assert.Equal("old@x", stored.Email);
        Assert.NotNull(stored.PendingEmailTokenHash);
    }

    [Fact]
    public async Task RequestEmailChange_WithDuplicateEmail_Fails()
    {
        await Register("a@x");
        var u = await Register("b@x");

        var t = await _sut.RequestEmailChangeAsync(u.Id, "a@x", "P@ssword1");

        Assert.False(t.Succeeded);
        Assert.Equal(AuthFailureReason.EmailAlreadyTaken, t.Failure!.Reason);
    }

    [Fact]
    public async Task RequestEmailChange_WithWrongPassword_Fails()
    {
        var u = await Register();
        var t = await _sut.RequestEmailChangeAsync(u.Id, "new@x", "wrong");
        Assert.False(t.Succeeded);
    }

    [Fact]
    public async Task ConfirmEmailChange_AppliesNewEmailAndRotatesStamp()
    {
        var u = await Register("old@x", "P@ssword1");
        var t = await _sut.RequestEmailChangeAsync(u.Id, "new@x", "P@ssword1");
        var oldStamp = (await _sut.FindByIdAsync(u.Id))!.SecurityStamp;

        var result = await _sut.ConfirmEmailChangeAsync(u.Id, t.Token!);

        Assert.True(result.Succeeded);
        var stored = await _sut.FindByIdAsync(u.Id);
        Assert.Equal("new@x", stored!.Email);
        Assert.Equal("NEW@X", stored.NormalizedEmail);
        Assert.True(stored.EmailConfirmed);
        Assert.Null(stored.PendingEmail);
        Assert.NotEqual(oldStamp, stored.SecurityStamp);
    }

    // ── Profile ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateProfile_SetsAndTrimsDisplayName()
    {
        var u = await Register();
        await _sut.UpdateProfileAsync(u.Id, "  Alice  ", theme: null);

        var stored = await _sut.FindByIdAsync(u.Id);
        Assert.Equal("Alice", stored!.DisplayName);
    }

    [Fact]
    public async Task UpdateProfile_WhitespaceClearsDisplayName()
    {
        var u = await Register();
        await _sut.UpdateProfileAsync(u.Id, "Alice", theme: null);
        await _sut.UpdateProfileAsync(u.Id, "   ", theme: null);

        var stored = await _sut.FindByIdAsync(u.Id);
        Assert.Null(stored!.DisplayName);
    }

    [Fact]
    public async Task UpdateProfile_NewUser_DefaultsToSystemTheme()
    {
        var u = await Register();
        var stored = await _sut.FindByIdAsync(u.Id);
        Assert.Equal("system", stored!.Theme);
    }

    [Fact]
    public async Task UpdateProfile_PersistsTheme()
    {
        var u = await Register();
        await _sut.UpdateProfileAsync(u.Id, "Alice", theme: "dark");

        var stored = await _sut.FindByIdAsync(u.Id);
        Assert.Equal("dark", stored!.Theme);
        Assert.Equal("Alice", stored.DisplayName);
    }

    [Fact]
    public async Task UpdateProfile_NullTheme_LeavesThemeUnchanged()
    {
        var u = await Register();
        await _sut.UpdateProfileAsync(u.Id, "Alice", theme: "dark");
        await _sut.UpdateProfileAsync(u.Id, "Alice2", theme: null);

        var stored = await _sut.FindByIdAsync(u.Id);
        Assert.Equal("dark", stored!.Theme);
        Assert.Equal("Alice2", stored.DisplayName);
    }

    [Fact]
    public async Task UpdateProfile_InvalidTheme_Fails()
    {
        var u = await Register();
        var result = await _sut.UpdateProfileAsync(u.Id, "Alice", theme: "purple");
        Assert.False(result.Succeeded);

        var stored = await _sut.FindByIdAsync(u.Id);
        Assert.Equal("system", stored!.Theme);
    }

    [Fact]
    public async Task SetTheme_UpdatesOnlyThemeColumn()
    {
        var u = await Register();
        await _sut.UpdateProfileAsync(u.Id, "Alice", theme: null);

        var result = await _sut.SetThemeAsync(u.Id, "light");

        Assert.True(result.Succeeded);
        var stored = await _sut.FindByIdAsync(u.Id);
        Assert.Equal("light", stored!.Theme);
        Assert.Equal("Alice", stored.DisplayName);
    }

    [Theory]
    [InlineData("system")]
    [InlineData("light")]
    [InlineData("dark")]
    public async Task SetTheme_AcceptsAllValidValues(string theme)
    {
        var u = await Register();
        var result = await _sut.SetThemeAsync(u.Id, theme);
        Assert.True(result.Succeeded);
        var stored = await _sut.FindByIdAsync(u.Id);
        Assert.Equal(theme, stored!.Theme);
    }

    [Fact]
    public async Task SetTheme_RejectsInvalidValue()
    {
        var u = await Register();
        var result = await _sut.SetThemeAsync(u.Id, "neon");
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task SetTheme_UnknownUser_Fails()
    {
        var result = await _sut.SetThemeAsync(Guid.NewGuid(), "dark");
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task SetDashboardPreferences_PersistsSortAndGrouping()
    {
        var user = await Register();

        var result = await _sut.SetDashboardPreferencesAsync(user.Id, "category", true);

        Assert.True(result.Succeeded);
        var stored = await _sut.FindByIdAsync(user.Id);
        Assert.Equal("category", stored!.DashboardSortMode);
        Assert.True(stored.DashboardGroupByCategory);
    }

    [Fact]
    public async Task SetDashboardPreferences_InvalidSortMode_Fails()
    {
        var user = await Register();

        var result = await _sut.SetDashboardPreferencesAsync(user.Id, "priority", true);

        Assert.False(result.Succeeded);
        var stored = await _sut.FindByIdAsync(user.Id);
        Assert.Equal("name", stored!.DashboardSortMode);
        Assert.False(stored.DashboardGroupByCategory);
    }

    [Fact]
    public async Task SetDashboardPreferences_UnknownUser_Fails()
    {
        var result = await _sut.SetDashboardPreferencesAsync(Guid.NewGuid(), "name", false);
        Assert.False(result.Succeeded);
    }

    // ── Telegram settings ────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateTelegramSettings_PersistsValues()
    {
        var u = await Register();

        var result = await _sut.UpdateTelegramSettingsAsync(u.Id, "bot-token", "123456", true);

        Assert.True(result.Succeeded);
        var persisted = await _dbContext.Users.AsNoTracking().SingleAsync(user => user.Id == u.Id);
        Assert.Equal("enc:bot-token", persisted.TelegramBotTokenEncrypted);

        var stored = await _sut.FindByIdAsync(u.Id);
        Assert.Equal("bot-token", stored!.TelegramBotToken);
        Assert.Equal("123456", stored.TelegramChatId);
        Assert.True(stored.TelegramNotificationsEnabled);
    }

    [Fact]
    public async Task UpdateTelegramSettings_WhenCredentialsMissing_DisablesNotifications()
    {
        var u = await Register();

        var result = await _sut.UpdateTelegramSettingsAsync(u.Id, "", "", true);

        Assert.True(result.Succeeded);
        var stored = await _sut.FindByIdAsync(u.Id);
        Assert.Null(stored!.TelegramBotToken);
        Assert.Null(stored.TelegramChatId);
        Assert.False(stored.TelegramNotificationsEnabled);
    }

    // ── Account deletion ────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteAccount_WithCorrectPassword_RemovesUserRow()
    {
        var u = await Register("u@x", "P@ssword1");

        var result = await _sut.DeleteAccountAsync(u.Id, "P@ssword1");

        Assert.True(result.Succeeded);
        Assert.Null(await _sut.FindByIdAsync(u.Id));
    }

    [Fact]
    public async Task DeleteAccount_CascadesUserOwnedDomainData()
    {
        var u = await Register("u@x", "P@ssword1");
        _dbContext.Categories.Add(new Stashboard.Core.Entities.CategoryEntity
        {
            UserId = u.Id, Name = "C", Color = "#fff",
        });
        _dbContext.Tags.Add(new Stashboard.Core.Entities.TagEntity { UserId = u.Id, Name = "t" });
        _dbContext.WebResources.Add(new Stashboard.Core.Entities.WebResourceEntity
        {
            UserId = u.Id, Name = "S", MainUrl = "https://x",
        });
        await _dbContext.SaveChangesAsync();

        await _sut.DeleteAccountAsync(u.Id, "P@ssword1");

        Assert.Equal(0, _dbContext.Categories.Count());
        Assert.Equal(0, _dbContext.Tags.Count());
        Assert.Equal(0, _dbContext.WebResources.Count());
    }

    [Fact]
    public async Task DeleteAccount_WithWrongPassword_Fails()
    {
        var u = await Register();
        var result = await _sut.DeleteAccountAsync(u.Id, "wrong");
        Assert.False(result.Succeeded);
        Assert.NotNull(await _sut.FindByIdAsync(u.Id));
    }
}
