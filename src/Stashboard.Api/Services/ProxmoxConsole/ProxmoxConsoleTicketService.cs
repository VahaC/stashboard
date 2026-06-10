using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Stashboard.Core.Options;

namespace Stashboard.Api.Services.ProxmoxConsole;

/// <summary>
/// V6.6 — in-memory implementation of <see cref="IProxmoxConsoleTicketService"/>.
/// Process-local is sufficient (single-instance deployment, seconds-long TTL, a
/// restart simply invalidates outstanding tickets). Uses the injected
/// <see cref="TimeProvider"/> so expiry is testable. Mirrors the V5.7
/// <c>ContainerExecTicketService</c>; the ticket payload carries the console
/// target (host connection + LXC vmid + command).
/// </summary>
public sealed class ProxmoxConsoleTicketService(
    IOptions<ProxmoxConsoleOptions> options, TimeProvider timeProvider) : IProxmoxConsoleTicketService
{
    private sealed record Entry(
        Guid UserId,
        Guid ConnectionId,
        int VmId,
        IReadOnlyList<string> Command,
        DateTimeOffset ExpiresUtc);

    private readonly ConcurrentDictionary<string, Entry> _tickets = new(StringComparer.Ordinal);

    public string Issue(Guid userId, Guid connectionId, int vmId, IReadOnlyList<string> command)
    {
        PruneExpired();

        var token = Base64UrlToken(32);
        var ttl = TimeSpan.FromSeconds(Math.Max(1, options.Value.TicketTtlSeconds));
        _tickets[token] = new Entry(
            userId, connectionId, vmId, command, timeProvider.GetUtcNow().Add(ttl));
        return token;
    }

    public ProxmoxConsoleTicket? Redeem(string token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        if (!_tickets.TryRemove(token, out var entry)) return null; // unknown or already used
        if (entry.ExpiresUtc <= timeProvider.GetUtcNow()) return null; // expired (already removed)
        return new ProxmoxConsoleTicket(entry.UserId, entry.ConnectionId, entry.VmId, entry.Command);
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
