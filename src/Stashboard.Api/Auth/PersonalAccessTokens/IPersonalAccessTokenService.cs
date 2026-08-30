using Stashboard.Api.Data;

namespace Stashboard.Api.Auth.PersonalAccessTokens;

/// <summary>Outcome of minting a token: the persisted entity plus the plaintext secret (returned once).</summary>
public sealed record CreatePatResult(PersonalAccessTokenEntity Token, string PlaintextSecret);

/// <summary>Decoded identity behind a valid PAT — the owning user and the token's scope.</summary>
public sealed record PatPrincipal(Guid UserId, PersonalAccessTokenScope Scope);

public interface IPersonalAccessTokenService
{
    /// <summary>
    /// Mints a new token for the user. Generates a high-entropy secret, stores only its hash, and
    /// returns the plaintext exactly once for the caller to surface to the user.
    /// </summary>
    Task<CreatePatResult> CreateAsync(
        Guid userId, string name, PersonalAccessTokenScope scope, DateTime? expiresUtc, CancellationToken cancellationToken = default);

    /// <summary>Lists the user's tokens (active and revoked), newest first.</summary>
    Task<IReadOnlyList<PersonalAccessTokenEntity>> ListAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Soft-revokes a token (stamps <c>RevokedUtc</c>). Returns false if not found / not owned.</summary>
    Task<bool> RevokeAsync(Guid userId, Guid tokenId, CancellationToken cancellationToken = default);

    /// <summary>Permanently deletes a token row (purge from history). Returns false if not found / not owned.</summary>
    Task<bool> DeleteAsync(Guid userId, Guid tokenId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates a presented plaintext token: looks it up by hash, rejecting revoked/expired tokens,
    /// and (best-effort, throttled) stamps <c>LastUsedUtc</c>. Returns null when the token is invalid.
    /// </summary>
    Task<PatPrincipal?> ValidateAsync(string presentedToken, CancellationToken cancellationToken = default);
}
