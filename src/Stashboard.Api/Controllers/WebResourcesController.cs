using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Stashboard.Api.Auth;
using Stashboard.Api.Contracts;
using Stashboard.Api.Data;
using Stashboard.Api.Mapping;
using Stashboard.Api.Notifications;
using Stashboard.Api.Services.HealthCheckSettings;
using Stashboard.Core.Abstractions;
using Stashboard.Core.Entities;
using Stashboard.Core.Enums;

namespace Stashboard.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/services")]
public class WebResourcesController(
    ApplicationDbContext db,
    IUserService users,
    IEncryptionService encryption,
    IServiceHealthChecker healthChecker,
    IWebResourceMapper mapper,
    IServiceStatusNotificationService statusNotifications,
    IWebHostEnvironment env,
    IFaviconService faviconService,
    IHealthCheckSettingsService healthCheckSettings) : ControllerBase
{
    private Guid UserId => User.GetUserId();

    [HttpGet]
    public async Task<ActionResult<List<WebResourceResponse>>> List(CancellationToken cancellationToken)
    {
        var userId = UserId;
        var services = await db.WebResources.AsNoTracking()
            .Include(s => s.Category)
            .Include(s => s.Credentials)
            .Include(s => s.WebResourceTags).ThenInclude(st => st.Tag)
            .Include(s => s.DockerWatches)
            .Where(s => s.UserId == userId)
            .OrderBy(s => s.Name)
            .ToListAsync(cancellationToken);

        var result = new List<WebResourceResponse>(services.Count);
        foreach (var s in services)
            result.Add(await mapper.MapAsync(s, cancellationToken));
        return Ok(result);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<WebResourceResponse>> Get(Guid id, CancellationToken cancellationToken)
    {
        var s = await LoadOwnedAsync(id, cancellationToken);
        return s is null ? NotFound() : Ok(await mapper.MapAsync(s, cancellationToken));
    }

    [HttpPost]
    public async Task<ActionResult<WebResourceResponse>> Create([FromBody] WebResourceUpsertRequest request, CancellationToken cancellationToken)
    {
        if (!await IsOwnedConnectionOrNullAsync(request.DockerConnectionId, cancellationToken))
            return BadRequest(new { error = "Docker connection does not exist." });

        var entity = new WebResourceEntity { UserId = UserId };
        ApplyScalar(entity, request);
        db.WebResources.Add(entity);
        await db.SaveChangesAsync(cancellationToken);
        await ReplaceCredentialsAsync(entity, request.Credentials, cancellationToken);
        await ReplaceTagsAsync(entity, request.Tags, cancellationToken);
        await StoreFaviconBase64Async(entity, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        var fresh = await LoadOwnedAsync(entity.Id, cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = entity.Id }, await mapper.MapAsync(fresh!, cancellationToken));
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<WebResourceResponse>> Update(Guid id, [FromBody] WebResourceUpsertRequest request, CancellationToken cancellationToken)
    {
        var entity = await LoadOwnedAsync(id, cancellationToken);
        if (entity is null) return NotFound();
        if (!await IsOwnedConnectionOrNullAsync(request.DockerConnectionId, cancellationToken))
            return BadRequest(new { error = "Docker connection does not exist." });
        var isShouldRecheckHealth = entity.MainUrl != request.MainUrl
            || entity.MainUrlHealthCheckEnabled != request.MainUrlHealthCheckEnabled
            || entity.AdditionalUrl != request.AdditionalUrl
            || entity.AdditionalUrlHealthCheckEnabled != request.AdditionalUrlHealthCheckEnabled
            || entity.HealthCheckUrl != request.HealthCheckUrl;
        var isUrlChanged = entity.MainUrl != request.MainUrl || entity.LogoSource != request.LogoSource;
        ApplyScalar(entity, request);
        await ReplaceCredentialsAsync(entity, request.Credentials, cancellationToken);
        await ReplaceTagsAsync(entity, request.Tags, cancellationToken);
        if (isUrlChanged)
            await StoreFaviconBase64Async(entity, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

        if (isShouldRecheckHealth)
            return await CheckNow(id, cancellationToken);

        var fresh = await LoadOwnedAsync(id, cancellationToken);
        return Ok(await mapper.MapAsync(fresh!, cancellationToken));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var entity = await LoadOwnedAsync(id, cancellationToken);
        if (entity is null) return NotFound();
        db.WebResources.Remove(entity);
        await db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    [HttpPost("{id:guid}/check")]
    public async Task<ActionResult<WebResourceResponse>> CheckNow(Guid id, CancellationToken cancellationToken)
    {
        var entity = await LoadOwnedAsync(id, cancellationToken);
        if (entity is null) return NotFound();

        if (ShouldForceDisableOfflineNotifications(entity))
            entity.OfflineNotificationsEnabled = false;

        if (!ShouldRunAnyHealthCheck(entity))
        {
            ClearMainHealthState(entity);
            ClearAdditionalHealthState(entity);
            await db.SaveChangesAsync(cancellationToken);
            return Ok(await mapper.MapAsync(entity, cancellationToken));
        }

        var previousMainStatus = entity.CurrentStatus;
        var previousAdditionalStatus = entity.AdditionalUrlStatus;
        var hcSettings = await healthCheckSettings.GetAsync(cancellationToken);
        var checkResult = await healthChecker.CheckAsync(
            entity, new HealthCheckRetrySettings(hcSettings.RetryCount, hcSettings.RetryDelayMs), cancellationToken);
        entity.CurrentStatus = checkResult.Main.Status;
        entity.LastResponseTimeMs = checkResult.Main.ResponseTimeMs;
        entity.LastError = checkResult.Main.Error;
        entity.LastCheckedUtc = checkResult.Main.Status == Stashboard.Core.Enums.ServiceStatus.Unknown
            && !entity.MainUrlHealthCheckEnabled
            ? null
            : DateTime.UtcNow;

        if (checkResult.Additional is { } additional)
        {
            entity.AdditionalUrlStatus = additional.Status;
            entity.AdditionalUrlLastResponseTimeMs = additional.ResponseTimeMs;
            entity.AdditionalUrlLastError = additional.Error;
        }
        else
        {
            ClearAdditionalHealthState(entity);
        }

        await db.SaveChangesAsync(cancellationToken);

        var user = await users.FindByIdAsync(entity.UserId, cancellationToken);
        if (user is not null)
            await statusNotifications.NotifyIfNeededAsync(user, entity, previousMainStatus, previousAdditionalStatus, cancellationToken);

        return Ok(await mapper.MapAsync(entity, cancellationToken));
    }

    [HttpPost("{id:guid}/logo")]
    [RequestSizeLimit(2 * 1024 * 1024)]
    public async Task<ActionResult<object>> UploadLogo(Guid id, IFormFile file, CancellationToken cancellationToken)
    {
        var entity = await LoadOwnedAsync(id, cancellationToken);
        if (entity is null) return NotFound();
        if (file is null || file.Length == 0) return BadRequest(new { error = "Empty file" });
        if (!file.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { error = "Not an image" });

        var webRoot = env.WebRootPath ?? Path.Combine(env.ContentRootPath, "wwwroot");
        var dir = Path.Combine(webRoot, "uploads", "logos");
        Directory.CreateDirectory(dir);
        var ext = Path.GetExtension(file.FileName);
        var fileName = $"{Guid.NewGuid():N}{ext}";
        var path = Path.Combine(dir, fileName);
        await using (var fs = System.IO.File.Create(path))
            await file.CopyToAsync(fs, cancellationToken);

        // Store the uploaded image as base64 so it can be served without the file system
        using var readStream = new MemoryStream();
        file.OpenReadStream().CopyTo(readStream);
        var uploadedBytes = readStream.ToArray();
        var mime = file.ContentType;
        entity.LogoBase64 = uploadedBytes.Length > 0
            ? $"data:{mime};base64,{Convert.ToBase64String(uploadedBytes)}"
            : null;

        entity.CustomLogoPath = $"/uploads/logos/{fileName}";
        entity.LogoSource = Stashboard.Core.Enums.LogoSource.Custom;
        await db.SaveChangesAsync(cancellationToken);
        return Ok(new { path = entity.CustomLogoPath });
    }

    [HttpPost("{id:guid}/favicon/refresh")]
    public async Task<ActionResult<WebResourceResponse>> RefreshFavicon(Guid id, CancellationToken cancellationToken)
    {
        var entity = await LoadOwnedAsync(id, cancellationToken);
        if (entity is null) return NotFound();

        DeleteCustomLogoFileIfExists(entity.CustomLogoPath);
        entity.CustomLogoPath = null;
        entity.LogoSource = LogoSource.AutoFavicon;
        entity.LogoBase64 = null;
        entity.UpdatedUtc = DateTime.UtcNow;

        faviconService.InvalidateSiteFaviconCache(entity.MainUrl);
        await StoreFaviconBase64Async(entity, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

        var fresh = await LoadOwnedAsync(id, cancellationToken);
        return Ok(await mapper.MapAsync(fresh!, cancellationToken));
    }

    private async Task StoreFaviconBase64Async(WebResourceEntity entity, CancellationToken cancellationToken)
    {
        if (entity.LogoSource == LogoSource.Custom) return;

        var url = await faviconService.ResolveFaviconUrlAsync(entity.MainUrl, cancellationToken);
        if (url is null) return;

        var base64 = await faviconService.DownloadAsBase64Async(url, cancellationToken);
        entity.LogoBase64 = base64;
    }

    private void DeleteCustomLogoFileIfExists(string? customLogoPath)
    {
        if (string.IsNullOrWhiteSpace(customLogoPath))
            return;

        var normalizedPath = customLogoPath.Replace('\\', '/');
        if (!normalizedPath.StartsWith("/uploads/logos/", StringComparison.OrdinalIgnoreCase))
            return;

        var webRoot = env.WebRootPath ?? Path.Combine(env.ContentRootPath, "wwwroot");
        var candidateFilePath = Path.GetFullPath(Path.Combine(webRoot, normalizedPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar)));
        var logosDirectory = Path.GetFullPath(Path.Combine(webRoot, "uploads", "logos"));

        if (!candidateFilePath.StartsWith(logosDirectory, StringComparison.OrdinalIgnoreCase))
            return;

        if (System.IO.File.Exists(candidateFilePath))
            System.IO.File.Delete(candidateFilePath);
    }

    private Task<WebResourceEntity?> LoadOwnedAsync(Guid id, CancellationToken cancellationToken)
    {
        var userId = UserId;
        return db.WebResources
            .Include(s => s.Category)
            .Include(s => s.Credentials)
            .Include(s => s.WebResourceTags).ThenInclude(st => st.Tag)
            .Include(s => s.DockerWatches)
            .FirstOrDefaultAsync(s => s.Id == id && s.UserId == userId, cancellationToken);
    }

    /// <summary>True when the id is null (= unassign) or points at a Docker
    /// connection the current user owns. Refuses cross-tenant assignment.</summary>
    private async Task<bool> IsOwnedConnectionOrNullAsync(Guid? connectionId, CancellationToken cancellationToken)
    {
        if (connectionId is null) return true;
        var userId = UserId;
        return await db.DockerConnections.AsNoTracking()
            .AnyAsync(c => c.Id == connectionId.Value && c.UserId == userId, cancellationToken);
    }

    private void ApplyScalar(WebResourceEntity entity, WebResourceUpsertRequest req)
    {
        entity.Name = req.Name;
        entity.MainUrl = req.MainUrl;
        entity.MainUrlHealthCheckEnabled = req.MainUrlHealthCheckEnabled;
        entity.AdditionalUrl = string.IsNullOrWhiteSpace(req.AdditionalUrl) ? null : req.AdditionalUrl;
        entity.AdditionalUrlHealthCheckEnabled = req.AdditionalUrlHealthCheckEnabled;
        entity.HealthCheckUrl = string.IsNullOrWhiteSpace(req.HealthCheckUrl) ? null : req.HealthCheckUrl;
        entity.HealthCheckMethod = req.HealthCheckMethod;
        entity.ExpectedStatusRange = string.IsNullOrWhiteSpace(req.ExpectedStatusRange) ? null : req.ExpectedStatusRange;
        entity.Notes = req.Notes;
        entity.CategoryId = req.CategoryId;
        entity.DockerConnectionId = req.DockerConnectionId;
        entity.LogoSource = req.LogoSource;
        entity.CustomLogoPath = req.CustomLogoPath;
        entity.OfflineNotificationsEnabled = req.OfflineNotificationsEnabled;
        entity.UpdatedUtc = DateTime.UtcNow;

        if (!entity.MainUrlHealthCheckEnabled)
            ClearMainHealthState(entity);

        if (!entity.AdditionalUrlHealthCheckEnabled || string.IsNullOrWhiteSpace(entity.AdditionalUrl))
            ClearAdditionalHealthState(entity);

        if (ShouldForceDisableOfflineNotifications(entity))
            entity.OfflineNotificationsEnabled = false;
    }

    private static bool ShouldRunAnyHealthCheck(WebResourceEntity entity)
    {
        if (entity.MainUrlHealthCheckEnabled)
            return true;

        return !string.IsNullOrWhiteSpace(entity.AdditionalUrl)
            && entity.AdditionalUrlHealthCheckEnabled;
    }

    private static bool ShouldForceDisableOfflineNotifications(WebResourceEntity entity)
    {
        var isMainDisabled = !entity.MainUrlHealthCheckEnabled;
        var isAdditionalDisabled = !entity.AdditionalUrlHealthCheckEnabled || string.IsNullOrWhiteSpace(entity.AdditionalUrl);
        var hasNoHealthCheckUrl = string.IsNullOrWhiteSpace(entity.HealthCheckUrl);

        return isMainDisabled && isAdditionalDisabled && hasNoHealthCheckUrl;
    }

    private static void ClearMainHealthState(WebResourceEntity entity)
    {
        entity.CurrentStatus = Stashboard.Core.Enums.ServiceStatus.Unknown;
        entity.LastCheckedUtc = null;
        entity.LastResponseTimeMs = null;
        entity.LastError = null;
    }

    private static void ClearAdditionalHealthState(WebResourceEntity entity)
    {
        entity.AdditionalUrlStatus = Stashboard.Core.Enums.ServiceStatus.Unknown;
        entity.AdditionalUrlLastResponseTimeMs = null;
        entity.AdditionalUrlLastError = null;
    }

    private async Task ReplaceCredentialsAsync(WebResourceEntity entity, List<CredentialUpsert> creds, CancellationToken cancellationToken)
    {
        if (entity.Credentials.Count > 0)
            db.Credentials.RemoveRange(entity.Credentials);

        foreach (var c in creds.Where(c => !string.IsNullOrWhiteSpace(c.Key)))
        {
            db.Credentials.Add(new CredentialEntity
            {
                WebResourceId = entity.Id,
                Key = c.Key,
                EncryptedValue = encryption.Encrypt(c.Value ?? string.Empty),
                IsSecret = c.IsSecret,
            });
        }
        await Task.CompletedTask;
    }

    private async Task ReplaceTagsAsync(WebResourceEntity entity, List<string> tagNames, CancellationToken cancellationToken)
    {
        var distinct = tagNames
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Remove ServiceTags whose tag is no longer in the new list.
        var toRemove = entity.WebResourceTags
            .Where(st => !distinct.Contains(st.Tag.Name, StringComparer.OrdinalIgnoreCase))
            .ToList();
        if (toRemove.Count > 0)
            db.WebResourceTags.RemoveRange(toRemove);

        // Add ServiceTags that are new (skip ones that already exist to avoid PK conflicts).
        // Track tag IDs already attached within this call to guard against null Tag navigations
        // on newly-constructed ServiceTag objects that haven't been saved yet.
        var attachedTagIds = entity.WebResourceTags
            .Where(st => st.Tag is not null)
            .Select(st => st.TagId)
            .ToHashSet();

        foreach (var name in distinct)
        {
            if (entity.WebResourceTags.Any(st => st.Tag is not null && string.Equals(st.Tag.Name, name, StringComparison.OrdinalIgnoreCase)))
                continue;

            var tag = await db.Tags.FirstOrDefaultAsync(t => t.UserId == entity.UserId && t.Name == name, cancellationToken);
            if (tag is null)
            {
                tag = new TagEntity { UserId = entity.UserId, Name = name };
                db.Tags.Add(tag);
                await db.SaveChangesAsync(cancellationToken);
            }

            if (attachedTagIds.Add(tag.Id))
                entity.WebResourceTags.Add(new WebResourceTagEntity { TagId = tag.Id });
        }
    }

    }
