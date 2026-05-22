namespace Stashboard.Core.Entities;

/// <summary>
/// Adds creation audit timestamp on top of <see cref="BaseEntity"/>. Inherited by entities
/// that need to track when the row was first persisted.
/// </summary>
public abstract class AuditableEntity : BaseEntity
{
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}
