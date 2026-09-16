using System.Collections.Concurrent;

namespace Stashboard.Api.Auth.Oidc;

/// <summary>The short-lived per-request data tied to one in-flight authorization (keyed by <c>state</c>).</summary>
public sealed record OidcAuthState(string CodeVerifier, string Nonce);

/// <summary>
/// Holds the PKCE verifier + nonce between <c>/oidc/start</c> and <c>/oidc/callback</c>. Single-instance,
/// in-memory and single-use: a state can be consumed exactly once and expires after a few minutes.
/// </summary>
public interface IOidcStateStore
{
    void Save(string state, OidcAuthState data, TimeSpan ttl);

    /// <summary>Atomically removes and returns the state's data, or null if missing / expired / already used.</summary>
    OidcAuthState? Consume(string state);
}

/// <summary>Default in-memory store. Lives as a singleton; safe for the single-instance deployment model.</summary>
public sealed class OidcStateStore(TimeProvider time) : IOidcStateStore
{
    private readonly ConcurrentDictionary<string, (OidcAuthState Data, DateTimeOffset Expiry)> _entries = new();

    public void Save(string state, OidcAuthState data, TimeSpan ttl)
    {
        Sweep();
        _entries[state] = (data, time.GetUtcNow() + ttl);
    }

    public OidcAuthState? Consume(string state)
    {
        if (!_entries.TryRemove(state, out var entry)) return null;
        return entry.Expiry > time.GetUtcNow() ? entry.Data : null;
    }

    /// <summary>Drops expired entries so an abandoned-login leak can't grow unbounded.</summary>
    private void Sweep()
    {
        var now = time.GetUtcNow();
        foreach (var (key, entry) in _entries)
            if (entry.Expiry <= now)
                _entries.TryRemove(key, out _);
    }
}
