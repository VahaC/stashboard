using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Stashboard.Api.Data;
using Stashboard.Api.Services.StatusPages;
using Stashboard.Core.Abstractions;
using Stashboard.Core.Entities;
using Stashboard.Core.Enums;

namespace Stashboard.Api.Services;

/// <summary>
/// Exports / imports a single user's full configuration as portable JSON:
/// categories, tags, Docker connections, services (with credentials + tags +
/// the Docker connection link), Docker watches, Proxmox connections (with their
/// per-guest monitoring intent), the V7.9 service↔guest and container↔guest
/// cross-links (keyed by the guest's stable (connection, vmid)), and the user's
/// own settings.
/// <para>
/// Encrypted-at-rest values (credential values, TLS/SSH material, registry/AWS/
/// GitHub secrets, Proxmox API token + SSH key) are decrypted on export and
/// re-encrypted on import, so a backup is portable across instances that use
/// different encryption keys.
/// Runtime status (digests, last-checked timestamps, update history) is
/// intentionally not exported — it is re-derived by the background checker.
/// Bearer secrets that only make sense on one instance — refresh tokens and
/// V10.4 personal access tokens — are likewise <b>not</b> exported (an export
/// would either leak them or be useless); after a restore the user mints fresh
/// tokens.
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

    // Legacy tolerance: rows written before a value was encrypted at rest (e.g.
    // a Telegram bot token that predates the EncryptTelegramBotToken column
    // rename) hold raw plaintext in an *Encrypted column. Export the stored
    // value as-is instead of failing the whole backup — same convention as
    // UserService.MaterializeTelegramToken; import re-encrypts it properly.
    private string? Dec(string? cipher)
    {
        if (string.IsNullOrEmpty(cipher)) return cipher;
        try { return encryption.Decrypt(cipher); }
        catch (CryptographicException) { return cipher; }
        catch (FormatException) { return cipher; }
    }

    private string? Enc(string? plain) => string.IsNullOrEmpty(plain) ? plain : encryption.Encrypt(plain);

    public async Task<byte[]> ExportAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        // V10.3 — the user's 2FA recovery codes (hashes only). The enabled flag, encrypted
        // secret and replay step travel on the user DTO; the codes are a separate collection.
        var recoveryCodes = await db.TwoFactorRecoveryCodes.AsNoTracking()
            .Where(c => c.UserId == userId)
            .ToListAsync(cancellationToken);
        // V9.0 — app-wide MQTT / Home Assistant integration config (singleton, password
        // encrypted at rest). Included so a restore brings the integration back.
        var mqtt = await db.MqttSettings.AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == MqttSettingsEntity.SingletonId, cancellationToken);
        // V10.0 — app-wide Apprise notification config (singleton, URLs encrypted at
        // rest). Included so a restore brings the notification channel back.
        var apprise = await db.AppriseSettings.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == AppriseSettingsEntity.SingletonId, cancellationToken);
        var categories = await db.Categories.AsNoTracking().Where(c => c.UserId == userId).ToListAsync(cancellationToken);
        var tags = await db.Tags.AsNoTracking().Where(t => t.UserId == userId).ToListAsync(cancellationToken);
        var connections = await db.DockerConnections.AsNoTracking().Where(c => c.UserId == userId).ToListAsync(cancellationToken);
        var services = await db.WebResources.AsNoTracking()
            .Include(s => s.Credentials)
            .Include(s => s.WebResourceTags)
            .Where(s => s.UserId == userId)
            .ToListAsync(cancellationToken);
        var watches = await db.DockerWatches.AsNoTracking().Where(w => w.UserId == userId).ToListAsync(cancellationToken);
        var proxmoxConnections = await db.ProxmoxConnections.AsNoTracking().Where(c => c.UserId == userId).ToListAsync(cancellationToken);
        var proxmoxConnectionIds = proxmoxConnections.Select(c => c.Id).ToList();
        // Only the guest rows carrying user intent (monitoring turned off, or
        // snoozed) are worth exporting; everything else on a guest is scan output
        // that repopulates on the next scan. Keyed by VmId so it re-attaches.
        var proxmoxGuests = await db.ProxmoxGuests.AsNoTracking()
            .Where(g => proxmoxConnectionIds.Contains(g.ProxmoxConnectionId)
                && (!g.MonitoringEnabled || g.MonitoringSnoozedUntil != null))
            .ToListAsync(cancellationToken);
        var guestsByConnection = proxmoxGuests
            .GroupBy(g => g.ProxmoxConnectionId)
            .ToDictionary(grp => grp.Key, grp => grp.ToList());

        // V7.8 — custom card-icon uploads (base64). Only Custom rows carry content;
        // Auto rows are re-resolved live on the target, so they're not exported.
        // Nested under their connection so import re-attaches by name / vmid.
        var containerIcons = await db.ContainerIcons.AsNoTracking()
            .Where(i => i.UserId == userId && i.IconSource == ContainerIconSource.Custom && i.LogoBase64 != null)
            .ToListAsync(cancellationToken);
        var containerIconsByConnection = containerIcons
            .GroupBy(i => i.DockerConnectionId)
            .ToDictionary(grp => grp.Key, grp => grp.ToList());
        var guestIcons = await db.ProxmoxGuestIcons.AsNoTracking()
            .Where(i => i.UserId == userId && i.IconSource == ContainerIconSource.Custom && i.LogoBase64 != null)
            .ToListAsync(cancellationToken);
        var guestIconsByConnection = guestIcons
            .GroupBy(i => i.ProxmoxConnectionId)
            .ToDictionary(grp => grp.Key, grp => grp.ToList());

        // V7.9 — service↔guest and container↔guest cross-links. Both reference the
        // target guest by its stable (connection, vmid) key so they re-attach after
        // the guest rows are re-discovered by a scan on the target instance.
        var serviceProxmoxLinks = await db.WebResourceProxmoxGuestLinks.AsNoTracking()
            .Where(l => db.WebResources.Any(s => s.Id == l.WebResourceId && s.UserId == userId))
            .ToListAsync(cancellationToken);
        var containerProxmoxLinks = await db.ContainerProxmoxLinks.AsNoTracking()
            .Where(l => l.UserId == userId)
            .ToListAsync(cancellationToken);

        // V10.2 — public status pages + their service selections and publish state. The items
        // reference services by id (remapped via idMap on import); the slug is regenerated on
        // import if it collides (it is globally unique).
        var statusPages = await db.StatusPages.AsNoTracking()
            .Include(p => p.Items)
            .Where(p => p.UserId == userId)
            .ToListAsync(cancellationToken);

        var dto = new BackupDto(
            ExportedUtc: DateTime.UtcNow,
            User: user is null ? null : new UserSettingsDto(
                user.DisplayName, user.Theme, user.DashboardSortMode, user.DashboardGroupByCategory,
                Dec(user.TelegramBotTokenEncrypted), user.TelegramChatId, user.TelegramNotificationsEnabled,
                // V10.3 — secret decrypted on export, re-encrypted on import (portable across instances),
                // like every other secret here. Recovery codes are already hashes, so they travel as-is.
                user.TwoFactorEnabled, Dec(user.TwoFactorSecretEncrypted), user.TwoFactorLastUsedStep,
                recoveryCodes.Select(c => new RecoveryCodeDto(c.CodeHash, c.UsedUtc)).ToList()),
            Categories: categories.Select(c => new CategoryDto(c.Id, c.Name, c.Color)).ToList(),
            Tags: tags.Select(t => new TagDto(t.Id, t.Name)).ToList(),
            DockerConnections: connections.Select(c => new DockerConnectionDto(
                c.Id, c.Name, c.HostType, c.HostUrl,
                Dec(c.TlsCaCertEncrypted), Dec(c.TlsClientCertEncrypted), Dec(c.TlsClientKeyEncrypted),
                c.SshHost, c.SshPort, c.SshUsername,
                Dec(c.SshPrivateKeyEncrypted), Dec(c.SshPrivateKeyPassphraseEncrypted), c.SshRemoteSocketPath,
                c.ComposePathHostPrefix, c.ComposePathContainerPrefix,
                c.AllowImagePrune, c.PruneUnusedImages,
                (containerIconsByConnection.TryGetValue(c.Id, out var ci) ? ci : [])
                    .Select(i => new ContainerIconDto(i.ContainerName, i.LogoBase64!)).ToList())).ToList(),
            Services: services.Select(s => new ServiceDto(
                s.Id, s.Name, s.MainUrl, s.MainUrlHealthCheckEnabled, s.AdditionalUrl, s.AdditionalUrlHealthCheckEnabled,
                s.OfflineNotificationsEnabled, s.HealthCheckUrl, s.HealthCheckMethod, s.ExpectedStatusRange,
                s.Notes, s.CategoryId, s.LogoSource, s.CustomLogoPath, s.LogoBase64, s.DockerConnectionId,
                s.Credentials.Select(c => new CredentialDto(c.Key, Dec(c.EncryptedValue)!, c.IsSecret)).ToList(),
                s.WebResourceTags.Select(st => st.TagId).ToList(), s.ProxmoxConnectionId)).ToList(),
            DockerWatches: watches.Select(w => new DockerWatchDto(
                w.Id, w.DockerConnectionId, w.WebResourceId, w.Label, w.Enabled, w.ImageReference,
                w.RegistryHost, w.Repository, w.Tag, w.ContainerName,
                Dec(w.RegistryUsernameEncrypted), Dec(w.RegistryPasswordEncrypted), Dec(w.GitHubPatEncrypted),
                w.RegistryAuthType, Dec(w.AwsAccessKeyIdEncrypted), Dec(w.AwsSecretAccessKeyEncrypted), w.AwsRegion,
                w.UpdateNotificationsEnabled, w.TelegramNotificationsEnabled, w.ScheduleType, w.CheckEveryHours,
                w.CheckAtTime, w.CheckOnDayOfWeek, w.TagPatternFilter, w.WebhookToken,
                w.AppriseNotificationsEnabled)).ToList(),
            ProxmoxConnections: proxmoxConnections.Select(c => new ProxmoxConnectionDto(
                c.Id, c.Name, c.ApiBaseUrl, c.NodeName, c.ServerType, c.ApiTokenId,
                Dec(c.ApiTokenSecretEncrypted), c.SkipTlsVerify,
                c.SshHost, c.SshPort, c.SshUsername,
                Dec(c.SshPrivateKeyEncrypted), Dec(c.SshPrivateKeyPassphraseEncrypted),
                c.AllowConsole, c.AllowUpdates, c.AllowDestroy, c.AllowCreate, c.Enabled,
                c.TelemetryPollSeconds, c.UpdateNotificationsEnabled, c.TelegramNotificationsEnabled,
                c.ScheduleType, c.CheckEveryHours, c.CheckAtTime, c.CheckOnDayOfWeek, c.WebhookToken,
                (guestsByConnection.TryGetValue(c.Id, out var gs) ? gs : [])
                    .Select(g => new ProxmoxGuestDto(
                        g.VmId, g.GuestType, g.Name, g.MonitoringEnabled, g.MonitoringSnoozedUntil))
                    .ToList(),
                (guestIconsByConnection.TryGetValue(c.Id, out var gi) ? gi : [])
                    .Select(i => new GuestIconDto(i.VmId, i.LogoBase64!)).ToList(),
                c.AppriseNotificationsEnabled)).ToList(),
            ServiceProxmoxLinks: serviceProxmoxLinks
                .Select(l => new WebResourceProxmoxGuestLinkDto(l.WebResourceId, l.ProxmoxConnectionId, l.VmId)).ToList(),
            ContainerProxmoxLinks: containerProxmoxLinks
                .Select(l => new ContainerProxmoxLinkDto(l.DockerConnectionId, l.ContainerName, l.ProxmoxConnectionId, l.VmId)).ToList(),
            Mqtt: mqtt is null ? null : new MqttDto(
                mqtt.Enabled, mqtt.Host, mqtt.Port, mqtt.UseTls, mqtt.AllowUntrustedTls,
                mqtt.Username, Dec(mqtt.PasswordEncrypted), mqtt.ClientId, mqtt.DiscoveryPrefix, mqtt.EntityPrefix,
                mqtt.DeviceName, mqtt.Manufacturer),
            Apprise: apprise is null ? null : new AppriseDto(
                apprise.Enabled, apprise.BaseUrl, Dec(apprise.UrlsEncrypted)),
            StatusPages: statusPages.Select(p => new StatusPageDto(
                p.Title, p.Description, p.Slug, p.IsPublished,
                p.Items
                    .OrderBy(i => i.SortOrder)
                    .Select(i => new StatusPageItemDto(i.WebResourceId, i.DisplayName, i.SortOrder))
                    .ToList())).ToList());

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

                // V10.3 — restore 2FA: re-encrypt the secret under this instance's key so the same
                // authenticator app keeps working, and carry the replay step so the reuse window
                // doesn't re-open on restore. Recovery codes are replaced wholesale (below).
                user.TwoFactorSecretEncrypted = Enc(settings.TwoFactorSecret);
                user.TwoFactorEnabled = settings.TwoFactorEnabled && user.TwoFactorSecretEncrypted is not null;
                user.TwoFactorLastUsedStep = settings.TwoFactorLastUsedStep;

                await db.TwoFactorRecoveryCodes.Where(c => c.UserId == userId).ExecuteDeleteAsync(cancellationToken);
                if (settings.RecoveryCodes is { Count: > 0 } codes)
                {
                    foreach (var c in codes)
                        db.TwoFactorRecoveryCodes.Add(new TwoFactorRecoveryCodeEntity
                        {
                            UserId = userId,
                            CodeHash = c.CodeHash,
                            UsedUtc = c.UsedUtc,
                        });
                }
            }
        }

        // ── V9.0 — app-wide MQTT integration config (singleton upsert) ──
        // Re-encrypted on import so it's portable across instances with different
        // encryption keys, mirroring every other secret in this service.
        if (dto.Mqtt is { } m)
        {
            var mqtt = await db.MqttSettings.FirstOrDefaultAsync(x => x.Id == MqttSettingsEntity.SingletonId, cancellationToken);
            if (mqtt is null)
            {
                mqtt = new MqttSettingsEntity { Id = MqttSettingsEntity.SingletonId };
                db.MqttSettings.Add(mqtt);
            }
            mqtt.Enabled = m.Enabled;
            mqtt.Host = m.Host;
            mqtt.Port = m.Port;
            mqtt.UseTls = m.UseTls;
            mqtt.AllowUntrustedTls = m.AllowUntrustedTls;
            mqtt.Username = m.Username;
            mqtt.PasswordEncrypted = Enc(m.Password);
            mqtt.ClientId = m.ClientId;
            mqtt.DiscoveryPrefix = m.DiscoveryPrefix;
            mqtt.EntityPrefix = m.EntityPrefix;
            // V9.1 fields — default for backups taken before they existed.
            mqtt.DeviceName = string.IsNullOrWhiteSpace(m.DeviceName) ? "Stashboard" : m.DeviceName;
            mqtt.Manufacturer = string.IsNullOrWhiteSpace(m.Manufacturer) ? "Stashboard" : m.Manufacturer;
            mqtt.UpdatedUtc = DateTime.UtcNow;
        }

        // ── V10.0 — app-wide Apprise notification config (singleton upsert) ──
        // The URLs are re-encrypted on import so they're portable across instances
        // with different encryption keys, like every other secret in this service.
        if (dto.Apprise is { } a)
        {
            var appriseRow = await db.AppriseSettings.FirstOrDefaultAsync(
                x => x.Id == AppriseSettingsEntity.SingletonId, cancellationToken);
            if (appriseRow is null)
            {
                appriseRow = new AppriseSettingsEntity { Id = AppriseSettingsEntity.SingletonId };
                db.AppriseSettings.Add(appriseRow);
            }
            appriseRow.Enabled = a.Enabled;
            appriseRow.BaseUrl = a.BaseUrl;
            appriseRow.UrlsEncrypted = Enc(a.Urls);
            appriseRow.UpdatedUtc = DateTime.UtcNow;
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
                    // V7.1 — project paths are discovered from labels now; only
                    // the optional LocalSocket prefix mapping is persisted. The
                    // pre-7.1 ComposeProjectPath key in old backups is ignored.
                    ComposePathHostPrefix = dc.ComposePathHostPrefix,
                    ComposePathContainerPrefix = dc.ComposePathContainerPrefix,
                    AllowImagePrune = dc.AllowImagePrune,
                    PruneUnusedImages = dc.PruneUnusedImages,
                };
                db.DockerConnections.Add(fresh);
                idMap[dc.Id] = fresh.Id;
            }
            else { idMap[dc.Id] = existing.Id; }

            // V7.8 — custom container-icon uploads for this connection. Additive:
            // only seed rows that don't already exist (keyed by container name).
            var dockerConnId = idMap[dc.Id];
            foreach (var icon in dc.ContainerIcons ?? [])
            {
                if (await db.ContainerIcons.AnyAsync(x => x.UserId == userId
                        && x.DockerConnectionId == dockerConnId && x.ContainerName == icon.ContainerName, cancellationToken))
                    continue;
                db.ContainerIcons.Add(new ContainerIconEntity
                {
                    UserId = userId,
                    DockerConnectionId = dockerConnId,
                    ContainerName = icon.ContainerName,
                    IconSource = ContainerIconSource.Custom,
                    LogoBase64 = icon.LogoBase64,
                });
            }
        }

        // ── Proxmox connections (merge by name) ──
        foreach (var pc in dto.ProxmoxConnections ?? [])
        {
            var existing = await db.ProxmoxConnections.FirstOrDefaultAsync(x => x.UserId == userId && x.Name == pc.Name, cancellationToken);
            Guid connectionId;
            if (existing is null)
            {
                // Webhook tokens are globally unique. On a same-instance re-import
                // the original host may still hold this token — drop it rather than
                // fail the import; the user can re-issue a webhook URL from the UI.
                var webhookToken = pc.WebhookToken;
                if (webhookToken is not null
                    && await db.ProxmoxConnections.AnyAsync(x => x.WebhookToken == webhookToken, cancellationToken))
                {
                    webhookToken = null;
                }

                var fresh = new ProxmoxConnectionEntity
                {
                    UserId = userId,
                    Name = pc.Name,
                    ApiBaseUrl = pc.ApiBaseUrl,
                    NodeName = pc.NodeName,
                    ServerType = pc.ServerType,
                    ApiTokenId = pc.ApiTokenId,
                    ApiTokenSecretEncrypted = Enc(pc.ApiTokenSecret),
                    SkipTlsVerify = pc.SkipTlsVerify,
                    SshHost = pc.SshHost,
                    SshPort = pc.SshPort,
                    SshUsername = pc.SshUsername,
                    SshPrivateKeyEncrypted = Enc(pc.SshPrivateKey),
                    SshPrivateKeyPassphraseEncrypted = Enc(pc.SshPrivateKeyPassphrase),
                    AllowConsole = pc.AllowConsole,
                    AllowUpdates = pc.AllowUpdates,
                    AllowDestroy = pc.AllowDestroy,
                    AllowCreate = pc.AllowCreate,
                    Enabled = pc.Enabled,
                    TelemetryPollSeconds = pc.TelemetryPollSeconds,
                    UpdateNotificationsEnabled = pc.UpdateNotificationsEnabled,
                    TelegramNotificationsEnabled = pc.TelegramNotificationsEnabled,
                    AppriseNotificationsEnabled = pc.AppriseNotificationsEnabled,
                    ScheduleType = pc.ScheduleType,
                    CheckEveryHours = pc.CheckEveryHours,
                    CheckAtTime = pc.CheckAtTime,
                    CheckOnDayOfWeek = pc.CheckOnDayOfWeek,
                    WebhookToken = webhookToken,
                };
                db.ProxmoxConnections.Add(fresh);
                connectionId = fresh.Id;
            }
            else { connectionId = existing.Id; }

            // V7.9 — record the source→target mapping so the cross-link passes
            // below can remap each link's ProxmoxConnectionId.
            idMap[pc.Id] = connectionId;

            // Per-guest monitoring intent. Additive: only seed rows that don't
            // already exist for this host (keyed by VmId); the next scan fills in
            // the scan-derived fields and preserves these flags across re-discovery.
            foreach (var g in pc.Guests ?? [])
            {
                var guestExists = await db.ProxmoxGuests
                    .AnyAsync(x => x.ProxmoxConnectionId == connectionId && x.VmId == g.VmId, cancellationToken);
                if (guestExists) continue;

                db.ProxmoxGuests.Add(new ProxmoxGuestEntity
                {
                    ProxmoxConnectionId = connectionId,
                    VmId = g.VmId,
                    GuestType = g.GuestType,
                    Name = g.Name,
                    MonitoringEnabled = g.MonitoringEnabled,
                    MonitoringSnoozedUntil = g.MonitoringSnoozedUntil,
                });
            }

            // V7.8 — custom guest card-icon uploads. Additive: only seed rows that
            // don't already exist for this host (keyed by VmId).
            foreach (var icon in pc.GuestIcons ?? [])
            {
                if (await db.ProxmoxGuestIcons.AnyAsync(x => x.UserId == userId
                        && x.ProxmoxConnectionId == connectionId && x.VmId == icon.VmId, cancellationToken))
                    continue;
                db.ProxmoxGuestIcons.Add(new ProxmoxGuestIconEntity
                {
                    UserId = userId,
                    ProxmoxConnectionId = connectionId,
                    VmId = icon.VmId,
                    IconSource = ContainerIconSource.Custom,
                    LogoBase64 = icon.LogoBase64,
                });
            }
        }

        // ── V7.9 — container↔guest cross-links ──
        // Both the Docker and Proxmox connections are mapped by now. Additive:
        // only seed links that don't already exist (keyed by the (user, connection,
        // container) natural key), and skip a link whose connection wasn't imported.
        foreach (var l in dto.ContainerProxmoxLinks ?? [])
        {
            if (!idMap.TryGetValue(l.DockerConnectionId, out var dockerConnId)
                || !idMap.TryGetValue(l.ProxmoxConnectionId, out var proxmoxConnId))
                continue;

            if (await db.ContainerProxmoxLinks.AnyAsync(x => x.UserId == userId
                    && x.DockerConnectionId == dockerConnId && x.ContainerName == l.ContainerName, cancellationToken))
                continue;

            db.ContainerProxmoxLinks.Add(new ContainerProxmoxLinkEntity
            {
                UserId = userId,
                DockerConnectionId = dockerConnId,
                ContainerName = l.ContainerName,
                ProxmoxConnectionId = proxmoxConnId,
                VmId = l.VmId,
            });
        }

        await db.SaveChangesAsync(cancellationToken);

        // ── Services (merge by name + main URL) ──
        // Re-importing a backup into an instance that already holds the same
        // services used to duplicate every one of them (V6.15.1). An existing
        // service is left untouched and only mapped so watches re-attach to it.
        var imported = 0;
        foreach (var s in dto.Services ?? [])
        {
            var existingSvc = await db.WebResources
                .FirstOrDefaultAsync(x => x.UserId == userId && x.Name == s.Name && x.MainUrl == s.MainUrl, cancellationToken);
            if (existingSvc is not null)
            {
                idMap[s.Id] = existingSvc.Id;
                continue;
            }

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
                ProxmoxConnectionId = MapOrNull(idMap, s.ProxmoxConnectionId),
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
                AppriseNotificationsEnabled = w.AppriseNotificationsEnabled,
                ScheduleType = w.ScheduleType,
                CheckEveryHours = w.CheckEveryHours,
                CheckAtTime = w.CheckAtTime,
                CheckOnDayOfWeek = w.CheckOnDayOfWeek,
                TagPatternFilter = w.TagPatternFilter,
                WebhookToken = webhookToken,
            });
        }

        // ── V7.9 — service↔guest links (skip duplicates) ──
        // Services and Proxmox connections are both mapped by now. Skip a link
        // whose service or connection wasn't imported, and one already present.
        foreach (var l in dto.ServiceProxmoxLinks ?? [])
        {
            if (!idMap.TryGetValue(l.WebResourceId, out var webResourceId)
                || !idMap.TryGetValue(l.ProxmoxConnectionId, out var proxmoxConnId))
                continue;

            if (await db.WebResourceProxmoxGuestLinks.AnyAsync(x => x.WebResourceId == webResourceId
                    && x.ProxmoxConnectionId == proxmoxConnId && x.VmId == l.VmId, cancellationToken))
                continue;

            db.WebResourceProxmoxGuestLinks.Add(new WebResourceProxmoxGuestLinkEntity
            {
                WebResourceId = webResourceId,
                ProxmoxConnectionId = proxmoxConnId,
                VmId = l.VmId,
            });
        }

        // ── V10.2 — public status pages (merge by title) ──
        // Services are mapped by now, so each item's WebResourceId remaps to the
        // imported service (unmapped selections are skipped). The slug is globally
        // unique, so a colliding slug is regenerated rather than failing the import.
        foreach (var p in dto.StatusPages ?? [])
        {
            if (await db.StatusPages.AnyAsync(x => x.UserId == userId && x.Title == p.Title, cancellationToken))
                continue;

            var page = new StatusPageEntity
            {
                UserId = userId,
                Title = p.Title,
                Description = p.Description,
                Slug = await UniqueSlugAsync(p.Slug, p.Title, cancellationToken),
                IsPublished = p.IsPublished,
                UpdatedUtc = DateTime.UtcNow,
            };
            foreach (var item in (p.Items ?? []).OrderBy(i => i.SortOrder))
            {
                if (!idMap.TryGetValue(item.WebResourceId, out var webResourceId))
                    continue;
                page.Items.Add(new StatusPageItemEntity
                {
                    WebResourceId = webResourceId,
                    DisplayName = item.DisplayName,
                    SortOrder = item.SortOrder,
                });
            }
            db.StatusPages.Add(page);
        }

        await db.SaveChangesAsync(cancellationToken);
        return imported;
    }

    private static Guid? MapOrNull(IReadOnlyDictionary<Guid, Guid> idMap, Guid? sourceId) =>
        sourceId.HasValue && idMap.TryGetValue(sourceId.Value, out var mapped) ? mapped : null;

    /// <summary>V10.2 — pick a globally-unique slug for an imported status page: the original
    /// slug if free, else a title-derived one, else a random one — looping until no collision.</summary>
    private async Task<string> UniqueSlugAsync(string? sourceSlug, string title, CancellationToken cancellationToken)
    {
        var candidate = StatusPageSlug.IsValid(sourceSlug) ? sourceSlug! : StatusPageSlug.Slugify(title);
        if (!StatusPageSlug.IsValid(candidate)) candidate = StatusPageSlug.Random();

        var slug = candidate;
        while (await db.StatusPages.AnyAsync(p => p.Slug == slug, cancellationToken))
            slug = StatusPageSlug.Random();
        return slug;
    }

    private sealed record BackupDto(
        DateTime ExportedUtc,
        UserSettingsDto? User,
        List<CategoryDto>? Categories,
        List<TagDto>? Tags,
        List<DockerConnectionDto>? DockerConnections,
        List<ServiceDto>? Services,
        List<DockerWatchDto>? DockerWatches,
        List<ProxmoxConnectionDto>? ProxmoxConnections = null,
        List<WebResourceProxmoxGuestLinkDto>? ServiceProxmoxLinks = null,
        List<ContainerProxmoxLinkDto>? ContainerProxmoxLinks = null,
        MqttDto? Mqtt = null,
        AppriseDto? Apprise = null,
        // V10.2 — nullable/defaulted so a pre-V10.2 backup still deserializes.
        List<StatusPageDto>? StatusPages = null);

    // V10.2 — a public status page: its publish state, slug and the chosen services
    // (referenced by id, remapped on import; display-name overrides preserved).
    private sealed record StatusPageDto(
        string Title, string? Description, string Slug, bool IsPublished, List<StatusPageItemDto> Items);

    private sealed record StatusPageItemDto(Guid WebResourceId, string? DisplayName, int SortOrder);

    // V10.0 — app-wide Apprise notification config. URLs decrypted on export and
    // re-encrypted on import (portable across instances), like every other secret here.
    private sealed record AppriseDto(bool Enabled, string BaseUrl, string? Urls);

    // V9.0 — app-wide MQTT integration config. Password is decrypted on export and
    // re-encrypted on import (portable across instances), like every other secret here.
    private sealed record MqttDto(
        bool Enabled, string Host, int Port, bool UseTls, bool AllowUntrustedTls,
        string Username, string? Password, string ClientId, string DiscoveryPrefix, string EntityPrefix,
        // V9.1 — nullable so a pre-V9.1 backup still deserializes; defaulted on import.
        string? DeviceName = null, string? Manufacturer = null);

    private sealed record UserSettingsDto(
        string? DisplayName, string Theme, string DashboardSortMode, bool DashboardGroupByCategory,
        string? TelegramBotToken, string? TelegramChatId, bool TelegramNotificationsEnabled,
        // V10.3 — nullable/defaulted so a pre-V10.3 backup still deserializes (and restores as "2FA off").
        bool TwoFactorEnabled = false, string? TwoFactorSecret = null, long? TwoFactorLastUsedStep = null,
        List<RecoveryCodeDto>? RecoveryCodes = null);

    // V10.3 — a single recovery code: its hash (one-way, never decryptable) and used state.
    private sealed record RecoveryCodeDto(string CodeHash, DateTime? UsedUtc);

    private sealed record CategoryDto(Guid Id, string Name, string Color);
    private sealed record TagDto(Guid Id, string Name);

    private sealed record DockerConnectionDto(
        Guid Id, string Name, DockerHostType HostType, string? HostUrl,
        string? TlsCaCert, string? TlsClientCert, string? TlsClientKey,
        string? SshHost, int? SshPort, string? SshUsername,
        string? SshPrivateKey, string? SshPrivateKeyPassphrase, string? SshRemoteSocketPath,
        string? ComposePathHostPrefix = null,
        string? ComposePathContainerPrefix = null,
        bool AllowImagePrune = true,
        bool PruneUnusedImages = false,
        List<ContainerIconDto>? ContainerIcons = null);

    private sealed record ContainerIconDto(string ContainerName, string LogoBase64);
    private sealed record GuestIconDto(int VmId, string LogoBase64);

    private sealed record ServiceDto(
        Guid Id, string Name, string MainUrl, bool MainUrlHealthCheckEnabled, string? AdditionalUrl,
        bool AdditionalUrlHealthCheckEnabled, bool OfflineNotificationsEnabled, string? HealthCheckUrl,
        HealthCheckMethod HealthCheckMethod, string? ExpectedStatusRange, string? Notes, Guid? CategoryId,
        LogoSource LogoSource, string? CustomLogoPath, string? LogoBase64, Guid? DockerConnectionId,
        List<CredentialDto> Credentials, List<Guid> TagIds, Guid? ProxmoxConnectionId = null);

    private sealed record CredentialDto(string Key, string Value, bool IsSecret);

    private sealed record DockerWatchDto(
        Guid Id, Guid DockerConnectionId, Guid? WebResourceId, string Label, bool Enabled, string ImageReference,
        string RegistryHost, string Repository, string Tag, string ContainerName,
        string? RegistryUsername, string? RegistryPassword, string? GitHubPat, RegistryAuthType RegistryAuthType,
        string? AwsAccessKeyId, string? AwsSecretAccessKey, string? AwsRegion,
        bool UpdateNotificationsEnabled, bool TelegramNotificationsEnabled, CheckScheduleType ScheduleType,
        int CheckEveryHours, TimeOnly? CheckAtTime, DayOfWeek? CheckOnDayOfWeek, string? TagPatternFilter, string? WebhookToken,
        // V10.0 — nullable/defaulted so a pre-V10.0 backup still deserializes.
        bool AppriseNotificationsEnabled = false);

    private sealed record ProxmoxConnectionDto(
        Guid Id, string Name, string ApiBaseUrl, string NodeName, ProxmoxServerType ServerType, string ApiTokenId,
        string? ApiTokenSecret, bool SkipTlsVerify,
        string? SshHost, int? SshPort, string? SshUsername,
        string? SshPrivateKey, string? SshPrivateKeyPassphrase,
        bool AllowConsole, bool AllowUpdates, bool AllowDestroy, bool AllowCreate, bool Enabled,
        int? TelemetryPollSeconds, bool UpdateNotificationsEnabled, bool TelegramNotificationsEnabled,
        CheckScheduleType ScheduleType, int CheckEveryHours, TimeOnly? CheckAtTime, DayOfWeek? CheckOnDayOfWeek,
        string? WebhookToken, List<ProxmoxGuestDto> Guests, List<GuestIconDto>? GuestIcons = null,
        // V10.0 — nullable/defaulted so a pre-V10.0 backup still deserializes.
        bool AppriseNotificationsEnabled = false);

    private sealed record ProxmoxGuestDto(
        int VmId, ProxmoxGuestType GuestType, string Name, bool MonitoringEnabled, DateTime? MonitoringSnoozedUntil);

    // V7.9 — both link kinds reference the source connection guid (remapped via
    // idMap on import) + the guest's stable vmid.
    private sealed record WebResourceProxmoxGuestLinkDto(Guid WebResourceId, Guid ProxmoxConnectionId, int VmId);
    private sealed record ContainerProxmoxLinkDto(
        Guid DockerConnectionId, string ContainerName, Guid ProxmoxConnectionId, int VmId);
}
