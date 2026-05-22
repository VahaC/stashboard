using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Stashboard.Api.Contracts;
using Stashboard.Api.Data;
using Stashboard.Api.Mapping;
using Stashboard.Core.Abstractions;

namespace Stashboard.Api.Notifications;

/// <inheritdoc cref="IEmailSettingsService"/>
public sealed class EmailSettingsService(
    ApplicationDbContext db,
    IEncryptionService encryption,
    IOptions<EmailOptions> seedDefaults,
    IStashboardMapper mapper,
    TimeProvider time) : IEmailSettingsService
{
    private readonly EmailOptions _seed = seedDefaults.Value;

    public async Task<ResolvedEmailSettings> GetResolvedAsync(CancellationToken cancellationToken = default)
    {
        var entity = await GetOrCreateAsync(cancellationToken);
        var password = string.IsNullOrEmpty(entity.PasswordEncrypted)
            ? ""
            : encryption.Decrypt(entity.PasswordEncrypted);

        return new ResolvedEmailSettings(
            entity.Provider,
            entity.Host,
            entity.Port,
            entity.UseStartTls,
            entity.Username,
            password,
            entity.FromAddress,
            entity.FromName,
            entity.AppBaseUrl);
    }

    public async Task<EmailSettingsResponse> GetAsync(CancellationToken cancellationToken = default)
    {
        var entity = await GetOrCreateAsync(cancellationToken);
        return mapper.MapToEmailSettingsResponse(entity);
    }

    public async Task UpdateAsync(UpdateEmailSettingsRequest request, CancellationToken cancellationToken = default)
    {
        var entity = await GetOrCreateAsync(cancellationToken);

        entity.Provider = request.Provider;
        entity.Host = request.Host?.Trim() ?? "";
        entity.Port = request.Port;
        entity.UseStartTls = request.UseStartTls;
        entity.Username = request.Username?.Trim() ?? "";
        entity.FromAddress = string.IsNullOrWhiteSpace(request.FromAddress)
            ? "no-reply@stashboard.local"
            : request.FromAddress.Trim();
        entity.FromName = string.IsNullOrWhiteSpace(request.FromName) ? "Stashboard" : request.FromName.Trim();
        entity.AppBaseUrl = string.IsNullOrWhiteSpace(request.AppBaseUrl)
            ? "http://localhost:5173"
            : request.AppBaseUrl.Trim();

        ApplyPassword(entity, request.Password);

        entity.UpdatedUtc = time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(cancellationToken);
    }

    private void ApplyPassword(EmailSettingsEntity entity, SecretValueUpsert? password)
    {
        switch (password?.Action)
        {
            case SecretValueAction.Set:
                entity.PasswordEncrypted = string.IsNullOrEmpty(password.Value)
                    ? null
                    : encryption.Encrypt(password.Value);
                break;
            case SecretValueAction.Clear:
                entity.PasswordEncrypted = null;
                break;
            // Keep (or null) — leave the persisted value untouched.
        }
    }

    private async Task<EmailSettingsEntity> GetOrCreateAsync(CancellationToken cancellationToken)
    {
        var entity = await db.EmailSettings
            .FirstOrDefaultAsync(e => e.Id == EmailSettingsEntity.SingletonId, cancellationToken);
        if (entity is not null) return entity;

        var now = time.GetUtcNow().UtcDateTime;
        entity = new EmailSettingsEntity
        {
            Id = EmailSettingsEntity.SingletonId,
            Provider = string.IsNullOrWhiteSpace(_seed.Provider) ? "LogOnly" : _seed.Provider.Trim(),
            Host = _seed.Host?.Trim() ?? "",
            Port = _seed.Port,
            UseStartTls = _seed.UseStartTls,
            Username = _seed.Username?.Trim() ?? "",
            PasswordEncrypted = string.IsNullOrEmpty(_seed.Password) ? null : encryption.Encrypt(_seed.Password),
            FromAddress = string.IsNullOrWhiteSpace(_seed.FromAddress)
                ? "no-reply@stashboard.local"
                : _seed.FromAddress.Trim(),
            FromName = string.IsNullOrWhiteSpace(_seed.FromName) ? "Stashboard" : _seed.FromName.Trim(),
            AppBaseUrl = string.IsNullOrWhiteSpace(_seed.AppBaseUrl)
                ? "http://localhost:5173"
                : _seed.AppBaseUrl.Trim(),
            CreatedUtc = now,
            UpdatedUtc = now,
        };
        db.EmailSettings.Add(entity);
        await db.SaveChangesAsync(cancellationToken);
        return entity;
    }
}
