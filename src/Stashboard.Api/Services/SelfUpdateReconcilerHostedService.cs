using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Stashboard.Api.Services;

/// <summary>
/// V9.2 — runs <see cref="SelfUpdateReconciler"/> once shortly after startup, so
/// a self-update that restarted the app is confirmed (or marked failed) as soon
/// as the new container is up. See <see cref="SelfUpdateReconciler"/> for the
/// confirmation logic.
/// </summary>
public sealed class SelfUpdateReconcilerHostedService(
    IServiceScopeFactory scopeFactory,
    ILogger<SelfUpdateReconcilerHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let the app + daemon settle after a restart before inspecting.
        try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
        catch (OperationCanceledException) { return; }

        try
        {
            using var scope = scopeFactory.CreateScope();
            var reconciler = scope.ServiceProvider.GetRequiredService<SelfUpdateReconciler>();
            await reconciler.ReconcileAsync(stoppingToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Self-update reconcile on startup failed.");
        }
    }
}
