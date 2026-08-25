namespace Stashboard.Api.Auth;

/// <summary>
/// Failure reasons returned from <see cref="IUserService"/>. Lets the controller layer
/// translate domain outcomes into appropriate HTTP responses without leaking internals.
/// </summary>
public enum AuthFailureReason
{
    InvalidCredentials,
    EmailAlreadyTaken,
    AccountLocked,
    EmailNotConfirmed,
    InvalidToken,
    UserNotFound,
    TwoFactorRequired,
    InvalidTwoFactorCode,
}

public sealed record AuthFailure(AuthFailureReason Reason, string Message);
