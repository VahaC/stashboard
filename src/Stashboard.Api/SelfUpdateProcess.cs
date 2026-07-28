using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Stashboard.Core.Abstractions;
using Stashboard.Core.Options;
using Stashboard.Infrastructure;

namespace Stashboard.Api;

/// <summary>
/// V9.2 — the <c>self-update</c> / <c>self-update-project</c> CLI entry point the
/// detached helper container runs (see <see cref="ISelfUpdateLauncher"/>). It is
/// the same Stashboard image invoked as <c>dotnet Stashboard.Api.dll
/// self-update[-project]</c> with a serialized profile in
/// <see cref="SelfUpdateProtocol.ProfileEnvVar"/>. It spins up a minimal host
/// (just the infrastructure services the updater needs — no web server, no
/// database), waits briefly so the parent's HTTP response can flush, runs the
/// proven recreate once, and exits. The helper container is auto-removed on exit
/// (declared with AutoRemove), so nothing lingers in the UI.
/// </summary>
internal static class SelfUpdateProcess
{
    public static async Task<int> RunAsync(string[] args)
    {
        var command = args[0];

        var json = Environment.GetEnvironmentVariable(SelfUpdateProtocol.ProfileEnvVar);
        if (string.IsNullOrWhiteSpace(json))
        {
            Console.Error.WriteLine($"self-update: {SelfUpdateProtocol.ProfileEnvVar} is not set.");
            return 2;
        }

        var delaySeconds = int.TryParse(Environment.GetEnvironmentVariable(SelfUpdateProtocol.DelayEnvVar), out var d) && d >= 0
            ? d
            : SelfUpdateProtocol.DefaultStartDelaySeconds;

        var builder = Host.CreateApplicationBuilder(args);
        builder.Configuration.AddEnvironmentVariables(prefix: "STASHBOARD_");
        builder.Services.Configure<DockerUpdateOptions>(builder.Configuration.GetSection(DockerUpdateOptions.SectionName));
        builder.Services.AddStashboardInfrastructure(builder.Configuration);
        using var host = builder.Build();

        var logger = host.Services.GetRequiredService<ILogger<Program>>();

        // Give the parent process time to flush its "update scheduled" HTTP
        // response before we stop / remove anything.
        if (delaySeconds > 0)
            await Task.Delay(TimeSpan.FromSeconds(delaySeconds));

        try
        {
            var success = string.Equals(command, SelfUpdateProtocol.ProjectCommandName, StringComparison.Ordinal)
                ? await RunProjectAsync(host.Services, logger, json)
                : await RunSingleAsync(host.Services, logger, json);
            return success ? 0 : 1;
        }
        catch (JsonException ex)
        {
            Console.Error.WriteLine($"self-update: profile is not valid JSON: {ex.Message}");
            return 2;
        }
    }

    private static async Task<bool> RunSingleAsync(IServiceProvider services, ILogger logger, string json)
    {
        var profile = JsonSerializer.Deserialize<DockerUpdateProfile>(json, SelfUpdateProtocol.Options)
            ?? throw new JsonException("profile deserialized to null");

        logger.LogInformation(
            "self-update: recreating {Container} with {Image}", profile.ContainerName, profile.ImageReference);

        var result = await services.GetRequiredService<IDockerImageUpdater>().UpdateAsync(profile);

        logger.LogInformation(
            "self-update: finished with status {Status}. {Error}", result.Status, result.Error);

        return result.IsSuccess;
    }

    private static async Task<bool> RunProjectAsync(IServiceProvider services, ILogger logger, string json)
    {
        var profile = JsonSerializer.Deserialize<DockerProjectUpdateProfile>(json, SelfUpdateProtocol.Options)
            ?? throw new JsonException("project profile deserialized to null");

        logger.LogInformation(
            "self-update: recreating compose project {Project} ({Count} service(s))",
            profile.ProjectName, profile.Services.Count);

        var result = await services.GetRequiredService<IDockerProjectUpdater>().UpdateProjectAsync(profile);

        logger.LogInformation(
            "self-update: project finished with status {Status} (mode {Mode}). {Error}",
            result.AggregateStatus, result.Mode, result.Error);

        return result.IsSuccess;
    }
}
