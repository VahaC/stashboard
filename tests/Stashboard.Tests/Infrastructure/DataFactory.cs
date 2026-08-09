using Microsoft.EntityFrameworkCore;
using Stashboard.Api.Auth;
using Stashboard.Api.Data;
using Stashboard.Core.Abstractions;
using Stashboard.Core.Entities;
using Stashboard.Core.Enums;

namespace Stashboard.Tests.Infrastructure;

public sealed class DataFactory(
    ApplicationDbContext db,
    IEncryptionService encryption,
    IPasswordHasher hasher,
    Guid defaultUserId)
    : IDataFactory
{
    public async Task<UserEntity> UserAsync(string? email = null, string password = "P@ssword1")
    {
        var resolved = email ?? $"user-{Guid.NewGuid():N}@test.local";
        var user = new UserEntity
        {
            Id = Guid.NewGuid(),
            Email = resolved,
            NormalizedEmail = resolved.ToUpperInvariant(),
            PasswordHash = hasher.Hash(password),
            SecurityStamp = Guid.NewGuid().ToString("N"),
            CreatedUtc = DateTime.UtcNow,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return user;
    }

    public async Task<WebResourceEntity> ServiceAsync(
        Guid? userId = null,
        string name = "My Service",
        string mainUrl = "https://example.com",
        string? notes = null,
        Guid? categoryId = null)
    {
        var entity = new WebResourceEntity
        {
            Id = Guid.NewGuid(),
            UserId = userId ?? defaultUserId,
            Name = name,
            MainUrl = mainUrl,
            Notes = notes,
            CategoryId = categoryId,
            CurrentStatus = ServiceStatus.Up,
        };
        db.WebResources.Add(entity);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return entity;
    }

    public async Task<CategoryEntity> CategoryAsync(
        Guid? userId = null,
        string name = "Infrastructure",
        string color = "#ff0000")
    {
        var entity = new CategoryEntity
        {
            Id = Guid.NewGuid(),
            UserId = userId ?? defaultUserId,
            Name = name,
            Color = color,
        };
        db.Categories.Add(entity);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return entity;
    }

    public async Task<TagEntity> TagAsync(string name = "tag", Guid? userId = null)
    {
        var owner = userId ?? defaultUserId;
        var existing = await db.Tags.AsNoTracking()
            .FirstOrDefaultAsync(t => t.UserId == owner && t.Name == name);
        if (existing is not null) return existing;

        var entity = new TagEntity { Id = Guid.NewGuid(), UserId = owner, Name = name };
        db.Tags.Add(entity);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return entity;
    }

    public async Task AttachTagAsync(WebResourceEntity service, TagEntity tag)
    {
        db.WebResourceTags.Add(new WebResourceTagEntity { WebResourceId = service.Id, TagId = tag.Id });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    public async Task AttachCredentialAsync(WebResourceEntity service, string key, string value, bool isSecret = true)
    {
        db.Credentials.Add(new CredentialEntity
        {
            Id = Guid.NewGuid(),
            WebResourceId = service.Id,
            Key = key,
            EncryptedValue = encryption.Encrypt(value),
            IsSecret = isSecret,
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }
}



