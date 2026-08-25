using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
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
    private readonly PrefixEncryption _encryption = new();
    private readonly JwtOptions _opt = new()
    {
        Secret = "test-secret-test-secret-test-secret-test-secret",
        MaxFailedAccessAttempts = 3,
        LockoutMinutes = 10,
    };

    // A valid Base32 TOTP secret used to seed 2FA-enabled users deterministically.
    private const string TotpSecret = "JBSWY3DPEHPK3PXP";

    private AuthController _ctrl = default!;
    private UserService _users = default!;
    private TokenService _tokens = default!;
    private TwoFactorService _twoFactor = default!;

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        _users = new UserService(_dbContext, _hasher, _encryption, Options.Create(_opt), _time);
        _tokens = new TokenService(Options.Create(_opt), _dbContext, _time, NullLogger<TokenService>.Instance);
        _twoFactor = new TwoFactorService(_dbContext, _hasher, _encryption, Options.Create(_opt), _time);
        _ctrl = new AuthController(_users, _tokens, _twoFactor, TestMapperFactory.Create(), Options.Create(_opt));
    }

    private static string CurrentTotp(string base32) =>
        new OtpNet.Totp(OtpNet.Base32Encoding.ToBytes(base32)).ComputeTotp();

    /// <summary>Registers a user and flips on 2FA with a known secret (no replay step consumed yet).</summary>
    private async Task<Guid> RegisterWithTwoFactor(string email = "u@x", string password = "P@ssword1")
    {
        var reg = await _ctrl.Register(new RegisterRequest(email, password), default);
        var id = ((AuthResponse)((OkObjectResult)reg.Result!).Value!).User.Id;
        var user = await _dbContext.Users.Where(u => u.Id == id).FirstAsync();
        user.TwoFactorEnabled = true;
        user.TwoFactorSecretEncrypted = _encryption.Encrypt(TotpSecret);
        await _dbContext.SaveChangesAsync();
        return id;
    }

    private async Task<string> ChallengeTokenFor(string email = "u@x", string password = "P@ssword1")
    {
        var login = await _ctrl.Login(new LoginRequest(email, password), default);
        var ok = Assert.IsType<OkObjectResult>(login.Result);
        return Assert.IsType<TwoFactorChallengeResponse>(ok.Value).ChallengeToken;
    }

    private sealed class PrefixEncryption : Stashboard.Core.Abstractions.IEncryptionService
    {
        public string Encrypt(string plaintext) => "enc:" + plaintext;
        public string Decrypt(string ciphertext) => ciphertext.StartsWith("enc:") ? ciphertext[4..] : ciphertext;
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

    // ── Two-factor login flow ─────────────────────────────────────────────────

    [Fact]
    public async Task Login_TwoFactorEnabled_ReturnsChallengeWithoutTokens()
    {
        await RegisterWithTwoFactor();

        var result = await _ctrl.Login(new LoginRequest("u@x", "P@ssword1"), default);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var challenge = Assert.IsType<TwoFactorChallengeResponse>(ok.Value);
        Assert.True(challenge.RequiresTwoFactor);
        Assert.NotEmpty(challenge.ChallengeToken);
    }

    [Fact]
    public async Task LoginTwoFactor_ValidCode_IssuesTokens()
    {
        await RegisterWithTwoFactor();
        var token = await ChallengeTokenFor();

        var result = await _ctrl.LoginTwoFactor(new TwoFactorLoginRequest(token, CurrentTotp(TotpSecret)), default);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var body = Assert.IsType<AuthResponse>(ok.Value);
        Assert.NotEmpty(body.AccessToken);
        Assert.NotEmpty(body.RefreshToken);
    }

    [Fact]
    public async Task LoginTwoFactor_WrongCode_Returns401()
    {
        await RegisterWithTwoFactor();
        var token = await ChallengeTokenFor();

        var result = await _ctrl.LoginTwoFactor(new TwoFactorLoginRequest(token, "000000"), default);

        Assert.IsType<UnauthorizedObjectResult>(result.Result);
    }

    [Fact]
    public async Task LoginTwoFactor_RepeatedWrongCodes_LocksAccount()
    {
        await RegisterWithTwoFactor();
        var token = await ChallengeTokenFor();

        // MaxFailedAccessAttempts == 3 in the test options — the third wrong code trips the lockout.
        await _ctrl.LoginTwoFactor(new TwoFactorLoginRequest(token, "000000"), default);
        await _ctrl.LoginTwoFactor(new TwoFactorLoginRequest(token, "000000"), default);
        var locked = await _ctrl.LoginTwoFactor(new TwoFactorLoginRequest(token, "000000"), default);

        var status = Assert.IsType<ObjectResult>(locked.Result);
        Assert.Equal(StatusCodes.Status423Locked, status.StatusCode);
    }

    [Fact]
    public async Task LoginTwoFactor_RecoveryCode_WorksOnceThenFails()
    {
        var id = await RegisterWithTwoFactor();
        _dbContext.TwoFactorRecoveryCodes.Add(new Stashboard.Api.Data.TwoFactorRecoveryCodeEntity
        {
            UserId = id, CodeHash = _hasher.Hash("ABCDEFGHIJ"),
        });
        await _dbContext.SaveChangesAsync();

        var first = await _ctrl.LoginTwoFactor(new TwoFactorLoginRequest(await ChallengeTokenFor(), "ABCDE-FGHIJ"), default);
        Assert.IsType<OkObjectResult>(first.Result);

        var second = await _ctrl.LoginTwoFactor(new TwoFactorLoginRequest(await ChallengeTokenFor(), "ABCDE-FGHIJ"), default);
        Assert.IsType<UnauthorizedObjectResult>(second.Result);
    }

    [Fact]
    public async Task LoginTwoFactor_ChallengeStaleAfterSecurityStampChange_Returns401()
    {
        var id = await RegisterWithTwoFactor();
        var token = await ChallengeTokenFor();

        // A password change / logout-all between the two steps rotates the stamp.
        await _users.RotateSecurityStampAsync(id);

        var result = await _ctrl.LoginTwoFactor(new TwoFactorLoginRequest(token, CurrentTotp(TotpSecret)), default);

        Assert.IsType<UnauthorizedObjectResult>(result.Result);
    }

    [Fact]
    public async Task LoginTwoFactor_InvalidChallengeToken_Returns401()
    {
        var result = await _ctrl.LoginTwoFactor(new TwoFactorLoginRequest("not-a-real-token", "123456"), default);

        Assert.IsType<UnauthorizedObjectResult>(result.Result);
    }

    [Fact]
    public async Task LoginTwoFactor_ExpiredChallenge_Returns401()
    {
        await RegisterWithTwoFactor();
        var token = await ChallengeTokenFor();

        // Past the challenge lifetime (default 5 min) — the token is no longer accepted.
        _time.Advance(TimeSpan.FromMinutes(_opt.TwoFactorChallengeMinutes + 1));

        var result = await _ctrl.LoginTwoFactor(new TwoFactorLoginRequest(token, CurrentTotp(TotpSecret)), default);

        Assert.IsType<UnauthorizedObjectResult>(result.Result);
    }
}




