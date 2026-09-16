using Stashboard.Api.Data;

namespace Stashboard.Api.Auth.Oidc;

/// <summary>Typed reasons an OIDC sign-in can fail, mapped to HTTP responses by the controller.</summary>
public enum OidcFailureReason
{
    NotConfigured,
    InvalidState,
    TokenExchangeFailed,
    InvalidIdToken,
    EmailNotVerified,
    RegistrationDisabled,
    AccountConflict,
}

/// <summary>Outcome of completing an OIDC sign-in: either the linked/provisioned user, or a typed failure.</summary>
public sealed record OidcLoginResult(UserEntity? User, OidcFailureReason? Failure, string? Message)
{
    public bool Succeeded => User is not null && Failure is null;
    public static OidcLoginResult Ok(UserEntity user) => new(user, null, null);
    public static OidcLoginResult Fail(OidcFailureReason reason, string message) => new(null, reason, message);
}

/// <summary>
/// Drives the Authorization-Code + PKCE login against the configured provider, finishing by linking
/// or provisioning a Stashboard user. The caller issues the normal token pair from the returned user.
/// </summary>
public interface IOidcService
{
    /// <summary>
    /// Begins a sign-in: stores state + PKCE verifier + nonce and returns the provider authorize URL the
    /// SPA should redirect to. Returns null when OIDC isn't configured/enabled.
    /// </summary>
    Task<string?> StartAsync(CancellationToken cancellationToken = default);

    /// <summary>Completes a sign-in by exchanging the authorization code and resolving the Stashboard user.</summary>
    Task<OidcLoginResult> CompleteAsync(string code, string state, CancellationToken cancellationToken = default);
}
