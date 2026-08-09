using Microsoft.Extensions.Options;
using Stashboard.Api.Services.ContainerExec;
using Stashboard.Core.Options;
using Stashboard.Tests.Infrastructure;

namespace Stashboard.Tests.Services.ContainerExec;

/// <summary>
/// V5.7 — unit tests for <see cref="ContainerExecTicketService"/>. Drives a
/// <see cref="TestTimeProvider"/> so expiry is deterministic. The contract:
/// tickets are single-use, short-lived, and bound to a specific
/// (user, connection, container, command) tuple.
/// </summary>
public class ContainerExecTicketServiceTests
{
    private readonly TestTimeProvider _time = new();
    private readonly ContainerExecTicketService _service;

    public ContainerExecTicketServiceTests()
    {
        var options = Options.Create(new ContainerExecOptions { TicketTtlSeconds = 30 });
        _service = new ContainerExecTicketService(options, _time);
    }

    [Fact]
    public void Redeem_ValidUnexpiredTicket_ReturnsBoundPayload()
    {
        var userId = Guid.NewGuid();
        var connectionId = Guid.NewGuid();
        var command = new[] { "/bin/bash" };
        var token = _service.Issue(userId, connectionId, "web-1", command);

        var redeemed = _service.Redeem(token);

        Assert.NotNull(redeemed);
        Assert.Equal(userId, redeemed!.UserId);
        Assert.Equal(connectionId, redeemed.ConnectionId);
        Assert.Equal("web-1", redeemed.ContainerName);
        Assert.Equal(command, redeemed.Command);
    }

    [Fact]
    public void Redeem_SameTicketTwice_FailsSecondTime()
    {
        var token = _service.Issue(Guid.NewGuid(), Guid.NewGuid(), "c", new[] { "/bin/sh" });

        Assert.NotNull(_service.Redeem(token));
        Assert.Null(_service.Redeem(token)); // single-use
    }

    [Fact]
    public void Redeem_AfterTtlElapsed_Fails()
    {
        var token = _service.Issue(Guid.NewGuid(), Guid.NewGuid(), "c", new[] { "/bin/sh" });

        _time.Advance(TimeSpan.FromSeconds(31)); // TTL is 30s

        Assert.Null(_service.Redeem(token));
    }

    [Fact]
    public void Redeem_JustBeforeExpiry_StillWorks()
    {
        var token = _service.Issue(Guid.NewGuid(), Guid.NewGuid(), "c", new[] { "/bin/sh" });

        _time.Advance(TimeSpan.FromSeconds(29));

        Assert.NotNull(_service.Redeem(token));
    }

    [Fact]
    public void Redeem_UnknownToken_ReturnsNull()
    {
        Assert.Null(_service.Redeem("not-a-real-ticket"));
        Assert.Null(_service.Redeem(string.Empty));
    }

    [Fact]
    public void Issue_ProducesDistinctUrlSafeTokens()
    {
        var a = _service.Issue(Guid.NewGuid(), Guid.NewGuid(), "c", new[] { "/bin/sh" });
        var b = _service.Issue(Guid.NewGuid(), Guid.NewGuid(), "c", new[] { "/bin/sh" });

        Assert.NotEqual(a, b);
        foreach (var token in new[] { a, b })
        {
            Assert.DoesNotContain('+', token);
            Assert.DoesNotContain('/', token);
            Assert.DoesNotContain('=', token);
        }
    }
}



