using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Stashboard.Api.Auth;
using Stashboard.Api.Contracts;
using Stashboard.Api.Controllers;
using Stashboard.Api.Services.StatusPages;
using Stashboard.Core.Entities;
using Stashboard.Core.Enums;
using Stashboard.Tests.Infrastructure;

namespace Stashboard.Tests.Controllers.StatusPages;

/// <summary>
/// V10.2 — the unauthenticated public status endpoint. Asserts the acceptance bar: a published
/// slug returns only the whitelisted display fields (never URLs / credentials / notes / internal
/// names), an unpublished or unknown slug 404s identically, only the page's selected services are
/// shown, and the endpoint is anonymous + rate-limited.
/// </summary>
public class PublicStatusControllerTests : DatabaseTestBase
{
    private const string SecretUrl = "https://secret-internal-host.lan:8443/admin";
    private const string SecretNote = "INTERNAL-RUNBOOK-NOTE";
    private const string InternalName = "prod-jellyfin-01";

    private Guid _userId;
    private DataFactory _factory = default!;
    private PublicStatusController _ctrl = default!;

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        await _dbContext.StatusPageItems.ExecuteDeleteAsync();
        await _dbContext.StatusPages.ExecuteDeleteAsync();
        await _dbContext.HealthCheckEvents.ExecuteDeleteAsync();

        var hasher = new Pbkdf2PasswordHasher();
        _factory = new DataFactory(_dbContext, new NoopEncryption(), hasher, Guid.Empty);
        _userId = (await _factory.UserAsync("owner@x")).Id;
        _ctrl = new PublicStatusController(_dbContext, new PublicStatusPageBuilder(_dbContext))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
    }

    [Fact]
    public async Task Published_ReturnsOnlyWhitelistedDisplayFields()
    {
        var svc = await _factory.ServiceAsync(_userId, InternalName, SecretUrl, notes: SecretNote);
        await AddEventAsync(svc.Id, ServiceStatus.Up, hoursAgo: 2);
        await PublishPageAsync("home", "Home", svc.Id, displayName: "Media Server");

        var result = await _ctrl.Get("home", default);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var body = Assert.IsType<PublicStatusPageResponse>(ok.Value);
        var service = Assert.Single(body.Services);
        Assert.Equal("Media Server", service.Name);   // display-name override, not the internal name
        Assert.Equal("Up", service.Status);
        Assert.NotEmpty(service.History);

        // The serialized payload must never carry any owner-private string.
        var json = JsonSerializer.Serialize(body, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.DoesNotContain(SecretUrl, json);
        Assert.DoesNotContain(SecretNote, json);
        Assert.DoesNotContain(InternalName, json);
        Assert.DoesNotContain(svc.Id.ToString(), json);
    }

    [Fact]
    public async Task FallsBackToServiceName_WhenNoDisplayNameOverride()
    {
        var svc = await _factory.ServiceAsync(_userId, "Vaultwarden", "https://vault.lan");
        await PublishPageAsync("vault", "Vault", svc.Id, displayName: null);

        var result = await _ctrl.Get("vault", default);

        var body = Assert.IsType<PublicStatusPageResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal("Vaultwarden", Assert.Single(body.Services).Name);
    }

    [Fact]
    public async Task UnpublishedPage_Returns404()
    {
        var svc = await _factory.ServiceAsync(_userId, "Svc", "https://svc.lan");
        await PublishPageAsync("draft", "Draft", svc.Id, displayName: null, published: false);

        var result = await _ctrl.Get("draft", default);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task UnknownSlug_Returns404()
    {
        var result = await _ctrl.Get("does-not-exist", default);
        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task InvalidSlug_Returns404()
    {
        var result = await _ctrl.Get("Not A Slug!", default);
        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task OnlyShowsSelectedServices()
    {
        var shown = await _factory.ServiceAsync(_userId, "Shown", "https://a.lan");
        await _factory.ServiceAsync(_userId, "Hidden", "https://b.lan"); // not added to the page
        await PublishPageAsync("page", "Page", shown.Id, displayName: null);

        var result = await _ctrl.Get("page", default);

        var body = Assert.IsType<PublicStatusPageResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal("Shown", Assert.Single(body.Services).Name);
    }

    [Fact]
    public async Task Published_SetsCacheHeader()
    {
        var svc = await _factory.ServiceAsync(_userId, "Svc", "https://svc.lan");
        await PublishPageAsync("cached", "Cached", svc.Id, displayName: null);

        await _ctrl.Get("cached", default);

        Assert.Contains("max-age", _ctrl.Response.Headers.CacheControl.ToString());
    }

    [Fact]
    public void Endpoint_IsAnonymousAndRateLimited()
    {
        var type = typeof(PublicStatusController);
        Assert.NotNull(type.GetCustomAttribute<AllowAnonymousAttribute>());
        var rate = type.GetCustomAttribute<EnableRateLimitingAttribute>();
        Assert.NotNull(rate);
        Assert.Equal("public-status", rate!.PolicyName);
    }

    // ── helpers ──

    private async Task PublishPageAsync(string slug, string title, Guid serviceId, string? displayName, bool published = true)
    {
        var page = new StatusPageEntity
        {
            UserId = _userId,
            Title = title,
            Slug = slug,
            IsPublished = published,
        };
        page.Items.Add(new StatusPageItemEntity { WebResourceId = serviceId, DisplayName = displayName, SortOrder = 0 });
        _dbContext.StatusPages.Add(page);
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();
    }

    private async Task AddEventAsync(Guid serviceId, ServiceStatus status, double hoursAgo)
    {
        _dbContext.HealthCheckEvents.Add(new HealthCheckEventEntity
        {
            WebResourceId = serviceId,
            Target = HealthCheckTarget.Main,
            Status = status,
            ResponseTimeMs = 100,
            TimestampUtc = DateTime.UtcNow.AddHours(-hoursAgo),
        });
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();
    }

    private sealed class NoopEncryption : Stashboard.Core.Abstractions.IEncryptionService
    {
        public string Encrypt(string plain) => plain;
        public string Decrypt(string cipher) => cipher;
    }
}
