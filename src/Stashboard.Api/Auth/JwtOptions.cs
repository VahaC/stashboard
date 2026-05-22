namespace Stashboard.Api.Auth;

public class JwtOptions
{
    public const string SectionName = "Jwt";

    /// <summary>HMAC SHA-256 signing secret. Minimum 32 characters. Required.</summary>
    public string Secret { get; set; } = default!;

    public string Issuer { get; set; } = "Stashboard";
    public string Audience { get; set; } = "Stashboard";

    public int AccessTokenMinutes { get; set; } = 15;

    /// <summary>Sliding lifetime of an individual refresh token (renewed on each rotation).</summary>
    public int RefreshTokenDays { get; set; } = 30;

    /// <summary>
    /// Absolute lifetime of a refresh-token family — i.e. the maximum length of a single
    /// continuous session before re-authentication is required, regardless of how often
    /// the refresh token is rotated. Defaults to 90 days.
    /// </summary>
    public int SessionMaxDays { get; set; } = 90;

    /// <summary>Maximum failed login attempts before the account is locked.</summary>
    public int MaxFailedAccessAttempts { get; set; } = 5;

    /// <summary>Lockout duration after MaxFailedAccessAttempts is exceeded.</summary>
    public int LockoutMinutes { get; set; } = 15;

    /// <summary>Cleanup interval for expired/revoked refresh tokens.</summary>
    public int CleanupIntervalHours { get; set; } = 24;

    /// <summary>How long after expiry/revocation a refresh-token row is kept (audit trail).</summary>
    public int CleanupRetentionDays { get; set; } = 30;

    /// <summary>If true, login is rejected for users that haven't confirmed their email.</summary>
    public bool RequireConfirmedEmail { get; set; } = false;

    /// <summary>Lifetime of the email-confirmation token (also used for email-change).</summary>
    public int EmailConfirmationTokenLifetimeHours { get; set; } = 24;

    /// <summary>Lifetime of the password-reset token. Keep short to limit exposure.</summary>
    public int PasswordResetTokenLifetimeHours { get; set; } = 2;
}

