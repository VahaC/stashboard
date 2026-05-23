using System.Text.RegularExpressions;
using Stashboard.Api.Contracts;
using Stashboard.Core.Abstractions;
using Stashboard.Core.Entities;
using Stashboard.Core.Enums;
using Stashboard.Core.Scheduling;
using Stashboard.Infrastructure.Docker;

namespace Stashboard.Api.Mapping;

/// <summary>
/// Maps between <see cref="DockerWatchEntity"/> and its API contracts. Owns
/// two responsibilities the controller would otherwise inline:
/// (1) Decrypting / re-encrypting registry-credential secrets, applying the
///     tri-state <see cref="SecretValueAction"/> semantics.
/// (2) Parsing the user's raw <c>ImageReference</c> into the canonical
///     registry/repository/tag triple stored on the entity, so the background
///     scan can query without re-parsing per row.
/// Host transport (socket / TLS) lives on the sibling
/// <see cref="DockerConnectionEntity"/> and is composed in
/// <see cref="BuildProfileFromEntity"/>.
/// </summary>
public interface IDockerWatchMapper
{
    DockerWatchResponse ToResponse(DockerWatchEntity entity);

    /// <summary>
    /// Applies <paramref name="request"/> to <paramref name="entity"/> in place.
    /// Parses the image reference (throws <see cref="FormatException"/> on
    /// malformed input). Honours the tri-state action for the registry-credential
    /// secrets — <see cref="SecretValueAction.Keep"/> leaves the encrypted column alone.
    /// </summary>
    /// <exception cref="FormatException">Thrown when <c>request.ImageReference</c> can't be parsed.</exception>
    void ApplyUpsert(DockerWatchEntity entity, DockerWatchUpsertRequest request);

    /// <summary>
    /// Builds a decrypted <see cref="DockerWatchProfile"/> for the orchestrator
    /// from a persisted watch and its parent connection.
    /// </summary>
    DockerWatchProfile BuildProfileFromEntity(DockerWatchEntity watch, DockerConnectionEntity connection);

    /// <summary>
    /// Builds a decrypted <see cref="DockerWatchProfile"/> from a per-watch
    /// test-connection request and the existing parent connection (host config
    /// is read straight off the connection entity). Resolves tri-state registry
    /// credentials against the persisted watch if one exists.
    /// </summary>
    /// <exception cref="FormatException">When the request's image reference is malformed.</exception>
    DockerWatchProfile BuildProfileFromTestRequest(
        DockerWatchTestRequest request,
        DockerConnectionEntity connection,
        DockerWatchEntity? existing);

    /// <summary>
    /// V2.7 — builds the decrypted profile the <see cref="Stashboard.Core.Abstractions.IDockerImageUpdater"/>
    /// needs to pull + recreate a container. Reuses the same connection +
    /// secret-resolution paths as the orchestrator profile but exposes the
    /// shape <c>IDockerImageUpdater</c> expects.
    /// </summary>
    DockerUpdateProfile BuildUpdateProfile(DockerWatchEntity watch, DockerConnectionEntity connection);

    /// <summary>V2.7 — map an audit row to its response shape.</summary>
    DockerUpdateAttemptResponse ToResponse(DockerUpdateAttemptEntity entity);
}

public sealed class DockerWatchMapper(
    IEncryptionService encryption,
    IImageReferenceParser imageReferenceParser) : IDockerWatchMapper
{
    public DockerWatchResponse ToResponse(DockerWatchEntity entity) =>
        new(
            entity.Id,
            entity.DockerConnectionId,
            entity.WebResourceId,
            entity.Label,
            entity.Enabled,
            entity.ImageReference,
            entity.RegistryHost,
            entity.Repository,
            entity.Tag,
            entity.ContainerName,
            HasRegistryCredentials: HasAnyRegistryCreds(entity),
            HasGitHubPat: !string.IsNullOrEmpty(entity.GitHubPatEncrypted),
            entity.RegistryAuthType,
            HasAwsCredentials: HasAwsCreds(entity),
            entity.AwsRegion,
            entity.UpdateNotificationsEnabled,
            entity.TelegramNotificationsEnabled,
            entity.ScheduleType,
            entity.CheckEveryHours,
            entity.CheckAtTime,
            entity.CheckOnDayOfWeek,
            entity.TagPatternFilter,
            entity.UpdateStatus,
            entity.CurrentDigest,
            entity.LatestDigest,
            entity.CurrentVersionTag,
            entity.LatestVersionTag,
            entity.LatestReleaseUrl,
            entity.LatestReleaseBody,
            entity.LastCheckedUtc,
            entity.LastUpdateDetectedUtc,
            CheckScheduleEvaluator.NextCheckUtc(
                entity.ScheduleType, entity.CheckEveryHours,
                entity.CheckAtTime, entity.CheckOnDayOfWeek,
                entity.LastCheckedUtc, DateTime.UtcNow),
            entity.LastError,
            entity.WebhookToken,
            entity.LastWebhookReceivedUtc,
            entity.CreatedUtc,
            entity.UpdatedUtc);

    public void ApplyUpsert(DockerWatchEntity entity, DockerWatchUpsertRequest request)
    {
        var parsed = imageReferenceParser.Parse(request.ImageReference);
        var tagFilter = NormalizeTagPatternFilter(request.TagPatternFilter);
        var schedule = NormalizeSchedule(request);
        var auth = NormalizeRegistryAuth(request, parsed.RegistryHost);

        entity.Label = request.Label.Trim();
        entity.Enabled = request.Enabled;
        entity.ImageReference = request.ImageReference.Trim();
        entity.RegistryHost = parsed.RegistryHost;
        entity.Repository = parsed.Repository;
        entity.Tag = parsed.Tag;
        entity.ContainerName = request.ContainerName.Trim();
        entity.UpdateNotificationsEnabled = request.UpdateNotificationsEnabled;
        entity.TelegramNotificationsEnabled = request.TelegramNotificationsEnabled;
        entity.ScheduleType = schedule.ScheduleType;
        entity.CheckEveryHours = schedule.CheckEveryHours;
        entity.CheckAtTime = schedule.CheckAtTime;
        entity.CheckOnDayOfWeek = schedule.CheckOnDayOfWeek;
        entity.TagPatternFilter = tagFilter;

        entity.RegistryUsernameEncrypted = ApplySecret(entity.RegistryUsernameEncrypted, request.RegistryUsername);
        entity.RegistryPasswordEncrypted = ApplySecret(entity.RegistryPasswordEncrypted, request.RegistryPassword);
        entity.GitHubPatEncrypted = ApplySecret(entity.GitHubPatEncrypted, request.GitHubPat);

        // V2.4 — apply the resolved auth type + AWS fields. When the strategy
        // is NOT AwsEcr we deliberately clear the AWS columns so a watch that
        // was previously ECR doesn't leak stale credentials after the user
        // switches to a different registry.
        entity.RegistryAuthType = auth;
        if (auth == RegistryAuthType.AwsEcr)
        {
            entity.AwsAccessKeyIdEncrypted = ApplySecret(entity.AwsAccessKeyIdEncrypted, request.AwsAccessKeyId);
            entity.AwsSecretAccessKeyEncrypted = ApplySecret(entity.AwsSecretAccessKeyEncrypted, request.AwsSecretAccessKey);
            entity.AwsRegion = NormalizeAwsRegion(request.AwsRegion, entity.RegistryHost);
        }
        else
        {
            entity.AwsAccessKeyIdEncrypted = null;
            entity.AwsSecretAccessKeyEncrypted = null;
            entity.AwsRegion = null;
        }

        entity.UpdatedUtc = DateTime.UtcNow;
    }

    public DockerWatchProfile BuildProfileFromEntity(DockerWatchEntity watch, DockerConnectionEntity connection)
    {
        var tls = BuildTlsFromEncrypted(connection.TlsCaCertEncrypted, connection.TlsClientCertEncrypted, connection.TlsClientKeyEncrypted);
        var creds = BuildCredentialsFromEncrypted(watch.RegistryUsernameEncrypted, watch.RegistryPasswordEncrypted);
        var pat = SafeDecrypt(watch.GitHubPatEncrypted);
        var ssh = BuildSshFromEntity(connection);

        return new DockerWatchProfile(
            watch.ImageReference,
            watch.RegistryHost,
            watch.Repository,
            watch.Tag,
            connection.HostType,
            connection.HostUrl,
            watch.ContainerName,
            tls,
            creds,
            watch.TagPatternFilter,
            pat,
            watch.RegistryAuthType,
            SafeDecrypt(watch.AwsAccessKeyIdEncrypted),
            SafeDecrypt(watch.AwsSecretAccessKeyEncrypted),
            watch.AwsRegion,
            ssh);
    }

    public DockerUpdateProfile BuildUpdateProfile(DockerWatchEntity watch, DockerConnectionEntity connection)
    {
        var tls = BuildTlsFromEncrypted(connection.TlsCaCertEncrypted, connection.TlsClientCertEncrypted, connection.TlsClientKeyEncrypted);
        var creds = BuildCredentialsFromEncrypted(watch.RegistryUsernameEncrypted, watch.RegistryPasswordEncrypted);
        var ssh = BuildSshFromEntity(connection);
        var transport = new DockerHostTransport(connection.HostType, connection.HostUrl, tls, ssh);
        return new DockerUpdateProfile(
            watch.ImageReference,
            watch.RegistryHost,
            watch.Repository,
            watch.Tag,
            watch.ContainerName,
            transport,
            creds,
            watch.RegistryAuthType,
            SafeDecrypt(watch.AwsAccessKeyIdEncrypted),
            SafeDecrypt(watch.AwsSecretAccessKeyEncrypted),
            watch.AwsRegion,
            // V5.2 — only honoured for local-socket hosts; the updater enforces
            // that, but passing it unconditionally keeps the mapper simple.
            connection.ComposeProjectPath);
    }

    public DockerUpdateAttemptResponse ToResponse(DockerUpdateAttemptEntity entity) =>
        new(
            entity.Id,
            entity.DockerWatchId,
            entity.WebResourceId,
            entity.DockerConnectionId,
            entity.ContainerId,
            entity.ActionType,
            entity.Status,
            entity.ImageReference,
            entity.ContainerName,
            entity.PreviousDigest,
            entity.NewDigest,
            entity.Error,
            entity.CompletedUtc,
            entity.CreatedUtc,
            entity.HealthVerified,
            entity.HealthVerifiedUtc);

    public DockerWatchProfile BuildProfileFromTestRequest(
        DockerWatchTestRequest request,
        DockerConnectionEntity connection,
        DockerWatchEntity? existing)
    {
        var parsed = imageReferenceParser.Parse(request.ImageReference);
        var tagFilter = NormalizeTagPatternFilter(request.TagPatternFilter);

        var usernamePlain = ResolveSecret(existing?.RegistryUsernameEncrypted, request.RegistryUsername);
        var passwordPlain = ResolveSecret(existing?.RegistryPasswordEncrypted, request.RegistryPassword);
        var patPlain = ResolveSecret(existing?.GitHubPatEncrypted, request.GitHubPat);
        var awsKeyPlain = ResolveSecret(existing?.AwsAccessKeyIdEncrypted, request.AwsAccessKeyId);
        var awsSecretPlain = ResolveSecret(existing?.AwsSecretAccessKeyEncrypted, request.AwsSecretAccessKey);
        var awsRegion = !string.IsNullOrWhiteSpace(request.AwsRegion)
            ? request.AwsRegion.Trim()
            : existing?.AwsRegion;

        var tls = BuildTlsFromEncrypted(connection.TlsCaCertEncrypted, connection.TlsClientCertEncrypted, connection.TlsClientKeyEncrypted);

        RegistryCredentials? creds = !string.IsNullOrEmpty(usernamePlain) && !string.IsNullOrEmpty(passwordPlain)
            ? new RegistryCredentials(usernamePlain, passwordPlain)
            : null;

        // V2.4 — same auto-promotion as ApplyUpsert: when the user leaves
        // RegistryAuthType at Auto and the hostname looks like ECR, switch
        // to AwsEcr so the test exercises the real auth path. Region is
        // pulled from the hostname when the user didn't supply one.
        var authType = request.RegistryAuthType;
        if (authType == RegistryAuthType.Auto && ImageReferenceParser.LooksLikeEcrHost(parsed.RegistryHost))
            authType = RegistryAuthType.AwsEcr;
        if (authType == RegistryAuthType.AwsEcr && string.IsNullOrEmpty(awsRegion))
            awsRegion = ImageReferenceParser.TryExtractEcrRegion(parsed.RegistryHost);

        return new DockerWatchProfile(
            request.ImageReference,
            parsed.RegistryHost,
            parsed.Repository,
            parsed.Tag,
            connection.HostType,
            connection.HostUrl,
            request.ContainerName,
            tls,
            creds,
            tagFilter,
            patPlain,
            authType,
            awsKeyPlain,
            awsSecretPlain,
            awsRegion,
            BuildSshFromEntity(connection));
    }

    // ── private helpers ──────────────────────────────────────────────────────

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

    /// <summary>
    /// V2.5 — decrypts SSH credentials off the connection entity for the
    /// orchestrator. Returns <c>null</c> when the host type isn't Ssh or any
    /// required field is missing — the host client interprets that as
    /// <c>HostUnreachable</c> rather than silently treating it as a
    /// LocalSocket call.
    /// </summary>
    private DockerSshCredentials? BuildSshFromEntity(DockerConnectionEntity connection)
    {
        if (connection.HostType != DockerHostType.Ssh) return null;
        var privateKey = SafeDecrypt(connection.SshPrivateKeyEncrypted);
        if (string.IsNullOrEmpty(connection.SshHost)
            || string.IsNullOrEmpty(connection.SshUsername)
            || string.IsNullOrEmpty(privateKey))
            return null;
        return new DockerSshCredentials(
            connection.SshHost,
            connection.SshPort ?? 22,
            connection.SshUsername,
            privateKey,
            SafeDecrypt(connection.SshPrivateKeyPassphraseEncrypted),
            string.IsNullOrWhiteSpace(connection.SshRemoteSocketPath)
                ? DockerConnectionMapper.DefaultRemoteSocketPath
                : connection.SshRemoteSocketPath);
    }

    private DockerTlsMaterial? BuildTlsFromEncrypted(string? caEnc, string? clientCertEnc, string? clientKeyEnc)
    {
        if (string.IsNullOrEmpty(caEnc) && string.IsNullOrEmpty(clientCertEnc) && string.IsNullOrEmpty(clientKeyEnc))
            return null;
        return new DockerTlsMaterial(
            SafeDecrypt(caEnc) ?? string.Empty,
            SafeDecrypt(clientCertEnc) ?? string.Empty,
            SafeDecrypt(clientKeyEnc) ?? string.Empty);
    }

    private RegistryCredentials? BuildCredentialsFromEncrypted(string? userEnc, string? passEnc)
    {
        if (string.IsNullOrEmpty(userEnc) || string.IsNullOrEmpty(passEnc)) return null;
        var user = SafeDecrypt(userEnc);
        var pass = SafeDecrypt(passEnc);
        if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(pass)) return null;
        return new RegistryCredentials(user, pass);
    }

    private string? SafeDecrypt(string? encrypted)
    {
        if (string.IsNullOrEmpty(encrypted)) return null;
        try { return encryption.Decrypt(encrypted); }
        catch { return null; }
    }

    private static bool HasAnyRegistryCreds(DockerWatchEntity entity) =>
        !string.IsNullOrEmpty(entity.RegistryUsernameEncrypted)
        && !string.IsNullOrEmpty(entity.RegistryPasswordEncrypted);

    private static bool HasAwsCreds(DockerWatchEntity entity) =>
        !string.IsNullOrEmpty(entity.AwsAccessKeyIdEncrypted)
        && !string.IsNullOrEmpty(entity.AwsSecretAccessKeyEncrypted);

    /// <summary>
    /// V2.4 — normalises the request's <see cref="RegistryAuthType"/>:
    /// auto-promotes <c>Auto</c> to <c>AwsEcr</c> when the parsed hostname
    /// matches the ECR pattern. The user can still explicitly pick another
    /// strategy to override.
    /// </summary>
    private static RegistryAuthType NormalizeRegistryAuth(DockerWatchUpsertRequest request, string registryHost)
    {
        if (request.RegistryAuthType != RegistryAuthType.Auto)
            return request.RegistryAuthType;
        return ImageReferenceParser.LooksLikeEcrHost(registryHost)
            ? RegistryAuthType.AwsEcr
            : RegistryAuthType.Auto;
    }

    /// <summary>
    /// V2.4 — prefers the user-supplied region; falls back to the region
    /// embedded in the ECR hostname so a watch built from `123…dkr.ecr.eu-central-1.amazonaws.com/foo`
    /// doesn't require the user to type the region twice.
    /// </summary>
    private static string? NormalizeAwsRegion(string? requested, string registryHost)
    {
        if (!string.IsNullOrWhiteSpace(requested)) return requested.Trim();
        return ImageReferenceParser.TryExtractEcrRegion(registryHost);
    }

    /// <summary>
    /// Validates that the supplied filter compiles as a .NET regex and
    /// normalises blank input to <c>null</c> so the entity stays clean
    /// (a null filter = "use the pinned tag's digest directly", V1 behaviour).
    /// Throws <see cref="FormatException"/> on a malformed pattern, mirroring
    /// the existing image-reference validation contract.
    /// </summary>
    private static string? NormalizeTagPatternFilter(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var trimmed = raw.Trim();
        try { _ = new Regex(trimmed); }
        catch (RegexParseException ex)
        {
            throw new FormatException($"Tag pattern filter is not a valid regex: {ex.Message}");
        }
        return trimmed;
    }

    /// <summary>
    /// V2.2 — validates the schedule fields against the chosen
    /// <see cref="CheckScheduleType"/> and returns a normalised tuple that's safe
    /// to write straight onto the entity. Throws <see cref="FormatException"/>
    /// on invalid combinations (e.g. <c>Daily</c> with no <c>CheckAtTime</c>,
    /// or an Hourly value outside the allowed set).
    /// </summary>
    private static (CheckScheduleType ScheduleType, int CheckEveryHours, TimeOnly? CheckAtTime, DayOfWeek? CheckOnDayOfWeek)
        NormalizeSchedule(DockerWatchUpsertRequest request)
    {
        switch (request.ScheduleType)
        {
            case CheckScheduleType.Hourly:
                if (!CheckScheduleEvaluator.AllowedHourlyValues.Contains(request.CheckEveryHours))
                    throw new FormatException(
                        $"CheckEveryHours must be one of {string.Join(", ", CheckScheduleEvaluator.AllowedHourlyValues)} "
                        + $"for an Hourly schedule (got {request.CheckEveryHours}).");
                return (CheckScheduleType.Hourly, request.CheckEveryHours, null, null);

            case CheckScheduleType.Daily:
                if (request.CheckAtTime is null)
                    throw new FormatException("CheckAtTime is required for a Daily schedule.");
                return (CheckScheduleType.Daily, 24, request.CheckAtTime, null);

            case CheckScheduleType.Weekly:
                if (request.CheckAtTime is null)
                    throw new FormatException("CheckAtTime is required for a Weekly schedule.");
                if (request.CheckOnDayOfWeek is null)
                    throw new FormatException("CheckOnDayOfWeek is required for a Weekly schedule.");
                return (CheckScheduleType.Weekly, 24, request.CheckAtTime, request.CheckOnDayOfWeek);

            default:
                throw new FormatException($"Unknown schedule type '{request.ScheduleType}'.");
        }
    }
}
