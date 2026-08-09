using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Stashboard.Api.Contracts;
using Stashboard.Core.Entities;
using Stashboard.Core.Enums;

namespace Stashboard.Tests.Controllers.WebResources;

/// <summary>
/// V10.1 — the owner-scoped uptime-history endpoints, and that a manual "Check now" appends a
/// history row.
/// </summary>
public class HealthHistoryEndpointTests : WebResourcesControllerTestBase
{
    [Fact]
    public async Task Metrics_ForeignService_Returns404()
    {
        var foreignId = await SeedServiceAsync(_otherUserId);

        var controller = BuildController(_userId);
        var result = await controller.HealthMetrics(foreignId, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task Metrics_OwnedService_ComputesUptimeFromEvents()
    {
        var id = await SeedServiceAsync(_userId);
        var now = DateTime.UtcNow;
        // 6h Up, 2h Down, then back Up 2h ago → 30d window is dominated by Up.
        await SeedEventsAsync(id,
            (now.AddHours(-6), ServiceStatus.Up, 100),
            (now.AddHours(-2), ServiceStatus.Down, null),
            (now.AddHours(-1), ServiceStatus.Up, 120));

        var controller = BuildController(_userId);
        var result = await controller.HealthMetrics(id, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var payload = Assert.IsType<ServiceUptimeResponse>(ok.Value);
        Assert.Equal(90, payload.RetentionDays);
        Assert.Equal(3, payload.Main.EventCount);
        Assert.NotNull(payload.Main.Uptime24h);
        // One resolved incident (the 1-hour Down span).
        var incident = Assert.Single(payload.Main.Incidents);
        Assert.False(incident.Ongoing);
        Assert.Null(payload.Additional); // no additional URL on the seeded service
    }

    [Fact]
    public async Task Events_ForeignService_Returns404()
    {
        var foreignId = await SeedServiceAsync(_otherUserId);

        var controller = BuildController(_userId);
        var result = await controller.HealthEvents(foreignId, target: null, skip: 0, take: 50, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task CheckNow_AppendsHistoryEvent_OnTransition()
    {
        // A fresh service starts Unknown; the base health-checker mock returns Up → a transition.
        var id = await SeedServiceAsync(_userId);

        var controller = BuildController(_userId);
        await controller.CheckNow(id, CancellationToken.None);

        var events = await _dbContext.HealthCheckEvents.AsNoTracking()
            .Where(e => e.WebResourceId == id).ToListAsync();
        var row = Assert.Single(events);
        Assert.Equal(HealthCheckTarget.Main, row.Target);
        Assert.Equal(ServiceStatus.Up, row.Status);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private async Task<Guid> SeedServiceAsync(Guid userId)
    {
        var entity = new WebResourceEntity
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Name = "svc",
            MainUrl = "https://example.com",
            CurrentStatus = ServiceStatus.Unknown,
        };
        _dbContext.WebResources.Add(entity);
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();
        return entity.Id;
    }

    private async Task SeedEventsAsync(Guid serviceId, params (DateTime ts, ServiceStatus status, int? rt)[] events)
    {
        foreach (var (ts, status, rt) in events)
        {
            _dbContext.HealthCheckEvents.Add(new HealthCheckEventEntity
            {
                WebResourceId = serviceId,
                Target = HealthCheckTarget.Main,
                Status = status,
                ResponseTimeMs = rt,
                Error = status == ServiceStatus.Down ? "boom" : null,
                TimestampUtc = ts,
            });
        }
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();
    }
}
