using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Stashboard.Api.Contracts;
using Stashboard.Api.Data;
using Stashboard.Core.Abstractions;

namespace Stashboard.Api.Services.Mqtt;

/// <inheritdoc cref="IMqttSettingsService"/>
public sealed class MqttSettingsService(
    ApplicationDbContext db,
    IEncryptionService encryption,
    IOptions<MqttOptions> seedDefaults,
    TimeProvider time) : IMqttSettingsService
{
    private readonly MqttOptions _seed = seedDefaults.Value;

    public async Task<ResolvedMqttSettings> GetResolvedAsync(CancellationToken cancellationToken = default)
    {
        var entity = await GetOrCreateAsync(cancellationToken);
        var password = string.IsNullOrEmpty(entity.PasswordEncrypted)
            ? ""
            : encryption.Decrypt(entity.PasswordEncrypted);

        return new ResolvedMqttSettings(
            entity.Enabled,
            entity.Host,
            entity.Port,
            entity.UseTls,
            entity.AllowUntrustedTls,
            entity.Username,
            password,
            NormalizePrefix(entity.ClientId, "stashboard"),
            NormalizePrefix(entity.DiscoveryPrefix, "homeassistant"),
            NormalizePrefix(entity.EntityPrefix, "stashboard"),
            NormalizePrefix(entity.DeviceName, "Stashboard"),
            NormalizePrefix(entity.Manufacturer, "Stashboard"));
    }

    public async Task<MqttSettingsResponse> GetAsync(CancellationToken cancellationToken = default)
    {
        var entity = await GetOrCreateAsync(cancellationToken);
        return new MqttSettingsResponse(
            entity.Enabled,
            entity.Host,
            entity.Port,
            entity.UseTls,
            entity.AllowUntrustedTls,
            entity.Username,
            !string.IsNullOrEmpty(entity.PasswordEncrypted),
            entity.ClientId,
            entity.DiscoveryPrefix,
            entity.EntityPrefix,
            entity.DeviceName,
            entity.Manufacturer);
    }

    public async Task UpdateAsync(UpdateMqttSettingsRequest request, CancellationToken cancellationToken = default)
    {
        var entity = await GetOrCreateAsync(cancellationToken);

        entity.Enabled = request.Enabled;
        entity.Host = request.Host?.Trim() ?? "";
        entity.Port = request.Port;
        entity.UseTls = request.UseTls;
        entity.AllowUntrustedTls = request.AllowUntrustedTls;
        entity.Username = request.Username?.Trim() ?? "";
        entity.ClientId = NormalizePrefix(request.ClientId, "stashboard");
        entity.DiscoveryPrefix = NormalizePrefix(request.DiscoveryPrefix, "homeassistant");
        entity.EntityPrefix = NormalizePrefix(request.EntityPrefix, "stashboard");
        entity.DeviceName = NormalizePrefix(request.DeviceName, "Stashboard");
        entity.Manufacturer = NormalizePrefix(request.Manufacturer, "Stashboard");

        ApplyPassword(entity, request.Password);

        entity.UpdatedUtc = time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(cancellationToken);
    }

    private static string NormalizePrefix(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private void ApplyPassword(MqttSettingsEntity entity, SecretValueUpsert? password)
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

    private async Task<MqttSettingsEntity> GetOrCreateAsync(CancellationToken cancellationToken)
    {
        var entity = await db.MqttSettings
            .FirstOrDefaultAsync(e => e.Id == MqttSettingsEntity.SingletonId, cancellationToken);
        if (entity is not null) return entity;

        var now = time.GetUtcNow().UtcDateTime;
        entity = new MqttSettingsEntity
        {
            Id = MqttSettingsEntity.SingletonId,
            Enabled = _seed.Enabled,
            Host = _seed.Host?.Trim() ?? "",
            Port = _seed.Port,
            UseTls = _seed.UseTls,
            AllowUntrustedTls = _seed.AllowUntrustedTls,
            Username = _seed.Username?.Trim() ?? "",
            PasswordEncrypted = string.IsNullOrEmpty(_seed.Password) ? null : encryption.Encrypt(_seed.Password),
            ClientId = NormalizePrefix(_seed.ClientId, "stashboard"),
            DiscoveryPrefix = NormalizePrefix(_seed.DiscoveryPrefix, "homeassistant"),
            EntityPrefix = NormalizePrefix(_seed.EntityPrefix, "stashboard"),
            DeviceName = NormalizePrefix(_seed.DeviceName, "Stashboard"),
            Manufacturer = NormalizePrefix(_seed.Manufacturer, "Stashboard"),
            CreatedUtc = now,
            UpdatedUtc = now,
        };
        db.MqttSettings.Add(entity);
        await db.SaveChangesAsync(cancellationToken);
        return entity;
    }
}
