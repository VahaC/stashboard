using Stashboard.Api.Services;
using Stashboard.Core.Abstractions;
using Stashboard.Core.Entities;
using Stashboard.Core.Enums;

namespace Stashboard.Tests.Services;

public class HealthCheckStatusEvaluatorTests
{
    private static ServiceCheckResult MainProbe(ServiceStatus status, string? error = null)
        => new(new HealthCheckResult(status, status == ServiceStatus.Down ? 5 : 10, error), null);

    [Fact]
    public void Apply_SingleFailureBelowThreshold_KeepsPreviousStatusAndError()
    {
        var service = new WebResourceEntity
        {
            MainUrl = "https://x",
            CurrentStatus = ServiceStatus.Up,
            LastError = null,
        };

        HealthCheckStatusEvaluator.Apply(service, MainProbe(ServiceStatus.Down, "Timeout"), failureThreshold: 3, DateTime.UtcNow);

        Assert.Equal(ServiceStatus.Up, service.CurrentStatus);
        Assert.Equal(1, service.MainUrlConsecutiveFailures);
        Assert.Null(service.LastError); // prior confirmed state is left untouched during the pending window
        Assert.NotNull(service.LastCheckedUtc); // but the check timestamp still advances
    }

    [Fact]
    public void Apply_FailuresReachingThreshold_FlipsToDown()
    {
        var service = new WebResourceEntity { MainUrl = "https://x", CurrentStatus = ServiceStatus.Up };

        for (var i = 0; i < 3; i++)
            HealthCheckStatusEvaluator.Apply(service, MainProbe(ServiceStatus.Down, "Timeout"), failureThreshold: 3, DateTime.UtcNow);

        Assert.Equal(ServiceStatus.Down, service.CurrentStatus);
        Assert.Equal(3, service.MainUrlConsecutiveFailures);
        Assert.Equal("Timeout", service.LastError);
    }

    [Fact]
    public void Apply_SuccessResetsFailureCounter()
    {
        var service = new WebResourceEntity
        {
            MainUrl = "https://x",
            CurrentStatus = ServiceStatus.Up,
            MainUrlConsecutiveFailures = 2,
        };

        HealthCheckStatusEvaluator.Apply(service, MainProbe(ServiceStatus.Up), failureThreshold: 3, DateTime.UtcNow);

        Assert.Equal(ServiceStatus.Up, service.CurrentStatus);
        Assert.Equal(0, service.MainUrlConsecutiveFailures);
        Assert.Null(service.LastError);
    }

    [Fact]
    public void Apply_SuccessBetweenFailures_RebuildsThresholdFromZero()
    {
        var service = new WebResourceEntity { MainUrl = "https://x", CurrentStatus = ServiceStatus.Up };

        HealthCheckStatusEvaluator.Apply(service, MainProbe(ServiceStatus.Down, "e"), failureThreshold: 3, DateTime.UtcNow);
        HealthCheckStatusEvaluator.Apply(service, MainProbe(ServiceStatus.Down, "e"), failureThreshold: 3, DateTime.UtcNow);
        HealthCheckStatusEvaluator.Apply(service, MainProbe(ServiceStatus.Up), failureThreshold: 3, DateTime.UtcNow);
        HealthCheckStatusEvaluator.Apply(service, MainProbe(ServiceStatus.Down, "e"), failureThreshold: 3, DateTime.UtcNow);

        Assert.Equal(ServiceStatus.Up, service.CurrentStatus); // a recovered blip cannot push the next single failure to Down
        Assert.Equal(1, service.MainUrlConsecutiveFailures);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    [InlineData(-5)]
    public void Apply_ThresholdFlooredToOne_FlipsImmediately(int threshold)
    {
        var service = new WebResourceEntity { MainUrl = "https://x", CurrentStatus = ServiceStatus.Up };

        HealthCheckStatusEvaluator.Apply(service, MainProbe(ServiceStatus.Down, "HTTP 500"), threshold, DateTime.UtcNow);

        Assert.Equal(ServiceStatus.Down, service.CurrentStatus);
    }

    [Fact]
    public void Apply_AdditionalUrl_ThresholdAppliesIndependentlyOfMain()
    {
        var service = new WebResourceEntity
        {
            MainUrl = "https://x",
            AdditionalUrl = "https://y",
            CurrentStatus = ServiceStatus.Up,
            AdditionalUrlStatus = ServiceStatus.Up,
        };
        var result = new ServiceCheckResult(
            new HealthCheckResult(ServiceStatus.Up, 10, null),
            new HealthCheckResult(ServiceStatus.Down, 5, "Timeout"));

        HealthCheckStatusEvaluator.Apply(service, result, failureThreshold: 2, DateTime.UtcNow);

        Assert.Equal(ServiceStatus.Up, service.CurrentStatus);
        Assert.Equal(ServiceStatus.Up, service.AdditionalUrlStatus); // first additional failure, below threshold
        Assert.Equal(1, service.AdditionalUrlConsecutiveFailures);

        HealthCheckStatusEvaluator.Apply(service, result, failureThreshold: 2, DateTime.UtcNow);

        Assert.Equal(ServiceStatus.Down, service.AdditionalUrlStatus); // second failure hits the threshold
        Assert.Equal("Timeout", service.AdditionalUrlLastError);
    }

    [Fact]
    public void Apply_NoAdditionalResult_ClearsAdditionalState()
    {
        var service = new WebResourceEntity
        {
            MainUrl = "https://x",
            AdditionalUrlStatus = ServiceStatus.Down,
            AdditionalUrlConsecutiveFailures = 4,
            AdditionalUrlLastError = "stale",
        };

        HealthCheckStatusEvaluator.Apply(service, MainProbe(ServiceStatus.Up), failureThreshold: 3, DateTime.UtcNow);

        Assert.Equal(ServiceStatus.Unknown, service.AdditionalUrlStatus);
        Assert.Equal(0, service.AdditionalUrlConsecutiveFailures);
        Assert.Null(service.AdditionalUrlLastError);
    }
}


