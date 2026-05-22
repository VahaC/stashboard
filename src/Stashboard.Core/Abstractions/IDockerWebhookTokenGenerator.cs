namespace Stashboard.Core.Abstractions;

/// <summary>
/// V2.6 — generator for the URL secret that authenticates a registry
/// webhook delivery. Behind an interface so tests can substitute a
/// deterministic token without touching <c>RandomNumberGenerator</c>.
/// </summary>
public interface IDockerWebhookTokenGenerator
{
    /// <summary>Returns 64 lowercase hex characters (32 random bytes from
    /// a CSPRNG).</summary>
    string Generate();
}
