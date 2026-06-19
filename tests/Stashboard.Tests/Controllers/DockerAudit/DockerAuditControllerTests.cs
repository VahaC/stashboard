using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using Stashboard.Api.Auth;
using Stashboard.Api.Contracts;
using Stashboard.Api.Controllers;
using Stashboard.Api.Data;
using Stashboard.Api.Mapping;
using Stashboard.Core.Abstractions;
using Stashboard.Core.Entities;
using Stashboard.Core.Enums;
using Stashboard.Infrastructure.Docker;

namespace Stashboard.Tests.Controllers.DockerAudit;

/// <summary>
/// V5.8 — the read-only session audit viewer endpoints. Confirms newest-first
/// ordering, paging, owner scoping (a foreign user's rows are not returned) and
/// that both open (<c>EndedUtc == null</c>) and finalised sessions surface.
/// </summary>
public class DockerAuditControllerTests : IDisposable
{
    private readonly ApplicationDbContext _db;

    public DockerAuditControllerTests()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite($"DataSource=file:audit-{Guid.NewGuid():N}?mode=memory&cache=shared")
            .Options;
        _db = new ApplicationDbContext(options);
        _db.Database.OpenConnection();
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db.Database.CloseConnection();
        _db.Dispose();
    }

    private DockerAuditController NewController(Guid userId)
    {
        var encryption = new Mock<IEncryptionService>();
        encryption.Setup(e => e.Decrypt(It.IsAny<string>())).Returns<string>(v => v);
        var mapper = new DockerWatchMapper(encryption.Object, new ImageReferenceParser());
        return new DockerAuditController(_db, mapper)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = BuildUser(userId) },
            },
        };
    }

    private static ClaimsPrincipal BuildUser(Guid userId) =>
        new(new ClaimsIdentity(new[] { new Claim(StashboardClaims.UserId, userId.ToString()) }, "test"));

    private static IReadOnlyList<T> Body<T>(ActionResult<IReadOnlyList<T>> result)
    {
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        return Assert.IsAssignableFrom<IReadOnlyList<T>>(ok.Value);
    }

    /// <summary>Seeds the user rows the audit FKs reference (InitiatedByUserId,
    /// DockerConnections.UserId). Idempotent for a given id.</summary>
    private void SeedUsers(params Guid[] userIds)
    {
        foreach (var id in userIds.Distinct())
        {
            _db.Users.Add(new UserEntity
            {
                Id = id,
                Email = $"{id:N}@test.local",
                NormalizedEmail = $"{id:N}@TEST.LOCAL",
                PasswordHash = "x",
                SecurityStamp = Guid.NewGuid().ToString("N"),
                CreatedUtc = DateTime.UtcNow,
            });
        }
    }

    /// <summary>Seeds a Docker connection so a session's DockerConnectionId FK
    /// (and the connection's own UserId FK) resolves.</summary>
    private void SeedConnection(Guid connectionId, Guid userId, string name)
    {
        _db.DockerConnections.Add(new DockerConnectionEntity
        {
            Id = connectionId,
            UserId = userId,
            Name = name,
            HostType = DockerHostType.LocalSocket,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow,
        });
    }

    // ── host-shell sessions ──────────────────────────────────────────────────

    [Fact]
    public async Task HostShell_ReturnsOwnerRows_NewestFirst_AndExcludesForeignAndSurfacesOpen()
    {
        var me = Guid.NewGuid();
        var other = Guid.NewGuid();
        var conn = Guid.NewGuid();
        SeedUsers(me, other);
        SeedConnection(conn, me, "c");

        _db.HostShellSessions.AddRange(
            new HostShellSessionEntity
            {
                Id = Guid.NewGuid(), InitiatedByUserId = me, DockerConnectionId = conn,
                ConnectionName = "old", SshHost = "h", SshUsername = "u",
                StartedUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                EndedUtc = new DateTime(2026, 1, 1, 0, 5, 0, DateTimeKind.Utc),
                EndReason = HostShellSessionEndReason.ClosedByClient,
            },
            new HostShellSessionEntity
            {
                Id = Guid.NewGuid(), InitiatedByUserId = me, DockerConnectionId = conn,
                ConnectionName = "new", SshHost = "h", SshUsername = "u",
                StartedUtc = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc),
                EndedUtc = null, // still open
                EndReason = HostShellSessionEndReason.Active,
            },
            new HostShellSessionEntity
            {
                Id = Guid.NewGuid(), InitiatedByUserId = other, DockerConnectionId = null,
                ConnectionName = "foreign", StartedUtc = DateTime.UtcNow,
                EndReason = HostShellSessionEndReason.Active,
            });
        await _db.SaveChangesAsync();

        var rows = Body(await NewController(me).GetHostShellSessions());

        Assert.Equal(2, rows.Count);                       // foreign excluded
        Assert.Equal("new", rows[0].ConnectionName);       // newest first
        Assert.Equal("old", rows[1].ConnectionName);
        Assert.True(rows[0].Active);                        // open session surfaced
        Assert.False(rows[1].Active);
    }

    [Fact]
    public async Task HostShell_Pages_WithSkipAndTake()
    {
        var me = Guid.NewGuid();
        SeedUsers(me);
        for (var i = 0; i < 5; i++)
        {
            _db.HostShellSessions.Add(new HostShellSessionEntity
            {
                Id = Guid.NewGuid(), InitiatedByUserId = me,
                ConnectionName = $"c{i}",
                StartedUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(i),
                EndReason = HostShellSessionEndReason.ClosedByClient,
            });
        }
        await _db.SaveChangesAsync();

        var page = Body(await NewController(me).GetHostShellSessions(skip: 1, take: 2));

        Assert.Equal(2, page.Count);
        Assert.Equal("c3", page[0].ConnectionName); // newest is c4 (skipped), then c3, c2
        Assert.Equal("c2", page[1].ConnectionName);
    }

    [Fact]
    public async Task HostShell_FiltersByConnectionId()
    {
        var me = Guid.NewGuid();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        SeedUsers(me);
        SeedConnection(a, me, "A");
        SeedConnection(b, me, "B");
        _db.HostShellSessions.AddRange(
            new HostShellSessionEntity { Id = Guid.NewGuid(), InitiatedByUserId = me, DockerConnectionId = a, ConnectionName = "A", StartedUtc = DateTime.UtcNow, EndReason = HostShellSessionEndReason.Active },
            new HostShellSessionEntity { Id = Guid.NewGuid(), InitiatedByUserId = me, DockerConnectionId = b, ConnectionName = "B", StartedUtc = DateTime.UtcNow, EndReason = HostShellSessionEndReason.Active });
        await _db.SaveChangesAsync();

        var rows = Body(await NewController(me).GetHostShellSessions(connectionId: a));

        Assert.Single(rows);
        Assert.Equal("A", rows[0].ConnectionName);
    }

    // ── container-exec sessions ──────────────────────────────────────────────

    [Fact]
    public async Task Exec_ReturnsOwnerRows_NewestFirst_WithOpenAndFinalised()
    {
        var me = Guid.NewGuid();
        var other = Guid.NewGuid();
        SeedUsers(me, other);

        _db.DockerExecSessions.AddRange(
            new DockerExecSessionEntity
            {
                Id = Guid.NewGuid(), InitiatedByUserId = me, ConnectionName = "c", ContainerName = "web",
                Command = "/bin/sh",
                StartedUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                EndedUtc = new DateTime(2026, 1, 1, 0, 1, 0, DateTimeKind.Utc),
                EndReason = HostShellSessionEndReason.RemoteClosed,
            },
            new DockerExecSessionEntity
            {
                Id = Guid.NewGuid(), InitiatedByUserId = me, ConnectionName = "c", ContainerName = "db",
                Command = "/bin/bash",
                StartedUtc = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
                EndedUtc = null,
                EndReason = HostShellSessionEndReason.Active,
            },
            new DockerExecSessionEntity
            {
                Id = Guid.NewGuid(), InitiatedByUserId = other, ConnectionName = "x", ContainerName = "foreign",
                StartedUtc = DateTime.UtcNow, EndReason = HostShellSessionEndReason.Active,
            });
        await _db.SaveChangesAsync();

        var rows = Body(await NewController(me).GetContainerExecSessions());

        Assert.Equal(2, rows.Count);
        Assert.Equal("db", rows[0].ContainerName);   // newest first
        Assert.True(rows[0].Active);
        Assert.Equal("web", rows[1].ContainerName);
        Assert.False(rows[1].Active);
    }

    // ── update attempts ──────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateAttempts_OwnerScoped_NewestFirst()
    {
        var me = Guid.NewGuid();
        var other = Guid.NewGuid();
        SeedUsers(me, other);

        _db.DockerUpdateAttempts.AddRange(
            new DockerUpdateAttemptEntity
            {
                Id = Guid.NewGuid(), InitiatedByUserId = me, ContainerName = "web", ImageReference = "nginx:1",
                Status = DockerUpdateAttemptStatus.Success, ActionType = DockerContainerActionType.Update,
                CompletedUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            },
            new DockerUpdateAttemptEntity
            {
                Id = Guid.NewGuid(), InitiatedByUserId = me, ContainerName = "web", ImageReference = "nginx:2",
                Status = DockerUpdateAttemptStatus.PullFailed, ActionType = DockerContainerActionType.Update,
                CompletedUtc = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc),
            },
            new DockerUpdateAttemptEntity
            {
                Id = Guid.NewGuid(), InitiatedByUserId = other, ContainerName = "foreign", ImageReference = "x:1",
                Status = DockerUpdateAttemptStatus.Success, ActionType = DockerContainerActionType.Update,
                CompletedUtc = DateTime.UtcNow,
            });
        await _db.SaveChangesAsync();

        var rows = Body(await NewController(me).GetUpdateAttempts());

        Assert.Equal(2, rows.Count);
        Assert.Equal("nginx:2", rows[0].ImageReference); // newest first
        Assert.Equal("nginx:1", rows[1].ImageReference);
    }

    // ── prune runs ───────────────────────────────────────────────────────────

    [Fact]
    public async Task PruneRuns_IncludeOwnedConnectionScheduledRuns_AndOwnManualRuns_ExcludeForeign()
    {
        var me = Guid.NewGuid();
        var other = Guid.NewGuid();
        var myConn = Guid.NewGuid();
        var otherConn = Guid.NewGuid();
        SeedUsers(me, other);
        SeedConnection(myConn, me, "mine");
        SeedConnection(otherConn, other, "theirs");

        _db.DockerPruneRuns.AddRange(
            // scheduled run (no user) against MY connection → included
            new DockerPruneRunEntity { Id = Guid.NewGuid(), DockerConnectionId = myConn, InitiatedByUserId = null, Trigger = DockerPruneTrigger.Scheduled, Status = DockerPruneStatus.Success, StartedUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) },
            // manual run by me, connection since deleted (null) → included
            new DockerPruneRunEntity { Id = Guid.NewGuid(), DockerConnectionId = null, InitiatedByUserId = me, Trigger = DockerPruneTrigger.Manual, Status = DockerPruneStatus.Success, StartedUtc = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc) },
            // scheduled run against someone else's connection → excluded
            new DockerPruneRunEntity { Id = Guid.NewGuid(), DockerConnectionId = otherConn, InitiatedByUserId = null, Trigger = DockerPruneTrigger.Scheduled, Status = DockerPruneStatus.Success, StartedUtc = DateTime.UtcNow });
        await _db.SaveChangesAsync();

        var rows = Body(await NewController(me).GetPruneRuns());

        Assert.Equal(2, rows.Count);                          // foreign excluded
        Assert.Equal(DockerPruneTrigger.Manual, rows[0].Trigger);    // 2026-02 newest first
        Assert.Equal(DockerPruneTrigger.Scheduled, rows[1].Trigger); // 2026-01
    }

    [Fact]
    public async Task PruneRuns_ConnectionIdFilter_RejectsForeignConnection()
    {
        var me = Guid.NewGuid();
        var other = Guid.NewGuid();
        var otherConn = Guid.NewGuid();
        SeedUsers(me, other);
        SeedConnection(otherConn, other, "theirs");
        _db.DockerPruneRuns.Add(new DockerPruneRunEntity { Id = Guid.NewGuid(), DockerConnectionId = otherConn, Trigger = DockerPruneTrigger.Scheduled, Status = DockerPruneStatus.Success, StartedUtc = DateTime.UtcNow });
        await _db.SaveChangesAsync();

        var rows = Body(await NewController(me).GetPruneRuns(connectionId: otherConn));

        Assert.Empty(rows); // a connection the caller doesn't own yields nothing
    }

    // ── compose changes (V7.6) ────────────────────────────────────────────────

    [Fact]
    public async Task ComposeChanges_OwnerScoped_NewestFirst_AndSplitsServices()
    {
        var me = Guid.NewGuid();
        var other = Guid.NewGuid();
        SeedUsers(me, other);

        _db.ComposeChangeAudits.AddRange(
            new ComposeChangeAuditEntity
            {
                Id = Guid.NewGuid(), InitiatedByUserId = me, ComposeProject = "media", FileName = "docker-compose.yml",
                ChangeType = ComposeChangeType.Save, ChangedServices = "web,db", Success = true,
                ChangedUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            },
            new ComposeChangeAuditEntity
            {
                Id = Guid.NewGuid(), InitiatedByUserId = me, ComposeProject = "media", FileName = "docker-compose.yml",
                ChangeType = ComposeChangeType.Apply, ChangedServices = "web", Success = false, Error = "port clash",
                ChangedUtc = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc),
            },
            new ComposeChangeAuditEntity
            {
                Id = Guid.NewGuid(), InitiatedByUserId = other, ComposeProject = "foreign",
                ChangeType = ComposeChangeType.Save, Success = true, ChangedUtc = DateTime.UtcNow,
            });
        await _db.SaveChangesAsync();

        var rows = Body(await NewController(me).GetComposeChanges());

        Assert.Equal(2, rows.Count); // foreign excluded
        Assert.Equal(ComposeChangeType.Apply, rows[0].ChangeType); // 2026-02 newest first
        Assert.False(rows[0].Success);
        Assert.Equal(new[] { "web" }, rows[0].ChangedServices);
        Assert.Equal(new[] { "web", "db" }, rows[1].ChangedServices);
    }
}
