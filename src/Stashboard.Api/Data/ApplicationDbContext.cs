using Microsoft.EntityFrameworkCore;
using Stashboard.Core.Entities;

namespace Stashboard.Api.Data;

/// <summary>
/// Plain EF Core DbContext — no ASP.NET Core Identity inheritance.
/// </summary>
public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : DbContext(options)
{
    public DbSet<UserEntity> Users => Set<UserEntity>();
    public DbSet<RefreshTokenEntity> RefreshTokens => Set<RefreshTokenEntity>();
    public DbSet<EmailSettingsEntity> EmailSettings => Set<EmailSettingsEntity>();

    public DbSet<WebResourceEntity> WebResources => Set<WebResourceEntity>();
    public DbSet<CredentialEntity> Credentials => Set<CredentialEntity>();
    public DbSet<CategoryEntity> Categories => Set<CategoryEntity>();
    public DbSet<TagEntity> Tags => Set<TagEntity>();
    public DbSet<WebResourceTagEntity> WebResourceTags => Set<WebResourceTagEntity>();
    public DbSet<DockerConnectionEntity> DockerConnections => Set<DockerConnectionEntity>();
    public DbSet<DockerWatchEntity> DockerWatches => Set<DockerWatchEntity>();
    public DbSet<DockerUpdateAttemptEntity> DockerUpdateAttempts => Set<DockerUpdateAttemptEntity>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<UserEntity>(e =>
        {
            e.HasIndex(u => u.NormalizedEmail).IsUnique();
            e.Property(u => u.SecurityStamp).HasMaxLength(64).IsRequired();
        });

        // Single-row, app-wide SMTP/email config (see EmailSettingsEntity.SingletonId).
        builder.Entity<EmailSettingsEntity>();

        builder.Entity<RefreshTokenEntity>(e =>
        {
            e.HasIndex(t => t.TokenHash).IsUnique();
            e.HasIndex(t => t.UserId);
            e.HasIndex(t => t.FamilyId);
            e.HasOne(t => t.User)
                .WithMany(u => u.RefreshTokens)
                .HasForeignKey(t => t.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<WebResourceEntity>(e =>
        {
            e.HasIndex(s => new { s.UserId, s.Name });
            e.HasMany(s => s.Credentials)
                .WithOne(c => c.WebResource)
                .HasForeignKey(c => c.WebResourceId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(s => s.Category)
                .WithMany(c => c.Services)
                .HasForeignKey(s => s.CategoryId)
                .OnDelete(DeleteBehavior.SetNull);
            // No navigation property on User — configure FK via shadow relationship so deleting
            // a user cascades through their domain data without polluting Core entities.
            e.HasOne<UserEntity>()
                .WithMany()
                .HasForeignKey(s => s.UserId)
                .OnDelete(DeleteBehavior.Cascade);
            // Optional FK to the user-level Docker connection. SetNull so an
            // accidental connection delete leaves the service intact (the app
            // enforces "can't delete a connection in use" at the controller).
            e.HasOne(s => s.DockerConnection)
                .WithMany()
                .HasForeignKey(s => s.DockerConnectionId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<CategoryEntity>(e =>
        {
            e.HasIndex(c => new { c.UserId, c.Name }).IsUnique();
            e.HasOne<UserEntity>()
                .WithMany()
                .HasForeignKey(c => c.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<TagEntity>(e =>
        {
            e.HasIndex(t => new { t.UserId, t.Name }).IsUnique();
            e.HasOne<UserEntity>()
                .WithMany()
                .HasForeignKey(t => t.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<WebResourceTagEntity>(e =>
        {
            e.HasKey(st => new { st.WebResourceId, st.TagId });
            e.HasOne(st => st.Service)
                .WithMany(s => s.WebResourceTags)
                .HasForeignKey(st => st.WebResourceId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(st => st.Tag)
                .WithMany(t => t.WebResourceTags)
                .HasForeignKey(st => st.TagId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<DockerConnectionEntity>(e =>
        {
            // User-scoped, named, reusable across services. The (UserId, Name)
            // unique index keeps the dropdown labels stable per user.
            e.HasIndex(c => new { c.UserId, c.Name }).IsUnique();
            e.HasOne<UserEntity>()
                .WithMany()
                .HasForeignKey(c => c.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<DockerWatchEntity>(e =>
        {
            // V3.6 — a watch is a tracked container, identified by its name on a
            // given host. One watch per (connection, container).
            e.HasIndex(w => new { w.DockerConnectionId, w.ContainerName }).IsUnique();
            // Composite index supports the background scan filter:
            //   "enabled watches whose LastCheckedUtc is null OR is due per schedule".
            // V2.2 keeps the same shape — the scan loads all enabled watches and
            // filters in memory using the schedule fields.
            e.HasIndex(w => new { w.UserId, w.Enabled, w.LastCheckedUtc });
            // Speeds up the per-connection watch list on the Docker page.
            e.HasIndex(w => w.DockerConnectionId);
            // V2.6 — webhook token is the URL secret for the public webhook
            // endpoint. Unique so a tampered URL can't accidentally collide
            // with another user's watch. NULL is allowed for the majority of
            // watches that haven't opted in to webhook delivery; both Postgres
            // and SQLite treat multiple NULLs as distinct in a unique index.
            e.HasIndex(w => w.WebhookToken).IsUnique();
            // V3.6 — the watch is owned by its Docker host connection. Deleting
            // the connection removes every container watch on it (the host is
            // gone, the tracking is meaningless).
            e.HasOne(w => w.DockerConnection)
                .WithMany()
                .HasForeignKey(w => w.DockerConnectionId)
                .OnDelete(DeleteBehavior.Cascade);
            // V3.6 — the service link is now optional. Deleting a service
            // detaches its containers (SetNull) rather than deleting them —
            // the containers live on independently.
            e.HasOne(w => w.WebResource)
                .WithMany(s => s.DockerWatches)
                .HasForeignKey(w => w.WebResourceId)
                .OnDelete(DeleteBehavior.SetNull);
            // Owner cascade — deleting a user removes their watches.
            e.HasOne<UserEntity>()
                .WithMany()
                .HasForeignKey(w => w.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<DockerUpdateAttemptEntity>(e =>
        {
            // V2.7 — audit history for the per-watch "Update now" button.
            // V3.5 — same table doubles as the per-connection activity
            // log for the instances page; DockerWatchId / WebResourceId
            // are nullable for actions on containers that aren't tracked
            // by a watch.
            // Index supports the per-watch history endpoint
            // (`/watches/{id}/updates`) which reads "newest first" — adding
            // CompletedUtc DESC at query time is cheap on this composite.
            e.HasIndex(a => new { a.DockerWatchId, a.CompletedUtc });
            // V3.5 — index for the per-connection activity log on the
            // Docker instances page.
            e.HasIndex(a => new { a.DockerConnectionId, a.CompletedUtc });
            // Cascade through the parent watch so deleting a watch drops its
            // history. Audit isn't useful when the container it audits no
            // longer exists.
            e.HasOne<DockerWatchEntity>()
                .WithMany()
                .HasForeignKey(a => a.DockerWatchId)
                .OnDelete(DeleteBehavior.Cascade);
            // V3.6 — the service link is optional and a container can outlive
            // its service, so deleting a service only detaches the historical
            // rows (SetNull) instead of erasing them.
            e.HasOne<WebResourceEntity>()
                .WithMany()
                .HasForeignKey(a => a.WebResourceId)
                .OnDelete(DeleteBehavior.SetNull);
            // V3.5 — connection FK for the instances page activity log.
            // SetNull so deleting a connection keeps the historical
            // record around (the connection name is captured in the
            // controller code at write time if we ever want to surface
            // it).
            e.HasOne<DockerConnectionEntity>()
                .WithMany()
                .HasForeignKey(a => a.DockerConnectionId)
                .OnDelete(DeleteBehavior.SetNull);
        });
    }
}
