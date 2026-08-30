using System.ComponentModel.DataAnnotations;

namespace Stashboard.Api.Contracts;

/// <summary>Request to mint a personal access token. Re-authenticates with the current password.</summary>
public sealed record CreatePersonalAccessTokenRequest(
    [Required, StringLength(128, MinimumLength = 1)] string Name,
    [Required, RegularExpression("^(read|full)$", ErrorMessage = "Scope must be 'read' or 'full'.")]
    string Scope,
    /// <summary>Optional absolute expiry (UTC). Omit / null for a token that never expires.</summary>
    DateTime? ExpiresUtc,
    [Required, StringLength(128, MinimumLength = 1)] string CurrentPassword
);

/// <summary>A token as shown in the management list. Never includes the secret.</summary>
public sealed record PersonalAccessTokenResponse(
    Guid Id,
    string Name,
    string DisplayHint,
    string Scope,
    DateTime? ExpiresUtc,
    DateTime? LastUsedUtc,
    DateTime? RevokedUtc,
    DateTime CreatedUtc,
    /// <summary>Computed lifecycle state: <c>active</c>, <c>revoked</c>, or <c>expired</c>.</summary>
    string Status
);

/// <summary>Response to a create call — the new token's metadata plus its plaintext secret, shown exactly once.</summary>
public sealed record CreatedPersonalAccessTokenResponse(
    PersonalAccessTokenResponse Token,
    string Secret
);
