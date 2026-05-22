using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Moq;
using Stashboard.Api.Auth;
using Stashboard.Api.Contracts;
using Stashboard.Api.Controllers;
using Stashboard.Api.Data;
using Stashboard.Api.Notifications;
using Stashboard.Tests.Infrastructure;

namespace Stashboard.Tests.Controllers;

public class AccountControllerTests : DatabaseTestBase
{
    private readonly TestTimeProvider _time = new();
    private readonly Pbkdf2PasswordHasher _hasher = new();
    private readonly Mock<IEmailSender> _email = new();

    private readonly JwtOptions _jwt = new()
    {
        Secret = "test-secret-test-secret-test-secret-test-secret",
        EmailConfirmationTokenLifetimeHours = 24,
        PasswordResetTokenLifetimeHours = 2,
    };
    private readonly EmailOptions _emailOpt = new() { AppBaseUrl = "https://app.example" };

    private UserService _users = default!;
    private EmailSettingsService _emailSettings = default!;
    private AccountController _ctrl = default!;

    /// <summary>Reversible fake so tests can assert the column holds ciphertext, not plaintext.</summary>
    private sealed class PrefixEncryption : Stashboard.Core.Abstractions.IEncryptionService
    {
        public string Encrypt(string plaintext) => "enc:" + plaintext;
        public string Decrypt(string ciphertext) => ciphertext.StartsWith("enc:") ? ciphertext[4..] : ciphertext;
    }

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        _users = new UserService(_dbContext, _hasher, new PrefixEncryption(), Options.Create(_jwt), _time);
        _emailSettings = new EmailSettingsService(
            _dbContext, new PrefixEncryption(), Options.Create(_emailOpt), TestMapperFactory.Create(), _time);
        _email.Setup(e => e.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var notifications = new AccountNotificationService(
            _email.Object,
            _emailSettings,
            new HttpContextAccessor());
        _ctrl = new AccountController(_users, notifications, _emailSettings, TestMapperFactory.Create());
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

    private async Task<Stashboard.Api.Data.UserEntity> Register(string email = "u@x", string pwd = "P@ssword1")
    {
        var r = await _users.RegisterAsync(email, pwd);
        return r.User!;
    }

    // ── ResendConfirmation ───────────────────────────────────────────────────

    [Fact]
    public async Task ResendConfirmation_KnownUnconfirmedUser_SendsEmail()
    {
        var u = await Register();

        var result = await _ctrl.ResendConfirmation(new ResendConfirmationRequest(u.Email), default);

        Assert.IsType<NoContentResult>(result);
        _email.Verify(e => e.SendAsync(It.Is<EmailMessage>(m => m.To == u.Email), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ResendConfirmation_UnknownEmail_ReturnsNoContent_NoEmailSent()
    {
        var result = await _ctrl.ResendConfirmation(new ResendConfirmationRequest("ghost@x"), default);

        Assert.IsType<NoContentResult>(result);
        _email.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ResendConfirmation_AlreadyConfirmed_NoEmailSent()
    {
        var u = await Register();
        var t = await _users.CreateEmailConfirmationTokenAsync(u.Id);
        await _users.ConfirmEmailAsync(u.Email, t.Token!);

        var result = await _ctrl.ResendConfirmation(new ResendConfirmationRequest(u.Email), default);

        Assert.IsType<NoContentResult>(result);
        _email.VerifyNoOtherCalls();
    }

    // ── ConfirmEmail ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ConfirmEmail_WithBadToken_ReturnsBadRequest()
    {
        var u = await Register();
        await _users.CreateEmailConfirmationTokenAsync(u.Id);

        var result = await _ctrl.ConfirmEmail(new ConfirmEmailRequest(u.Email, "wrong"), default);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    // ── ForgotPassword ───────────────────────────────────────────────────────

    [Fact]
    public async Task ForgotPassword_KnownEmail_SendsEmail()
    {
        var u = await Register();

        var result = await _ctrl.ForgotPassword(new ForgotPasswordRequest(u.Email), default);

        Assert.IsType<NoContentResult>(result);
        _email.Verify(e => e.SendAsync(It.IsAny<EmailMessage>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ForgotPassword_UnknownEmail_StillReturns204_NoEmailSent()
    {
        var result = await _ctrl.ForgotPassword(new ForgotPasswordRequest("ghost@x"), default);

        Assert.IsType<NoContentResult>(result);
        _email.VerifyNoOtherCalls();
    }

    // ── ResetPassword ────────────────────────────────────────────────────────

    [Fact]
    public async Task ResetPassword_HappyPath_ReturnsNoContent()
    {
        var u = await Register("u@x", "Old@1234");
        var t = await _users.CreatePasswordResetTokenAsync(u.Email);

        var result = await _ctrl.ResetPassword(new ResetPasswordRequest(u.Email, t.Token!, "New@1234"), default);

        Assert.IsType<NoContentResult>(result);
    }

    // ── ChangePassword (auth) ────────────────────────────────────────────────

    [Fact]
    public async Task ChangePassword_HappyPath_ReturnsNoContent()
    {
        var u = await Register("u@x", "Old@1234");
        SignIn(u.Id);

        var result = await _ctrl.ChangePassword(new ChangePasswordRequest("Old@1234", "New@1234"), default);

        Assert.IsType<NoContentResult>(result);
    }

    [Fact]
    public async Task ChangePassword_WrongCurrent_ReturnsBadRequest()
    {
        var u = await Register("u@x", "Old@1234");
        SignIn(u.Id);

        var result = await _ctrl.ChangePassword(new ChangePasswordRequest("wrong", "New@1234"), default);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    // ── ChangeEmail ──────────────────────────────────────────────────────────

    [Fact]
    public async Task ChangeEmail_HappyPath_SendsEmailToNewAddress()
    {
        var u = await Register("old@x", "P@ssword1");
        SignIn(u.Id);

        var result = await _ctrl.ChangeEmail(new ChangeEmailRequest("new@x", "P@ssword1"), default);

        Assert.IsType<NoContentResult>(result);
        _email.Verify(e => e.SendAsync(It.Is<EmailMessage>(m => m.To == "new@x"), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Profile ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetProfile_ReturnsCurrentUserData()
    {
        var u = await Register();
        SignIn(u.Id);

        var result = await _ctrl.GetProfile(default);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var body = Assert.IsType<ProfileResponse>(ok.Value);
        Assert.Equal(u.Email, body.Email);
        Assert.False(body.EmailConfirmed);
    }

    [Fact]
    public async Task UpdateProfile_PersistsDisplayName()
    {
        var u = await Register();
        SignIn(u.Id);

        var result = await _ctrl.UpdateProfile(new UpdateProfileRequest("Alice", null), default);

        Assert.IsType<NoContentResult>(result);
        var stored = await _users.FindByIdAsync(u.Id);
        Assert.Equal("Alice", stored!.DisplayName);
    }

    [Fact]
    public async Task UpdateProfile_WithTheme_PersistsBoth()
    {
        var u = await Register();
        SignIn(u.Id);

        var result = await _ctrl.UpdateProfile(new UpdateProfileRequest("Alice", "dark"), default);

        Assert.IsType<NoContentResult>(result);
        var stored = await _users.FindByIdAsync(u.Id);
        Assert.Equal("Alice", stored!.DisplayName);
        Assert.Equal("dark", stored.Theme);
    }

    [Fact]
    public async Task UpdateTheme_PersistsTheme()
    {
        var u = await Register();
        SignIn(u.Id);

        var result = await _ctrl.UpdateTheme(new UpdateThemeRequest("dark"), default);

        Assert.IsType<NoContentResult>(result);
        var stored = await _users.FindByIdAsync(u.Id);
        Assert.Equal("dark", stored!.Theme);
    }

    [Fact]
    public async Task UpdateTheme_InvalidValue_ReturnsBadRequest()
    {
        var u = await Register();
        SignIn(u.Id);

        var result = await _ctrl.UpdateTheme(new UpdateThemeRequest("plaid"), default);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task GetDashboardPreferences_ReturnsCurrentValues()
    {
        var user = await Register();
        await _users.SetDashboardPreferencesAsync(user.Id, "category", true);
        SignIn(user.Id);

        var result = await _ctrl.GetDashboardPreferences(default);

        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var body = Assert.IsType<DashboardPreferencesResponse>(okResult.Value);
        Assert.Equal("category", body.SortMode);
        Assert.True(body.GroupByCategory);
    }

    [Fact]
    public async Task UpdateDashboardPreferences_PersistsValues()
    {
        var user = await Register();
        SignIn(user.Id);

        var result = await _ctrl.UpdateDashboardPreferences(new UpdateDashboardPreferencesRequest("category", true), default);

        Assert.IsType<NoContentResult>(result);
        var stored = await _users.FindByIdAsync(user.Id);
        Assert.Equal("category", stored!.DashboardSortMode);
        Assert.True(stored.DashboardGroupByCategory);
    }

    [Fact]
    public async Task UpdateDashboardPreferences_InvalidSortMode_ReturnsBadRequest()
    {
        var user = await Register();
        SignIn(user.Id);

        var result = await _ctrl.UpdateDashboardPreferences(new UpdateDashboardPreferencesRequest("priority", true), default);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    // ── Delete ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_WithCorrectPassword_RemovesUser()
    {
        var u = await Register("u@x", "P@ssword1");
        SignIn(u.Id);

        var result = await _ctrl.Delete(new DeleteAccountRequest("P@ssword1"), default);

        Assert.IsType<NoContentResult>(result);
        Assert.Null(await _users.FindByIdAsync(u.Id));
    }

    [Fact]
    public async Task Delete_WithWrongPassword_ReturnsBadRequest()
    {
        var u = await Register("u@x", "P@ssword1");
        SignIn(u.Id);

        var result = await _ctrl.Delete(new DeleteAccountRequest("wrong"), default);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.NotNull(await _users.FindByIdAsync(u.Id));
    }

    // ── TelegramSettings ─────────────────────────────────────────────────────

    [Fact]
    public async Task GetTelegramSettings_ReturnsCurrentValues()
    {
        var u = await Register();
        await _users.UpdateTelegramSettingsAsync(u.Id, "bot-token", "123456", true);
        SignIn(u.Id);

        var result = await _ctrl.GetTelegramSettings(default);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var body = Assert.IsType<TelegramSettingsResponse>(ok.Value);
        Assert.Equal("bot-token", body.BotToken);
        Assert.Equal("123456", body.ChatId);
        Assert.True(body.NotificationsEnabled);
    }

    [Fact]
    public async Task UpdateTelegramSettings_PersistsValues()
    {
        var u = await Register();
        SignIn(u.Id);

        var result = await _ctrl.UpdateTelegramSettings(new UpdateTelegramSettingsRequest("bot-token", "123456", true), default);

        Assert.IsType<NoContentResult>(result);
        var persisted = await _dbContext.Users.AsNoTracking().SingleAsync(user => user.Id == u.Id);
        Assert.Equal("enc:bot-token", persisted.TelegramBotTokenEncrypted);

        var stored = await _users.FindByIdAsync(u.Id);
        Assert.Equal("bot-token", stored!.TelegramBotToken);
        Assert.Equal("123456", stored.TelegramChatId);
        Assert.True(stored.TelegramNotificationsEnabled);
    }

    // ── EmailSettings ──────────────────────────────────────────────────────────

    [Fact]
    public async Task GetEmailSettings_ReturnsSeededDefaults_NoStoredPassword()
    {
        var result = await _ctrl.GetEmailSettings(default);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var body = Assert.IsType<EmailSettingsResponse>(ok.Value);
        Assert.Equal("LogOnly", body.Provider);
        Assert.Equal("https://app.example", body.AppBaseUrl);
        Assert.False(body.HasPassword);
    }

    [Fact]
    public async Task GetEmailSettings_WhenSeedHasNullUsername_PersistsEmptyUsername()
    {
        var settingsWithNullUsername = new EmailSettingsService(
            _dbContext,
            new PrefixEncryption(),
            Options.Create(new EmailOptions { Username = null! }),
            TestMapperFactory.Create(),
            _time);
        var controller = new AccountController(_users, new AccountNotificationService(_email.Object, settingsWithNullUsername, new HttpContextAccessor()), settingsWithNullUsername, TestMapperFactory.Create());

        var result = await controller.GetEmailSettings(default);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        _ = Assert.IsType<EmailSettingsResponse>(ok.Value);
        var stored = await _dbContext.EmailSettings.AsNoTracking().SingleAsync(item => item.Id == EmailSettingsEntity.SingletonId);
        Assert.Equal(string.Empty, stored.Username);
    }

    [Fact]
    public async Task UpdateEmailSettings_PersistsValuesAndEncryptsPassword()
    {
        var req = new UpdateEmailSettingsRequest(
            "Smtp", "smtp.example.com", 587, true, "user@example.com",
            new SecretValueUpsert(SecretValueAction.Set, "s3cret"),
            "no-reply@example.com", "Stashboard", "https://app.example.com");

        var result = await _ctrl.UpdateEmailSettings(req, default);

        Assert.IsType<NoContentResult>(result);
        var stored = await _dbContext.EmailSettings.FindAsync(EmailSettingsEntity.SingletonId);
        Assert.Equal("Smtp", stored!.Provider);
        Assert.Equal("smtp.example.com", stored.Host);
        Assert.Equal(587, stored.Port);
        Assert.Equal("user@example.com", stored.Username);
        // Password is stored as ciphertext, never plaintext.
        Assert.Equal("enc:s3cret", stored.PasswordEncrypted);

        // The masked response advertises that a password is set but never returns it.
        var getResult = await _ctrl.GetEmailSettings(default);
        var body = Assert.IsType<EmailSettingsResponse>(Assert.IsType<OkObjectResult>(getResult.Result).Value);
        Assert.True(body.HasPassword);
    }

    [Fact]
    public async Task UpdateEmailSettings_KeepPassword_PreservesStoredValue()
    {
        await _ctrl.UpdateEmailSettings(new UpdateEmailSettingsRequest(
            "Smtp", "smtp.example.com", 587, true, "user@example.com",
            new SecretValueUpsert(SecretValueAction.Set, "s3cret"),
            "no-reply@example.com", "Stashboard", "https://app.example.com"), default);

        // Second update changes the host but keeps the password (null = keep).
        await _ctrl.UpdateEmailSettings(new UpdateEmailSettingsRequest(
            "Smtp", "smtp.changed.com", 465, false, "user@example.com",
            null,
            "no-reply@example.com", "Stashboard", "https://app.example.com"), default);

        var stored = await _dbContext.EmailSettings.FindAsync(EmailSettingsEntity.SingletonId);
        Assert.Equal("smtp.changed.com", stored!.Host);
        Assert.Equal("enc:s3cret", stored.PasswordEncrypted);
    }

    [Fact]
    public async Task UpdateEmailSettings_ClearPassword_DropsStoredValue()
    {
        await _ctrl.UpdateEmailSettings(new UpdateEmailSettingsRequest(
            "Smtp", "smtp.example.com", 587, true, "user@example.com",
            new SecretValueUpsert(SecretValueAction.Set, "s3cret"),
            "no-reply@example.com", "Stashboard", "https://app.example.com"), default);

        await _ctrl.UpdateEmailSettings(new UpdateEmailSettingsRequest(
            "Smtp", "smtp.example.com", 587, true, "user@example.com",
            new SecretValueUpsert(SecretValueAction.Clear, null),
            "no-reply@example.com", "Stashboard", "https://app.example.com"), default);

        var stored = await _dbContext.EmailSettings.FindAsync(EmailSettingsEntity.SingletonId);
        Assert.Null(stored!.PasswordEncrypted);
    }
}
