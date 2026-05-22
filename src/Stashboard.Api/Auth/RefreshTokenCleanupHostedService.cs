using Microsoft.Extensions.Options;

namespace Stashboard.Api.Auth;

/// <summary>
/// Periodically prunes expired/revoked refresh tokens from the database.
/// </summary>
public sealed class RefreshTokenCleanupHostedService(
    IServiceScopeFactory scopeFactory,
    IOptionsMonitor<JwtOptions> options,
    ILogger<RefreshTokenCleanupHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Initial delay so we don't compete with app startup work.
        try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); }
        catch (TaskCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var tokens = scope.ServiceProvider.GetRequiredService<ITokenService>();
                var deleted = await tokens.CleanupAsync(stoppingToken);
                if (deleted > 0) logger.LogInformation("Pruned {Count} expired refresh tokens", deleted);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { logger.LogError(ex, "Refresh-token cleanup failed"); }

            var hours = Math.Max(1, options.CurrentValue.CleanupIntervalHours);
            try { await Task.Delay(TimeSpan.FromHours(hours), stoppingToken); }
            catch (TaskCanceledException) { return; }
        }
    }
}
