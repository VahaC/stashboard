using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Stashboard.Api.Contracts;
using Stashboard.Api.Data;
using Stashboard.Core.Abstractions;

namespace Stashboard.Api.Notifications;

/// <inheritdoc cref="IAppriseSettingsService"/>
public sealed class AppriseSettingsService(
    ApplicationDbContext db,
    IEncryptionService encryption,
    IOptions<AppriseOptions> seedDefaults,
    TimeProvider time) : IAppriseSettingsService
{
    private readonly AppriseOptions _seed = seedDefaults.Value;

    public async Task<ResolvedAppriseSettings> GetResolvedAsync(CancellationToken cancellationToken = default)
    {
        var entity = await GetOrCreateAsync(cancellationToken);
        var urls = ParseUrls(string.IsNullOrEmpty(entity.UrlsEncrypted) ? "" : encryption.Decrypt(entity.UrlsEncrypted));
        return new ResolvedAppriseSettings(entity.Enabled, entity.BaseUrl, urls);
    }

    public async Task<AppriseSettingsResponse> GetAsync(CancellationToken cancellationToken = default)
    {
        var entity = await GetOrCreateAsync(cancellationToken);
        var urls = ParseUrls(string.IsNullOrEmpty(entity.UrlsEncrypted) ? "" : encryption.Decrypt(entity.UrlsEncrypted));
        return new AppriseSettingsResponse(
            entity.Enabled,
            entity.BaseUrl,
            urls.Count > 0,
            urls.Count,
            urls.Select(SchemeOf).Where(s => s.Length > 0).Distinct().ToList());
    }

    public async Task UpdateAsync(UpdateAppriseSettingsRequest request, CancellationToken cancellationToken = default)
    {
        var entity = await GetOrCreateAsync(cancellationToken);

        entity.Enabled = request.Enabled;
        entity.BaseUrl = request.BaseUrl?.Trim() ?? "";
        ApplyUrls(entity, request.Urls);

        entity.UpdatedUtc = time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(cancellationToken);
    }

    private void ApplyUrls(AppriseSettingsEntity entity, SecretValueUpsert? urls)
    {
        switch (urls?.Action)
        {
            case SecretValueAction.Set:
                // Re-serialise through the parser so blank lines / stray whitespace
                // never persist, and an all-empty payload clears the list.
                var normalised = string.Join("\n", ParseUrls(urls.Value ?? ""));
                entity.UrlsEncrypted = normalised.Length == 0 ? null : encryption.Encrypt(normalised);
                break;
            case SecretValueAction.Clear:
                entity.UrlsEncrypted = null;
                break;
            // Keep (or null) — leave the persisted value untouched.
        }
    }

    /// <summary>Splits a newline-separated blob into trimmed, non-empty Apprise URLs.</summary>
    internal static List<string> ParseUrls(string blob) => blob
        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .ToList();

    /// <summary>The non-secret scheme of an Apprise URL (the part before <c>://</c>).</summary>
    private static string SchemeOf(string url)
    {
        var idx = url.IndexOf("://", StringComparison.Ordinal);
        return idx <= 0 ? "" : url[..idx].Trim().ToLowerInvariant();
    }

    private async Task<AppriseSettingsEntity> GetOrCreateAsync(CancellationToken cancellationToken)
    {
        var entity = await db.AppriseSettings
            .FirstOrDefaultAsync(e => e.Id == AppriseSettingsEntity.SingletonId, cancellationToken);
        if (entity is not null) return entity;

        var now = time.GetUtcNow().UtcDateTime;
        var seedUrls = string.Join("\n", ParseUrls(_seed.Urls ?? ""));
        entity = new AppriseSettingsEntity
        {
            Id = AppriseSettingsEntity.SingletonId,
            Enabled = _seed.Enabled,
            BaseUrl = _seed.BaseUrl?.Trim() ?? "",
            UrlsEncrypted = seedUrls.Length == 0 ? null : encryption.Encrypt(seedUrls),
            CreatedUtc = now,
            UpdatedUtc = now,
        };
        db.AppriseSettings.Add(entity);
        await db.SaveChangesAsync(cancellationToken);
        return entity;
    }
}
