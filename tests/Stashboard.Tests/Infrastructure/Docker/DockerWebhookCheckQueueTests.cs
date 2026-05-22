using Stashboard.Infrastructure.Docker;

namespace Stashboard.Tests.Infrastructure.Docker;

/// <summary>
/// V2.6 — pure-in-memory queue used by the webhook receiver to ask the
/// background loop for an out-of-band check. The tests below pin the
/// guarantees the controller and background service rely on: duplicate
/// collapsing, bounded capacity, and idempotent drain.
/// </summary>
public class DockerWebhookCheckQueueTests
{
    [Fact]
    public void TryEnqueue_NewWatch_Accepts()
    {
        var sut = new DockerWebhookCheckQueue();
        Assert.True(sut.TryEnqueue(Guid.NewGuid()));
    }

    [Fact]
    public void TryEnqueue_Empty_Rejects()
    {
        var sut = new DockerWebhookCheckQueue();
        Assert.False(sut.TryEnqueue(Guid.Empty));
    }

    [Fact]
    public void TryEnqueue_DuplicateBeforeDrain_ReturnsFalseAndCollapses()
    {
        var sut = new DockerWebhookCheckQueue();
        var id = Guid.NewGuid();

        Assert.True(sut.TryEnqueue(id));
        Assert.False(sut.TryEnqueue(id));

        var drained = sut.DrainAll();
        Assert.Single(drained);
        Assert.Equal(id, drained[0]);
    }

    [Fact]
    public void DrainAll_AfterDrain_EmptyAndAcceptsSameIdAgain()
    {
        var sut = new DockerWebhookCheckQueue();
        var id = Guid.NewGuid();
        sut.TryEnqueue(id);

        var firstDrain = sut.DrainAll();
        Assert.Single(firstDrain);

        Assert.Empty(sut.DrainAll());
        Assert.True(sut.TryEnqueue(id), "Same id should be accepted again once it's drained.");
    }

    [Fact]
    public void TryEnqueue_AtCapacity_Rejects()
    {
        var sut = new DockerWebhookCheckQueue();
        for (var i = 0; i < DockerWebhookCheckQueue.Capacity; i++)
            Assert.True(sut.TryEnqueue(Guid.NewGuid()));

        // Capacity exceeded — the controller treats this as 202 still, but the
        // queue itself reports the overflow so the controller can log it.
        Assert.False(sut.TryEnqueue(Guid.NewGuid()));
    }
}
