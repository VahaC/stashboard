using Microsoft.EntityFrameworkCore;
using Stashboard.Api.Contracts;
using Stashboard.Api.Data;
using Stashboard.Core.Abstractions;

namespace Stashboard.Api.Auth.Oidc;

/// <inheritdoc cref="IOidcSettingsService"/>
public sealed class OidcSettingsService(
    ApplicationDbContext db,
    IEncryptionService encryption,
    TimeProvider time) : IOidcSettingsService
{
    public async Task<ResolvedOidcSettings> GetResolvedAsync(CancellationToken cancellationToken = default)
    {
        var entity = await GetOrCreateAsync(cancellationToken);
        var secret = string.IsNullOrEmpty(entity.ClientSecretEncrypted)
            ? null
            : encryption.Decrypt(entity.ClientSecretEncrypted);

        return new ResolvedOidcSettings(
            entity.Enabled, entity.DisplayName, entity.Issuer, entity.ClientId, secret,
            entity.Scopes, entity.RedirectBaseUrl, entity.AllowOidcRegistration);
    }

    public async Task<OidcSettingsResponse> GetAsync(CancellationToken cancellationToken = default)
    {
        var entity = await GetOrCreateAsync(cancellationToken);
        return new OidcSettingsResponse(
            entity.Enabled, entity.DisplayName, entity.Issuer, entity.ClientId,
            !string.IsNullOrEmpty(entity.ClientSecretEncrypted), entity.Scopes,
            entity.RedirectBaseUrl, entity.AllowOidcRegistration,
            OidcConstants.BuildRedirectUri(entity.RedirectBaseUrl));
    }

    public async Task<bool> IsEnabledAsync(CancellationToken cancellationToken = default)
    {
        var settings = await GetResolvedAsync(cancellationToken);
        return settings.IsUsable;
    }

    public async Task UpdateAsync(UpdateOidcSettingsRequest request, CancellationToken cancellationToken = default)
    {
        var entity = await GetOrCreateAsync(cancellationToken);

        entity.Enabled = request.Enabled;
        entity.DisplayName = request.DisplayName?.Trim() ?? "";
        entity.Issuer = request.Issuer?.Trim() ?? "";
        entity.ClientId = request.ClientId?.Trim() ?? "";
        entity.Scopes = string.IsNullOrWhiteSpace(request.Scopes) ? "openid profile email" : request.Scopes.Trim();
        entity.RedirectBaseUrl = request.RedirectBaseUrl?.Trim().TrimEnd('/') ?? "";
        entity.AllowOidcRegistration = request.AllowOidcRegistration;

        ApplySecret(entity, request.ClientSecret);

        entity.UpdatedUtc = time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(cancellationToken);
    }

    private void ApplySecret(OidcSettingsEntity entity, SecretValueUpsert? secret)
    {
        switch (secret?.Action)
        {
            case SecretValueAction.Set:
                entity.ClientSecretEncrypted = string.IsNullOrEmpty(secret.Value)
                    ? null
                    : encryption.Encrypt(secret.Value);
                break;
            case SecretValueAction.Clear:
                entity.ClientSecretEncrypted = null;
                break;
            // Keep (or null) — leave the persisted value untouched.
        }
    }

    private async Task<OidcSettingsEntity> GetOrCreateAsync(CancellationToken cancellationToken)
    {
        var entity = await db.OidcSettings
            .FirstOrDefaultAsync(e => e.Id == OidcSettingsEntity.SingletonId, cancellationToken);
        if (entity is not null) return entity;

        var now = time.GetUtcNow().UtcDateTime;
        entity = new OidcSettingsEntity
        {
            Id = OidcSettingsEntity.SingletonId,
            CreatedUtc = now,
            UpdatedUtc = now,
        };
        db.OidcSettings.Add(entity);
        await db.SaveChangesAsync(cancellationToken);
        return entity;
    }
}
