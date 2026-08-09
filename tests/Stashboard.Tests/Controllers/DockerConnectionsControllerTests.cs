using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using Stashboard.Api.Auth;
using Stashboard.Api.Controllers;
using Stashboard.Api.Mapping;
using Stashboard.Core.Abstractions;
using Stashboard.Core.Entities;
using Stashboard.Core.Enums;
using Stashboard.Tests.Infrastructure;

namespace Stashboard.Tests.Controllers;

/// <summary>
/// Delete-path tests for <see cref="DockerConnectionsController"/>: a connection
/// still referenced by services is refused with a 409 that names them (V6.15.1 —
/// the old count-only message left the user guessing which service blocked it).
/// </summary>
public class DockerConnectionsControllerTests : DatabaseTestBase
{
    private Guid _userId;
    private DockerConnectionsController _ctrl = default!;

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        var hasher = new Pbkdf2PasswordHasher();
        var factory = new DataFactory(_dbContext, new NoopEncryption(), hasher, Guid.Empty);
        _userId = (await factory.UserAsync("owner@x")).Id;
        _ctrl = new DockerConnectionsController(
            _dbContext,
            new DockerConnectionMapper(new NoopEncryption()),
            new Mock<IDockerHostClient>().Object)
        {
            ControllerContext = BuildContext(_userId),
        };
    }

    [Fact]
    public async Task Delete_ConnectionUsedByServices_Returns409NamingThem()
    {
        var connection = await AddConnectionAsync("DockerOMV");
        await AddServiceAsync("OMV", connection.Id);
        await AddServiceAsync("Jellyfin", connection.Id);

        var result = await _ctrl.Delete(connection.Id, default);

        var conflict = Assert.IsType<ConflictObjectResult>(result);
        var payload = JsonSerializer.Serialize(conflict.Value);
        using var doc = JsonDocument.Parse(payload);
        Assert.Equal(2, doc.RootElement.GetProperty("usageCount").GetInt32());
        var error = doc.RootElement.GetProperty("error").GetString()!;
        Assert.Contains("Jellyfin", error);
        Assert.Contains("OMV", error);
        var services = doc.RootElement.GetProperty("services").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Equal(new[] { "Jellyfin", "OMV" }, services);

        Assert.NotNull(await _dbContext.DockerConnections.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == connection.Id));
    }

    [Fact]
    public async Task Delete_UnusedConnection_RemovesRow()
    {
        var connection = await AddConnectionAsync("unused");

        var result = await _ctrl.Delete(connection.Id, default);

        Assert.IsType<NoContentResult>(result);
        Assert.Null(await _dbContext.DockerConnections.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == connection.Id));
    }

    private async Task<DockerConnectionEntity> AddConnectionAsync(string name)
    {
        var entity = new DockerConnectionEntity
        {
            Id = Guid.NewGuid(),
            UserId = _userId,
            Name = name,
            HostType = DockerHostType.LocalSocket,
            CreatedUtc = DateTime.UtcNow,
        };
        _dbContext.DockerConnections.Add(entity);
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();
        return entity;
    }

    private async Task AddServiceAsync(string name, Guid dockerConnectionId)
    {
        _dbContext.WebResources.Add(new WebResourceEntity
        {
            Id = Guid.NewGuid(),
            UserId = _userId,
            Name = name,
            MainUrl = $"https://{name.ToLowerInvariant()}.local",
            DockerConnectionId = dockerConnectionId,
        });
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();
    }

    private static ControllerContext BuildContext(Guid userId)
    {
        var identity = new ClaimsIdentity(
            new[] { new Claim(StashboardClaims.UserId, userId.ToString()) }, "Test");
        return new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };
    }

    private sealed class NoopEncryption : IEncryptionService
    {
        public string Encrypt(string plain) => plain;
        public string Decrypt(string cipher) => cipher;
    }
}




