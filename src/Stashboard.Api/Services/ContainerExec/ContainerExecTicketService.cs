using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Stashboard.Core.Options;

namespace Stashboard.Api.Services.ContainerExec;

/// <summary>
/// V5.7 — in-memory implementation of <see cref="IContainerExecTicketService"/>.
/// Process-local is sufficient (single-instance deployment, seconds-long TTL, a
/// restart simply invalidates outstanding tickets). Uses the injected
/// <see cref="TimeProvider"/> so expiry is testable. Mirrors the V5.3
/// <c>HostShellTicketService</c>; the only difference is the ticket payload also
/// carries the exec target (container + command).
/// </summary>
public sealed class ContainerExecTicketService(
    IOptions<ContainerExecOptions> options, TimeProvider timeProvider) : IContainerExecTicketService
{
    private sealed record Entry(
        Guid UserId,
        Guid ConnectionId,
        string ContainerName,
        IReadOnlyList<string> Command,
        DateTimeOffset ExpiresUtc);

    private readonly ConcurrentDictionary<string, Entry> _tickets = new(StringComparer.Ordinal);

    public string Issue(Guid userId, Guid connectionId, string containerName, IReadOnlyList<string> command)
    {
        PruneExpired();

        var token = Base64UrlToken(32);
        var ttl = TimeSpan.FromSeconds(Math.Max(1, options.Value.TicketTtlSeconds));
        _tickets[token] = new Entry(
            userId, connectionId, containerName, command, timeProvider.GetUtcNow().Add(ttl));
        return token;
    }

    public ContainerExecTicket? Redeem(string token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        if (!_tickets.TryRemove(token, out var entry)) return null; // unknown or already used
        if (entry.ExpiresUtc <= timeProvider.GetUtcNow()) return null; // expired (already removed)
        return new ContainerExecTicket(entry.UserId, entry.ConnectionId, entry.ContainerName, entry.Command);
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
