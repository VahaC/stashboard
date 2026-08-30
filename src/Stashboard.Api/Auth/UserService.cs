using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Stashboard.Api.Data;
using Stashboard.Core.Abstractions;

namespace Stashboard.Api.Auth;

public sealed class UserService(
    ApplicationDbContext db,
    IPasswordHasher hasher,
    IEncryptionService encryption,
    IOptions<JwtOptions> options,
    TimeProvider time) : IUserService
{
    private readonly JwtOptions _opt = options.Value;

    public async Task<AuthResult> RegisterAsync(string email, string password, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);

        var normalized = IUserService.Normalize(email);

        if (await db.Users.AnyAsync(u => u.NormalizedEmail == normalized, cancellationToken))
            return new AuthResult(null, new AuthFailure(AuthFailureReason.EmailAlreadyTaken, "Email is already registered."));

        var user = new UserEntity
        {
            Email = email.Trim(),
            NormalizedEmail = normalized,
            PasswordHash = hasher.Hash(password),
            CreatedUtc = time.GetUtcNow().UtcDateTime,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync(cancellationToken);
        return new AuthResult(user, null);
    }

    public async Task<AuthResult> ValidatePasswordAsync(string email, string password, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            return new AuthResult(null, new AuthFailure(AuthFailureReason.InvalidCredentials, "Invalid credentials."));

        var normalized = IUserService.Normalize(email);
        var user = await db.Users.FirstOrDefaultAsync(u => u.NormalizedEmail == normalized, cancellationToken);
        var now = time.GetUtcNow().UtcDateTime;

        if (user is null)
            return new AuthResult(null, new AuthFailure(AuthFailureReason.InvalidCredentials, "Invalid credentials."));

        if (user.LockoutEndUtc is { } end && end > now)
            return new AuthResult(null, new AuthFailure(AuthFailureReason.AccountLocked,
                $"Account is locked until {end:O}."));

        if (!hasher.Verify(password, user.PasswordHash))
        {
            user.FailedAccessCount++;
            if (user.FailedAccessCount >= _opt.MaxFailedAccessAttempts)
            {
                user.LockoutEndUtc = now.AddMinutes(_opt.LockoutMinutes);
                user.FailedAccessCount = 0;
                await db.SaveChangesAsync(cancellationToken);
                return new AuthResult(null, new AuthFailure(AuthFailureReason.AccountLocked,
                    $"Account is locked until {user.LockoutEndUtc:O}."));
            }
            await db.SaveChangesAsync(cancellationToken);
            return new AuthResult(null, new AuthFailure(AuthFailureReason.InvalidCredentials, "Invalid credentials."));
        }

        user.FailedAccessCount = 0;
        user.LockoutEndUtc = null;
        user.LastLoginUtc = now;
        await db.SaveChangesAsync(cancellationToken);
        return new AuthResult(user, null);
    }

    public async Task<UserEntity?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, cancellationToken);
        MaterializeTelegramToken(user);
        return user;
    }

    public async Task<UserEntity?> FindByEmailAsync(string email, CancellationToken cancellationToken = default)
    {
        var normalized = IUserService.Normalize(email);
        var user = await db.Users.FirstOrDefaultAsync(u => u.NormalizedEmail == normalized, cancellationToken);
        MaterializeTelegramToken(user);
        return user;
    }

    public async Task RotateSecurityStampAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null) return;
        user.SecurityStamp = Guid.NewGuid().ToString("N");
        await db.SaveChangesAsync(cancellationToken);
    }

    // ── Token helpers ────────────────────────────────────────────────────────
    // Plaintext tokens are sent by email; only their SHA-256 hash is stored. Same
    // principle as refresh-token storage: a DB leak does not expose live tokens.

    private static (string plain, string hash) NewToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        var plain = Base64UrlEncode(bytes);
        var hash = HashToken(plain);
        return (plain, hash);
    }

    private static string HashToken(string token)
        => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));

    private static string Base64UrlEncode(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private async Task RevokeAllRefreshTokensAsync(Guid userId, DateTime now, CancellationToken cancellationToken)
    {
        await db.RefreshTokens
            .Where(t => t.UserId == userId && t.RevokedUtc == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedUtc, (DateTime?)now), cancellationToken);
    }

    private void MaterializeTelegramToken(UserEntity? user)
    {
        if (user is null)
            return;

        if (string.IsNullOrWhiteSpace(user.TelegramBotTokenEncrypted))
        {
            user.TelegramBotToken = null;
            return;
        }

        try
        {
            user.TelegramBotToken = encryption.Decrypt(user.TelegramBotTokenEncrypted);
        }
        catch (CryptographicException)
        {
            user.TelegramBotToken = user.TelegramBotTokenEncrypted;
        }
        catch (FormatException)
        {
            user.TelegramBotToken = user.TelegramBotTokenEncrypted;
        }
    }

    private static string? NormalizeOptionalValue(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // ── Email confirmation ───────────────────────────────────────────────────

    public async Task<TokenIssueResult> CreateEmailConfirmationTokenAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null)
            return new TokenIssueResult(null, null, new AuthFailure(AuthFailureReason.UserNotFound, "User not found."));
        if (user.EmailConfirmed)
            return new TokenIssueResult(null, null, new AuthFailure(AuthFailureReason.InvalidToken, "Email already confirmed."));

        var (plain, hash) = NewToken();
        user.EmailConfirmationTokenHash = hash;
        user.EmailConfirmationTokenExpiresUtc = time.GetUtcNow().UtcDateTime
            .AddHours(_opt.EmailConfirmationTokenLifetimeHours);
        await db.SaveChangesAsync(cancellationToken);
        return new TokenIssueResult(user, plain, null);
    }

    public async Task<OperationResult> ConfirmEmailAsync(string email, string token, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(token))
            return OperationResult.Fail(AuthFailureReason.InvalidToken, "Invalid token.");

        var normalized = IUserService.Normalize(email);
        var user = await db.Users.FirstOrDefaultAsync(u => u.NormalizedEmail == normalized, cancellationToken);
        var now = time.GetUtcNow().UtcDateTime;

        if (user is null || user.EmailConfirmationTokenHash is null
            || user.EmailConfirmationTokenExpiresUtc is null
            || user.EmailConfirmationTokenExpiresUtc < now
            || !CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(HashToken(token)),
                System.Text.Encoding.UTF8.GetBytes(user.EmailConfirmationTokenHash)))
            return OperationResult.Fail(AuthFailureReason.InvalidToken, "Invalid or expired token.");

        user.EmailConfirmed = true;
        user.EmailConfirmationTokenHash = null;
        user.EmailConfirmationTokenExpiresUtc = null;
        await db.SaveChangesAsync(cancellationToken);
        return OperationResult.Ok();
    }

    // ── Password reset ───────────────────────────────────────────────────────

    public async Task<TokenIssueResult> CreatePasswordResetTokenAsync(string email, CancellationToken cancellationToken = default)
    {
        var user = await FindByEmailAsync(email, cancellationToken);
        if (user is null)
            // Caller must NOT leak existence; return failure but treat externally as success.
            return new TokenIssueResult(null, null, new AuthFailure(AuthFailureReason.UserNotFound, "User not found."));

        var (plain, hash) = NewToken();
        user.PasswordResetTokenHash = hash;
        user.PasswordResetTokenExpiresUtc = time.GetUtcNow().UtcDateTime
            .AddHours(_opt.PasswordResetTokenLifetimeHours);
        await db.SaveChangesAsync(cancellationToken);
        return new TokenIssueResult(user, plain, null);
    }

    public async Task<OperationResult> ResetPasswordAsync(string email, string token, string newPassword, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(newPassword))
            return OperationResult.Fail(AuthFailureReason.InvalidToken, "Invalid token.");

        var normalized = IUserService.Normalize(email);
        var user = await db.Users.FirstOrDefaultAsync(u => u.NormalizedEmail == normalized, cancellationToken);
        var now = time.GetUtcNow().UtcDateTime;

        if (user is null || user.PasswordResetTokenHash is null
            || user.PasswordResetTokenExpiresUtc is null
            || user.PasswordResetTokenExpiresUtc < now
            || !CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(HashToken(token)),
                System.Text.Encoding.UTF8.GetBytes(user.PasswordResetTokenHash)))
            return OperationResult.Fail(AuthFailureReason.InvalidToken, "Invalid or expired token.");

        user.PasswordHash = hasher.Hash(newPassword);
        user.PasswordResetTokenHash = null;
        user.PasswordResetTokenExpiresUtc = null;
        user.SecurityStamp = Guid.NewGuid().ToString("N");
        user.FailedAccessCount = 0;
        user.LockoutEndUtc = null;
        await RevokeAllRefreshTokensAsync(user.Id, now, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return OperationResult.Ok();
    }

    // ── Password change (authenticated) ──────────────────────────────────────

    public async Task<OperationResult> ChangePasswordAsync(Guid userId, string currentPassword, string newPassword, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(newPassword))
            return OperationResult.Fail(AuthFailureReason.InvalidCredentials, "New password is required.");

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null)
            return OperationResult.Fail(AuthFailureReason.UserNotFound, "User not found.");
        if (!hasher.Verify(currentPassword, user.PasswordHash))
            return OperationResult.Fail(AuthFailureReason.InvalidCredentials, "Current password is incorrect.");

        user.PasswordHash = hasher.Hash(newPassword);
        user.SecurityStamp = Guid.NewGuid().ToString("N");
        var now = time.GetUtcNow().UtcDateTime;
        await RevokeAllRefreshTokensAsync(user.Id, now, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return OperationResult.Ok();
    }

    public async Task<OperationResult> VerifyPasswordAsync(Guid userId, string currentPassword, CancellationToken cancellationToken = default)
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null)
            return OperationResult.Fail(AuthFailureReason.UserNotFound, "User not found.");
        if (!hasher.Verify(currentPassword, user.PasswordHash))
            return OperationResult.Fail(AuthFailureReason.InvalidCredentials, "Current password is incorrect.");
        return OperationResult.Ok();
    }

    // ── Email change ─────────────────────────────────────────────────────────

    public async Task<TokenIssueResult> RequestEmailChangeAsync(Guid userId, string newEmail, string currentPassword, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(newEmail))
            return new TokenIssueResult(null, null, new AuthFailure(AuthFailureReason.InvalidCredentials, "New email is required."));

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null)
            return new TokenIssueResult(null, null, new AuthFailure(AuthFailureReason.UserNotFound, "User not found."));
        if (!hasher.Verify(currentPassword, user.PasswordHash))
            return new TokenIssueResult(null, null, new AuthFailure(AuthFailureReason.InvalidCredentials, "Current password is incorrect."));

        var normalized = IUserService.Normalize(newEmail);
        if (await db.Users.AnyAsync(u => u.NormalizedEmail == normalized && u.Id != userId, cancellationToken))
            return new TokenIssueResult(null, null, new AuthFailure(AuthFailureReason.EmailAlreadyTaken, "Email is already registered."));

        var (plain, hash) = NewToken();
        user.PendingEmail = newEmail.Trim();
        user.PendingEmailTokenHash = hash;
        user.PendingEmailTokenExpiresUtc = time.GetUtcNow().UtcDateTime
            .AddHours(_opt.EmailConfirmationTokenLifetimeHours);
        await db.SaveChangesAsync(cancellationToken);
        return new TokenIssueResult(user, plain, null);
    }

    public async Task<OperationResult> ConfirmEmailChangeAsync(Guid userId, string token, CancellationToken cancellationToken = default)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        var now = time.GetUtcNow().UtcDateTime;

        if (user is null || string.IsNullOrEmpty(user.PendingEmail)
            || user.PendingEmailTokenHash is null
            || user.PendingEmailTokenExpiresUtc is null
            || user.PendingEmailTokenExpiresUtc < now
            || string.IsNullOrWhiteSpace(token)
            || !CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(HashToken(token)),
                System.Text.Encoding.UTF8.GetBytes(user.PendingEmailTokenHash)))
            return OperationResult.Fail(AuthFailureReason.InvalidToken, "Invalid or expired token.");

        var normalized = IUserService.Normalize(user.PendingEmail);
        if (await db.Users.AnyAsync(u => u.NormalizedEmail == normalized && u.Id != userId, cancellationToken))
            return OperationResult.Fail(AuthFailureReason.EmailAlreadyTaken, "Email is already registered.");

        user.Email = user.PendingEmail;
        user.NormalizedEmail = normalized;
        user.EmailConfirmed = true;
        user.PendingEmail = null;
        user.PendingEmailTokenHash = null;
        user.PendingEmailTokenExpiresUtc = null;
        user.SecurityStamp = Guid.NewGuid().ToString("N");
        await RevokeAllRefreshTokensAsync(user.Id, now, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return OperationResult.Ok();
    }

    // ── Profile ──────────────────────────────────────────────────────────────

    public async Task<OperationResult> UpdateProfileAsync(Guid userId, string? displayName, string? theme, CancellationToken cancellationToken = default)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null)
            return OperationResult.Fail(AuthFailureReason.UserNotFound, "User not found.");

        // displayName: always overwrite (matches existing PATCH behavior — "" clears).
        user.DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim();

        // theme: partial — only update when caller provided a value, since the column is NOT NULL.
        if (theme is not null)
        {
            if (theme is not ("system" or "light" or "dark"))
                return OperationResult.Fail(AuthFailureReason.InvalidCredentials, "Theme must be 'system', 'light', or 'dark'.");
            user.Theme = theme;
        }

        await db.SaveChangesAsync(cancellationToken);
        return OperationResult.Ok();
    }

    public async Task<OperationResult> SetThemeAsync(Guid userId, string theme, CancellationToken cancellationToken = default)
    {
        if (theme is not ("system" or "light" or "dark"))
            return OperationResult.Fail(AuthFailureReason.InvalidCredentials, "Theme must be 'system', 'light', or 'dark'.");

        var rows = await db.Users
            .Where(u => u.Id == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.Theme, theme), cancellationToken);
        if (rows == 0)
            return OperationResult.Fail(AuthFailureReason.UserNotFound, "User not found.");

        var tracked = db.ChangeTracker.Entries<UserEntity>()
            .FirstOrDefault(e => e.Entity.Id == userId);
        if (tracked is not null) tracked.Entity.Theme = theme;

        return OperationResult.Ok();
    }

    public async Task<OperationResult> SetDashboardPreferencesAsync(Guid userId, string sortMode, bool groupByCategory, CancellationToken cancellationToken = default)
    {
        if (sortMode is not ("name" or "category"))
            return OperationResult.Fail(AuthFailureReason.InvalidCredentials, "Sort mode must be 'name' or 'category'.");

        var rows = await db.Users
            .Where(u => u.Id == userId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(user => user.DashboardSortMode, sortMode)
                    .SetProperty(user => user.DashboardGroupByCategory, groupByCategory),
                cancellationToken);
        if (rows == 0)
            return OperationResult.Fail(AuthFailureReason.UserNotFound, "User not found.");

        var tracked = db.ChangeTracker.Entries<UserEntity>()
            .FirstOrDefault(entry => entry.Entity.Id == userId);
        if (tracked is not null)
        {
            tracked.Entity.DashboardSortMode = sortMode;
            tracked.Entity.DashboardGroupByCategory = groupByCategory;
        }

        return OperationResult.Ok();
    }

    public async Task<OperationResult> UpdateTelegramSettingsAsync(
        Guid userId,
        string? botToken,
        string? chatId,
        bool notificationsEnabled,
        CancellationToken cancellationToken = default)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null)
            return OperationResult.Fail(AuthFailureReason.UserNotFound, "User not found.");

        user.TelegramBotToken = NormalizeOptionalValue(botToken);
        user.TelegramBotTokenEncrypted = string.IsNullOrWhiteSpace(user.TelegramBotToken)
            ? null
            : encryption.Encrypt(user.TelegramBotToken);
        user.TelegramChatId = NormalizeOptionalValue(chatId);
        user.TelegramNotificationsEnabled = notificationsEnabled
            && !string.IsNullOrWhiteSpace(user.TelegramBotTokenEncrypted)
            && !string.IsNullOrWhiteSpace(user.TelegramChatId);

        await db.SaveChangesAsync(cancellationToken);
        return OperationResult.Ok();
    }

    // ── Account deletion ─────────────────────────────────────────────────────

    public async Task<OperationResult> DeleteAccountAsync(Guid userId, string currentPassword, CancellationToken cancellationToken = default)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null)
            return OperationResult.Fail(AuthFailureReason.UserNotFound, "User not found.");
        if (!hasher.Verify(currentPassword, user.PasswordHash))
            return OperationResult.Fail(AuthFailureReason.InvalidCredentials, "Current password is incorrect.");

        // Cascade-delete refresh tokens via FK; user-owned domain entities (services, tags,
        // categories) are NOT cascaded by current model and would orphan — so we delete the
        // user row only after explicitly cleaning user-owned data here, when entities gain a
        // user FK with cascade. For now, refresh tokens cascade is enough.
        db.Users.Remove(user);
        await db.SaveChangesAsync(cancellationToken);
        return OperationResult.Ok();
    }
}
