using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Stashboard.Api.Data;

namespace Stashboard.Api.Auth.PersonalAccessTokens;

/// <summary>
/// Issues and validates personal access tokens. Mirrors the refresh-token security model:
/// the raw secret is never stored — only a peppered HMAC-SHA256 hash — and lookups are a single
/// indexed query on that hash.
/// </summary>
public sealed class PersonalAccessTokenService(
    ApplicationDbContext db,
    IOptions<JwtOptions> jwtOptions,
    TimeProvider time) : IPersonalAccessTokenService
{
    /// <summary>Prefix that distinguishes a PAT from a JWT in the <c>Authorization</c> header.</summary>
    public const string TokenPrefix = "sb_pat_";

    /// <summary>Don't write <c>LastUsedUtc</c> more than once per this window — keeps a polling script off the write path.</summary>
    private static readonly TimeSpan LastUsedThrottle = TimeSpan.FromSeconds(60);

    private readonly JwtOptions _jwt = jwtOptions.Value;

    public async Task<CreatePatResult> CreateAsync(
        Guid userId, string name, PersonalAccessTokenScope scope, DateTime? expiresUtc, CancellationToken cancellationToken = default)
    {
        var secret = GenerateSecret();
        var token = TokenPrefix + secret;
        var now = time.GetUtcNow().UtcDateTime;

        var entity = new PersonalAccessTokenEntity
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Name = name.Trim(),
            TokenHash = Hash(token),
            // Non-secret hint: prefix + first 4 chars of the secret, e.g. "sb_pat_AB12".
            DisplayHint = TokenPrefix + secret[..4],
            Scope = scope,
            ExpiresUtc = expiresUtc,
            CreatedUtc = now,
        };

        db.PersonalAccessTokens.Add(entity);
        await db.SaveChangesAsync(cancellationToken);

        return new CreatePatResult(entity, token);
    }

    public async Task<IReadOnlyList<PersonalAccessTokenEntity>> ListAsync(Guid userId, CancellationToken cancellationToken = default) =>
        await db.PersonalAccessTokens
            .AsNoTracking()
            .Where(t => t.UserId == userId)
            .OrderByDescending(t => t.CreatedUtc)
            .ToListAsync(cancellationToken);

    public async Task<bool> RevokeAsync(Guid userId, Guid tokenId, CancellationToken cancellationToken = default)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var affected = await db.PersonalAccessTokens
            .Where(t => t.Id == tokenId && t.UserId == userId && t.RevokedUtc == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedUtc, (DateTime?)now), cancellationToken);
        return affected > 0;
    }

    public async Task<bool> DeleteAsync(Guid userId, Guid tokenId, CancellationToken cancellationToken = default)
    {
        var affected = await db.PersonalAccessTokens
            .Where(t => t.Id == tokenId && t.UserId == userId)
            .ExecuteDeleteAsync(cancellationToken);
        return affected > 0;
    }

    public async Task<PatPrincipal?> ValidateAsync(string presentedToken, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(presentedToken) || !presentedToken.StartsWith(TokenPrefix, StringComparison.Ordinal))
            return null;

        var hash = Hash(presentedToken);
        var now = time.GetUtcNow().UtcDateTime;

        var match = await db.PersonalAccessTokens
            .AsNoTracking()
            .Where(t => t.TokenHash == hash)
            .Select(t => new { t.Id, t.UserId, t.Scope, t.RevokedUtc, t.ExpiresUtc, t.LastUsedUtc })
            .FirstOrDefaultAsync(cancellationToken);

        if (match is null) return null;
        if (match.RevokedUtc is not null) return null;
        if (match.ExpiresUtc is not null && match.ExpiresUtc <= now) return null;

        await StampLastUsedAsync(match.Id, match.LastUsedUtc, now, cancellationToken);
        return new PatPrincipal(match.UserId, match.Scope);
    }

    /// <summary>Best-effort, throttled <c>LastUsedUtc</c> write — never let an audit-stamp failure fail the request.</summary>
    private async Task StampLastUsedAsync(Guid tokenId, DateTime? lastUsed, DateTime now, CancellationToken cancellationToken)
    {
        if (lastUsed is not null && now - lastUsed.Value < LastUsedThrottle) return;
        try
        {
            await db.PersonalAccessTokens
                .Where(t => t.Id == tokenId
                    && (t.LastUsedUtc == null || t.LastUsedUtc < now - LastUsedThrottle))
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.LastUsedUtc, (DateTime?)now), cancellationToken);
        }
        catch
        {
            // Stamping is observability only — swallow so a transient write error can't reject a valid token.
        }
    }

    /// <summary>256-bit url-safe secret, matching the refresh-token entropy budget.</summary>
    private static string GenerateSecret()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>
    /// Peppered HMAC-SHA256 of the full token (prefix included), keyed with the JWT secret — a DB leak
    /// alone can't be reversed against a precomputed table. Identical approach to refresh tokens.
    /// </summary>
    private string Hash(string token)
    {
        var key = Encoding.UTF8.GetBytes(_jwt.Secret);
        var data = Encoding.UTF8.GetBytes(token);
        return Convert.ToHexString(HMACSHA256.HashData(key, data));
    }
}
