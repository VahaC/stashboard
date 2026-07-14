using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Stashboard.Core.Abstractions;
using Stashboard.Core.Options;

namespace Stashboard.Api.Services.ProxmoxConsole;

/// <summary>
/// V8.6 — in-memory implementation of <see cref="IProxmoxVncTicketService"/>.
/// Process-local is sufficient (single-instance deployment, seconds-long TTL, a
/// restart simply invalidates outstanding tickets). Uses the injected
/// <see cref="TimeProvider"/> so expiry is testable. Mirrors
/// <see cref="ProxmoxConsoleTicketService"/>; the payload carries the VM-console
/// target (host connection + VM vmid + the minted VNC session) rather than a shell
/// command. Shares the <see cref="ProxmoxConsoleOptions"/> TTL with the LXC
/// console.
/// </summary>
public sealed class ProxmoxVncTicketService(
    IOptions<ProxmoxConsoleOptions> options, TimeProvider timeProvider) : IProxmoxVncTicketService
{
    private sealed record Entry(
        Guid UserId,
        Guid ConnectionId,
        int VmId,
        ProxmoxVncProxyTicket VncProxy,
        DateTimeOffset ExpiresUtc);

    private readonly ConcurrentDictionary<string, Entry> _tickets = new(StringComparer.Ordinal);

    public string Issue(Guid userId, Guid connectionId, int vmId, ProxmoxVncProxyTicket vncProxy)
    {
        PruneExpired();

        var token = Base64UrlToken(32);
        var ttl = TimeSpan.FromSeconds(Math.Max(1, options.Value.TicketTtlSeconds));
        _tickets[token] = new Entry(
            userId, connectionId, vmId, vncProxy, timeProvider.GetUtcNow().Add(ttl));
        return token;
    }

    public ProxmoxVncTicket? Redeem(string token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        if (!_tickets.TryRemove(token, out var entry)) return null; // unknown or already used
        if (entry.ExpiresUtc <= timeProvider.GetUtcNow()) return null; // expired (already removed)
        return new ProxmoxVncTicket(entry.UserId, entry.ConnectionId, entry.VmId, entry.VncProxy);
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
