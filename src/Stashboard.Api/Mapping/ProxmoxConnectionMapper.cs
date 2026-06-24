using Stashboard.Api.Contracts;
using Stashboard.Core.Abstractions;
using Stashboard.Core.Entities;
using Stashboard.Core.Enums;
using Stashboard.Core.Scheduling;

namespace Stashboard.Api.Mapping;

/// <summary>
/// V6.0 — maps between <see cref="ProxmoxConnectionEntity"/> and its API
/// contracts. Owns the encryption / tri-state-secret semantics for the API
/// token secret and the SSH material, and builds the decrypted
/// <see cref="ProxmoxConnectionProfile"/> the checker consumes. Mirrors
/// <c>DockerConnectionMapper</c>.
/// </summary>
public interface IProxmoxConnectionMapper
{
    ProxmoxConnectionResponse ToResponse(
        ProxmoxConnectionEntity entity, IReadOnlyList<ProxmoxGuestEntity> guests,
        IReadOnlyDictionary<int, (Guid ServiceId, string ServiceName)>? linkedServices = null);

    void ApplyUpsert(ProxmoxConnectionEntity entity, ProxmoxConnectionUpsertRequest request);

    /// <summary>Decrypts the persisted entity into the checker's profile.</summary>
    ProxmoxConnectionProfile BuildProfile(ProxmoxConnectionEntity entity);

    /// <summary>Builds a transient profile for a test, resolving tri-state
    /// secrets against the persisted connection when one exists.</summary>
    ProxmoxConnectionProfile BuildProfile(ProxmoxConnectionPingRequest request, ProxmoxConnectionEntity? existing);
}

public sealed class ProxmoxConnectionMapper(IEncryptionService encryption) : IProxmoxConnectionMapper
{
    public ProxmoxConnectionResponse ToResponse(
        ProxmoxConnectionEntity entity, IReadOnlyList<ProxmoxGuestEntity> guests,
        IReadOnlyDictionary<int, (Guid ServiceId, string ServiceName)>? linkedServices = null) =>
        new(
            entity.Id,
            entity.Name,
            entity.ApiBaseUrl,
            entity.NodeName,
            entity.ServerType,
            entity.ApiTokenId,
            HasApiTokenSecret: !string.IsNullOrEmpty(entity.ApiTokenSecretEncrypted),
            entity.SkipTlsVerify,
            entity.SshHost,
            entity.SshPort,
            entity.SshUsername,
            HasSshPrivateKey: !string.IsNullOrEmpty(entity.SshPrivateKeyEncrypted),
            HasSshPrivateKeyPassphrase: !string.IsNullOrEmpty(entity.SshPrivateKeyPassphraseEncrypted),
            entity.AllowConsole,
            entity.AllowUpdates,
            entity.AllowDestroy,
            entity.AllowCreate,
            entity.AllowClone,
            entity.AllowRestore,
            entity.Enabled,
            entity.UpdateNotificationsEnabled,
            entity.TelegramNotificationsEnabled,
            entity.ScheduleType,
            entity.CheckEveryHours,
            entity.CheckAtTime,
            entity.CheckOnDayOfWeek,
            entity.LastCheckedUtc,
            entity.LastError,
            guests
                .OrderBy(g => g.GuestType)   // node (0) first, then LXCs
                .ThenBy(g => g.VmId)
                .Select(g => ToGuestResponse(g, linkedServices))
                .ToList(),
            entity.TelemetryPollSeconds,
            entity.WebhookToken,
            entity.LastWebhookReceivedUtc,
            entity.CreatedUtc,
            entity.UpdatedUtc);

    private static ProxmoxGuestResponse ToGuestResponse(
        ProxmoxGuestEntity g, IReadOnlyDictionary<int, (Guid ServiceId, string ServiceName)>? linkedServices)
    {
        (Guid ServiceId, string ServiceName)? link =
            linkedServices is not null && linkedServices.TryGetValue(g.VmId, out var l) ? l : null;
        return new(
            g.VmId, g.Name, g.GuestType, g.IsRunning, g.PendingUpdates, g.LastError,
            g.IpAddress, g.UptimeSeconds, g.CpuCores, g.MemoryBytes, g.DiskBytes,
            SplitTags(g.Tags), g.LastCheckedUtc, g.MonitoringEnabled, g.MonitoringSnoozedUntil,
            link?.ServiceId, link?.ServiceName);
    }

    private static IReadOnlyList<string> SplitTags(string? tags) =>
        string.IsNullOrWhiteSpace(tags)
            ? []
            : tags.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public void ApplyUpsert(ProxmoxConnectionEntity entity, ProxmoxConnectionUpsertRequest request)
    {
        entity.Name = request.Name.Trim();
        entity.ApiBaseUrl = request.ApiBaseUrl.Trim();
        entity.NodeName = request.NodeName.Trim();
        entity.ServerType = request.ServerType;
        entity.ApiTokenId = request.ApiTokenId.Trim();
        entity.ApiTokenSecretEncrypted = ApplySecret(entity.ApiTokenSecretEncrypted, request.ApiTokenSecret);
        entity.SkipTlsVerify = request.SkipTlsVerify;

        entity.SshHost = NormalizeNullableString(request.SshHost);
        entity.SshPort = request.SshPort is > 0 and <= 65535 ? request.SshPort : (entity.SshHost is null ? null : 22);
        entity.SshUsername = NormalizeNullableString(request.SshUsername);
        entity.SshPrivateKeyEncrypted = ApplySecret(entity.SshPrivateKeyEncrypted, request.SshPrivateKey);
        entity.SshPrivateKeyPassphraseEncrypted = ApplySecret(entity.SshPrivateKeyPassphraseEncrypted, request.SshPrivateKeyPassphrase);

        entity.AllowConsole = request.AllowConsole;
        entity.AllowUpdates = request.AllowUpdates;
        entity.AllowDestroy = request.AllowDestroy;
        entity.AllowCreate = request.AllowCreate;
        entity.AllowClone = request.AllowClone;
        entity.AllowRestore = request.AllowRestore;

        entity.Enabled = request.Enabled;
        // Telemetry poll interval: clamp a supplied value to 5..300s; null resets
        // the host to the app default (the queries fall back to 20s).
        entity.TelemetryPollSeconds = request.TelemetryPollSeconds is { } poll
            ? Math.Clamp(poll, 5, 300)
            : null;
        entity.UpdateNotificationsEnabled = request.UpdateNotificationsEnabled;
        entity.TelegramNotificationsEnabled = request.TelegramNotificationsEnabled;

        entity.ScheduleType = request.ScheduleType;
        entity.CheckEveryHours = CheckScheduleEvaluator.AllowedHourlyValues.Contains(request.CheckEveryHours)
            ? request.CheckEveryHours
            : 24;
        entity.CheckAtTime = request.ScheduleType is CheckScheduleType.Daily or CheckScheduleType.Weekly
            ? request.CheckAtTime
            : null;
        entity.CheckOnDayOfWeek = request.ScheduleType == CheckScheduleType.Weekly
            ? request.CheckOnDayOfWeek
            : null;

        entity.UpdatedUtc = DateTime.UtcNow;
    }

    public ProxmoxConnectionProfile BuildProfile(ProxmoxConnectionEntity entity) =>
        new(
            entity.ApiBaseUrl,
            entity.NodeName,
            entity.ApiTokenId,
            SafeDecrypt(entity.ApiTokenSecretEncrypted) ?? string.Empty,
            entity.SkipTlsVerify,
            BuildSshFromEntity(entity),
            entity.ServerType);

    public ProxmoxConnectionProfile BuildProfile(ProxmoxConnectionPingRequest request, ProxmoxConnectionEntity? existing) =>
        new(
            request.ApiBaseUrl.Trim(),
            request.NodeName.Trim(),
            request.ApiTokenId.Trim(),
            ResolveSecret(existing?.ApiTokenSecretEncrypted, request.ApiTokenSecret) ?? string.Empty,
            request.SkipTlsVerify,
            BuildSshFromRequest(request, existing),
            request.ServerType);

    // ── private helpers (mirror DockerConnectionMapper) ───────────────────────

    private string? ApplySecret(string? existingEncrypted, SecretValueUpsert? upsert)
    {
        if (upsert is null) return existingEncrypted;
        return upsert.Action switch
        {
            SecretValueAction.Keep => existingEncrypted,
            SecretValueAction.Clear => null,
            SecretValueAction.Set => string.IsNullOrEmpty(upsert.Value) ? null : encryption.Encrypt(upsert.Value),
            _ => existingEncrypted,
        };
    }

    private string? ResolveSecret(string? existingEncrypted, SecretValueUpsert? upsert)
    {
        if (upsert is null) return SafeDecrypt(existingEncrypted);
        return upsert.Action switch
        {
            SecretValueAction.Keep => SafeDecrypt(existingEncrypted),
            SecretValueAction.Clear => null,
            SecretValueAction.Set => upsert.Value,
            _ => SafeDecrypt(existingEncrypted),
        };
    }

    private DockerSshCredentials? BuildSshFromEntity(ProxmoxConnectionEntity entity)
    {
        var privateKey = SafeDecrypt(entity.SshPrivateKeyEncrypted);
        if (string.IsNullOrEmpty(entity.SshHost)
            || string.IsNullOrEmpty(entity.SshUsername)
            || string.IsNullOrEmpty(privateKey))
            return null;

        // RemoteSocketPath is unused for `pct exec` (it's a Docker-tunnel
        // concept); the SSH command runner reads only host/port/user/key.
        return new DockerSshCredentials(
            entity.SshHost,
            entity.SshPort ?? 22,
            entity.SshUsername,
            privateKey,
            SafeDecrypt(entity.SshPrivateKeyPassphraseEncrypted),
            RemoteSocketPath: string.Empty);
    }

    private DockerSshCredentials? BuildSshFromRequest(ProxmoxConnectionPingRequest request, ProxmoxConnectionEntity? existing)
    {
        var host = NormalizeNullableString(request.SshHost) ?? existing?.SshHost;
        var username = NormalizeNullableString(request.SshUsername) ?? existing?.SshUsername;
        var port = request.SshPort is > 0 and <= 65535 ? request.SshPort.Value : existing?.SshPort ?? 22;
        var privateKey = ResolveSecret(existing?.SshPrivateKeyEncrypted, request.SshPrivateKey);
        var passphrase = ResolveSecret(existing?.SshPrivateKeyPassphraseEncrypted, request.SshPrivateKeyPassphrase);

        if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(username) || string.IsNullOrEmpty(privateKey))
            return null;

        return new DockerSshCredentials(host, port, username, privateKey, passphrase, RemoteSocketPath: string.Empty);
    }

    private static string? NormalizeNullableString(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private string? SafeDecrypt(string? encrypted)
    {
        if (string.IsNullOrEmpty(encrypted)) return null;
        try { return encryption.Decrypt(encrypted); }
        catch { return null; }
    }
}
