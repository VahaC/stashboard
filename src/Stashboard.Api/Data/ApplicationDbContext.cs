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
    public DbSet<HostShellSettingsEntity> HostShellSettings => Set<HostShellSettingsEntity>();
    public DbSet<ContainerExecSettingsEntity> ContainerExecSettings => Set<ContainerExecSettingsEntity>();
    public DbSet<ProxmoxConsoleSettingsEntity> ProxmoxConsoleSettings => Set<ProxmoxConsoleSettingsEntity>();
    public DbSet<ProxmoxUpdateApplySettingsEntity> ProxmoxUpdateApplySettings => Set<ProxmoxUpdateApplySettingsEntity>();
    public DbSet<ProxmoxDestroySettingsEntity> ProxmoxDestroySettings => Set<ProxmoxDestroySettingsEntity>();
    public DbSet<ProxmoxCreateSettingsEntity> ProxmoxCreateSettings => Set<ProxmoxCreateSettingsEntity>();
    public DbSet<ProxmoxCloneSettingsEntity> ProxmoxCloneSettings => Set<ProxmoxCloneSettingsEntity>();
    public DbSet<ProxmoxRestoreSettingsEntity> ProxmoxRestoreSettings => Set<ProxmoxRestoreSettingsEntity>();
    public DbSet<HealthCheckSettingsEntity> HealthCheckSettings => Set<HealthCheckSettingsEntity>();
    public DbSet<MqttSettingsEntity> MqttSettings => Set<MqttSettingsEntity>();
    public DbSet<AppriseSettingsEntity> AppriseSettings => Set<AppriseSettingsEntity>();

    public DbSet<WebResourceEntity> WebResources => Set<WebResourceEntity>();
    public DbSet<HealthCheckEventEntity> HealthCheckEvents => Set<HealthCheckEventEntity>();
    public DbSet<StatusPageEntity> StatusPages => Set<StatusPageEntity>();
    public DbSet<StatusPageItemEntity> StatusPageItems => Set<StatusPageItemEntity>();
    public DbSet<CredentialEntity> Credentials => Set<CredentialEntity>();
    public DbSet<CategoryEntity> Categories => Set<CategoryEntity>();
    public DbSet<TagEntity> Tags => Set<TagEntity>();
    public DbSet<WebResourceTagEntity> WebResourceTags => Set<WebResourceTagEntity>();
    public DbSet<DockerConnectionEntity> DockerConnections => Set<DockerConnectionEntity>();
    public DbSet<DockerWatchEntity> DockerWatches => Set<DockerWatchEntity>();
    public DbSet<DockerUpdateAttemptEntity> DockerUpdateAttempts => Set<DockerUpdateAttemptEntity>();
    public DbSet<ContainerIconEntity> ContainerIcons => Set<ContainerIconEntity>();
    public DbSet<ContainerProxmoxLinkEntity> ContainerProxmoxLinks => Set<ContainerProxmoxLinkEntity>();
    public DbSet<WebResourceProxmoxGuestLinkEntity> WebResourceProxmoxGuestLinks => Set<WebResourceProxmoxGuestLinkEntity>();
    public DbSet<ComposeChangeAuditEntity> ComposeChangeAudits => Set<ComposeChangeAuditEntity>();
    public DbSet<HostShellSessionEntity> HostShellSessions => Set<HostShellSessionEntity>();
    public DbSet<DockerExecSessionEntity> DockerExecSessions => Set<DockerExecSessionEntity>();
    public DbSet<ImagePruneSettingsEntity> ImagePruneSettings => Set<ImagePruneSettingsEntity>();
    public DbSet<DockerPruneRunEntity> DockerPruneRuns => Set<DockerPruneRunEntity>();
    public DbSet<ProxmoxConnectionEntity> ProxmoxConnections => Set<ProxmoxConnectionEntity>();
    public DbSet<ProxmoxGuestEntity> ProxmoxGuests => Set<ProxmoxGuestEntity>();
    public DbSet<ProxmoxGuestIconEntity> ProxmoxGuestIcons => Set<ProxmoxGuestIconEntity>();
    public DbSet<ProxmoxConsoleSessionEntity> ProxmoxConsoleSessions => Set<ProxmoxConsoleSessionEntity>();
    public DbSet<ProxmoxUpdateSessionEntity> ProxmoxUpdateSessions => Set<ProxmoxUpdateSessionEntity>();
    public DbSet<ProxmoxMonitoringAuditEntity> ProxmoxMonitoringAudits => Set<ProxmoxMonitoringAuditEntity>();
    public DbSet<ProxmoxDestroyAuditEntity> ProxmoxDestroyAudits => Set<ProxmoxDestroyAuditEntity>();
    public DbSet<ProxmoxCreateAuditEntity> ProxmoxCreateAudits => Set<ProxmoxCreateAuditEntity>();
    public DbSet<ProxmoxCloneAuditEntity> ProxmoxCloneAudits => Set<ProxmoxCloneAuditEntity>();
    public DbSet<ProxmoxRestoreAuditEntity> ProxmoxRestoreAudits => Set<ProxmoxRestoreAuditEntity>();
    public DbSet<ProxmoxConfigAuditEntity> ProxmoxConfigAudits => Set<ProxmoxConfigAuditEntity>();
    public DbSet<ProxmoxNodeAlertSettingsEntity> ProxmoxNodeAlertSettings => Set<ProxmoxNodeAlertSettingsEntity>();
    public DbSet<ProxmoxNodeAlertStateEntity> ProxmoxNodeAlertStates => Set<ProxmoxNodeAlertStateEntity>();

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

        // Single-row, app-wide host-terminal master switch (see HostShellSettingsEntity.SingletonId).
        builder.Entity<HostShellSettingsEntity>();

        // V5.7 — single-row, app-wide container-exec master switch (see ContainerExecSettingsEntity.SingletonId).
        builder.Entity<ContainerExecSettingsEntity>();

        // V6.6 — single-row, app-wide LXC-console master switch (see ProxmoxConsoleSettingsEntity.SingletonId).
        builder.Entity<ProxmoxConsoleSettingsEntity>();

        // V6.7.1 — single-row, app-wide Proxmox "Update now" master switch (see ProxmoxUpdateApplySettingsEntity.SingletonId).
        builder.Entity<ProxmoxUpdateApplySettingsEntity>();

        // V6.13 — single-row, app-wide destroy-LXC master switch (see ProxmoxDestroySettingsEntity.SingletonId).
        builder.Entity<ProxmoxDestroySettingsEntity>();

        // V6.13.1 — single-row, app-wide create-LXC master switch (see ProxmoxCreateSettingsEntity.SingletonId).
        builder.Entity<ProxmoxCreateSettingsEntity>();

        // V8.0 — single-row, app-wide clone/snapshot master switch (see ProxmoxCloneSettingsEntity.SingletonId).
        builder.Entity<ProxmoxCloneSettingsEntity>();

        // V8.1 — single-row, app-wide restore-LXC master switch (see ProxmoxRestoreSettingsEntity.SingletonId).
        builder.Entity<ProxmoxRestoreSettingsEntity>();

        // V5.5 — single-row, app-wide image-prune settings (see ImagePruneSettingsEntity.SingletonId).
        builder.Entity<ImagePruneSettingsEntity>();

        // V5.6 — single-row, app-wide health-check tuning (see HealthCheckSettingsEntity.SingletonId).
        builder.Entity<HealthCheckSettingsEntity>();

        // V9.0 — single-row, app-wide MQTT / Home Assistant integration config (see MqttSettingsEntity.SingletonId).
        builder.Entity<MqttSettingsEntity>();

        // V10.0 — single-row, app-wide Apprise notification config (see AppriseSettingsEntity.SingletonId).
        builder.Entity<AppriseSettingsEntity>();

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
            // V7.9 — optional Proxmox host association, mirroring the Docker one.
            e.HasOne(s => s.ProxmoxConnection)
                .WithMany()
                .HasForeignKey(s => s.ProxmoxConnectionId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<HealthCheckEventEntity>(e =>
        {
            // V10.1 — append-only uptime history. The per-(service, target) timeline is read
            // newest-first and over a time window, so a single composite index covers both the
            // metrics roll-up and the paginated history endpoint. The prune scans by timestamp.
            e.HasIndex(ev => new { ev.WebResourceId, ev.Target, ev.TimestampUtc });
            e.HasIndex(ev => ev.TimestampUtc);
            // Cascade with the service — history is meaningless once the service is gone.
            e.HasOne(ev => ev.WebResource)
                .WithMany()
                .HasForeignKey(ev => ev.WebResourceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<StatusPageEntity>(e =>
        {
            // V10.2 — public status pages. The slug is the public lookup key with no user
            // context, so it must be globally unique. Owner cascade removes a user's pages.
            e.HasIndex(p => p.Slug).IsUnique();
            e.HasIndex(p => new { p.UserId, p.Title });
            e.HasOne<UserEntity>()
                .WithMany()
                .HasForeignKey(p => p.UserId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasMany(p => p.Items)
                .WithOne(i => i.StatusPage)
                .HasForeignKey(i => i.StatusPageId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<StatusPageItemEntity>(e =>
        {
            // One row per (page, service); the same service can sit on many pages.
            e.HasIndex(i => new { i.StatusPageId, i.WebResourceId }).IsUnique();
            // Removing the underlying service drops it from every page it was shown on.
            e.HasOne(i => i.WebResource)
                .WithMany()
                .HasForeignKey(i => i.WebResourceId)
                .OnDelete(DeleteBehavior.Cascade);
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
            // V5.4 — aggregate "Update project" rows reference their child
            // service rows via ParentAttemptId. Cascade so removing the
            // parent (e.g. when the connection is deleted) also drops the
            // children — a child row is never useful in isolation.
            e.HasIndex(a => a.ParentAttemptId);
            e.HasOne<DockerUpdateAttemptEntity>()
                .WithMany()
                .HasForeignKey(a => a.ParentAttemptId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<ContainerIconEntity>(e =>
        {
            // V7.8 — one icon override per (user, connection, container). The
            // container name is the stable key (the container id is ephemeral),
            // so a recreate keeps the chosen icon. Unique so the controller can
            // upsert by the triple.
            e.HasIndex(i => new { i.UserId, i.DockerConnectionId, i.ContainerName }).IsUnique();
            // Deleting the connection removes its icon overrides — they're
            // meaningless without the host, mirroring the watch convention.
            e.HasOne<DockerConnectionEntity>()
                .WithMany()
                .HasForeignKey(i => i.DockerConnectionId)
                .OnDelete(DeleteBehavior.Cascade);
            // Owner cascade — deleting a user removes their icon overrides.
            e.HasOne<UserEntity>()
                .WithMany()
                .HasForeignKey(i => i.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<ComposeChangeAuditEntity>(e =>
        {
            // V7.6 — audit log for Compose save / restore / apply changes. Same
            // shape and conventions as the other audit tables: per-user history
            // (newest first applied at query time) and per-connection history.
            e.HasIndex(s => new { s.InitiatedByUserId, s.ChangedUtc });
            e.HasIndex(s => new { s.DockerConnectionId, s.ChangedUtc });
            // Keep the audit row when the connection is deleted — the connection
            // name is denormalised onto the row precisely so the history survives.
            // SetNull mirrors the DockerUpdateAttempt convention.
            e.HasOne<DockerConnectionEntity>()
                .WithMany()
                .HasForeignKey(s => s.DockerConnectionId)
                .OnDelete(DeleteBehavior.SetNull);
            // Owner cascade — deleting a user removes their change history.
            e.HasOne<UserEntity>()
                .WithMany()
                .HasForeignKey(s => s.InitiatedByUserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<DockerPruneRunEntity>(e =>
        {
            // V5.5 — audit log for image-prune runs. The storage widget reads
            // the most recent N rows per connection, newest first, so a single
            // composite index covers both queries.
            e.HasIndex(p => new { p.DockerConnectionId, p.StartedUtc });
            // SetNull mirrors the DockerUpdateAttempt convention: the audit row
            // outlives the connection so the user can still see the history.
            e.HasOne<DockerConnectionEntity>()
                .WithMany()
                .HasForeignKey(p => p.DockerConnectionId)
                .OnDelete(DeleteBehavior.SetNull);
            e.HasOne<UserEntity>()
                .WithMany()
                .HasForeignKey(p => p.InitiatedByUserId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<HostShellSessionEntity>(e =>
        {
            // V5.3 — audit log for browser host-terminal sessions. Index the
            // per-user history (newest first is applied at query time) and the
            // per-connection history for a future per-host activity view.
            e.HasIndex(s => new { s.InitiatedByUserId, s.StartedUtc });
            e.HasIndex(s => new { s.DockerConnectionId, s.StartedUtc });
            // Keep the audit row when the connection is deleted — the host
            // details are denormalised onto the row precisely so the history
            // survives. SetNull mirrors the DockerUpdateAttempt convention.
            e.HasOne<DockerConnectionEntity>()
                .WithMany()
                .HasForeignKey(s => s.DockerConnectionId)
                .OnDelete(DeleteBehavior.SetNull);
            // Owner cascade — deleting a user removes their shell history.
            e.HasOne<UserEntity>()
                .WithMany()
                .HasForeignKey(s => s.InitiatedByUserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<ProxmoxConnectionEntity>(e =>
        {
            // V6.0 — user-scoped, named Proxmox host. (UserId, Name) unique so
            // the list has stable labels, mirroring DockerConnection.
            e.HasIndex(c => new { c.UserId, c.Name }).IsUnique();
            // The scan loads all enabled connections and filters due-ness in
            // memory (same V2.2 schedule model as Docker watches).
            e.HasIndex(c => new { c.UserId, c.Enabled, c.LastCheckedUtc });
            // V6.11 — webhook token is the URL secret for the public update-check
            // webhook endpoint. Unique so a tampered URL can't collide with
            // another host's token; NULL is allowed for the majority of hosts
            // that haven't opted in (both Postgres and SQLite treat multiple
            // NULLs as distinct in a unique index).
            e.HasIndex(c => c.WebhookToken).IsUnique();
            e.HasOne<UserEntity>()
                .WithMany()
                .HasForeignKey(c => c.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<ProxmoxGuestEntity>(e =>
        {
            // V6.0 — auto-discovered node + LXC rows. (connection, vmid) is the
            // natural key the scan upserts against.
            e.HasIndex(g => new { g.ProxmoxConnectionId, g.VmId }).IsUnique();
            // V6.7 — backfill existing rows to "monitoring on" so the toggle is
            // additive and preserves current behaviour.
            e.Property(g => g.MonitoringEnabled).HasDefaultValue(true);
            // Deleting the host removes its discovered guests — they're
            // meaningless without it.
            e.HasOne(g => g.ProxmoxConnection)
                .WithMany()
                .HasForeignKey(g => g.ProxmoxConnectionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<WebResourceProxmoxGuestLinkEntity>(e =>
        {
            // V7.9 — a service↔guest link, keyed by the guest's stable
            // (connection, vmid) natural key. One link per (service, connection, vmid).
            e.HasIndex(l => new { l.WebResourceId, l.ProxmoxConnectionId, l.VmId }).IsUnique();
            // Speeds up resolving every link's guest detail per host.
            e.HasIndex(l => new { l.ProxmoxConnectionId, l.VmId });
            // Deleting the service cascades its links away (the guests live on).
            e.HasOne(l => l.WebResource)
                .WithMany(s => s.ProxmoxGuestLinks)
                .HasForeignKey(l => l.WebResourceId)
                .OnDelete(DeleteBehavior.Cascade);
            // Deleting the Proxmox host removes links pointing at its guests —
            // the link is meaningless without the host, mirroring the guest /
            // icon convention.
            e.HasOne<ProxmoxConnectionEntity>()
                .WithMany()
                .HasForeignKey(l => l.ProxmoxConnectionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<ContainerProxmoxLinkEntity>(e =>
        {
            // V7.9 — one cross-link per (user, connection, container), mirroring
            // ContainerIconEntity. Unique so the controller can upsert by the triple.
            e.HasIndex(l => new { l.UserId, l.DockerConnectionId, l.ContainerName }).IsUnique();
            // Deleting the Docker host removes its container links.
            e.HasOne<DockerConnectionEntity>()
                .WithMany()
                .HasForeignKey(l => l.DockerConnectionId)
                .OnDelete(DeleteBehavior.Cascade);
            // Deleting the Proxmox host removes links pointing at its guests.
            e.HasOne<ProxmoxConnectionEntity>()
                .WithMany()
                .HasForeignKey(l => l.ProxmoxConnectionId)
                .OnDelete(DeleteBehavior.Cascade);
            // Owner cascade — deleting a user removes their container links.
            e.HasOne<UserEntity>()
                .WithMany()
                .HasForeignKey(l => l.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<DockerExecSessionEntity>(e =>
        {
            // V5.7 — audit log for browser container-exec sessions. Same shape
            // and conventions as the V5.3 host-shell audit table.
            e.HasIndex(s => new { s.InitiatedByUserId, s.StartedUtc });
            e.HasIndex(s => new { s.DockerConnectionId, s.StartedUtc });
            // Keep the audit row when the connection is deleted — the container
            // details are denormalised onto the row precisely so the history
            // survives. SetNull mirrors the host-shell convention.
            e.HasOne<DockerConnectionEntity>()
                .WithMany()
                .HasForeignKey(s => s.DockerConnectionId)
                .OnDelete(DeleteBehavior.SetNull);
            // Owner cascade — deleting a user removes their exec history.
            e.HasOne<UserEntity>()
                .WithMany()
                .HasForeignKey(s => s.InitiatedByUserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<ProxmoxGuestIconEntity>(e =>
        {
            // V7.8 — one icon override per (user, connection, vmid). Unique so the
            // controller can upsert by the triple; mirrors ContainerIconEntity.
            e.HasIndex(i => new { i.UserId, i.ProxmoxConnectionId, i.VmId }).IsUnique();
            // Deleting the host removes its icon overrides.
            e.HasOne<ProxmoxConnectionEntity>()
                .WithMany()
                .HasForeignKey(i => i.ProxmoxConnectionId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne<UserEntity>()
                .WithMany()
                .HasForeignKey(i => i.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<ProxmoxConsoleSessionEntity>(e =>
        {
            // V6.6 — audit log for browser LXC-console sessions. Same shape and
            // conventions as the V5.7 container-exec audit table.
            e.HasIndex(s => new { s.InitiatedByUserId, s.StartedUtc });
            e.HasIndex(s => new { s.ProxmoxConnectionId, s.StartedUtc });
            // Keep the audit row when the host is deleted — the host / guest
            // details are denormalised onto the row precisely so the history
            // survives. SetNull mirrors the container-exec convention.
            e.HasOne<ProxmoxConnectionEntity>()
                .WithMany()
                .HasForeignKey(s => s.ProxmoxConnectionId)
                .OnDelete(DeleteBehavior.SetNull);
            // Owner cascade — deleting a user removes their console history.
            e.HasOne<UserEntity>()
                .WithMany()
                .HasForeignKey(s => s.InitiatedByUserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<ProxmoxUpdateSessionEntity>(e =>
        {
            // V6.7.1 — audit log for one-click "Update now" runs. Same shape and
            // conventions as the V6.6 LXC-console audit table.
            e.HasIndex(s => new { s.InitiatedByUserId, s.StartedUtc });
            e.HasIndex(s => new { s.ProxmoxConnectionId, s.StartedUtc });
            // Keep the audit row when the host is deleted — the host / target
            // details are denormalised onto the row precisely so the history
            // survives. SetNull mirrors the console convention.
            e.HasOne<ProxmoxConnectionEntity>()
                .WithMany()
                .HasForeignKey(s => s.ProxmoxConnectionId)
                .OnDelete(DeleteBehavior.SetNull);
            // Owner cascade — deleting a user removes their update history.
            e.HasOne<UserEntity>()
                .WithMany()
                .HasForeignKey(s => s.InitiatedByUserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<ProxmoxMonitoringAuditEntity>(e =>
        {
            // V6.11 — audit log for LXC monitoring changes (toggle / bulk /
            // snooze). Same shape and conventions as the V6.7.1 update-session
            // audit table: per-user history (newest first applied at query time)
            // and per-host history.
            e.HasIndex(s => new { s.InitiatedByUserId, s.ChangedUtc });
            e.HasIndex(s => new { s.ProxmoxConnectionId, s.ChangedUtc });
            // Keep the audit row when the host is deleted — the host / guest
            // details are denormalised onto the row precisely so the history
            // survives. SetNull mirrors the update-session convention.
            e.HasOne<ProxmoxConnectionEntity>()
                .WithMany()
                .HasForeignKey(s => s.ProxmoxConnectionId)
                .OnDelete(DeleteBehavior.SetNull);
            // Owner cascade — deleting a user removes their monitoring history.
            e.HasOne<UserEntity>()
                .WithMany()
                .HasForeignKey(s => s.InitiatedByUserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<ProxmoxDestroyAuditEntity>(e =>
        {
            // V6.13 — audit log for LXC destroys. Same shape and conventions as
            // the V6.11 monitoring audit table: per-user history (newest first
            // applied at query time) and per-host history.
            e.HasIndex(s => new { s.InitiatedByUserId, s.DestroyedUtc });
            e.HasIndex(s => new { s.ProxmoxConnectionId, s.DestroyedUtc });
            // Keep the audit row when the host is deleted — the host / guest
            // details are denormalised onto the row precisely so the history
            // survives. SetNull mirrors the monitoring-audit convention.
            e.HasOne<ProxmoxConnectionEntity>()
                .WithMany()
                .HasForeignKey(s => s.ProxmoxConnectionId)
                .OnDelete(DeleteBehavior.SetNull);
            // Owner cascade — deleting a user removes their destroy history.
            e.HasOne<UserEntity>()
                .WithMany()
                .HasForeignKey(s => s.InitiatedByUserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<ProxmoxCreateAuditEntity>(e =>
        {
            // V6.13.1 — audit log for LXC creates. Same shape and conventions as
            // the V6.13 destroy audit table: per-user history (newest first
            // applied at query time) and per-host history.
            e.HasIndex(s => new { s.InitiatedByUserId, s.CreatedAtUtc });
            e.HasIndex(s => new { s.ProxmoxConnectionId, s.CreatedAtUtc });
            // Keep the audit row when the host is deleted — the host / guest
            // details are denormalised onto the row precisely so the history
            // survives. SetNull mirrors the destroy-audit convention.
            e.HasOne<ProxmoxConnectionEntity>()
                .WithMany()
                .HasForeignKey(s => s.ProxmoxConnectionId)
                .OnDelete(DeleteBehavior.SetNull);
            // Owner cascade — deleting a user removes their create history.
            e.HasOne<UserEntity>()
                .WithMany()
                .HasForeignKey(s => s.InitiatedByUserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<ProxmoxCloneAuditEntity>(e =>
        {
            // V8.0 — audit log for clone + snapshot actions. Same shape and
            // conventions as the V6.13.1 create audit table: per-user history and
            // per-host / per-guest history (newest first applied at query time).
            e.HasIndex(s => new { s.InitiatedByUserId, s.CreatedAtUtc });
            e.HasIndex(s => new { s.ProxmoxConnectionId, s.VmId, s.CreatedAtUtc });
            // Store the action as its readable name rather than an int so the audit
            // history is legible straight from the database.
            e.Property(s => s.Action).HasConversion<string>().HasMaxLength(32);
            // Keep the audit row when the host is deleted — the host details are
            // denormalised onto the row precisely so the history survives. SetNull
            // mirrors the create / destroy-audit convention.
            e.HasOne<ProxmoxConnectionEntity>()
                .WithMany()
                .HasForeignKey(s => s.ProxmoxConnectionId)
                .OnDelete(DeleteBehavior.SetNull);
            // Owner cascade — deleting a user removes their clone/snapshot history.
            e.HasOne<UserEntity>()
                .WithMany()
                .HasForeignKey(s => s.InitiatedByUserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<ProxmoxRestoreAuditEntity>(e =>
        {
            // V8.1 — audit log for LXC restores. Same shape and conventions as the
            // V6.13.1 create audit table: per-user history and per-host history
            // (newest first applied at query time).
            e.HasIndex(s => new { s.InitiatedByUserId, s.CreatedAtUtc });
            e.HasIndex(s => new { s.ProxmoxConnectionId, s.CreatedAtUtc });
            // Keep the audit row when the host is deleted — the host details are
            // denormalised onto the row precisely so the history survives. SetNull
            // mirrors the create / destroy / clone-audit convention.
            e.HasOne<ProxmoxConnectionEntity>()
                .WithMany()
                .HasForeignKey(s => s.ProxmoxConnectionId)
                .OnDelete(DeleteBehavior.SetNull);
            // Owner cascade — deleting a user removes their restore history.
            e.HasOne<UserEntity>()
                .WithMany()
                .HasForeignKey(s => s.InitiatedByUserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<ProxmoxConfigAuditEntity>(e =>
        {
            // V8.5 — audit log for guest config edits (LXC + VM). Same shape and
            // conventions as the V6.13.1 create audit table: per-user history and
            // per-host / per-guest history (newest first applied at query time).
            e.HasIndex(s => new { s.InitiatedByUserId, s.CreatedAtUtc });
            e.HasIndex(s => new { s.ProxmoxConnectionId, s.VmId, s.CreatedAtUtc });
            // Keep the audit row when the host is deleted — the host details are
            // denormalised onto the row precisely so the history survives. SetNull
            // mirrors the create / clone / restore-audit convention.
            e.HasOne<ProxmoxConnectionEntity>()
                .WithMany()
                .HasForeignKey(s => s.ProxmoxConnectionId)
                .OnDelete(DeleteBehavior.SetNull);
            // Owner cascade — deleting a user removes their config-edit history.
            e.HasOne<UserEntity>()
                .WithMany()
                .HasForeignKey(s => s.InitiatedByUserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<ProxmoxNodeAlertSettingsEntity>(e =>
        {
            // V6.8.1 — one node-alert settings row per host (1:1). Unique so the
            // service can upsert by connection id. Deleting the host cascades
            // these preferences away.
            e.HasIndex(s => s.ProxmoxConnectionId).IsUnique();
            e.HasOne(s => s.ProxmoxConnection)
                .WithMany()
                .HasForeignKey(s => s.ProxmoxConnectionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<ProxmoxNodeAlertStateEntity>(e =>
        {
            // V6.8.1 — per-category debounce/throttle state. (connection,
            // category) is the natural key the evaluation loop upserts against,
            // mirroring ProxmoxGuest's (connection, vmid).
            e.HasIndex(s => new { s.ProxmoxConnectionId, s.Category }).IsUnique();
            e.HasOne(s => s.ProxmoxConnection)
                .WithMany()
                .HasForeignKey(s => s.ProxmoxConnectionId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
