using System.Security.Cryptography;
using Stashboard.Core.Abstractions;

namespace Stashboard.Infrastructure.Docker;

/// <summary>
/// V2.6 — CSPRNG-backed token generator. 32 bytes of randomness gives
/// 256 bits of entropy — same ballpark as the refresh-token hash and well
/// beyond what's needed to defeat brute force on a public webhook URL.
/// </summary>
public sealed class DockerWebhookTokenGenerator : IDockerWebhookTokenGenerator
{
    public string Generate()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
