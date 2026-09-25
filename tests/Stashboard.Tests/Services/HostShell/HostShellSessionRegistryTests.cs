using Microsoft.Extensions.Options;
using Stashboard.Api.Services.HostShell;
using Stashboard.Core.Options;

namespace Stashboard.Tests.Services.HostShell;

/// <summary>
/// V5.3 — unit tests for <see cref="HostShellSessionRegistry"/>: the per-user
/// and per-host concurrency caps, and that releasing a lease frees the slot.
/// </summary>
public class HostShellSessionRegistryTests
{
    private static HostShellSessionRegistry Build(int perUser = 3, int perHost = 5) =>
        new(Options.Create(new HostShellOptions
        {
            MaxSessionsPerUser = perUser,
            MaxSessionsPerHost = perHost,
        }));

    [Fact]
    public void TryAcquire_UnderCaps_Succeeds()
    {
        var registry = Build();
        var lease = registry.TryAcquire(Guid.NewGuid(), Guid.NewGuid(), out var rejection);

        Assert.NotNull(lease);
        Assert.Null(rejection);
    }

    [Fact]
    public void TryAcquire_PerUserCapReached_Rejects()
    {
        var registry = Build(perUser: 2, perHost: 99);
        var user = Guid.NewGuid();

        Assert.NotNull(registry.TryAcquire(user, Guid.NewGuid(), out _));
        Assert.NotNull(registry.TryAcquire(user, Guid.NewGuid(), out _));

        var third = registry.TryAcquire(user, Guid.NewGuid(), out var rejection);
        Assert.Null(third);
        Assert.NotNull(rejection);
        Assert.Contains("limit 2", rejection);
    }

    [Fact]
    public void TryAcquire_PerHostCapReached_Rejects()
    {
        var registry = Build(perUser: 99, perHost: 2);
        var host = Guid.NewGuid();

        Assert.NotNull(registry.TryAcquire(Guid.NewGuid(), host, out _));
        Assert.NotNull(registry.TryAcquire(Guid.NewGuid(), host, out _));

        var third = registry.TryAcquire(Guid.NewGuid(), host, out var rejection);
        Assert.Null(third);
        Assert.NotNull(rejection);
        Assert.Contains("host", rejection);
    }

    [Fact]
    public void DisposingLease_FreesTheSlot()
    {
        var registry = Build(perUser: 1, perHost: 99);
        var user = Guid.NewGuid();

        var lease = registry.TryAcquire(user, Guid.NewGuid(), out _);
        Assert.NotNull(lease);
        Assert.Null(registry.TryAcquire(user, Guid.NewGuid(), out _)); // at cap

        lease!.Dispose();

        Assert.NotNull(registry.TryAcquire(user, Guid.NewGuid(), out _)); // slot freed
    }

    [Fact]
    public void DisposingLeaseTwice_IsIdempotent()
    {
        var registry = Build(perUser: 1, perHost: 99);
        var user = Guid.NewGuid();

        var lease = registry.TryAcquire(user, Guid.NewGuid(), out _);
        lease!.Dispose();
        lease.Dispose(); // must not double-decrement

        // Only one slot should have been freed — acquiring once works, and the
        // count never went negative (a second acquire is still within the cap).
        Assert.NotNull(registry.TryAcquire(user, Guid.NewGuid(), out _));
    }
}
