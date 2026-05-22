using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Stashboard.Api.Data;

namespace Stashboard.Api.Auth;

/// <summary>
/// Issues and rotates JWT access tokens + opaque refresh tokens.
/// Implements industry-standard refresh-token best practices: per-token hashing (HMAC-SHA256),
/// rotation, family-wide reuse detection, and absolute session windows.
/// </summary>
public sealed class TokenService(
    IOptions<JwtOptions> options,
    ApplicationDbContext db,
    TimeProvider time,
    ILogger<TokenService> logger) : ITokenService
{
    private readonly JwtOptions _opt = options.Value;

    public async Task<TokenPair> IssueAsync(UserEntity user, CancellationToken cancellationToken = default)
    {
        var familyId = Guid.NewGuid();
        var now = time.GetUtcNow().UtcDateTime;
        var sessionExpires = now.AddDays(_opt.SessionMaxDays);
        return await IssueInternalAsync(user, familyId, sessionExpires, now, cancellationToken);
    }

    public async Task<RotateResult> RotateAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
            return RotateResult.Fail(RotateFailureReason.NotFound);

        var hash = HashRefreshToken(refreshToken);
        var now = time.GetUtcNow().UtcDateTime;

        // Atomically claim the token: only proceed if it is still active. The single UPDATE
        // closes the read-then-write race window; concurrent refresh calls touching the same
        // row will see RowsAffected == 0.
        var affected = await db.RefreshTokens
            .Where(t => t.TokenHash == hash
                && t.RevokedUtc == null
                && t.ExpiresUtc > now
                && t.SessionExpiresUtc > now)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(t => t.RevokedUtc, (DateTime?)now)
                .SetProperty(t => t.RevokedReason, (RefreshTokenRevokeReason?)RefreshTokenRevokeReason.Rotated), cancellationToken);

        if (affected == 0)
        {
            // Row not active — load to find out why and react accordingly.
            var existing = await db.RefreshTokens
                .AsNoTracking()
                .FirstOrDefaultAsync(t => t.TokenHash == hash, cancellationToken);

            if (existing is null) return RotateResult.Fail(RotateFailureReason.NotFound);

            if (existing.RevokedUtc is not null)
            {
                // Reuse detection — someone replayed an already-revoked refresh token.
                // Treat as compromise, kill the entire family.
                await RevokeFamilyAsync(existing.FamilyId, RefreshTokenRevokeReason.Reuse, now, cancellationToken);
                logger.LogWarning("Refresh-token reuse detected for family {FamilyId} (user {UserId})",
                    existing.FamilyId, existing.UserId);
                return RotateResult.Fail(RotateFailureReason.Reused);
            }

            if (existing.SessionExpiresUtc <= now) return RotateResult.Fail(RotateFailureReason.SessionExpired);
            return RotateResult.Fail(RotateFailureReason.Expired);
        }

        // Re-fetch the token we just revoked to obtain its FamilyId and owner.
        var rotated = await db.RefreshTokens
            .FirstOrDefaultAsync(t => t.TokenHash == hash, cancellationToken);

        if (rotated is null) return RotateResult.Fail(RotateFailureReason.NotFound);

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == rotated.UserId, cancellationToken);
        if (user is null) return RotateResult.Fail(RotateFailureReason.UserMissing);

        var pair = await IssueInternalAsync(
            user,
            familyId: rotated.FamilyId,
            sessionExpires: rotated.SessionExpiresUtc,
            now: now,
            cancellationToken: cancellationToken,
            predecessor: rotated);

        return RotateResult.Success(pair, user);
    }

    public async Task RevokeAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(refreshToken)) return;
        var hash = HashRefreshToken(refreshToken);
        var now = time.GetUtcNow().UtcDateTime;

        var token = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, cancellationToken);
        if (token is null) return;
        await RevokeFamilyAsync(token.FamilyId, RefreshTokenRevokeReason.LoggedOut, now, cancellationToken);
    }

    public async Task RevokeAllForUserAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var now = time.GetUtcNow().UtcDateTime;
        await db.RefreshTokens
            .Where(t => t.UserId == userId && t.RevokedUtc == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.RevokedUtc, (DateTime?)now)
                .SetProperty(t => t.RevokedReason, (RefreshTokenRevokeReason?)RefreshTokenRevokeReason.SecurityStampChanged), cancellationToken);
    }

    public async Task<int> CleanupAsync(CancellationToken cancellationToken = default)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var cutoff = now.AddDays(-_opt.CleanupRetentionDays);
        return await db.RefreshTokens
            .Where(t => (t.RevokedUtc != null && t.RevokedUtc < cutoff)
                || (t.RevokedUtc == null && t.ExpiresUtc < cutoff))
            .ExecuteDeleteAsync(cancellationToken);
    }

    public TokenValidationParameters BuildValidationParameters() => BuildValidationParameters(_opt);

    public static TokenValidationParameters BuildValidationParameters(JwtOptions opt) => new()
    {
        ValidateIssuer = true,
        ValidIssuer = opt.Issuer,
        ValidateAudience = true,
        ValidAudience = opt.Audience,
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = SigningKey(opt),
        ValidateLifetime = true,
        ClockSkew = TimeSpan.FromSeconds(15),
        // Use our compact custom claim — the legacy XML-namespace mapping is disabled in
        // Program.cs by clearing JwtSecurityTokenHandler.DefaultInboundClaimTypeMap.
        NameClaimType = StashboardClaims.UserId,
    };

    // ── internals ─────────────────────────────────────────────────────────────

    private async Task<TokenPair> IssueInternalAsync(
        UserEntity user,
        Guid familyId,
        DateTime sessionExpires,
        DateTime now,
        CancellationToken cancellationToken,
        RefreshTokenEntity? predecessor = null)
    {
        var accessExpires = now.AddMinutes(_opt.AccessTokenMinutes);

        // Sliding expiry never exceeds the absolute session window.
        var refreshExpires = now.AddDays(_opt.RefreshTokenDays);
        if (refreshExpires > sessionExpires) refreshExpires = sessionExpires;

        var refresh = GenerateRefreshToken();
        var entity = new RefreshTokenEntity
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            TokenHash = HashRefreshToken(refresh),
            FamilyId = familyId,
            ExpiresUtc = refreshExpires,
            SessionExpiresUtc = sessionExpires,
            CreatedUtc = now,
        };
        db.RefreshTokens.Add(entity);

        if (predecessor is not null)
        {
            // Link predecessor → successor for audit / family traversal. Predecessor is
            // already revoked at this point (atomic ExecuteUpdate above).
            predecessor.ReplacedById = entity.Id;
        }

        await db.SaveChangesAsync(cancellationToken);

        var access = CreateAccessToken(user, accessExpires, now);
        return new TokenPair(access, accessExpires, refresh, refreshExpires, sessionExpires);
    }

    private async Task RevokeFamilyAsync(Guid familyId, RefreshTokenRevokeReason reason, DateTime now, CancellationToken cancellationToken)
    {
        await db.RefreshTokens
            .Where(t => t.FamilyId == familyId && t.RevokedUtc == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.RevokedUtc, (DateTime?)now)
                .SetProperty(t => t.RevokedReason, (RefreshTokenRevokeReason?)reason), cancellationToken);
    }

    private string CreateAccessToken(UserEntity user, DateTime expires, DateTime now)
    {
        var creds = new SigningCredentials(SigningKey(_opt), SecurityAlgorithms.HmacSha256);
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            new(StashboardClaims.UserId, user.Id.ToString()),
            new(StashboardClaims.Email, user.Email),
            new(StashboardClaims.SecurityStamp, user.SecurityStamp),
        };

        var token = new JwtSecurityToken(
            issuer: _opt.Issuer,
            audience: _opt.Audience,
            claims: claims,
            notBefore: now,
            expires: expires,
            signingCredentials: creds);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static SymmetricSecurityKey SigningKey(JwtOptions opt)
    {
        if (string.IsNullOrWhiteSpace(opt.Secret) || opt.Secret.Length < 32)
            throw new InvalidOperationException("Jwt:Secret must be at least 32 characters.");
        return new SymmetricSecurityKey(Encoding.UTF8.GetBytes(opt.Secret));
    }

    private static string GenerateRefreshToken()
    {
        // 384 bits of entropy — far beyond the 128 minimum recommended by OWASP.
        var bytes = RandomNumberGenerator.GetBytes(48);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>
    /// Computes a peppered HMAC-SHA256 hash of the refresh token. Hashing with the JWT secret
    /// as the HMAC key means even a database leak does not let an attacker reverse-lookup
    /// stolen refresh tokens against a precomputed table.
    /// </summary>
    private string HashRefreshToken(string token)
    {
        var key = Encoding.UTF8.GetBytes(_opt.Secret);
        var data = Encoding.UTF8.GetBytes(token);
        var hash = HMACSHA256.HashData(key, data);
        return Convert.ToHexString(hash);
    }
}
