using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Stashboard.Api.Auth;
using Stashboard.Api.Data;
using Stashboard.Api.Notifications;
using Stashboard.Core.Abstractions;
using Stashboard.Core.Options;

namespace Stashboard.Api.Services;

public sealed class HealthCheckBackgroundService(
    IServiceScopeFactory scopeFactory,
    IOptionsMonitor<HealthCheckOptions> options,
    ILogger<HealthCheckBackgroundService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
        catch (TaskCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await ScanOnceAsync(stoppingToken); }
            catch (Exception ex) { logger.LogError(ex, "Healthcheck scan failed"); }

            var delay = TimeSpan.FromSeconds(Math.Max(10, options.CurrentValue.IntervalSeconds));
            try { await Task.Delay(delay, stoppingToken); }
            catch (TaskCanceledException) { return; }
        }
    }

    private async Task ScanOnceAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var checker = scope.ServiceProvider.GetRequiredService<IServiceHealthChecker>();
        var userService = scope.ServiceProvider.GetRequiredService<IUserService>();
        var statusNotifications = scope.ServiceProvider.GetRequiredService<IServiceStatusNotificationService>();

        var services = await db.WebResources.AsTracking().ToListAsync(cancellationToken);
        var userIds = services.Select(service => service.UserId).Distinct().ToList();

        foreach (var service in services)
        {
            if (cancellationToken.IsCancellationRequested) break;

            if (ShouldForceDisableOfflineNotifications(service))
                service.OfflineNotificationsEnabled = false;

            if (!ShouldRunAnyHealthCheck(service))
            {
                ClearHealthState(service);
                continue;
            }

            var previousMainStatus = service.CurrentStatus;
            var previousAdditionalStatus = service.AdditionalUrlStatus;
            var result = await checker.CheckAsync(service, cancellationToken);
            HealthCheckStatusEvaluator.Apply(service, result, options.CurrentValue.FailureThreshold, DateTime.UtcNow);

            if (userIds.Contains(service.UserId))
            {
                var user = await userService.FindByIdAsync(service.UserId, cancellationToken);
                if (user is not null)
                    await statusNotifications.NotifyIfNeededAsync(user, service, previousMainStatus, previousAdditionalStatus, cancellationToken);
            }
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    private static bool ShouldRunAnyHealthCheck(Stashboard.Core.Entities.WebResourceEntity service)
    {
        if (service.MainUrlHealthCheckEnabled)
            return true;

        return !string.IsNullOrWhiteSpace(service.AdditionalUrl)
            && service.AdditionalUrlHealthCheckEnabled;
    }

    private static bool ShouldForceDisableOfflineNotifications(Stashboard.Core.Entities.WebResourceEntity service)
    {
        var isMainDisabled = !service.MainUrlHealthCheckEnabled;
        var isAdditionalDisabled = !service.AdditionalUrlHealthCheckEnabled || string.IsNullOrWhiteSpace(service.AdditionalUrl);
        var hasNoHealthCheckUrl = string.IsNullOrWhiteSpace(service.HealthCheckUrl);

        return isMainDisabled && isAdditionalDisabled && hasNoHealthCheckUrl;
    }

    private static void ClearHealthState(Stashboard.Core.Entities.WebResourceEntity service)
    {
        service.CurrentStatus = Stashboard.Core.Enums.ServiceStatus.Unknown;
        service.LastResponseTimeMs = null;
        service.LastError = null;
        service.LastCheckedUtc = null;
        service.MainUrlConsecutiveFailures = 0;
        service.AdditionalUrlStatus = Stashboard.Core.Enums.ServiceStatus.Unknown;
        service.AdditionalUrlLastResponseTimeMs = null;
        service.AdditionalUrlLastError = null;
        service.AdditionalUrlConsecutiveFailures = 0;
    }
}
