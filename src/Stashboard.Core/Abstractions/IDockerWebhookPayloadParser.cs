namespace Stashboard.Core.Abstractions;

/// <summary>
/// V2.6 — best-effort parser for the JSON webhook payloads Stashboard accepts.
/// The URL token (<c>/api/docker/webhooks/{watchToken}</c>) already
/// authenticates the call, so a malformed or unrecognised body is NOT a
/// failure — it just yields an empty <see cref="DockerWebhookPayload"/>
/// and the watch is re-checked anyway. The parsed metadata is recorded
/// alongside the inbound delivery for diagnostics ("Docker Hub push for
/// owner/repo:latest at 2026-05-18 …").
/// </summary>
public interface IDockerWebhookPayloadParser
{
    /// <summary>Parse a webhook body. Never throws; returns the empty payload
    /// when the body is unrecognised, empty, or malformed.</summary>
    DockerWebhookPayload Parse(string? rawBody);
}

/// <summary>
/// Normalised view across Docker Hub <c>push</c> events, GitHub
/// <c>registry_package</c> events, and generic OCI registry pushes. Every
/// field is optional — different sources populate different subsets.
/// </summary>
/// <param name="Source">Best-effort detected source ("docker-hub", "ghcr",
/// "generic-oci", or "unknown").</param>
/// <param name="Repository">Repository path when present (e.g.
/// <c>library/nginx</c> or <c>owner/repo</c>).</param>
/// <param name="Tag">Pushed tag, when present.</param>
/// <param name="PushedAtUtc">Best-effort push timestamp (Unix epoch
/// seconds in Docker Hub, ISO-8601 in GitHub).</param>
public sealed record DockerWebhookPayload(
    string Source,
    string? Repository,
    string? Tag,
    DateTime? PushedAtUtc)
{
    public static DockerWebhookPayload Unknown { get; } = new("unknown", null, null, null);
}
