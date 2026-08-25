using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OtpNet;
using Stashboard.Api.Data;
using Stashboard.Core.Abstractions;

namespace Stashboard.Api.Auth;

/// <summary>
/// TOTP two-factor authentication built on <see href="https://www.nuget.org/packages/Otp.NET">Otp.NET</see>
/// (RFC 6238). Verification uses a ±1 step window for clock tolerance and rejects replayed codes via
/// <see cref="UserEntity.TwoFactorLastUsedStep"/>. The shared secret is encrypted at rest; recovery
/// codes are stored as PBKDF2 hashes and consumed once each.
/// </summary>
public sealed class TwoFactorService(
    ApplicationDbContext db,
    IPasswordHasher hasher,
    IEncryptionService encryption,
    IOptions<JwtOptions> options,
    TimeProvider time) : ITwoFactorService
{
    private readonly JwtOptions _opt = options.Value;

    private const string Issuer = "Stashboard";
    private const int SecretBytes = 20;          // 160-bit secret — the standard size for SHA1 TOTP.
    private const int RecoveryCodeCount = 10;
    private const int RecoveryCodeLength = 10;
    // Unambiguous alphabet — excludes 0/O/1/I/L to avoid transcription errors.
    private const string RecoveryAlphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";

    // ±1 30-second step tolerates ~±30–60s of clock drift between the device and the server.
    private static readonly VerificationWindow Window = new(previous: 1, future: 1);

    // ── Enrollment ─────────────────────────────────────────────────────────────

    public async Task<TwoFactorEnrollment?> BeginEnrollAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null) return null;

        var base32 = Base32Encoding.ToString(KeyGeneration.GenerateRandomKey(SecretBytes));

        // Stored encrypted but NOT enabled until the first code is confirmed; this also overwrites
        // any earlier abandoned enrollment. Login is gated on TwoFactorEnabled, never on the secret.
        user.TwoFactorSecretEncrypted = encryption.Encrypt(base32);
        user.TwoFactorEnabled = false;
        user.TwoFactorLastUsedStep = null;
        await db.SaveChangesAsync(cancellationToken);

        return new TwoFactorEnrollment(BuildOtpauthUri(user.Email, base32), FormatManualKey(base32));
    }

    public async Task<RecoveryCodesResult> EnableAsync(Guid userId, string code, CancellationToken cancellationToken = default)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null) return RecoveryCodesResult.Fail(AuthFailureReason.UserNotFound, "User not found.");
        if (user.TwoFactorEnabled) return RecoveryCodesResult.Fail(AuthFailureReason.InvalidToken, "Two-factor authentication is already enabled.");
        if (string.IsNullOrWhiteSpace(user.TwoFactorSecretEncrypted))
            return RecoveryCodesResult.Fail(AuthFailureReason.InvalidToken, "Start enrollment first.");

        var secret = DecryptSecret(user);
        if (secret is null || !TryVerifyTotp(secret, code, user.TwoFactorLastUsedStep, out var step))
            return RecoveryCodesResult.Fail(AuthFailureReason.InvalidTwoFactorCode, "Invalid authentication code.");

        user.TwoFactorEnabled = true;
        user.TwoFactorLastUsedStep = step;

        var codes = await ReplaceRecoveryCodesAsync(user.Id, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return RecoveryCodesResult.Ok(codes);
    }

    // ── Disable / regenerate (security-sensitive) ────────────────────────────────

    public async Task<OperationResult> DisableAsync(Guid userId, string currentPassword, CancellationToken cancellationToken = default)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null) return OperationResult.Fail(AuthFailureReason.UserNotFound, "User not found.");
        if (!hasher.Verify(currentPassword, user.PasswordHash))
            return OperationResult.Fail(AuthFailureReason.InvalidCredentials, "Current password is incorrect.");

        user.TwoFactorEnabled = false;
        user.TwoFactorSecretEncrypted = null;
        user.TwoFactorLastUsedStep = null;
        await db.TwoFactorRecoveryCodes.Where(c => c.UserId == userId).ExecuteDeleteAsync(cancellationToken);

        await InvalidateAllSessionsAsync(user, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return OperationResult.Ok();
    }

    public async Task<RecoveryCodesResult> RegenerateRecoveryCodesAsync(Guid userId, string currentPassword, CancellationToken cancellationToken = default)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null) return RecoveryCodesResult.Fail(AuthFailureReason.UserNotFound, "User not found.");
        if (!user.TwoFactorEnabled) return RecoveryCodesResult.Fail(AuthFailureReason.InvalidToken, "Two-factor authentication is not enabled.");
        if (!hasher.Verify(currentPassword, user.PasswordHash))
            return RecoveryCodesResult.Fail(AuthFailureReason.InvalidCredentials, "Current password is incorrect.");

        var codes = await ReplaceRecoveryCodesAsync(user.Id, cancellationToken);
        await InvalidateAllSessionsAsync(user, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return RecoveryCodesResult.Ok(codes);
    }

    // ── Login second step ────────────────────────────────────────────────────────

    public async Task<AuthResult> CompleteLoginAsync(Guid userId, string code, CancellationToken cancellationToken = default)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null || !user.TwoFactorEnabled)
            return new AuthResult(null, new AuthFailure(AuthFailureReason.InvalidCredentials, "Invalid credentials."));

        var now = time.GetUtcNow().UtcDateTime;
        if (user.LockoutEndUtc is { } end && end > now)
            return new AuthResult(null, new AuthFailure(AuthFailureReason.AccountLocked, $"Account is locked until {end:O}."));

        // 1) TOTP code.
        var secret = DecryptSecret(user);
        if (secret is not null && TryVerifyTotp(secret, code, user.TwoFactorLastUsedStep, out var step))
        {
            user.TwoFactorLastUsedStep = step;
            return await SucceedAsync(user, now, cancellationToken);
        }

        // 2) One-time recovery code.
        if (await TryConsumeRecoveryCodeAsync(user.Id, code, now, cancellationToken))
            return await SucceedAsync(user, now, cancellationToken);

        // 3) Neither matched — count as a failed attempt (shared lockout with the password step).
        return await FailAsync(user, now, cancellationToken);
    }

    // ── internals ──────────────────────────────────────────────────────────────

    private async Task<AuthResult> SucceedAsync(UserEntity user, DateTime now, CancellationToken cancellationToken)
    {
        user.FailedAccessCount = 0;
        user.LockoutEndUtc = null;
        user.LastLoginUtc = now;
        await db.SaveChangesAsync(cancellationToken);
        return new AuthResult(user, null);
    }

    private async Task<AuthResult> FailAsync(UserEntity user, DateTime now, CancellationToken cancellationToken)
    {
        user.FailedAccessCount++;
        if (user.FailedAccessCount >= _opt.MaxFailedAccessAttempts)
        {
            user.LockoutEndUtc = now.AddMinutes(_opt.LockoutMinutes);
            user.FailedAccessCount = 0;
            await db.SaveChangesAsync(cancellationToken);
            return new AuthResult(null, new AuthFailure(AuthFailureReason.AccountLocked, $"Account is locked until {user.LockoutEndUtc:O}."));
        }
        await db.SaveChangesAsync(cancellationToken);
        return new AuthResult(null, new AuthFailure(AuthFailureReason.InvalidTwoFactorCode, "Invalid authentication code."));
    }

    /// <summary>Rotates the SecurityStamp and revokes every active refresh token for the user.</summary>
    private async Task InvalidateAllSessionsAsync(UserEntity user, CancellationToken cancellationToken)
    {
        user.SecurityStamp = Guid.NewGuid().ToString("N");
        var now = time.GetUtcNow().UtcDateTime;
        await db.RefreshTokens
            .Where(t => t.UserId == user.Id && t.RevokedUtc == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.RevokedUtc, (DateTime?)now)
                .SetProperty(t => t.RevokedReason, (RefreshTokenRevokeReason?)RefreshTokenRevokeReason.SecurityStampChanged), cancellationToken);
    }

    private async Task<List<string>> ReplaceRecoveryCodesAsync(Guid userId, CancellationToken cancellationToken)
    {
        await db.TwoFactorRecoveryCodes.Where(c => c.UserId == userId).ExecuteDeleteAsync(cancellationToken);

        var now = time.GetUtcNow().UtcDateTime;
        var plaintext = new List<string>(RecoveryCodeCount);
        for (var i = 0; i < RecoveryCodeCount; i++)
        {
            var raw = GenerateRecoveryCode();
            plaintext.Add(FormatRecoveryCode(raw));
            db.TwoFactorRecoveryCodes.Add(new TwoFactorRecoveryCodeEntity
            {
                UserId = userId,
                CodeHash = hasher.Hash(raw),
                CreatedUtc = now,
            });
        }
        return plaintext;
    }

    private async Task<bool> TryConsumeRecoveryCodeAsync(Guid userId, string input, DateTime now, CancellationToken cancellationToken)
    {
        var canonical = Canonical(input);
        if (canonical.Length == 0) return false;

        var candidates = await db.TwoFactorRecoveryCodes
            .Where(c => c.UserId == userId && c.UsedUtc == null)
            .ToListAsync(cancellationToken);

        foreach (var row in candidates)
        {
            if (hasher.Verify(canonical, row.CodeHash))
            {
                row.UsedUtc = now;
                return true;
            }
        }
        return false;
    }

    private string? DecryptSecret(UserEntity user)
    {
        if (string.IsNullOrWhiteSpace(user.TwoFactorSecretEncrypted)) return null;
        try { return encryption.Decrypt(user.TwoFactorSecretEncrypted); }
        catch (CryptographicException) { return null; }
        catch (FormatException) { return null; }
    }

    private static bool TryVerifyTotp(string base32Secret, string code, long? lastUsedStep, out long step)
    {
        step = 0;
        if (string.IsNullOrWhiteSpace(code)) return false;

        byte[] bytes;
        try { bytes = Base32Encoding.ToBytes(base32Secret); }
        catch (ArgumentException) { return false; }

        var totp = new Totp(bytes);
        if (!totp.VerifyTotp(code.Trim(), out var matched, Window)) return false;
        // Replay guard — a code at or before the last consumed step is rejected.
        if (lastUsedStep is { } last && matched <= last) return false;

        step = matched;
        return true;
    }

    private static string BuildOtpauthUri(string email, string base32Secret)
    {
        var label = Uri.EscapeDataString($"{Issuer}:{email}");
        var issuer = Uri.EscapeDataString(Issuer);
        return $"otpauth://totp/{label}?secret={base32Secret}&issuer={issuer}&algorithm=SHA1&digits=6&period=30";
    }

    /// <summary>Groups the Base32 secret into 4-char blocks so it's easier to type by hand.</summary>
    private static string FormatManualKey(string base32) =>
        string.Join(' ', Enumerable.Range(0, (base32.Length + 3) / 4).Select(i => base32.Substring(i * 4, Math.Min(4, base32.Length - i * 4))));

    private static string GenerateRecoveryCode()
    {
        var chars = new char[RecoveryCodeLength];
        for (var i = 0; i < chars.Length; i++)
            chars[i] = RecoveryAlphabet[RandomNumberGenerator.GetInt32(RecoveryAlphabet.Length)];
        return new string(chars);
    }

    /// <summary>Display form with a separator (e.g. <c>ABCDE-FGHIJ</c>); the hash is over the canonical form.</summary>
    private static string FormatRecoveryCode(string raw) => $"{raw[..5]}-{raw[5..]}";

    /// <summary>Strips separators/whitespace and upper-cases so the user can paste either form.</summary>
    private static string Canonical(string input)
    {
        var chars = new char[input.Length];
        var n = 0;
        foreach (var ch in input)
            if (char.IsLetterOrDigit(ch)) chars[n++] = char.ToUpperInvariant(ch);
        return new string(chars, 0, n);
    }
}
