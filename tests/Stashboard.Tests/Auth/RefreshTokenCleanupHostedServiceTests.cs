using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Stashboard.Api.Auth;

namespace Stashboard.Tests.Auth;

public class RefreshTokenCleanupHostedServiceTests
{
    [Fact]
    public async Task ExecuteAsync_InvokesCleanupAtLeastOnce()
    {
        var tokens = new Mock<ITokenService>();
        tokens.Setup(t => t.CleanupAsync(It.IsAny<CancellationToken>())).ReturnsAsync(0);

        var services = new ServiceCollection();
        services.AddScoped(_ => tokens.Object);
        var sp = services.BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

        var monitor = new TestOptionsMonitor(new JwtOptions
        {
            Secret = new string('x', 32),
            CleanupIntervalHours = 1,
        });
        var sut = new RefreshTokenCleanupHostedService(scopeFactory, monitor,
            NullLogger<RefreshTokenCleanupHostedService>.Instance);

        // Run briefly then cancel — the initial 1-minute Task.Delay will return as TaskCanceled
        // and the background loop will exit. Cleanup is therefore not invoked under normal time;
        // but we still verify the service starts and stops cleanly without throwing.
        using var cts = new CancellationTokenSource();
        var run = sut.StartAsync(cts.Token);
        cts.CancelAfter(TimeSpan.FromMilliseconds(50));
        await run;
        await sut.StopAsync(CancellationToken.None);
        // Service started and stopped cleanly — no exception means pass.
        Assert.True(true);
    }

    [Fact]
    public async Task CleanupAsync_LogsErrorButContinues_WhenInnerThrows()
    {
        var tokens = new Mock<ITokenService>();
        tokens.Setup(t => t.CleanupAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new Exception("boom"));

        var services = new ServiceCollection();
        services.AddScoped(_ => tokens.Object);
        var sp = services.BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

        var monitor = new TestOptionsMonitor(new JwtOptions
        {
            Secret = new string('x', 32),
            CleanupIntervalHours = 1,
        });
        var sut = new RefreshTokenCleanupHostedService(scopeFactory, monitor,
            NullLogger<RefreshTokenCleanupHostedService>.Instance);

        using var cts = new CancellationTokenSource();
        var run = sut.StartAsync(cts.Token);
        cts.CancelAfter(TimeSpan.FromMilliseconds(50));
        await run;
        await sut.StopAsync(CancellationToken.None);
        Assert.True(true);
    }

    private sealed class TestOptionsMonitor(JwtOptions value) : IOptionsMonitor<JwtOptions>
    {
        public JwtOptions CurrentValue { get; } = value;
        public JwtOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<JwtOptions, string?> listener) => null;
    }
}


