using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Stashboard.Api.Auth;
using Stashboard.Api.Contracts;
using Stashboard.Api.Data;
using Stashboard.Api.Mapping;
using Stashboard.Core.Abstractions;
using Stashboard.Core.Entities;
using Stashboard.Core.Enums;

namespace Stashboard.Api.Controllers;

/// <summary>
/// V7.0/V7.1 — visual Compose viewer + editor. Projects are discovered per
/// connection from the containers' standard <c>com.docker.compose.project</c>
/// labels; each project's directory comes from its
/// <c>…project.working_dir</c> label, translated through the connection's
/// optional path mapping (LocalSocket) or used as-is on the host (Ssh).
/// Replaces the V7.0 single <c>ComposeProjectPath</c>-per-connection model,
/// which broke on hosts running more than one Compose project.
/// Path: <c>/api/docker/connections/{connectionId}/compose</c>.
/// </summary>
[ApiController]
[Authorize]
[Route("api/docker/connections/{connectionId:guid}/compose")]
public class ComposeProjectsController(
    ApplicationDbContext db,
    IDockerConnectionMapper mapper,
    IDockerHostClient hostClient,
    IComposeProjectReader reader,
    IComposeFileParser parser,
    IComposeLinter linter,
    IComposeFileEditor editor,
    IComposeProjectWriter writer,
    IComposeHistoryStore historyStore,
    IComposeCommandRunner composeCommandRunner,
    IImageReferenceParser imageReferenceParser,
    IRegistryClient registryClient,
    IMemoryCache cache) : ControllerBase
{
    private Guid UserId => User.GetUserId();

    /// <summary>V7.2 — how long a host's reserved-capacity snapshot is reused
    /// before the next allocation request re-inspects the running containers.</summary>
    private static readonly TimeSpan AllocationCacheTtl = TimeSpan.FromSeconds(60);

    /// <summary>V7.1 — lists the Compose projects discovered on this
    /// connection from the running/stopped containers' labels.</summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ComposeDiscoveredProjectResponse>>> ListProjects(
        Guid connectionId, CancellationToken cancellationToken)
    {
        var connection = await LoadOwnedConnectionAsync(connectionId, cancellationToken);
        if (connection is null) return NotFound();

        var discovered = await DiscoverProjectsAsync(connection, cancellationToken);
        if (discovered.Error is not null)
            return StatusCode(StatusCodes.Status502BadGateway, new { error = discovered.Error });

        return Ok(discovered.Projects.Select(p => p.Response).ToList());
    }

    /// <summary>V7.0 — parses the project's Compose file into the typed viewer
    /// subset (project-scoped since V7.1).</summary>
    [HttpGet("{project}")]
    public async Task<ActionResult<ComposeProjectResponse>> GetProject(
        Guid connectionId, string project, CancellationToken cancellationToken)
    {
        var connection = await LoadOwnedConnectionAsync(connectionId, cancellationToken);
        if (connection is null) return NotFound();

        var located = await LocateProjectAsync(connection, project, cancellationToken);
        if (located.Failure is not null) return located.Failure;

        var read = await ReadProjectFileAsync(connection, located.Path!, cancellationToken);
        if (read.Failure is not null) return read.Failure;

        var parsed = parser.Parse(read.Result!.Content!);
        if (parsed.Project is null)
            return UnprocessableEntity(new { error = parsed.Error });

        return Ok(BuildProjectResponse(parsed.Project, read.Result.FileName!, located.Path!, read.Result.Content!));
    }

    /// <summary>
    /// V7.1 — edits one service's basic fields. The desired state is diffed
    /// against the file per key (untouched fields are a guaranteed zero-diff),
    /// the result is validated by <c>docker compose config -q</c> and renamed
    /// over the original atomically. 409 when the file uses constructs the
    /// editor can't round-trip; 422 with the raw stderr when validation fails.
    /// </summary>
    [HttpPut("{project}/services/{serviceName}")]
    public async Task<ActionResult<ComposeServiceEditResponse>> EditService(
        Guid connectionId, string project, string serviceName,
        [FromBody] ComposeServiceEditRequest request, CancellationToken cancellationToken)
    {
        var connection = await LoadOwnedConnectionAsync(connectionId, cancellationToken);
        if (connection is null) return NotFound();

        var located = await LocateProjectAsync(connection, project, cancellationToken);
        if (located.Failure is not null) return located.Failure;

        var read = await ReadProjectFileAsync(connection, located.Path!, cancellationToken);
        if (read.Failure is not null) return read.Failure;
        var fileName = read.Result!.FileName!;
        var originalContent = read.Result.Content!;

        // The V7.0 banner contract: a file using x-* / extends / merge keys is
        // read-only until round-trip support lands — block the write up front
        // instead of silently dropping the merged values.
        var parsed = parser.Parse(originalContent);
        if (parsed.Project is null)
            return UnprocessableEntity(new { error = parsed.Error });
        if (parsed.Project.UnsupportedFeatures.Count > 0)
            return Conflict(new
            {
                error = "This Compose file is read-only: it uses "
                    + string.Join(", ", parsed.Project.UnsupportedFeatures) + ".",
            });

        var edit = BuildServiceEdit(
            request.Image, request.Restart, request.Ports, request.Volumes,
            request.Environment, request.Labels, request.Command, request.Entrypoint,
            request.User, request.WorkingDir, request.Resources);

        var edited = editor.ApplyServiceEdit(originalContent, serviceName, edit);
        if (edited.Error is not null)
            return UnprocessableEntity(new { error = edited.Error });

        if (edited.Changed)
        {
            var failure = await WriteComposeFileAsync(connection, located.Path!, fileName, edited.Content!, cancellationToken);
            if (failure is not null) return failure;
        }

        // Re-parse what was saved so the UI refreshes from the same source of
        // truth the file now holds.
        var reparsed = parser.Parse(edited.Content!);
        if (reparsed.Project is null)
            return UnprocessableEntity(new { error = reparsed.Error });

        return Ok(new ComposeServiceEditResponse(
            edited.Changed,
            BuildProjectResponse(reparsed.Project, fileName, located.Path!, edited.Content!)));
    }

    /// <summary>
    /// V7.4 — appends a brand-new service to the project's Compose file
    /// (the V7.4 "Add service" wizard). Same atomic, <c>docker compose config -q</c>
    /// validated, comment-preserving save path as <see cref="EditService"/>:
    /// 409 when the file uses read-only constructs, 422 when the name is
    /// malformed / already taken / the image is missing or validation fails.
    /// </summary>
    [HttpPost("{project}/services")]
    public async Task<ActionResult<ComposeServiceEditResponse>> CreateService(
        Guid connectionId, string project,
        [FromBody] ComposeServiceCreateRequest request, CancellationToken cancellationToken)
    {
        var connection = await LoadOwnedConnectionAsync(connectionId, cancellationToken);
        if (connection is null) return NotFound();

        var located = await LocateProjectAsync(connection, project, cancellationToken);
        if (located.Failure is not null) return located.Failure;

        var read = await ReadProjectFileAsync(connection, located.Path!, cancellationToken);
        if (read.Failure is not null) return read.Failure;
        var fileName = read.Result!.FileName!;
        var originalContent = read.Result.Content!;

        var parsed = parser.Parse(originalContent);
        if (parsed.Project is null)
            return UnprocessableEntity(new { error = parsed.Error });
        if (parsed.Project.UnsupportedFeatures.Count > 0)
            return Conflict(new
            {
                error = "This Compose file is read-only: it uses "
                    + string.Join(", ", parsed.Project.UnsupportedFeatures) + ".",
            });

        var edit = BuildServiceEdit(
            request.Image, request.Restart, request.Ports, request.Volumes,
            request.Environment, request.Labels, request.Command, request.Entrypoint,
            request.User, request.WorkingDir, request.Resources);

        var added = editor.AddService(originalContent, request.Name?.Trim() ?? "", edit);
        if (added.Error is not null)
            return UnprocessableEntity(new { error = added.Error });

        if (added.Changed)
        {
            var failure = await WriteComposeFileAsync(connection, located.Path!, fileName, added.Content!, cancellationToken);
            if (failure is not null) return failure;
        }

        var reparsed = parser.Parse(added.Content!);
        if (reparsed.Project is null)
            return UnprocessableEntity(new { error = reparsed.Error });

        return Ok(new ComposeServiceEditResponse(
            added.Changed,
            BuildProjectResponse(reparsed.Project, fileName, located.Path!, added.Content!)));
    }

    /// <summary>V7.4 — the project's Compose file as raw text for the "Raw YAML"
    /// tab.</summary>
    [HttpGet("{project}/file")]
    public async Task<ActionResult<ComposeFileResponse>> GetFile(
        Guid connectionId, string project, CancellationToken cancellationToken)
    {
        var connection = await LoadOwnedConnectionAsync(connectionId, cancellationToken);
        if (connection is null) return NotFound();

        var located = await LocateProjectAsync(connection, project, cancellationToken);
        if (located.Failure is not null) return located.Failure;

        var read = await ReadProjectFileAsync(connection, located.Path!, cancellationToken);
        if (read.Failure is not null) return read.Failure;

        return Ok(new ComposeFileResponse(read.Result!.FileName!, located.Path!, read.Result.Content!));
    }

    /// <summary>
    /// V7.4 — replaces the whole Compose file with hand-edited text (the "Raw
    /// YAML" tab). Validated by <c>docker compose config -q</c> and renamed over
    /// the original atomically — the same writer the field editor uses. 422 with
    /// the raw stderr when validation fails; <c>changed: false</c> when the text
    /// already matched the file on disk.
    /// </summary>
    [HttpPut("{project}/file")]
    public async Task<ActionResult<ComposeFileSaveResponse>> SaveFile(
        Guid connectionId, string project,
        [FromBody] ComposeFileSaveRequest request, CancellationToken cancellationToken)
    {
        var connection = await LoadOwnedConnectionAsync(connectionId, cancellationToken);
        if (connection is null) return NotFound();

        var located = await LocateProjectAsync(connection, project, cancellationToken);
        if (located.Failure is not null) return located.Failure;

        var read = await ReadProjectFileAsync(connection, located.Path!, cancellationToken);
        if (read.Failure is not null) return read.Failure;
        var fileName = read.Result!.FileName!;

        var newContent = request.Content ?? "";
        var changed = !string.Equals(read.Result.Content, newContent, StringComparison.Ordinal);
        if (changed)
        {
            var failure = await WriteComposeFileAsync(connection, located.Path!, fileName, newContent, cancellationToken);
            if (failure is not null) return failure;

            // V7.6 — metadata-only audit row for the save (the file content itself
            // is kept in the on-disk history the writer just snapshotted).
            await WriteChangeAuditAsync(connection, project, fileName, ComposeChangeType.Save,
                ComputeChangedServices(read.Result.Content!, newContent).Changed, true, null, cancellationToken);
        }

        return Ok(new ComposeFileSaveResponse(changed));
    }

    /// <summary>
    /// V7.6 — pre-save diff + dry-run: returns a line diff between the on-disk
    /// file and the proposed text, the <c>docker compose config -q</c> verdict
    /// (without ever touching the original), and which services the change
    /// touches — so the user can review before confirming a write. Nothing is
    /// written. 400 for TcpTls; 404 when the project / file is gone.
    /// </summary>
    [HttpPost("{project}/file/diff")]
    public async Task<ActionResult<ComposeFileDiffResponse>> DiffFile(
        Guid connectionId, string project,
        [FromBody] ComposeFileDiffRequest request, CancellationToken cancellationToken)
    {
        var connection = await LoadOwnedConnectionAsync(connectionId, cancellationToken);
        if (connection is null) return NotFound();

        var located = await LocateProjectAsync(connection, project, cancellationToken);
        if (located.Failure is not null) return located.Failure;

        var read = await ReadProjectFileAsync(connection, located.Path!, cancellationToken);
        if (read.Failure is not null) return read.Failure;
        var fileName = read.Result!.FileName!;
        var oldContent = read.Result.Content!;
        var newContent = request.Content ?? "";

        var diff = ComposeTextDiff.Compute(oldContent, newContent)
            .Select(l => new ComposeDiffLineDto(l.Type, l.Text, l.OldLine, l.NewLine))
            .ToList();

        var validation = await ValidateComposeFileAsync(connection, located.Path!, fileName, newContent, cancellationToken);
        var cliAvailable = validation.Status != ComposeWriteStatus.CliNotAvailable;

        var (changed, removed) = ComputeChangedServices(oldContent, newContent);

        return Ok(new ComposeFileDiffResponse(
            FileName: fileName,
            ProjectPath: located.Path!,
            Unchanged: !ComposeTextDiff.HasChanges(oldContent, newContent),
            Diff: diff,
            Valid: validation.IsSuccess,
            ValidationError: validation.IsSuccess ? null : validation.Error,
            CliAvailable: cliAvailable,
            ChangedServices: changed,
            RemovedServices: removed));
    }

    /// <summary>
    /// V7.6 — "Apply now": recreate the changed services so the just-saved file
    /// takes effect on the running containers. Runs <c>docker compose up -d
    /// &lt;services…&gt;</c> (no <c>pull</c> — this applies a config change, not an
    /// image update) against only the changed set, leaving the rest untouched. An
    /// empty service list applies the whole project. LocalSocket via the
    /// in-container CLI, Ssh on the remote host. 400 for TcpTls; 502 when the run
    /// fails. Always writes an audit row.
    /// </summary>
    [HttpPost("{project}/apply")]
    public async Task<ActionResult<ComposeApplyResponse>> ApplyProject(
        Guid connectionId, string project,
        [FromBody] ComposeApplyRequest request, CancellationToken cancellationToken)
    {
        var connection = await LoadOwnedConnectionAsync(connectionId, cancellationToken);
        if (connection is null) return NotFound();

        var located = await LocateProjectAsync(connection, project, cancellationToken);
        if (located.Failure is not null) return located.Failure;

        DockerSshCredentials? ssh = null;
        if (connection.HostType == DockerHostType.Ssh)
        {
            ssh = mapper.BuildTransport(connection).Ssh;
            if (ssh is null)
                return BadRequest(new { error = "SSH credentials are not fully configured for this connection." });
        }

        var services = (request.Services ?? [])
            .Select(s => s?.Trim() ?? "")
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        ComposeRunResult run;
        try
        {
            run = await composeCommandRunner.UpProjectAsync(
                new ComposeUpRequest(located.Path!, ssh, services.Count > 0 ? services : null), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await WriteChangeAuditAsync(connection, project, null,
                ComposeChangeType.Apply, services, false, ex.Message, cancellationToken);
            return StatusCode(StatusCodes.Status502BadGateway,
                new ComposeApplyResponse(false, services, null, $"docker compose up failed: {ex.Message}"));
        }

        await WriteChangeAuditAsync(connection, project, null, ComposeChangeType.Apply,
            services, run.IsSuccess, run.IsSuccess ? null : run.Error, cancellationToken);

        if (run.IsSuccess)
            return Ok(new ComposeApplyResponse(true, services, run.Output, null));

        return StatusCode(StatusCodes.Status502BadGateway,
            new ComposeApplyResponse(false, services, run.Output, run.Error));
    }

    /// <summary>V7.6 — lists the kept revisions of the project's Compose file
    /// (<c>&lt;project&gt;/.stashboard/history/</c>), newest first, for the History
    /// tab's Restore list. 400 for TcpTls; 502 on SSH failure.</summary>
    [HttpGet("{project}/history")]
    public async Task<ActionResult<IReadOnlyList<ComposeHistoryEntryResponse>>> GetHistory(
        Guid connectionId, string project, CancellationToken cancellationToken)
    {
        var connection = await LoadOwnedConnectionAsync(connectionId, cancellationToken);
        if (connection is null) return NotFound();

        var located = await LocateProjectAsync(connection, project, cancellationToken);
        if (located.Failure is not null) return located.Failure;

        var read = await ReadProjectFileAsync(connection, located.Path!, cancellationToken);
        if (read.Failure is not null) return read.Failure;
        var fileName = read.Result!.FileName!;

        var list = connection.HostType == DockerHostType.Ssh
            ? await historyStore.ListOverSshAsync(mapper.BuildTransport(connection).Ssh!, located.Path!, fileName, cancellationToken)
            : await historyStore.ListAsync(located.Path!, fileName, cancellationToken);

        if (list.Status == ComposeHistoryStatus.SshFailed)
            return StatusCode(StatusCodes.Status502BadGateway, new { error = list.Error });

        return Ok(list.Entries.Select(e => new ComposeHistoryEntryResponse(e.Id, e.SavedUtc, e.SizeBytes)).ToList());
    }

    /// <summary>V7.6 — the raw text of one kept revision, for diffing a revision
    /// against the current file before restoring it.</summary>
    [HttpGet("{project}/history/{id}")]
    public async Task<ActionResult<ComposeHistoryFileResponse>> GetHistoryFile(
        Guid connectionId, string project, string id, CancellationToken cancellationToken)
    {
        var connection = await LoadOwnedConnectionAsync(connectionId, cancellationToken);
        if (connection is null) return NotFound();

        var located = await LocateProjectAsync(connection, project, cancellationToken);
        if (located.Failure is not null) return located.Failure;

        var revision = connection.HostType == DockerHostType.Ssh
            ? await historyStore.ReadOverSshAsync(mapper.BuildTransport(connection).Ssh!, located.Path!, id, cancellationToken)
            : await historyStore.ReadAsync(located.Path!, id, cancellationToken);

        return revision.Status switch
        {
            ComposeHistoryStatus.Ok => Ok(new ComposeHistoryFileResponse(id, revision.Content!)),
            ComposeHistoryStatus.SshFailed => StatusCode(StatusCodes.Status502BadGateway, new { error = revision.Error }),
            _ => NotFound(new { error = revision.Error }),
        };
    }

    /// <summary>
    /// V7.6 — restores a kept revision: reads it back, re-validates it and writes
    /// it over the current file (snapshotting the current file into history first,
    /// so the restore is itself undoable). 404 when the revision is gone; 422 when
    /// the revision no longer validates. Writes an audit row.
    /// </summary>
    [HttpPost("{project}/history/{id}/restore")]
    public async Task<ActionResult<ComposeRestoreResponse>> RestoreHistory(
        Guid connectionId, string project, string id, CancellationToken cancellationToken)
    {
        var connection = await LoadOwnedConnectionAsync(connectionId, cancellationToken);
        if (connection is null) return NotFound();

        var located = await LocateProjectAsync(connection, project, cancellationToken);
        if (located.Failure is not null) return located.Failure;

        var read = await ReadProjectFileAsync(connection, located.Path!, cancellationToken);
        if (read.Failure is not null) return read.Failure;
        var fileName = read.Result!.FileName!;
        var currentContent = read.Result.Content!;

        var revision = connection.HostType == DockerHostType.Ssh
            ? await historyStore.ReadOverSshAsync(mapper.BuildTransport(connection).Ssh!, located.Path!, id, cancellationToken)
            : await historyStore.ReadAsync(located.Path!, id, cancellationToken);
        if (revision.Status == ComposeHistoryStatus.SshFailed)
            return StatusCode(StatusCodes.Status502BadGateway, new { error = revision.Error });
        if (revision.Status != ComposeHistoryStatus.Ok)
            return NotFound(new { error = revision.Error });

        var restoredContent = revision.Content!;
        var changed = !string.Equals(currentContent, restoredContent, StringComparison.Ordinal);
        if (changed)
        {
            var failure = await WriteComposeFileAsync(connection, located.Path!, fileName, restoredContent, cancellationToken);
            if (failure is not null) return failure;

            await WriteChangeAuditAsync(connection, project, fileName, ComposeChangeType.Restore,
                ComputeChangedServices(currentContent, restoredContent).Changed, true, null, cancellationToken);
        }

        var reparsed = parser.Parse(restoredContent);
        if (reparsed.Project is null)
            return UnprocessableEntity(new { error = reparsed.Error });

        return Ok(new ComposeRestoreResponse(changed, BuildProjectResponse(reparsed.Project, fileName, located.Path!, restoredContent)));
    }

    /// <summary>
    /// V7.4 — the "Save and run" CTA: runs <c>docker compose up -d</c> against
    /// the whole project so a freshly-added service is created/started without
    /// disturbing the already-running siblings. LocalSocket uses the Compose CLI
    /// inside the Stashboard container; Ssh runs it on the remote host over the
    /// connection's SSH credentials. 400 for TcpTls (no project files); 502 when
    /// the run fails.
    /// </summary>
    [HttpPost("{project}/up")]
    public async Task<ActionResult<ComposeUpResponse>> UpProject(
        Guid connectionId, string project, CancellationToken cancellationToken)
    {
        var connection = await LoadOwnedConnectionAsync(connectionId, cancellationToken);
        if (connection is null) return NotFound();

        var located = await LocateProjectAsync(connection, project, cancellationToken);
        if (located.Failure is not null) return located.Failure;

        DockerSshCredentials? ssh = null;
        if (connection.HostType == DockerHostType.Ssh)
        {
            ssh = mapper.BuildTransport(connection).Ssh;
            if (ssh is null)
                return BadRequest(new { error = "SSH credentials are not fully configured for this connection." });
        }

        ComposeRunResult run;
        try
        {
            run = await composeCommandRunner.UpProjectAsync(new ComposeUpRequest(located.Path!, ssh), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return StatusCode(StatusCodes.Status502BadGateway,
                new ComposeUpResponse(false, null, $"docker compose up failed: {ex.Message}"));
        }

        if (run.IsSuccess)
            return Ok(new ComposeUpResponse(true, run.Output, null));

        return StatusCode(StatusCodes.Status502BadGateway,
            new ComposeUpResponse(false, run.Output, run.Error));
    }

    /// <summary>
    /// V7.4.1 — bootstraps a brand-new Compose project: writes a fresh file holding
    /// a top-level <c>name:</c> + one service into the requested directory
    /// (created when asked), then optionally runs <c>docker compose up -d</c>. The
    /// guard refuses to clobber an existing Compose file in that directory (open
    /// it and use Add service instead). 400 for TCP+TLS; 422 on validation /
    /// malformed input; 409 when a project already lives there.
    /// </summary>
    [HttpPost("create-project")]
    public async Task<ActionResult<ComposeProjectCreateResponse>> CreateProject(
        Guid connectionId, [FromBody] ComposeProjectCreateRequest request, CancellationToken cancellationToken)
    {
        var connection = await LoadOwnedConnectionAsync(connectionId, cancellationToken);
        if (connection is null) return NotFound();
        if (connection.HostType == DockerHostType.TcpTls)
            return BadRequest(new
            {
                error = "Creating a Compose project is not available for TCP+TLS connections — the daemon API exposes no host filesystem.",
            });

        var directory = request.Directory?.Trim() ?? "";
        if (directory.Length == 0)
            return BadRequest(new { error = "A project directory is required." });

        var fileName = string.IsNullOrWhiteSpace(request.FileName) ? "docker-compose.yml" : request.FileName.Trim();
        if (fileName.Contains('/') || fileName.Contains('\\') || fileName is "." or "..")
            return BadRequest(new { error = "File name must be a bare file name (no path separators)." });

        var services = request.Services;
        if (services is null || services.Count == 0)
            return BadRequest(new { error = "At least one service is required." });

        // The first service seeds the file (top-level name: + services:); each
        // remaining service is appended through the same comment-preserving editor
        // the "Add service" flow uses, so multi-service templates land in one file.
        var first = services[0];
        var built = editor.CreateFile(
            request.ProjectName?.Trim() ?? "", first.Name?.Trim() ?? "", BuildServiceEditFrom(first));
        if (built.Error is not null)
            return UnprocessableEntity(new { error = built.Error });

        for (var i = 1; i < services.Count; i++)
        {
            var svc = services[i];
            built = editor.AddService(built.Content!, svc.Name?.Trim() ?? "", BuildServiceEditFrom(svc));
            if (built.Error is not null)
                return UnprocessableEntity(new { error = built.Error });
        }
        var created = built;

        // Guard: never overwrite an existing project. Probe the directory first.
        DockerSshCredentials? ssh = null;
        ComposeProjectReadResult existing;
        if (connection.HostType == DockerHostType.Ssh)
        {
            ssh = mapper.BuildTransport(connection).Ssh;
            if (ssh is null)
                return BadRequest(new { error = "SSH credentials are not fully configured for this connection." });
            existing = await reader.ReadOverSshAsync(ssh, directory, cancellationToken);
        }
        else
        {
            existing = await reader.ReadAsync(directory, cancellationToken);
        }

        if (existing.Status == ComposeProjectReadStatus.Ok)
            return Conflict(new
            {
                error = $"A Compose file ('{existing.FileName}') already exists in '{directory}'. "
                    + "Open it and use Add service instead.",
            });
        if (existing.Status == ComposeProjectReadStatus.SshFailed)
            return StatusCode(StatusCodes.Status502BadGateway, new { error = existing.Error });
        if (existing.Status == ComposeProjectReadStatus.DirectoryNotFound && !request.CreateDirectory)
            return BadRequest(new
            {
                error = $"Directory '{directory}' was not found. Enable 'create directory' to make it.",
            });

        var write = connection.HostType == DockerHostType.Ssh
            ? await writer.WriteOverSshAsync(ssh!, directory, fileName, created.Content!, request.CreateDirectory, cancellationToken)
            : await writer.WriteAsync(directory, fileName, created.Content!, request.CreateDirectory, cancellationToken);
        if (MapWriteFailure(write) is { } failure)
            return failure;

        var started = false;
        string? startError = null;
        if (request.Run)
        {
            try
            {
                var run = await composeCommandRunner.UpProjectAsync(new ComposeUpRequest(directory, ssh), cancellationToken);
                started = run.IsSuccess;
                startError = started ? null : run.Error;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                startError = $"docker compose up failed: {ex.Message}";
            }
        }

        return Ok(new ComposeProjectCreateResponse(
            request.ProjectName!.Trim(), fileName, directory, started, startError));
    }

    /// <summary>V7.4 — performs the atomic, validated write in whichever
    /// transport the connection uses, returning <c>null</c> on success or a
    /// populated error <see cref="ActionResult"/> mirroring
    /// <see cref="EditService"/>'s status mapping.</summary>
    private async Task<ActionResult?> WriteComposeFileAsync(
        DockerConnectionEntity connection, string path, string fileName, string content,
        CancellationToken cancellationToken)
    {
        var write = connection.HostType == DockerHostType.Ssh
            ? await writer.WriteOverSshAsync(
                mapper.BuildTransport(connection).Ssh!, path, fileName, content, cancellationToken)
            : await writer.WriteAsync(path, fileName, content, cancellationToken);

        return MapWriteFailure(write);
    }

    /// <summary>V7.6 — runs the dry-run validation in whichever transport the
    /// connection uses (writes a temp candidate, validates, deletes — the original
    /// is never touched).</summary>
    private async Task<ComposeWriteResult> ValidateComposeFileAsync(
        DockerConnectionEntity connection, string path, string fileName, string content,
        CancellationToken cancellationToken) =>
        connection.HostType == DockerHostType.Ssh
            ? await writer.ValidateOverSshAsync(
                mapper.BuildTransport(connection).Ssh!, path, fileName, content, cancellationToken)
            : await writer.ValidateAsync(path, fileName, content, cancellationToken);

    /// <summary>
    /// V7.6 — which services the change touches, comparing the normalised parsed
    /// model of each side. <c>Changed</c> is the set "Apply now" recreates (new +
    /// modified services, present in the proposed file); <c>Removed</c> is the
    /// services dropped from the file (a plain <c>up -d</c> can't stop those). When
    /// either side fails to parse the sets come back empty — the diff + validation
    /// already carry the actionable detail.
    /// </summary>
    private (IReadOnlyList<string> Changed, IReadOnlyList<string> Removed) ComputeChangedServices(
        string oldContent, string newContent)
    {
        var oldParsed = parser.Parse(oldContent).Project;
        var newParsed = parser.Parse(newContent).Project;
        if (oldParsed is null || newParsed is null)
            return ([], []);

        var oldByName = oldParsed.Services.ToDictionary(s => s.Name, ServiceFingerprint, StringComparer.Ordinal);
        var newByName = newParsed.Services.ToDictionary(s => s.Name, ServiceFingerprint, StringComparer.Ordinal);

        var changed = newByName
            .Where(kv => !oldByName.TryGetValue(kv.Key, out var fp) || !string.Equals(fp, kv.Value, StringComparison.Ordinal))
            .Select(kv => kv.Key)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
        var removed = oldByName.Keys
            .Where(n => !newByName.ContainsKey(n))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
        return (changed, removed);
    }

    /// <summary>A deterministic flattening of a service's normalised fields, so two
    /// services compare equal iff their effective definition is the same (records
    /// alone won't deep-compare the list members).</summary>
    private static string ServiceFingerprint(ComposeServiceModel s)
    {
        static string List(IEnumerable<string> xs) => string.Join("", xs);
        static string Env(IEnumerable<ComposeEnvVar> xs) => string.Join("", xs.Select(e => $"{e.Name}={e.Value}"));
        var r = s.Resources;
        return string.Join("", new[]
        {
            s.Image, s.ContainerName, s.Restart, s.Command, s.Entrypoint, s.User, s.WorkingDir,
            List(s.Ports), List(s.Volumes), Env(s.Environment), List(s.EnvFiles),
            List(s.DependsOn), List(s.Networks), Env(s.Labels),
            r.Convention, r.CpuLimit, r.CpuReservation, r.MemLimit, r.MemReservation, r.PidsLimit,
            r.CpuShares?.ToString(), r.OomKillDisable?.ToString(), r.OomScoreAdj?.ToString(), r.ShmSize,
            string.Join("", r.Ulimits.Select(u => $"{u.Name}:{u.Soft}:{u.Hard}")),
        }.Select(v => v ?? ""));
    }

    /// <summary>V7.6 — persists one metadata-only Compose-change audit row. The
    /// connection name is denormalised so the row survives a connection
    /// rename/delete.</summary>
    private async Task WriteChangeAuditAsync(
        DockerConnectionEntity connection, string project, string? fileName,
        ComposeChangeType changeType, IReadOnlyList<string> changedServices,
        bool success, string? error, CancellationToken cancellationToken)
    {
        db.ComposeChangeAudits.Add(new ComposeChangeAuditEntity
        {
            DockerConnectionId = connection.Id,
            ConnectionName = connection.Name,
            ComposeProject = project,
            FileName = fileName,
            ChangeType = changeType,
            ChangedServices = changedServices.Count > 0 ? string.Join(",", changedServices) : null,
            Success = success,
            Error = error,
            InitiatedByUserId = UserId,
            ChangedUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>V7.4/V7.4.1 — maps a failed write onto the HTTP status the editor
    /// surfaces; <c>null</c> when the write succeeded.</summary>
    private ActionResult? MapWriteFailure(ComposeWriteResult write) =>
        write.IsSuccess ? null : write.Status switch
        {
            ComposeWriteStatus.ValidationFailed => UnprocessableEntity(new { error = write.Error }),
            ComposeWriteStatus.CliNotAvailable => UnprocessableEntity(new { error = write.Error }),
            ComposeWriteStatus.SshFailed => StatusCode(StatusCodes.Status502BadGateway, new { error = write.Error }),
            _ => StatusCode(StatusCodes.Status500InternalServerError, new { error = write.Error }),
        };

    /// <summary>V7.5 — maps a service create request to the domain
    /// <see cref="ComposeServiceEdit"/> (shared by the multi-service
    /// CreateProject loop).</summary>
    private static ComposeServiceEdit BuildServiceEditFrom(ComposeServiceCreateRequest svc) =>
        BuildServiceEdit(
            svc.Image, svc.Restart, svc.Ports, svc.Volumes, svc.Environment, svc.Labels,
            svc.Command, svc.Entrypoint, svc.User, svc.WorkingDir, svc.Resources);

    /// <summary>V7.1/V7.4 — maps an edit/create request's flat fields to the
    /// domain <see cref="ComposeServiceEdit"/> (shared by EditService and
    /// CreateService).</summary>
    private static ComposeServiceEdit BuildServiceEdit(
        string? image, string? restart,
        IReadOnlyList<string>? ports, IReadOnlyList<string>? volumes,
        IReadOnlyList<ComposeEnvVarResponse>? environment, IReadOnlyList<ComposeEnvVarResponse>? labels,
        string? command, string? entrypoint, string? user, string? workingDir,
        ComposeResourceConstraintsResponse? resources) =>
        new(
            Image: NormalizeScalar(image),
            Restart: NormalizeScalar(restart),
            Ports: (ports ?? []).Select(p => p.Trim()).Where(p => p.Length > 0).ToList(),
            Volumes: (volumes ?? []).Select(v => v.Trim()).Where(v => v.Length > 0).ToList(),
            Environment: (environment ?? []).Select(e => new ComposeEnvVar(e.Name.Trim(), e.Value)).ToList(),
            Labels: (labels ?? []).Select(l => new ComposeEnvVar(l.Name.Trim(), l.Value)).ToList(),
            Command: NormalizeScalar(command),
            Entrypoint: NormalizeScalar(entrypoint),
            User: NormalizeScalar(user),
            WorkingDir: NormalizeScalar(workingDir),
            Resources: MapResources(resources));

    /// <summary>V7.1 — lists registry tags for the image dropdown. Anonymous
    /// registry access (public images); private registries surface the error
    /// and the UI falls back to free text.</summary>
    [HttpGet("image-tags")]
    public async Task<ActionResult<ComposeImageTagsResponse>> GetImageTags(
        Guid connectionId, [FromQuery] string image, CancellationToken cancellationToken)
    {
        var connection = await LoadOwnedConnectionAsync(connectionId, cancellationToken);
        if (connection is null) return NotFound();

        if (string.IsNullOrWhiteSpace(image) || !imageReferenceParser.TryParse(image.Trim(), out var parsed))
            return Ok(new ComposeImageTagsResponse(image ?? "", [], "Unrecognised image reference."));

        var tags = await registryClient.ListTagsAsync(
            parsed.RegistryHost, parsed.Repository, RegistryAuthContext.Anonymous,
            cancellationToken: cancellationToken);

        return Ok(new ComposeImageTagsResponse(
            parsed.Repository,
            tags.Tags,
            tags.IsSuccess ? null : tags.Error ?? $"Tag listing returned {tags.Status}."));
    }

    /// <summary>
    /// V7.2 — host capacity already reserved by <em>other</em> running
    /// containers (this project's own containers excluded), for the resources
    /// editor's over-commit panel. Cached ~60 s per connection so re-opening
    /// the editor doesn't re-inspect every container each time.
    /// </summary>
    [HttpGet("{project}/allocation")]
    public async Task<ActionResult<ComposeAllocationResponse>> GetAllocation(
        Guid connectionId, string project, CancellationToken cancellationToken)
    {
        var connection = await LoadOwnedConnectionAsync(connectionId, cancellationToken);
        if (connection is null) return NotFound();

        var cacheKey = $"compose-alloc:{connectionId}:{project}";
        if (cache.TryGetValue(cacheKey, out ComposeAllocationResponse? cached) && cached is not null)
            return Ok(cached);

        DockerResourceAllocation allocation;
        try
        {
            allocation = await hostClient.GetResourceAllocationAsync(
                mapper.BuildTransport(connection), excludeComposeProject: project, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { error = $"Docker host unreachable: {ex.Message}" });
        }

        var response = new ComposeAllocationResponse(
            allocation.ContainerCount, allocation.ReservedCpus, allocation.ReservedMemoryBytes);
        cache.Set(cacheKey, response, AllocationCacheTtl);
        return Ok(response);
    }

    /// <summary>
    /// V7.3 — adds or replaces one top-level resource entry (network / volume /
    /// secret / config). Same atomic, validated, comment-preserving save path as
    /// <see cref="EditService"/>. 400 for an unknown kind; 409 when the file uses
    /// read-only constructs; 422 with the raw stderr when validation fails.
    /// </summary>
    [HttpPut("{project}/resources/{kind}/{name}")]
    public async Task<ActionResult<ComposeResourceEditResponse>> EditResource(
        Guid connectionId, string project, string kind, string name,
        [FromBody] ComposeResourceEditRequest request, CancellationToken cancellationToken)
    {
        if (!TryParseResourceKind(kind, out var resourceKind))
            return BadRequest(new { error = $"Unknown resource kind '{kind}'." });

        var prepared = await PrepareResourceWriteAsync(connectionId, project, cancellationToken);
        if (prepared.Failure is not null) return prepared.Failure;

        var driverOpts = (request.DriverOpts ?? [])
            .Where(o => !string.IsNullOrWhiteSpace(o.Name))
            .Select(o => new ComposeEnvVar(o.Name.Trim(), o.Value))
            .ToList();

        var edit = new ComposeResourceEdit(
            Kind: resourceKind,
            Name: name.Trim(),
            External: request.External,
            NameOverride: NormalizeScalar(request.NameOverride),
            Driver: NormalizeScalar(request.Driver),
            Subnet: NormalizeScalar(request.Subnet),
            Gateway: NormalizeScalar(request.Gateway),
            File: NormalizeScalar(request.File),
            DriverOpts: driverOpts);

        var edited = editor.ApplyResourceEdit(prepared.Content!, edit);
        return await FinishResourceWriteAsync(prepared, edited, cancellationToken);
    }

    /// <summary>V7.3 — removes one top-level resource entry (and the now-empty
    /// section when it was the last one). 200 with <c>changed: false</c> when the
    /// entry was already absent.</summary>
    [HttpDelete("{project}/resources/{kind}/{name}")]
    public async Task<ActionResult<ComposeResourceEditResponse>> DeleteResource(
        Guid connectionId, string project, string kind, string name, CancellationToken cancellationToken)
    {
        if (!TryParseResourceKind(kind, out var resourceKind))
            return BadRequest(new { error = $"Unknown resource kind '{kind}'." });

        var prepared = await PrepareResourceWriteAsync(connectionId, project, cancellationToken);
        if (prepared.Failure is not null) return prepared.Failure;

        var edited = editor.RemoveResource(prepared.Content!, resourceKind, name.Trim());
        return await FinishResourceWriteAsync(prepared, edited, cancellationToken);
    }

    /// <summary>V7.3 — networks already defined on the host (name + subnets), for
    /// the network editor's subnet-overlap warning. Cached ~60 s per
    /// connection.</summary>
    [HttpGet("{project}/host-networks")]
    public async Task<ActionResult<IReadOnlyList<ComposeHostNetworkResponse>>> GetHostNetworks(
        Guid connectionId, string project, CancellationToken cancellationToken)
    {
        var connection = await LoadOwnedConnectionAsync(connectionId, cancellationToken);
        if (connection is null) return NotFound();

        var cacheKey = $"compose-host-networks:{connectionId}";
        if (cache.TryGetValue(cacheKey, out IReadOnlyList<ComposeHostNetworkResponse>? cached) && cached is not null)
            return Ok(cached);

        IReadOnlyList<DockerNetworkSummary> networks;
        try
        {
            networks = await hostClient.ListNetworksAsync(mapper.BuildTransport(connection), cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { error = $"Docker host unreachable: {ex.Message}" });
        }

        var response = networks
            .Select(n => new ComposeHostNetworkResponse(n.Name, n.Driver, n.Subnets))
            .ToList();
        cache.Set(cacheKey, (IReadOnlyList<ComposeHostNetworkResponse>)response, AllocationCacheTtl);
        return Ok(response);
    }

    /// <summary>V7.3 — on-disk usage of the host's named volumes (best-effort)
    /// for the volume editor's size hint. Cached ~60 s per connection.</summary>
    [HttpGet("{project}/volume-usage")]
    public async Task<ActionResult<IReadOnlyList<ComposeVolumeUsageResponse>>> GetVolumeUsage(
        Guid connectionId, string project, CancellationToken cancellationToken)
    {
        var connection = await LoadOwnedConnectionAsync(connectionId, cancellationToken);
        if (connection is null) return NotFound();

        var cacheKey = $"compose-volume-usage:{connectionId}";
        if (cache.TryGetValue(cacheKey, out IReadOnlyList<ComposeVolumeUsageResponse>? cached) && cached is not null)
            return Ok(cached);

        IReadOnlyList<DockerVolumeUsage> usage;
        try
        {
            usage = await hostClient.GetVolumeUsageAsync(mapper.BuildTransport(connection), cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { error = $"Docker host unreachable: {ex.Message}" });
        }

        var response = usage
            .Select(u => new ComposeVolumeUsageResponse(u.Name, u.SizeBytes, u.RefCount))
            .ToList();
        cache.Set(cacheKey, (IReadOnlyList<ComposeVolumeUsageResponse>)response, AllocationCacheTtl);
        return Ok(response);
    }

    private static bool TryParseResourceKind(string kind, out ComposeResourceKind resourceKind)
    {
        resourceKind = kind?.ToLowerInvariant() switch
        {
            "networks" => ComposeResourceKind.Network,
            "volumes" => ComposeResourceKind.Volume,
            "secrets" => ComposeResourceKind.Secret,
            "configs" => ComposeResourceKind.Config,
            _ => (ComposeResourceKind)(-1),
        };
        return (int)resourceKind >= 0;
    }

    /// <summary>V7.3 — the located project + read file content shared by the
    /// resource edit/delete endpoints, or a populated <see cref="Failure"/>.</summary>
    private sealed record ResourceWriteContext(
        DockerConnectionEntity Connection, string Path, string FileName, string? Content, ActionResult? Failure);

    private async Task<ResourceWriteContext> PrepareResourceWriteAsync(
        Guid connectionId, string project, CancellationToken cancellationToken)
    {
        var connection = await LoadOwnedConnectionAsync(connectionId, cancellationToken);
        if (connection is null) return new ResourceWriteContext(null!, "", "", null, NotFound());

        var located = await LocateProjectAsync(connection, project, cancellationToken);
        if (located.Failure is not null)
            return new ResourceWriteContext(connection, "", "", null, located.Failure);

        var read = await ReadProjectFileAsync(connection, located.Path!, cancellationToken);
        if (read.Failure is not null)
            return new ResourceWriteContext(connection, located.Path!, "", null, read.Failure);

        var content = read.Result!.Content!;
        var parsed = parser.Parse(content);
        if (parsed.Project is null)
            return new ResourceWriteContext(connection, located.Path!, read.Result.FileName!, null,
                UnprocessableEntity(new { error = parsed.Error }));
        if (parsed.Project.UnsupportedFeatures.Count > 0)
            return new ResourceWriteContext(connection, located.Path!, read.Result.FileName!, null,
                Conflict(new
                {
                    error = "This Compose file is read-only: it uses "
                        + string.Join(", ", parsed.Project.UnsupportedFeatures) + ".",
                }));

        return new ResourceWriteContext(connection, located.Path!, read.Result.FileName!, content, null);
    }

    private async Task<ActionResult<ComposeResourceEditResponse>> FinishResourceWriteAsync(
        ResourceWriteContext ctx, ComposeEditResult edited, CancellationToken cancellationToken)
    {
        if (edited.Error is not null)
            return UnprocessableEntity(new { error = edited.Error });

        if (edited.Changed)
        {
            var write = ctx.Connection.HostType == DockerHostType.Ssh
                ? await writer.WriteOverSshAsync(
                    mapper.BuildTransport(ctx.Connection).Ssh!, ctx.Path, ctx.FileName, edited.Content!, cancellationToken)
                : await writer.WriteAsync(ctx.Path, ctx.FileName, edited.Content!, cancellationToken);

            if (!write.IsSuccess)
            {
                return write.Status switch
                {
                    ComposeWriteStatus.ValidationFailed => UnprocessableEntity(new { error = write.Error }),
                    ComposeWriteStatus.SshFailed => StatusCode(StatusCodes.Status502BadGateway, new { error = write.Error }),
                    _ => StatusCode(StatusCodes.Status500InternalServerError, new { error = write.Error }),
                };
            }
        }

        var reparsed = parser.Parse(edited.Content!);
        if (reparsed.Project is null)
            return UnprocessableEntity(new { error = reparsed.Error });

        return Ok(new ComposeResourceEditResponse(
            edited.Changed,
            BuildProjectResponse(reparsed.Project, ctx.FileName, ctx.Path, edited.Content!)));
    }

    // ── discovery + shared plumbing ─────────────────────────────────────────

    private sealed record DiscoveredProject(ComposeDiscoveredProjectResponse Response);

    private sealed record DiscoveryResult(IReadOnlyList<DiscoveredProject> Projects, string? Error);

    private async Task<DiscoveryResult> DiscoverProjectsAsync(
        DockerConnectionEntity connection, CancellationToken cancellationToken)
    {
        var transport = mapper.BuildTransport(connection);
        IReadOnlyList<DockerContainerDetail> containers;
        try
        {
            containers = await hostClient.ListContainerDetailsAsync(transport, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new DiscoveryResult([], $"Docker host unreachable: {ex.Message}");
        }

        var mapping = DockerWatchMapper.BuildComposePathMapping(connection);
        var projects = containers
            .Where(c => !string.IsNullOrWhiteSpace(c.ComposeProject))
            .GroupBy(c => c.ComposeProject!, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var workingDir = g
                    .Select(c => c.Labels.GetValueOrDefault(ComposeProjectPaths.WorkingDirLabel))
                    .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
                var resolved = ComposeProjectPaths.Resolve(connection.HostType, mapping, workingDir);
                return new DiscoveredProject(new ComposeDiscoveredProjectResponse(
                    g.Key,
                    workingDir,
                    resolved,
                    ContainerCount: g.Count(),
                    RunningCount: g.Count(c => string.Equals(c.State, "running", StringComparison.OrdinalIgnoreCase))));
            })
            .ToList();

        return new DiscoveryResult(projects, null);
    }

    private sealed record LocateOutcome(string? Path, ActionResult? Failure);

    private async Task<LocateOutcome> LocateProjectAsync(
        DockerConnectionEntity connection, string project, CancellationToken cancellationToken)
    {
        if (connection.HostType == DockerHostType.TcpTls)
            return new LocateOutcome(null, BadRequest(new
            {
                error = "Compose file access is not available for TCP+TLS connections — the daemon API exposes no project files.",
            }));

        var discovered = await DiscoverProjectsAsync(connection, cancellationToken);
        if (discovered.Error is not null)
            return new LocateOutcome(null, StatusCode(StatusCodes.Status502BadGateway, new { error = discovered.Error }));

        var match = discovered.Projects.FirstOrDefault(p =>
            string.Equals(p.Response.Name, project, StringComparison.OrdinalIgnoreCase));
        if (match is null)
            return new LocateOutcome(null, NotFound(new
            {
                error = $"No containers with compose project '{project}' were found on this connection.",
            }));

        if (string.IsNullOrWhiteSpace(match.Response.ResolvedPath))
            return new LocateOutcome(null, NotFound(new
            {
                error = $"Compose project '{project}' does not advertise a project directory "
                    + "(no com.docker.compose.project.working_dir label on its containers).",
            }));

        return new LocateOutcome(match.Response.ResolvedPath, null);
    }

    private sealed record ReadOutcome(ComposeProjectReadResult? Result, ActionResult? Failure);

    private async Task<ReadOutcome> ReadProjectFileAsync(
        DockerConnectionEntity connection, string path, CancellationToken cancellationToken)
    {
        ComposeProjectReadResult read;
        if (connection.HostType == DockerHostType.Ssh)
        {
            var ssh = mapper.BuildTransport(connection).Ssh;
            if (ssh is null)
                return new ReadOutcome(null, BadRequest(new
                {
                    error = "SSH credentials are not fully configured for this connection.",
                }));
            read = await reader.ReadOverSshAsync(ssh, path, cancellationToken);
        }
        else
        {
            read = await reader.ReadAsync(path, cancellationToken);
        }

        if (read.Status == ComposeProjectReadStatus.SshFailed)
            return new ReadOutcome(null, StatusCode(StatusCodes.Status502BadGateway, new { error = read.Error }));
        if (read.Status != ComposeProjectReadStatus.Ok)
            return new ReadOutcome(null, NotFound(new { error = read.Error }));
        return new ReadOutcome(read, null);
    }

    /// <summary>V7.0/V7.7 — maps the parsed model to the wire response and (V7.7)
    /// attaches the linter findings for the same <paramref name="yamlText"/>, so
    /// every load and every save carries fresh findings without a second call.</summary>
    private ComposeProjectResponse BuildProjectResponse(
        ComposeProjectModel p, string fileName, string projectPath, string yamlText) =>
        new(
            ProjectName: p.ProjectName,
            FileName: fileName,
            ProjectPath: projectPath,
            Services: p.Services.Select(s => new ComposeServiceResponse(
                s.Name, s.Image, s.ContainerName, s.Restart,
                s.Ports, s.Volumes,
                s.Environment.Select(e => new ComposeEnvVarResponse(e.Name, e.Value)).ToList(),
                s.EnvFiles, s.DependsOn, s.Networks,
                ToResourcesResponse(s.Resources),
                s.Labels.Select(l => new ComposeEnvVarResponse(l.Name, l.Value)).ToList(),
                s.Command, s.Entrypoint, s.User, s.WorkingDir)).ToList(),
            Networks: p.Networks.Select(n => new ComposeNetworkResponse(
                n.Name, n.External, n.NameOverride, n.Driver, n.Subnet, n.Gateway,
                n.DriverOpts.Select(o => new ComposeEnvVarResponse(o.Name, o.Value)).ToList())).ToList(),
            Volumes: p.Volumes.Select(v => new ComposeVolumeResponse(
                v.Name, v.External, v.NameOverride, v.Driver,
                v.DriverOpts.Select(o => new ComposeEnvVarResponse(o.Name, o.Value)).ToList())).ToList(),
            Secrets: p.Secrets.Select(ToFileResourceResponse).ToList(),
            Configs: p.Configs.Select(ToFileResourceResponse).ToList(),
            UnsupportedFeatures: p.UnsupportedFeatures,
            Lint: linter.Lint(yamlText, projectPath)
                .Select(f => new ComposeLintFindingResponse(f.Rule, f.Severity, f.Message, f.Service))
                .ToList());

    private static ComposeFileResourceResponse ToFileResourceResponse(ComposeFileResourceModel m) =>
        new(m.Name, m.External, m.NameOverride, m.File);

    private static string? NormalizeScalar(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static ComposeResourceConstraintsResponse ToResourcesResponse(ComposeResourceConstraints r) =>
        new(r.Convention, r.CpuLimit, r.CpuReservation, r.MemLimit, r.MemReservation, r.PidsLimit,
            r.CpuShares, r.OomKillDisable, r.OomScoreAdj, r.ShmSize,
            r.Ulimits.Select(u => new ComposeUlimitDto(u.Name, u.Soft, u.Hard)).ToList());

    /// <summary>Maps the edit request's resource block to the domain shape; a
    /// missing block (old client) means "no resource constraints declared".</summary>
    private static ComposeResourceConstraints MapResources(ComposeResourceConstraintsResponse? r)
    {
        if (r is null) return ComposeResourceConstraints.Empty;
        return new ComposeResourceConstraints(
            Convention: r.Convention == "legacy" ? "legacy" : "deploy",
            CpuLimit: NormalizeScalar(r.CpuLimit),
            CpuReservation: NormalizeScalar(r.CpuReservation),
            MemLimit: NormalizeScalar(r.MemLimit),
            MemReservation: NormalizeScalar(r.MemReservation),
            PidsLimit: NormalizeScalar(r.PidsLimit),
            CpuShares: r.CpuShares,
            OomKillDisable: r.OomKillDisable,
            OomScoreAdj: r.OomScoreAdj,
            ShmSize: NormalizeScalar(r.ShmSize),
            Ulimits: (r.Ulimits ?? [])
                .Where(u => !string.IsNullOrWhiteSpace(u.Name))
                .Select(u => new ComposeUlimit(u.Name.Trim(), u.Soft, u.Hard))
                .ToList());
    }

    private async Task<DockerConnectionEntity?> LoadOwnedConnectionAsync(
        Guid connectionId, CancellationToken cancellationToken)
    {
        var userId = UserId;
        return await db.DockerConnections.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == connectionId && c.UserId == userId, cancellationToken);
    }
}
