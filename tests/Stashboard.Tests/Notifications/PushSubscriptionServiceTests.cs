using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Stashboard.Api.Auth;
using Stashboard.Api.Notifications.Push;
using Stashboard.Tests.Infrastructure;

namespace Stashboard.Tests.Notifications;

/// <summary>
/// V10.6 — web-push subscription storage + fan-out. Covers upsert/list/delete, the
/// "channel is on when the user has a subscription" gate, delivery, pruning an
/// expired (404/410) endpoint, and keeping a subscription on a transient failure.
/// </summary>
public class PushSubscriptionServiceTests : DatabaseTestBase
{
    private DataFactory _factory = default!;
    private Guid _userId;
    private Guid _otherUserId;

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        _factory = new DataFactory(_dbContext, new NoopEncryption(), new Pbkdf2PasswordHasher(), Guid.Empty);
        _userId = (await _factory.UserAsync("owner@x")).Id;
        _otherUserId = (await _factory.UserAsync("other@x")).Id;
    }

    private PushSubscriptionService Sut(IPushSender sender) =>
        new(_dbContext, sender, TimeProvider.System, NullLogger<PushSubscriptionService>.Instance);

    [Fact]
    public async Task Upsert_IsIdempotentPerEndpoint_AndRefreshesKeys()
    {
        var sut = Sut(Mock.Of<IPushSender>());

        await sut.UpsertAsync(_userId, "https://push/1", "p256-a", "auth-a", "Chrome on Android");
        await sut.UpsertAsync(_userId, "https://push/1", "p256-b", "auth-b", "Edge on Windows");

        var devices = await sut.ListAsync(_userId);
        Assert.Single(devices);
        Assert.Equal("p256-b", devices[0].P256dh);
        Assert.Equal("Edge on Windows", devices[0].Label);
        Assert.True(await sut.HasAnyAsync(_userId));
    }

    [Fact]
    public async Task Delete_RemovesOwnedSubscriptionOnly()
    {
        var sut = Sut(Mock.Of<IPushSender>());
        await sut.UpsertAsync(_userId, "https://push/1", "p", "a", null);
        var id = (await sut.ListAsync(_userId))[0].Id;

        Assert.False(await sut.DeleteAsync(_otherUserId, id)); // not their subscription
        Assert.True(await sut.DeleteAsync(_userId, id));
        Assert.False(await sut.HasAnyAsync(_userId));
    }

    [Fact]
    public async Task SendToUser_NoSubscriptions_ReturnsZeroAttempts()
    {
        var sut = Sut(Mock.Of<IPushSender>());

        var result = await sut.SendToUserAsync(_userId, new PushMessage("t", "b"));

        Assert.Equal(0, result.Attempted);
        Assert.False(result.HadTransientFailure);
    }

    [Fact]
    public async Task SendToUser_Delivers_AndStampsLastSuccess()
    {
        var sender = new Mock<IPushSender>();
        sender.Setup(s => s.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(PushSendOutcome.Delivered);
        var sut = Sut(sender.Object);
        await sut.UpsertAsync(_userId, "https://push/1", "p", "a", null);
        await sut.UpsertAsync(_userId, "https://push/2", "p", "a", null);

        var result = await sut.SendToUserAsync(_userId, new PushMessage("Down", "Service X is down", "/?service=1", "service-1"));

        Assert.Equal(2, result.Attempted);
        Assert.Equal(2, result.Delivered);
        Assert.Equal(0, result.Pruned);
        Assert.All(await sut.ListAsync(_userId), d => Assert.NotNull(d.LastSuccessUtc));
    }

    [Fact]
    public async Task SendToUser_PrunesExpiredEndpoints()
    {
        var sender = new Mock<IPushSender>();
        sender.Setup(s => s.SendAsync(It.Is<string>(e => e.Contains("gone")), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(PushSendOutcome.Expired);
        sender.Setup(s => s.SendAsync(It.Is<string>(e => e.Contains("ok")), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(PushSendOutcome.Delivered);
        var sut = Sut(sender.Object);
        await sut.UpsertAsync(_userId, "https://push/gone", "p", "a", null);
        await sut.UpsertAsync(_userId, "https://push/ok", "p", "a", null);

        var result = await sut.SendToUserAsync(_userId, new PushMessage("t", "b"));

        Assert.Equal(1, result.Delivered);
        Assert.Equal(1, result.Pruned);
        var remaining = await sut.ListAsync(_userId);
        Assert.Single(remaining);
        Assert.EndsWith("/ok", remaining[0].Endpoint);
    }

    [Fact]
    public async Task SendToUser_KeepsSubscription_OnTransientFailure()
    {
        var sender = new Mock<IPushSender>();
        sender.Setup(s => s.SendAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(PushSendOutcome.Failed);
        var sut = Sut(sender.Object);
        await sut.UpsertAsync(_userId, "https://push/1", "p", "a", null);

        var result = await sut.SendToUserAsync(_userId, new PushMessage("t", "b"));

        Assert.True(result.HadTransientFailure);
        Assert.Equal(0, result.Pruned);
        Assert.Single(await sut.ListAsync(_userId));
    }

    private sealed class NoopEncryption : Stashboard.Core.Abstractions.IEncryptionService
    {
        public string Encrypt(string plain) => plain;
        public string Decrypt(string cipher) => cipher;
    }
}
