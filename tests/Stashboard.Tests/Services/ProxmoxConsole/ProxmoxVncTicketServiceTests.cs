using Microsoft.Extensions.Options;
using Stashboard.Api.Services.ProxmoxConsole;
using Stashboard.Core.Abstractions;
using Stashboard.Core.Options;
using Stashboard.Tests.Infrastructure;

namespace Stashboard.Tests.Services.ProxmoxConsole;

/// <summary>
/// V8.6 — unit tests for <see cref="ProxmoxVncTicketService"/>. Drives a
/// <see cref="TestTimeProvider"/> so expiry is deterministic. The contract:
/// tickets are single-use, short-lived, and bound to a specific
/// (user, connection, vmid, vncProxy) tuple. Mirrors the V6.6
/// <see cref="ProxmoxConsoleTicketServiceTests"/>.
/// </summary>
public class ProxmoxVncTicketServiceTests
{
    private readonly TestTimeProvider _time = new();
    private readonly ProxmoxVncTicketService _service;

    public ProxmoxVncTicketServiceTests()
    {
        var options = Options.Create(new ProxmoxConsoleOptions { TicketTtlSeconds = 30 });
        _service = new ProxmoxVncTicketService(options, _time);
    }

    [Fact]
    public void Redeem_ValidUnexpiredTicket_ReturnsBoundPayload()
    {
        var userId = Guid.NewGuid();
        var connectionId = Guid.NewGuid();
        var vncProxy = new ProxmoxVncProxyTicket("PVEVNC:deadbeef", 5901);
        var token = _service.Issue(userId, connectionId, 110, vncProxy);

        var redeemed = _service.Redeem(token);

        Assert.NotNull(redeemed);
        Assert.Equal(userId, redeemed!.UserId);
        Assert.Equal(connectionId, redeemed.ConnectionId);
        Assert.Equal(110, redeemed.VmId);
        Assert.Equal(vncProxy, redeemed.VncProxy);
    }

    [Fact]
    public void Redeem_SameTicketTwice_FailsSecondTime()
    {
        var token = _service.Issue(Guid.NewGuid(), Guid.NewGuid(), 100, new ProxmoxVncProxyTicket("t", 5900));

        Assert.NotNull(_service.Redeem(token));
        Assert.Null(_service.Redeem(token)); // single-use
    }

    [Fact]
    public void Redeem_AfterTtlElapsed_Fails()
    {
        var token = _service.Issue(Guid.NewGuid(), Guid.NewGuid(), 100, new ProxmoxVncProxyTicket("t", 5900));

        _time.Advance(TimeSpan.FromSeconds(31)); // TTL is 30s

        Assert.Null(_service.Redeem(token));
    }

    [Fact]
    public void Redeem_JustBeforeExpiry_StillWorks()
    {
        var token = _service.Issue(Guid.NewGuid(), Guid.NewGuid(), 100, new ProxmoxVncProxyTicket("t", 5900));

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
        var a = _service.Issue(Guid.NewGuid(), Guid.NewGuid(), 100, new ProxmoxVncProxyTicket("t", 5900));
        var b = _service.Issue(Guid.NewGuid(), Guid.NewGuid(), 100, new ProxmoxVncProxyTicket("t", 5900));

        Assert.NotEqual(a, b);
        foreach (var token in new[] { a, b })
        {
            Assert.DoesNotContain('+', token);
            Assert.DoesNotContain('/', token);
            Assert.DoesNotContain('=', token);
        }
    }
}


