using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Stashboard.Core.Options;

namespace Stashboard.Api.Services.HostShell;

/// <summary>
/// V5.3 — in-memory implementation of <see cref="IHostShellTicketService"/>.
/// Process-local is sufficient: Stashboard is a single-instance deployment, the
/// tickets live for seconds, and a restart simply invalidates any outstanding
/// ones (the user re-clicks Connect). Uses the injected <see cref="TimeProvider"/>
/// so expiry is testable without real time.
/// </summary>
public sealed class HostShellTicketService(
    IOptions<HostShellOptions> options, TimeProvider timeProvider) : IHostShellTicketService
{
    private sealed record Entry(Guid UserId, Guid ConnectionId, DateTimeOffset ExpiresUtc);

    private readonly ConcurrentDictionary<string, Entry> _tickets = new(StringComparer.Ordinal);

    public string Issue(Guid userId, Guid connectionId)
    {
        PruneExpired();

        // 32 bytes of CSPRNG entropy, URL-safe so it rides on a query string.
        var token = Base64UrlToken(32);
        var ttl = TimeSpan.FromSeconds(Math.Max(1, options.Value.TicketTtlSeconds));
        _tickets[token] = new Entry(userId, connectionId, timeProvider.GetUtcNow().Add(ttl));
        return token;
    }

    public HostShellTicket? Redeem(string token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        if (!_tickets.TryRemove(token, out var entry)) return null; // unknown or already used
        if (entry.ExpiresUtc <= timeProvider.GetUtcNow()) return null; // expired (already removed)
        return new HostShellTicket(entry.UserId, entry.ConnectionId);
    }

    private void PruneExpired()
    {
        var now = timeProvider.GetUtcNow();
        foreach (var (key, entry) in _tickets)
            if (entry.ExpiresUtc <= now)
                _tickets.TryRemove(key, out _);
    }

    private static string Base64UrlToken(int bytes)
    {
        var buffer = RandomNumberGenerator.GetBytes(bytes);
        return Convert.ToBase64String(buffer)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}
