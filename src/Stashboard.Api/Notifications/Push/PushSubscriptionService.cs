using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Stashboard.Api.Data;

namespace Stashboard.Api.Notifications.Push;

/// <inheritdoc cref="IPushSubscriptionService"/>
public sealed class PushSubscriptionService(
    ApplicationDbContext db,
    IPushSender sender,
    TimeProvider time,
    ILogger<PushSubscriptionService> logger) : IPushSubscriptionService
{
    private static readonly JsonSerializerOptions PayloadJson = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<PushSubscriptionEntity>> ListAsync(Guid userId, CancellationToken cancellationToken = default) =>
        await db.PushSubscriptions.AsNoTracking()
            .Where(s => s.UserId == userId)
            .OrderByDescending(s => s.CreatedUtc)
            .ToListAsync(cancellationToken);

    public async Task UpsertAsync(
        Guid userId, string endpoint, string p256dh, string auth, string? label, CancellationToken cancellationToken = default)
    {
        var existing = await db.PushSubscriptions.FirstOrDefaultAsync(s => s.Endpoint == endpoint, cancellationToken);
        if (existing is not null)
        {
            // Re-subscribing the same device: refresh the (possibly rotated) keys and
            // re-home it to the current user, rather than duplicating the row.
            existing.UserId = userId;
            existing.P256dh = p256dh;
            existing.Auth = auth;
            existing.Label = label;
        }
        else
        {
            db.PushSubscriptions.Add(new PushSubscriptionEntity
            {
                UserId = userId,
                Endpoint = endpoint,
                P256dh = p256dh,
                Auth = auth,
                Label = label,
            });
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> DeleteAsync(Guid userId, Guid id, CancellationToken cancellationToken = default)
    {
        var row = await db.PushSubscriptions.FirstOrDefaultAsync(s => s.Id == id && s.UserId == userId, cancellationToken);
        if (row is null) return false;
        db.PushSubscriptions.Remove(row);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public Task<bool> HasAnyAsync(Guid userId, CancellationToken cancellationToken = default) =>
        db.PushSubscriptions.AnyAsync(s => s.UserId == userId, cancellationToken);

    public async Task<PushFanoutResult> SendToUserAsync(
        Guid userId, PushMessage message, CancellationToken cancellationToken = default)
    {
        var subscriptions = await db.PushSubscriptions.Where(s => s.UserId == userId).ToListAsync(cancellationToken);
        if (subscriptions.Count == 0)
            return new PushFanoutResult(0, 0, 0, HadTransientFailure: false);

        var payload = JsonSerializer.Serialize(message, PayloadJson);
        var delivered = 0;
        var expired = new List<PushSubscriptionEntity>();
        var hadTransientFailure = false;

        foreach (var subscription in subscriptions)
        {
            var outcome = await sender.SendAsync(
                subscription.Endpoint, subscription.P256dh, subscription.Auth, payload, cancellationToken);
            switch (outcome)
            {
                case PushSendOutcome.Delivered:
                    subscription.LastSuccessUtc = time.GetUtcNow().UtcDateTime;
                    delivered++;
                    break;
                case PushSendOutcome.Expired:
                    expired.Add(subscription);
                    break;
                case PushSendOutcome.Failed:
                    hadTransientFailure = true;
                    break;
            }
        }

        if (expired.Count > 0)
        {
            db.PushSubscriptions.RemoveRange(expired);
            logger.LogInformation("Pruned {Count} expired push subscription(s) for user {UserId}", expired.Count, userId);
        }

        await db.SaveChangesAsync(cancellationToken);
        return new PushFanoutResult(subscriptions.Count, delivered, expired.Count, hadTransientFailure);
    }
}
