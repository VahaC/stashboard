using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Stashboard.Api.Auth;
using Stashboard.Api.Contracts;
using Stashboard.Api.Mapping;
using Stashboard.Api.Notifications;

namespace Stashboard.Api.Controllers;

/// <summary>
/// Account-management endpoints: email confirmation, password change/reset, email change,
/// profile and account deletion. All sensitive mutations rotate the SecurityStamp + revoke
/// every refresh token, so all other sessions sign out immediately.
/// </summary>
[ApiController]
[Route("api/account")]
public class AccountController(
    IUserService users,
    IAccountNotificationService notifications,
    IEmailSettingsService emailSettings,
    ITwoFactorService twoFactor,
    IStashboardMapper mapper) : ControllerBase
{

    // ── Email confirmation ───────────────────────────────────────────────────

    /// <summary>
    /// Requests a fresh confirmation email. Always returns 204 to avoid leaking which
    /// addresses are registered.
    /// </summary>
    [HttpPost("resend-confirmation")]
    [AllowAnonymous]
    [EnableRateLimiting("account-email")]
    public async Task<IActionResult> ResendConfirmation([FromBody] ResendConfirmationRequest req, CancellationToken сancellationToken)
    {
        var user = await users.FindByEmailAsync(req.Email, сancellationToken);
        if (user is not null && !user.EmailConfirmed)
        {
            var issued = await users.CreateEmailConfirmationTokenAsync(user.Id, сancellationToken);
            if (issued.Succeeded)
                await notifications.SendEmailConfirmationAsync(issued.User!.Email, issued.Token!, сancellationToken);
        }
        return NoContent();
    }

    [HttpPost("confirm-email")]
    [AllowAnonymous]
    [EnableRateLimiting("account-token")]
    public async Task<IActionResult> ConfirmEmail([FromBody] ConfirmEmailRequest req, CancellationToken сancellationToken)
    {
        var result = await users.ConfirmEmailAsync(req.Email, req.Token, сancellationToken);
        if (!result.Succeeded) return BadRequest(new { error = result.Failure!.Message });
        return NoContent();
    }

    // ── Password reset (forgot password) ─────────────────────────────────────

    [HttpPost("forgot-password")]
    [AllowAnonymous]
    [EnableRateLimiting("account-email")]
    public async Task<IActionResult> ForgotPassword([FromBody] ForgotPasswordRequest req, CancellationToken сancellationToken)
    {
        var issued = await users.CreatePasswordResetTokenAsync(req.Email, сancellationToken);
        // Always return 204 so we never reveal whether the address is registered.
        if (issued.Succeeded)
            await notifications.SendPasswordResetAsync(issued.User!.Email, issued.Token!, сancellationToken);
        return NoContent();
    }

    [HttpPost("reset-password")]
    [AllowAnonymous]
    [EnableRateLimiting("account-token")]
    public async Task<IActionResult> ResetPassword([FromBody] ResetPasswordRequest req, CancellationToken сancellationToken)
    {
        var result = await users.ResetPasswordAsync(req.Email, req.Token, req.NewPassword, сancellationToken);
        if (!result.Succeeded) return BadRequest(new { error = result.Failure!.Message });
        return NoContent();
    }

    // ── Password change (authenticated) ──────────────────────────────────────

    [HttpPost("change-password")]
    [Authorize]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest req, CancellationToken сancellationToken)
    {
        var result = await users.ChangePasswordAsync(User.GetUserId(), req.CurrentPassword, req.NewPassword, сancellationToken);
        if (!result.Succeeded) return BadRequest(new { error = result.Failure!.Message });
        return NoContent();
    }

    // ── Email change ─────────────────────────────────────────────────────────

    [HttpPost("change-email")]
    [Authorize]
    public async Task<IActionResult> ChangeEmail([FromBody] ChangeEmailRequest req, CancellationToken сancellationToken)
    {
        var issued = await users.RequestEmailChangeAsync(User.GetUserId(), req.NewEmail, req.CurrentPassword, сancellationToken);
        if (!issued.Succeeded) return BadRequest(new { error = issued.Failure!.Message });
        await notifications.SendEmailChangeConfirmationAsync(issued.User!.PendingEmail!, issued.Token!, сancellationToken);
        return NoContent();
    }

    [HttpPost("confirm-email-change")]
    [Authorize]
    public async Task<IActionResult> ConfirmEmailChange([FromBody] ConfirmEmailChangeRequest req, CancellationToken сancellationToken)
    {
        var result = await users.ConfirmEmailChangeAsync(User.GetUserId(), req.Token, сancellationToken);
        if (!result.Succeeded) return BadRequest(new { error = result.Failure!.Message });
        return NoContent();
    }

    // ── Profile ──────────────────────────────────────────────────────────────

    [HttpGet("profile")]
    [Authorize]
    public async Task<ActionResult<ProfileResponse>> GetProfile(CancellationToken сancellationToken)
    {
        var user = await users.FindByIdAsync(User.GetUserId(), сancellationToken);
        if (user is null) return Unauthorized();
        return Ok(mapper.MapToProfileResponse(user));
    }

    [HttpPatch("profile")]
    [Authorize]
    public async Task<IActionResult> UpdateProfile([FromBody] UpdateProfileRequest req, CancellationToken сancellationToken)
    {
        var result = await users.UpdateProfileAsync(User.GetUserId(), req.DisplayName, req.Theme, сancellationToken);
        if (!result.Succeeded) return BadRequest(new { error = result.Failure!.Message });
        return NoContent();
    }

    /// <summary>
    /// Dedicated endpoint for the in-app theme switcher — single-column UPDATE so the picker
    /// can fire on every change cheaply (no row read, no other fields touched).
    /// </summary>
    [HttpPut("theme")]
    [Authorize]
    public async Task<IActionResult> UpdateTheme([FromBody] UpdateThemeRequest req, CancellationToken сancellationToken)
    {
        var result = await users.SetThemeAsync(User.GetUserId(), req.Theme, сancellationToken);
        if (!result.Succeeded) return BadRequest(new { error = result.Failure!.Message });
        return NoContent();
    }

    [HttpGet("dashboard-preferences")]
    [Authorize]
    public async Task<ActionResult<DashboardPreferencesResponse>> GetDashboardPreferences(CancellationToken cancellationToken)
    {
        var user = await users.FindByIdAsync(User.GetUserId(), cancellationToken);
        if (user is null) return Unauthorized();
        return Ok(mapper.MapToDashboardPreferencesResponse(user));
    }

    [HttpPut("dashboard-preferences")]
    [Authorize]
    public async Task<IActionResult> UpdateDashboardPreferences([FromBody] UpdateDashboardPreferencesRequest request, CancellationToken cancellationToken)
    {
        var result = await users.SetDashboardPreferencesAsync(User.GetUserId(), request.SortMode, request.GroupByCategory, cancellationToken);
        if (!result.Succeeded) return BadRequest(new { error = result.Failure!.Message });
        return NoContent();
    }

    // ── Telegram settings ─────────────────────────────────────────────────────

    [HttpGet("telegram")]
    [Authorize]
    public async Task<ActionResult<TelegramSettingsResponse>> GetTelegramSettings(CancellationToken сancellationToken)
    {
        var user = await users.FindByIdAsync(User.GetUserId(), сancellationToken);
        if (user is null) return Unauthorized();
        return Ok(mapper.MapToTelegramSettingsResponse(user));
    }

    [HttpPut("telegram")]
    [Authorize]
    public async Task<IActionResult> UpdateTelegramSettings([FromBody] UpdateTelegramSettingsRequest req, CancellationToken сancellationToken)
    {
        var result = await users.UpdateTelegramSettingsAsync(
            User.GetUserId(),
            req.BotToken,
            req.ChatId,
            req.NotificationsEnabled,
            сancellationToken);
        if (!result.Succeeded) return BadRequest(new { error = result.Failure!.Message });
        return NoContent();
    }

    // ── Two-factor authentication (TOTP) ───────────────────────────────────────

    /// <summary>
    /// Starts enrollment: generates a secret and returns the otpauth URI + manual key. The secret
    /// is stored encrypted but stays disabled until <see cref="EnableTwoFactor"/> confirms a code.
    /// </summary>
    [HttpPost("2fa/enroll")]
    [Authorize]
    public async Task<ActionResult<TwoFactorEnrollResponse>> EnrollTwoFactor(CancellationToken cancellationToken)
    {
        var enrollment = await twoFactor.BeginEnrollAsync(User.GetUserId(), cancellationToken);
        if (enrollment is null) return Unauthorized();
        return Ok(new TwoFactorEnrollResponse(enrollment.OtpauthUri, enrollment.ManualKey));
    }

    /// <summary>Confirms the first code, enables 2FA, and returns the one-time recovery codes.</summary>
    [HttpPost("2fa/enable")]
    [Authorize]
    public async Task<ActionResult<RecoveryCodesResponse>> EnableTwoFactor([FromBody] EnableTwoFactorRequest req, CancellationToken cancellationToken)
    {
        var result = await twoFactor.EnableAsync(User.GetUserId(), req.Code, cancellationToken);
        if (!result.Succeeded) return BadRequest(new { error = result.Failure!.Message });
        return Ok(new RecoveryCodesResponse(result.Codes!));
    }

    /// <summary>Disables 2FA after re-entering the password; rotates the stamp + signs out all sessions.</summary>
    [HttpPost("2fa/disable")]
    [Authorize]
    public async Task<IActionResult> DisableTwoFactor([FromBody] TwoFactorPasswordRequest req, CancellationToken cancellationToken)
    {
        var result = await twoFactor.DisableAsync(User.GetUserId(), req.CurrentPassword, cancellationToken);
        if (!result.Succeeded) return BadRequest(new { error = result.Failure!.Message });
        return NoContent();
    }

    /// <summary>Regenerates the recovery-code set after re-entering the password; old codes stop working.</summary>
    [HttpPost("2fa/recovery-codes")]
    [Authorize]
    public async Task<ActionResult<RecoveryCodesResponse>> RegenerateRecoveryCodes([FromBody] TwoFactorPasswordRequest req, CancellationToken cancellationToken)
    {
        var result = await twoFactor.RegenerateRecoveryCodesAsync(User.GetUserId(), req.CurrentPassword, cancellationToken);
        if (!result.Succeeded) return BadRequest(new { error = result.Failure!.Message });
        return Ok(new RecoveryCodesResponse(result.Codes!));
    }

    // ── Email server (SMTP) settings ──────────────────────────────────────────

    [HttpGet("email-settings")]
    [Authorize]
    public async Task<ActionResult<EmailSettingsResponse>> GetEmailSettings(CancellationToken cancellationToken)
        => Ok(await emailSettings.GetAsync(cancellationToken));

    [HttpPut("email-settings")]
    [Authorize]
    public async Task<IActionResult> UpdateEmailSettings([FromBody] UpdateEmailSettingsRequest req, CancellationToken cancellationToken)
    {
        await emailSettings.UpdateAsync(req, cancellationToken);
        return NoContent();
    }

    // ── Account deletion ─────────────────────────────────────────────────────

    [HttpDelete]
    [Authorize]
    public async Task<IActionResult> Delete([FromBody] DeleteAccountRequest req, CancellationToken сancellationToken)
    {
        var result = await users.DeleteAccountAsync(User.GetUserId(), req.CurrentPassword, сancellationToken);
        if (!result.Succeeded) return BadRequest(new { error = result.Failure!.Message });
        return NoContent();
    }
}
