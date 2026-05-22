using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Stashboard.Api.Auth;
using Stashboard.Core.Abstractions;

namespace Stashboard.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/backup")]
public class BackupController(IBackupService backup) : ControllerBase
{
    private Guid UserId => User.GetUserId();

    [HttpGet("export")]
    public async Task<IActionResult> Export(CancellationToken сancellationToken)
    {
        var bytes = await backup.ExportAsync(UserId, сancellationToken);
        var fileName = $"stashboard-backup-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json";
        return File(bytes, "application/json", fileName);
    }

    [HttpPost("import")]
    [RequestSizeLimit(16 * 1024 * 1024)]
    public async Task<ActionResult<object>> Import(IFormFile file, CancellationToken сancellationToken)
    {
        if (file is null || file.Length == 0) return BadRequest(new { error = "Empty file" });
        await using var stream = file.OpenReadStream();
        var n = await backup.ImportAsync(UserId, stream, сancellationToken);
        return Ok(new { imported = n });
    }
}
