using Microsoft.Extensions.Options;
using Stashboard.Api.Services.ContainerExec;
using Stashboard.Core.Options;

namespace Stashboard.Tests.Services.ContainerExec;

/// <summary>
/// V5.7 — unit tests for <see cref="ContainerExecSessionRegistry"/>: the
/// per-user and per-host concurrency caps, and that releasing a lease frees the
/// slot. Mirrors the V5.3 host-shell registry tests.
/// </summary>
public class ContainerExecSessionRegistryTests
{
    private static ContainerExecSessionRegistry Build(int perUser = 3, int perHost = 5) =>
        new(Options.Create(new ContainerExecOptions
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

        Assert.NotNull(registry.TryAcquire(user, Guid.NewGuid(), out _));
    }
}
