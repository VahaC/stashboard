namespace Stashboard.Core.Entities;

/// <summary>
/// Layer Supertype (Fowler) for every aggregate root / entity that has a single Guid primary key.
/// Centralises the identity contract so EF mappings, repositories and mappers can rely on it.
/// </summary>
public abstract class BaseEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
}
