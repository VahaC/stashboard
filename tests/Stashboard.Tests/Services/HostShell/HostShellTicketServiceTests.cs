using Microsoft.Extensions.Options;
using Stashboard.Api.Services.HostShell;
using Stashboard.Core.Options;
using Stashboard.Tests.Infrastructure;

namespace Stashboard.Tests.Services.HostShell;

/// <summary>
/// V5.3 — unit tests for <see cref="HostShellTicketService"/>. Drives a
/// <see cref="TestTimeProvider"/> so expiry is deterministic. The contract:
/// tickets are single-use, short-lived, and bound to a specific
/// (user, connection) pair.
/// </summary>
public class HostShellTicketServiceTests
{
    private readonly TestTimeProvider _time = new();
    private readonly HostShellTicketService _service;

    public HostShellTicketServiceTests()
    {
        var options = Options.Create(new HostShellOptions { TicketTtlSeconds = 30 });
        _service = new HostShellTicketService(options, _time);
    }

    [Fact]
    public void Redeem_ValidUnexpiredTicket_ReturnsBoundIdentity()
    {
        var userId = Guid.NewGuid();
        var connectionId = Guid.NewGuid();
        var token = _service.Issue(userId, connectionId);

        var redeemed = _service.Redeem(token);

        Assert.NotNull(redeemed);
        Assert.Equal(userId, redeemed!.UserId);
        Assert.Equal(connectionId, redeemed.ConnectionId);
    }

    [Fact]
    public void Redeem_SameTicketTwice_FailsSecondTime()
    {
        var token = _service.Issue(Guid.NewGuid(), Guid.NewGuid());

        Assert.NotNull(_service.Redeem(token));
        Assert.Null(_service.Redeem(token)); // single-use
    }

    [Fact]
    public void Redeem_AfterTtlElapsed_Fails()
    {
        var token = _service.Issue(Guid.NewGuid(), Guid.NewGuid());

        _time.Advance(TimeSpan.FromSeconds(31)); // TTL is 30s

        Assert.Null(_service.Redeem(token));
    }

    [Fact]
    public void Redeem_JustBeforeExpiry_StillWorks()
    {
        var token = _service.Issue(Guid.NewGuid(), Guid.NewGuid());

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
        var a = _service.Issue(Guid.NewGuid(), Guid.NewGuid());
        var b = _service.Issue(Guid.NewGuid(), Guid.NewGuid());

        Assert.NotEqual(a, b);
        // URL-safe base64: no '+', '/', or '=' padding so it rides on a query string.
        foreach (var token in new[] { a, b })
        {
            Assert.DoesNotContain('+', token);
            Assert.DoesNotContain('/', token);
            Assert.DoesNotContain('=', token);
        }
    }
}


