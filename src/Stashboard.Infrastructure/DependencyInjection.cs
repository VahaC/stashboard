using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Stashboard.Core.Abstractions;
using Stashboard.Core.Options;
using Stashboard.Infrastructure.Aws;
using Stashboard.Infrastructure.Docker;
using Stashboard.Infrastructure.GitHub;
using Stashboard.Infrastructure.Security;
using Stashboard.Infrastructure.Services;

namespace Stashboard.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddStashboardInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<EncryptionOptions>(configuration.GetSection(EncryptionOptions.SectionName));
        services.Configure<HealthCheckOptions>(configuration.GetSection(HealthCheckOptions.SectionName));

        services.AddMemoryCache();
        services.AddSingleton<IEncryptionService, AesEncryptionService>();

        services.AddHttpClient("favicon", c =>
        {
            c.Timeout = TimeSpan.FromSeconds(5);
            c.DefaultRequestHeaders.UserAgent.ParseAdd("Stashboard/1.0 (+favicon-probe)");
        });
        services.AddHttpClient("favicon-insecure", c =>
        {
            c.Timeout = TimeSpan.FromSeconds(5);
            c.DefaultRequestHeaders.UserAgent.ParseAdd("Stashboard/1.0 (+favicon-probe)");
        }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        });

        services.AddHttpClient("healthcheck", c =>
        {
            c.Timeout = TimeSpan.FromSeconds(10);
            c.DefaultRequestHeaders.UserAgent.ParseAdd("Stashboard/1.0 (+healthcheck)");
        });
        services.AddHttpClient("healthcheck-insecure", c =>
        {
            c.Timeout = TimeSpan.FromSeconds(10);
            c.DefaultRequestHeaders.UserAgent.ParseAdd("Stashboard/1.0 (+healthcheck)");
        }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        });

        // Docker registry probe client — Docker Hub and GHCR. Slightly longer timeout
        // than the healthcheck client because the manifest endpoint may chain through a
        // Bearer-token round-trip before responding.
        services.AddHttpClient(OciRegistryClient.HttpClientName, c =>
        {
            c.Timeout = TimeSpan.FromSeconds(15);
            c.DefaultRequestHeaders.UserAgent.ParseAdd("Stashboard/1.0 (+registry-probe)");
        });

        // V2.3 — GitHub Releases API client. Used inline by DockerUpdateChecker
        // to enrich a GHCR "Update available" result with the upstream
        // changelog. GitHub requires a User-Agent on every request.
        services.AddHttpClient(GitHubReleaseClient.HttpClientName, c =>
        {
            c.Timeout = TimeSpan.FromSeconds(10);
            c.DefaultRequestHeaders.UserAgent.ParseAdd("Stashboard/1.0 (+github-releases)");
        });

        // V2.4 — AWS ECR GetAuthorizationToken endpoint. Self-signs requests
        // with SigV4 so the client doesn't carry the official AWS SDK
        // dependency for a single POST.
        services.AddHttpClient(AwsEcrTokenProvider.HttpClientName, c =>
        {
            c.Timeout = TimeSpan.FromSeconds(10);
            c.DefaultRequestHeaders.UserAgent.ParseAdd("Stashboard/1.0 (+aws-ecr)");
        });

        services.AddSingleton<IFaviconService, FaviconService>();
        services.AddSingleton<IServiceHealthChecker, ServiceHealthChecker>();
        services.AddSingleton<IImageReferenceParser, ImageReferenceParser>();
        services.AddSingleton<IRegistryClient, OciRegistryClient>();
        services.AddSingleton<IGitHubReleaseClient, GitHubReleaseClient>();
        services.AddSingleton<IAwsEcrTokenProvider, AwsEcrTokenProvider>();
        services.AddSingleton<IDockerClientFactory, DockerClientFactory>();
        services.AddSingleton<IDockerHostClient, DockerHostClient>();
        services.AddSingleton<IDockerUpdateChecker, DockerUpdateChecker>();
        // V3.3 — streams stdcopy-multiplexed container logs to the browser.
        // Stateless, opens a Docker client per call; the daemon connection
        // lives as long as the caller's CancellationToken stays unsignalled.
        services.AddSingleton<IDockerLogStreamer, DockerLogStreamer>();
        // V3.4 — streams per-second CPU / memory / network / block-I/O
        // counters. Same per-call ownership semantics as the log streamer.
        services.AddSingleton<IDockerStatsStreamer, DockerStatsStreamer>();
        // V5.3 — opens an interactive SSH PTY for the browser host terminal.
        // Stateless: each Connect() owns its own SshClient + ShellStream, torn
        // down when the returned channel is disposed.
        services.AddSingleton<IHostShellConnector, Docker.Ssh.SshHostShellConnector>();
        // V5.7 — opens an interactive PTY *inside* a container via the daemon's
        // exec API. Stateless: each ConnectAsync() owns its own Docker client +
        // exec stream, torn down when the returned channel is disposed. Works
        // for every host type because it routes through the daemon, not SSH.
        services.AddSingleton<IContainerExecConnector, DockerContainerExecConnector>();
        // V5.2 — shells out to the host `docker compose` CLI for a true
        // Compose-aware recreate. Singleton so the CLI-availability probe is
        // cached for the process lifetime.
        services.AddSingleton<IComposeCommandRunner, ComposeCommandRunner>();
        // V2.7 — one-click "Update now" pulls + recreates the target
        // container. Singleton: stateless, pulls the per-request transport
        // from the same factory the host client uses.
        services.AddSingleton<IDockerImageUpdater, DockerImageUpdater>();
        // V5.4 — bulk "Update project" orchestrator that drives the
        // compose-aware path (one `docker compose pull && up -d` against the
        // project root) or falls back to per-service raw recreate ordered by
        // the `com.docker.compose.depends_on` label.
        services.AddSingleton<IDockerProjectUpdater, DockerProjectUpdater>();
        // V5.5 — image-prune orchestrator. Stateless wrapper around the host
        // client's prune call; the controller / background service own the
        // audit-row persistence.
        services.AddSingleton<IDockerPruneRunner, DockerPruneRunner>();

        // V2.6 — webhook receiver plumbing. The queue is a singleton process-
        // local buffer; the parser and token generator are pure stateless
        // helpers.
        services.AddSingleton<IDockerWebhookCheckQueue, DockerWebhookCheckQueue>();
        services.AddSingleton<IDockerWebhookPayloadParser, DockerWebhookPayloadParser>();
        services.AddSingleton<IDockerWebhookTokenGenerator, DockerWebhookTokenGenerator>();

        return services;
    }
}
