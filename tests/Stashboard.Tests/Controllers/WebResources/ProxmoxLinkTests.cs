using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Stashboard.Api.Contracts;
using Stashboard.Core.Entities;
using Stashboard.Core.Enums;

namespace Stashboard.Tests.Controllers.WebResources;

/// <summary>
/// V7.9 — service ↔ Proxmox guest link endpoints on the WebResources controller:
/// owner-scoping, the node-row guard, idempotent link/unlink, and the
/// delete-service-drops-links-but-keeps-guest cascade.
/// </summary>
public class ProxmoxLinkTests : WebResourcesControllerTestBase
{
    private async Task<Guid> SeedServiceAsync(Guid? ownerId = null)
    {
        var svc = new WebResourceEntity
        {
            UserId = ownerId ?? _userId,
            Name = "svc",
            MainUrl = "https://svc.example.com",
            HealthCheckMethod = HealthCheckMethod.Get,
        };
        _dbContext.WebResources.Add(svc);
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();
        return svc.Id;
    }

    private async Task<(Guid ConnectionId, int VmId)> SeedGuestAsync(
        Guid? ownerId = null, int vmId = 101, int? pending = 5, ProxmoxGuestType type = ProxmoxGuestType.Lxc)
    {
        var pve = new ProxmoxConnectionEntity
        {
            UserId = ownerId ?? _userId,
            Name = $"pve-{Guid.NewGuid():N}",
            ApiBaseUrl = "https://pve.lan:8006",
            NodeName = "pve",
            ServerType = ProxmoxServerType.Pve,
            ApiTokenId = "root@pam!stash",
        };
        _dbContext.ProxmoxConnections.Add(pve);
        _dbContext.ProxmoxGuests.Add(new ProxmoxGuestEntity
        {
            ProxmoxConnectionId = pve.Id,
            VmId = vmId,
            GuestType = type,
            Name = $"guest-{vmId}",
            IsRunning = true,
            PendingUpdates = pending,
        });
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();
        return (pve.Id, vmId);
    }

    private static WebResourceResponse Ok(ActionResult<WebResourceResponse> result) =>
        Assert.IsType<WebResourceResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);

    [Fact]
    public async Task Create_ForeignProxmoxHost_BadRequest()
    {
        var (foreignConnId, _) = await SeedGuestAsync(ownerId: _otherUserId);
        var controller = BuildController();

        var request = DefaultRequest() with { ProxmoxConnectionId = foreignConnId };
        var result = await controller.Create(request, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task Create_OwnedProxmoxHost_Assigns()
    {
        var (connId, _) = await SeedGuestAsync();
        var controller = BuildController();

        var request = DefaultRequest() with { ProxmoxConnectionId = connId };
        var result = await controller.Create(request, CancellationToken.None);

        var created = Assert.IsType<WebResourceResponse>(Assert.IsType<CreatedAtActionResult>(result.Result).Value);
        Assert.Equal(connId, created.ProxmoxConnectionId);
    }

    [Fact]
    public async Task AddProxmoxLink_ValidGuest_LinksAndReturnsBadge()
    {
        var serviceId = await SeedServiceAsync();
        var (connId, vmId) = await SeedGuestAsync(pending: 7);
        var controller = BuildController();

        var response = Ok(await controller.AddProxmoxLink(
            serviceId, new ProxmoxGuestLinkRequest(connId, vmId), CancellationToken.None));

        Assert.Equal(ProxmoxUpdateStatus.UpdateAvailable, response.ProxmoxUpdateStatus);
        var guest = Assert.Single(response.LinkedProxmoxGuests!);
        Assert.Equal(vmId, guest.VmId);
        Assert.Equal(7, guest.PendingUpdates);
        Assert.True(await _dbContext.WebResourceProxmoxGuestLinks
            .AnyAsync(l => l.WebResourceId == serviceId && l.ProxmoxConnectionId == connId && l.VmId == vmId));
    }

    [Fact]
    public async Task AddProxmoxLink_IsIdempotent()
    {
        var serviceId = await SeedServiceAsync();
        var (connId, vmId) = await SeedGuestAsync();
        var controller = BuildController();

        await controller.AddProxmoxLink(serviceId, new ProxmoxGuestLinkRequest(connId, vmId), CancellationToken.None);
        await controller.AddProxmoxLink(serviceId, new ProxmoxGuestLinkRequest(connId, vmId), CancellationToken.None);

        Assert.Equal(1, await _dbContext.WebResourceProxmoxGuestLinks.CountAsync(l => l.WebResourceId == serviceId));
    }

    [Fact]
    public async Task AddProxmoxLink_NodeRow_BadRequest()
    {
        var serviceId = await SeedServiceAsync();
        var (connId, _) = await SeedGuestAsync();
        var controller = BuildController();

        var result = await controller.AddProxmoxLink(
            serviceId, new ProxmoxGuestLinkRequest(connId, 0), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task AddProxmoxLink_ForeignConnection_BadRequest()
    {
        var serviceId = await SeedServiceAsync();
        // Guest belongs to the OTHER user's Proxmox host.
        var (connId, vmId) = await SeedGuestAsync(ownerId: _otherUserId);
        var controller = BuildController();

        var result = await controller.AddProxmoxLink(
            serviceId, new ProxmoxGuestLinkRequest(connId, vmId), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.False(await _dbContext.WebResourceProxmoxGuestLinks.AnyAsync(l => l.WebResourceId == serviceId));
    }

    [Fact]
    public async Task AddProxmoxLink_ForeignService_NotFound()
    {
        var serviceId = await SeedServiceAsync(ownerId: _otherUserId);
        var (connId, vmId) = await SeedGuestAsync(ownerId: _otherUserId);
        var controller = BuildController(); // acting as _userId

        var result = await controller.AddProxmoxLink(
            serviceId, new ProxmoxGuestLinkRequest(connId, vmId), CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task RemoveProxmoxLink_RemovesLink()
    {
        var serviceId = await SeedServiceAsync();
        var (connId, vmId) = await SeedGuestAsync();
        var controller = BuildController();
        await controller.AddProxmoxLink(serviceId, new ProxmoxGuestLinkRequest(connId, vmId), CancellationToken.None);

        var response = Ok(await controller.RemoveProxmoxLink(serviceId, connId, vmId, CancellationToken.None));

        Assert.Null(response.ProxmoxUpdateStatus);
        Assert.Empty(response.LinkedProxmoxGuests!);
        Assert.False(await _dbContext.WebResourceProxmoxGuestLinks.AnyAsync(l => l.WebResourceId == serviceId));
    }

    [Fact]
    public async Task DeleteService_DropsLinks_ButLeavesGuest()
    {
        var serviceId = await SeedServiceAsync();
        var (connId, vmId) = await SeedGuestAsync();
        var controller = BuildController();
        await controller.AddProxmoxLink(serviceId, new ProxmoxGuestLinkRequest(connId, vmId), CancellationToken.None);

        await controller.Delete(serviceId, CancellationToken.None);

        Assert.False(await _dbContext.WebResourceProxmoxGuestLinks.AnyAsync(l => l.WebResourceId == serviceId));
        // The guest row itself is untouched — it's owned by the connection, not the service.
        Assert.True(await _dbContext.ProxmoxGuests.AnyAsync(g => g.ProxmoxConnectionId == connId && g.VmId == vmId));
    }
}



