using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Stashboard.Api.Data;
using Stashboard.Core.Abstractions;
using Stashboard.Core.Entities;
using Stashboard.Core.Enums;

namespace Stashboard.Api.Services;

/// <summary>
/// Exports / imports a single user's full configuration as portable JSON:
/// categories, tags, Docker connections, services (with credentials + tags +
/// the Docker connection link), Docker watches, and the user's own settings.
/// <para>
/// Encrypted-at-rest values (credential values, TLS/SSH material, registry/AWS/
/// GitHub secrets) are decrypted on export and re-encrypted on import, so a
/// backup is portable across instances that use different encryption keys.
/// Runtime status (digests, last-checked timestamps, update history) is
/// intentionally not exported — it is re-derived by the background checker.
/// </para>
/// <para>
/// <b>Maintenance contract:</b> whenever a persisted field or entity is added,
/// removed, or renamed, this service (export + import) and its round-trip test
/// must be updated in the same change. See BUSINESS_REQUIREMENTS.md §"Backup".
/// </para>
/// </summary>
public sealed class BackupService(ApplicationDbContext db, IEncryptionService encryption) : IBackupService
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private string? Dec(string? cipher) => string.IsNullOrEmpty(cipher) ? cipher : encryption.Decrypt(cipher);
    private string? Enc(string? plain) => string.IsNullOrEmpty(plain) ? plain : encryption.Encrypt(plain);

    public async Task<byte[]> ExportAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        var categories = await db.Categories.AsNoTracking().Where(c => c.UserId == userId).ToListAsync(cancellationToken);
        var tags = await db.Tags.AsNoTracking().Where(t => t.UserId == userId).ToListAsync(cancellationToken);
        var connections = await db.DockerConnections.AsNoTracking().Where(c => c.UserId == userId).ToListAsync(cancellationToken);
        var services = await db.WebResources.AsNoTracking()
            .Include(s => s.Credentials)
            .Include(s => s.WebResourceTags)
            .Where(s => s.UserId == userId)
            .ToListAsync(cancellationToken);
        var watches = await db.DockerWatches.AsNoTracking().Where(w => w.UserId == userId).ToListAsync(cancellationToken);

        var dto = new BackupDto(
            ExportedUtc: DateTime.UtcNow,
            User: user is null ? null : new UserSettingsDto(
                user.DisplayName, user.Theme, user.DashboardSortMode, user.DashboardGroupByCategory,
                Dec(user.TelegramBotTokenEncrypted), user.TelegramChatId, user.TelegramNotificationsEnabled),
            Categories: categories.Select(c => new CategoryDto(c.Id, c.Name, c.Color)).ToList(),
            Tags: tags.Select(t => new TagDto(t.Id, t.Name)).ToList(),
            DockerConnections: connections.Select(c => new DockerConnectionDto(
                c.Id, c.Name, c.HostType, c.HostUrl,
                Dec(c.TlsCaCertEncrypted), Dec(c.TlsClientCertEncrypted), Dec(c.TlsClientKeyEncrypted),
                c.SshHost, c.SshPort, c.SshUsername,
                Dec(c.SshPrivateKeyEncrypted), Dec(c.SshPrivateKeyPassphraseEncrypted), c.SshRemoteSocketPath,
                c.ComposeProjectPath, c.AllowImagePrune, c.PruneUnusedImages)).ToList(),
            Services: services.Select(s => new ServiceDto(
                s.Id, s.Name, s.MainUrl, s.MainUrlHealthCheckEnabled, s.AdditionalUrl, s.AdditionalUrlHealthCheckEnabled,
                s.OfflineNotificationsEnabled, s.HealthCheckUrl, s.HealthCheckMethod, s.ExpectedStatusRange,
                s.Notes, s.CategoryId, s.LogoSource, s.CustomLogoPath, s.LogoBase64, s.DockerConnectionId,
                s.Credentials.Select(c => new CredentialDto(c.Key, Dec(c.EncryptedValue)!, c.IsSecret)).ToList(),
                s.WebResourceTags.Select(st => st.TagId).ToList())).ToList(),
            DockerWatches: watches.Select(w => new DockerWatchDto(
                w.Id, w.DockerConnectionId, w.WebResourceId, w.Label, w.Enabled, w.ImageReference,
                w.RegistryHost, w.Repository, w.Tag, w.ContainerName,
                Dec(w.RegistryUsernameEncrypted), Dec(w.RegistryPasswordEncrypted), Dec(w.GitHubPatEncrypted),
                w.RegistryAuthType, Dec(w.AwsAccessKeyIdEncrypted), Dec(w.AwsSecretAccessKeyEncrypted), w.AwsRegion,
                w.UpdateNotificationsEnabled, w.TelegramNotificationsEnabled, w.ScheduleType, w.CheckEveryHours,
                w.CheckAtTime, w.CheckOnDayOfWeek, w.TagPatternFilter, w.WebhookToken)).ToList());

        return JsonSerializer.SerializeToUtf8Bytes(dto, JsonOpts);
    }

    public async Task<int> ImportAsync(Guid userId, Stream jsonStream, CancellationToken cancellationToken = default)
    {
        var dto = await JsonSerializer.DeserializeAsync<BackupDto>(jsonStream, JsonOpts, cancellationToken)
            ?? throw new InvalidOperationException("Backup file is empty or invalid.");

        var idMap = new Dictionary<Guid, Guid>();

        // ── User settings (applied to the importing user) ──
        if (dto.User is { } settings)
        {
            var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
            if (user is not null)
            {
                user.DisplayName = settings.DisplayName;
                user.Theme = settings.Theme;
                user.DashboardSortMode = settings.DashboardSortMode;
                user.DashboardGroupByCategory = settings.DashboardGroupByCategory;
                user.TelegramBotTokenEncrypted = Enc(settings.TelegramBotToken);
                user.TelegramChatId = settings.TelegramChatId;
                user.TelegramNotificationsEnabled = settings.TelegramNotificationsEnabled;
            }
        }

        // ── Categories / Tags (merge by name) ──
        foreach (var c in dto.Categories ?? [])
        {
            var existing = await db.Categories.FirstOrDefaultAsync(x => x.UserId == userId && x.Name == c.Name, cancellationToken);
            if (existing is null)
            {
                var fresh = new CategoryEntity { UserId = userId, Name = c.Name, Color = c.Color };
                db.Categories.Add(fresh);
                idMap[c.Id] = fresh.Id;
            }
            else { idMap[c.Id] = existing.Id; }
        }

        foreach (var t in dto.Tags ?? [])
        {
            var existing = await db.Tags.FirstOrDefaultAsync(x => x.UserId == userId && x.Name == t.Name, cancellationToken);
            if (existing is null)
            {
                var fresh = new TagEntity { UserId = userId, Name = t.Name };
                db.Tags.Add(fresh);
                idMap[t.Id] = fresh.Id;
            }
            else { idMap[t.Id] = existing.Id; }
        }

        // ── Docker connections (merge by name) ──
        foreach (var dc in dto.DockerConnections ?? [])
        {
            var existing = await db.DockerConnections.FirstOrDefaultAsync(x => x.UserId == userId && x.Name == dc.Name, cancellationToken);
            if (existing is null)
            {
                var fresh = new DockerConnectionEntity
                {
                    UserId = userId,
                    Name = dc.Name,
                    HostType = dc.HostType,
                    HostUrl = dc.HostUrl,
                    TlsCaCertEncrypted = Enc(dc.TlsCaCert),
                    TlsClientCertEncrypted = Enc(dc.TlsClientCert),
                    TlsClientKeyEncrypted = Enc(dc.TlsClientKey),
                    SshHost = dc.SshHost,
                    SshPort = dc.SshPort,
                    SshUsername = dc.SshUsername,
                    SshPrivateKeyEncrypted = Enc(dc.SshPrivateKey),
                    SshPrivateKeyPassphraseEncrypted = Enc(dc.SshPrivateKeyPassphrase),
                    SshRemoteSocketPath = dc.SshRemoteSocketPath,
                    ComposeProjectPath = dc.ComposeProjectPath,
                    AllowImagePrune = dc.AllowImagePrune,
                    PruneUnusedImages = dc.PruneUnusedImages,
                };
                db.DockerConnections.Add(fresh);
                idMap[dc.Id] = fresh.Id;
            }
            else { idMap[dc.Id] = existing.Id; }
        }

        await db.SaveChangesAsync(cancellationToken);

        // ── Services (always created fresh) ──
        var imported = 0;
        foreach (var s in dto.Services ?? [])
        {
            var svc = new WebResourceEntity
            {
                UserId = userId,
                Name = s.Name,
                MainUrl = s.MainUrl,
                MainUrlHealthCheckEnabled = s.MainUrlHealthCheckEnabled,
                AdditionalUrl = s.AdditionalUrl,
                AdditionalUrlHealthCheckEnabled = s.AdditionalUrlHealthCheckEnabled,
                OfflineNotificationsEnabled = s.OfflineNotificationsEnabled,
                HealthCheckUrl = s.HealthCheckUrl,
                HealthCheckMethod = s.HealthCheckMethod,
                ExpectedStatusRange = s.ExpectedStatusRange,
                Notes = s.Notes,
                CategoryId = MapOrNull(idMap, s.CategoryId),
                LogoSource = s.LogoSource,
                CustomLogoPath = s.CustomLogoPath,
                LogoBase64 = s.LogoBase64,
                DockerConnectionId = MapOrNull(idMap, s.DockerConnectionId),
            };
            foreach (var c in s.Credentials ?? [])
            {
                svc.Credentials.Add(new CredentialEntity
                {
                    Key = c.Key,
                    EncryptedValue = Enc(c.Value)!,
                    IsSecret = c.IsSecret,
                });
            }
            foreach (var tagId in s.TagIds ?? [])
            {
                if (idMap.TryGetValue(tagId, out var mapped))
                    svc.WebResourceTags.Add(new WebResourceTagEntity { TagId = mapped });
            }
            db.WebResources.Add(svc);
            idMap[s.Id] = svc.Id;
            imported++;
        }

        await db.SaveChangesAsync(cancellationToken);

        // ── Docker watches (skip duplicates on the same connection) ──
        foreach (var w in dto.DockerWatches ?? [])
        {
            if (!idMap.TryGetValue(w.DockerConnectionId, out var connectionId))
                continue; // orphaned watch — its connection wasn't in the backup

            var alreadyTracked = await db.DockerWatches
                .AnyAsync(x => x.DockerConnectionId == connectionId && x.ContainerName == w.ContainerName, cancellationToken);
            if (alreadyTracked) continue;

            // Webhook tokens are globally unique. On a same-instance re-import the
            // original watch may still hold this token — drop it rather than fail
            // the import; the user can re-issue a webhook URL from the UI.
            var webhookToken = w.WebhookToken;
            if (webhookToken is not null
                && await db.DockerWatches.AnyAsync(x => x.WebhookToken == webhookToken, cancellationToken))
            {
                webhookToken = null;
            }

            db.DockerWatches.Add(new DockerWatchEntity
            {
                DockerConnectionId = connectionId,
                WebResourceId = MapOrNull(idMap, w.WebResourceId),
                UserId = userId,
                Label = w.Label,
                Enabled = w.Enabled,
                ImageReference = w.ImageReference,
                RegistryHost = w.RegistryHost,
                Repository = w.Repository,
                Tag = w.Tag,
                ContainerName = w.ContainerName,
                RegistryUsernameEncrypted = Enc(w.RegistryUsername),
                RegistryPasswordEncrypted = Enc(w.RegistryPassword),
                GitHubPatEncrypted = Enc(w.GitHubPat),
                RegistryAuthType = w.RegistryAuthType,
                AwsAccessKeyIdEncrypted = Enc(w.AwsAccessKeyId),
                AwsSecretAccessKeyEncrypted = Enc(w.AwsSecretAccessKey),
                AwsRegion = w.AwsRegion,
                UpdateNotificationsEnabled = w.UpdateNotificationsEnabled,
                TelegramNotificationsEnabled = w.TelegramNotificationsEnabled,
                ScheduleType = w.ScheduleType,
                CheckEveryHours = w.CheckEveryHours,
                CheckAtTime = w.CheckAtTime,
                CheckOnDayOfWeek = w.CheckOnDayOfWeek,
                TagPatternFilter = w.TagPatternFilter,
                WebhookToken = webhookToken,
            });
        }

        await db.SaveChangesAsync(cancellationToken);
        return imported;
    }

    private static Guid? MapOrNull(IReadOnlyDictionary<Guid, Guid> idMap, Guid? sourceId) =>
        sourceId.HasValue && idMap.TryGetValue(sourceId.Value, out var mapped) ? mapped : null;

    private sealed record BackupDto(
        DateTime ExportedUtc,
        UserSettingsDto? User,
        List<CategoryDto>? Categories,
        List<TagDto>? Tags,
        List<DockerConnectionDto>? DockerConnections,
        List<ServiceDto>? Services,
        List<DockerWatchDto>? DockerWatches);

    private sealed record UserSettingsDto(
        string? DisplayName, string Theme, string DashboardSortMode, bool DashboardGroupByCategory,
        string? TelegramBotToken, string? TelegramChatId, bool TelegramNotificationsEnabled);

    private sealed record CategoryDto(Guid Id, string Name, string Color);
    private sealed record TagDto(Guid Id, string Name);

    private sealed record DockerConnectionDto(
        Guid Id, string Name, DockerHostType HostType, string? HostUrl,
        string? TlsCaCert, string? TlsClientCert, string? TlsClientKey,
        string? SshHost, int? SshPort, string? SshUsername,
        string? SshPrivateKey, string? SshPrivateKeyPassphrase, string? SshRemoteSocketPath,
        string? ComposeProjectPath = null,
        bool AllowImagePrune = true,
        bool PruneUnusedImages = false);

    private sealed record ServiceDto(
        Guid Id, string Name, string MainUrl, bool MainUrlHealthCheckEnabled, string? AdditionalUrl,
        bool AdditionalUrlHealthCheckEnabled, bool OfflineNotificationsEnabled, string? HealthCheckUrl,
        HealthCheckMethod HealthCheckMethod, string? ExpectedStatusRange, string? Notes, Guid? CategoryId,
        LogoSource LogoSource, string? CustomLogoPath, string? LogoBase64, Guid? DockerConnectionId,
        List<CredentialDto> Credentials, List<Guid> TagIds);

    private sealed record CredentialDto(string Key, string Value, bool IsSecret);

    private sealed record DockerWatchDto(
        Guid Id, Guid DockerConnectionId, Guid? WebResourceId, string Label, bool Enabled, string ImageReference,
        string RegistryHost, string Repository, string Tag, string ContainerName,
        string? RegistryUsername, string? RegistryPassword, string? GitHubPat, RegistryAuthType RegistryAuthType,
        string? AwsAccessKeyId, string? AwsSecretAccessKey, string? AwsRegion,
        bool UpdateNotificationsEnabled, bool TelegramNotificationsEnabled, CheckScheduleType ScheduleType,
        int CheckEveryHours, TimeOnly? CheckAtTime, DayOfWeek? CheckOnDayOfWeek, string? TagPatternFilter, string? WebhookToken);
}
