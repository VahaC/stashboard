using System.Text.Json;
using Stashboard.Core.Abstractions;

namespace Stashboard.Infrastructure.Docker;

/// <summary>
/// V2.6 — recognises Docker Hub <c>push</c> events, GitHub
/// <c>registry_package</c> events, and a generic OCI shape. Anything else
/// degrades cleanly to <see cref="DockerWebhookPayload.Unknown"/>; the
/// caller still re-checks the watch.
/// </summary>
public sealed class DockerWebhookPayloadParser : IDockerWebhookPayloadParser
{
    public DockerWebhookPayload Parse(string? rawBody)
    {
        if (string.IsNullOrWhiteSpace(rawBody)) return DockerWebhookPayload.Unknown;

        JsonDocument document;
        try { document = JsonDocument.Parse(rawBody); }
        catch (JsonException) { return DockerWebhookPayload.Unknown; }

        using var _ = document;
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return DockerWebhookPayload.Unknown;

        // Docker Hub: { "push_data": { "tag": "...", "pushed_at": <unix> }, "repository": { "repo_name": "owner/repo" } }
        if (root.TryGetProperty("push_data", out var pushData)
            && pushData.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("repository", out var repository)
            && repository.ValueKind == JsonValueKind.Object)
        {
            var tag = TryGetString(pushData, "tag");
            var repoName = TryGetString(repository, "repo_name");
            var pushedAt = TryGetUnixSeconds(pushData, "pushed_at");
            return new DockerWebhookPayload("docker-hub", repoName, tag, pushedAt);
        }

        // GitHub: { "action": "published", "registry_package": { "package_version": { "container_metadata": { "tag": { "name": "latest" } } }, "name": "repo", "namespace": "owner" } }
        if (root.TryGetProperty("registry_package", out var pkg)
            && pkg.ValueKind == JsonValueKind.Object)
        {
            var name = TryGetString(pkg, "name");
            var ns = TryGetString(pkg, "namespace");
            var repo = !string.IsNullOrEmpty(ns) && !string.IsNullOrEmpty(name)
                ? $"{ns}/{name}"
                : name;

            string? tag = null;
            if (pkg.TryGetProperty("package_version", out var version)
                && version.ValueKind == JsonValueKind.Object
                && version.TryGetProperty("container_metadata", out var meta)
                && meta.ValueKind == JsonValueKind.Object
                && meta.TryGetProperty("tag", out var tagNode)
                && tagNode.ValueKind == JsonValueKind.Object)
            {
                tag = TryGetString(tagNode, "name");
            }

            var pushedAt = TryGetIsoTimestamp(pkg, "updated_at")
                ?? TryGetIsoTimestamp(pkg, "created_at");
            return new DockerWebhookPayload("ghcr", repo, tag, pushedAt);
        }

        // Generic OCI distribution: { "events": [ { "action": "push",
        //                                          "target": { "repository": "...", "tag": "..." },
        //                                          "timestamp": "..." } ] }
        if (root.TryGetProperty("events", out var events)
            && events.ValueKind == JsonValueKind.Array)
        {
            foreach (var ev in events.EnumerateArray())
            {
                if (ev.ValueKind != JsonValueKind.Object) continue;
                var action = TryGetString(ev, "action");
                if (action is not null && !string.Equals(action, "push", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!ev.TryGetProperty("target", out var target)
                    || target.ValueKind != JsonValueKind.Object) continue;
                var repo = TryGetString(target, "repository");
                var tag = TryGetString(target, "tag");
                var pushedAt = TryGetIsoTimestamp(ev, "timestamp");
                return new DockerWebhookPayload("generic-oci", repo, tag, pushedAt);
            }
        }

        return DockerWebhookPayload.Unknown;
    }

    private static string? TryGetString(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out var value)) return null;
        if (value.ValueKind != JsonValueKind.String) return null;
        var raw = value.GetString();
        return string.IsNullOrWhiteSpace(raw) ? null : raw;
    }

    private static DateTime? TryGetUnixSeconds(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out var value)) return null;
        if (value.ValueKind != JsonValueKind.Number) return null;
        if (!value.TryGetInt64(out var seconds)) return null;
        try { return DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime; }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    private static DateTime? TryGetIsoTimestamp(JsonElement parent, string property)
    {
        var raw = TryGetString(parent, property);
        if (raw is null) return null;
        return DateTime.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
            out var parsed) ? parsed : null;
    }
}
