using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Stashboard.Core.Abstractions;
using Stashboard.Core.Options;
using Stashboard.Infrastructure.Aws;
using Stashboard.Infrastructure.Docker;
using Stashboard.Infrastructure.GitHub;
using Stashboard.Infrastructure.Proxmox;
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

        // V7.8.0 — official container icon CDN client (homarr-labs dashboard-icons
        // on jsDelivr). Best-effort avatar lookup, so a short timeout keeps a slow
        // CDN from stalling a card refresh.
        services.AddHttpClient(ContainerIconResolver.HttpClientName, c =>
        {
            c.Timeout = TimeSpan.FromSeconds(5);
            c.DefaultRequestHeaders.UserAgent.ParseAdd("Stashboard/1.0 (+container-icon)");
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

        // V6.0 — Proxmox VE REST API client. Two variants: a validating one and
        // an insecure one for the self-signed certificates that homelab Proxmox
        // installs ship with (selected per-connection via SkipTlsVerify).
        services.AddHttpClient(ProxmoxApiClient.HttpClientName, c =>
        {
            c.Timeout = TimeSpan.FromSeconds(15);
            c.DefaultRequestHeaders.UserAgent.ParseAdd("Stashboard/1.0 (+proxmox)");
        });
        services.AddHttpClient(ProxmoxApiClient.InsecureHttpClientName, c =>
        {
            c.Timeout = TimeSpan.FromSeconds(15);
            c.DefaultRequestHeaders.UserAgent.ParseAdd("Stashboard/1.0 (+proxmox)");
        }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        });

        services.AddSingleton<IFaviconService, FaviconService>();
        // V7.8.0 — resolves a container's official icon from the dashboard-icons
        // CDN, slug derived via IImageReferenceParser. Stateless, 24h cache.
        services.AddSingleton<IContainerIconResolver, ContainerIconResolver>();
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
        // V7.0 — read-only Compose viewer: locates + reads the bind-mounted
        // Compose file and parses it into the viewer subset. Both stateless.
        services.AddSingleton<IComposeProjectReader, ComposeProjectReader>();
        services.AddSingleton<IComposeFileParser, ComposeFileParser>();
        // V7.7 — pure Compose linter (port clashes, depends_on cycles, missing
        // healthchecks, escaping bind mounts, deprecated keys, :latest tags). Its
        // findings ride on every project response (load + every save).
        services.AddSingleton<IComposeLinter, ComposeFileLinter>();
        // V7.1 — surgical Compose editor: splices the raw YAML text at the
        // edited keys only, so the rest of the file round-trips byte-for-byte.
        services.AddSingleton<IComposeFileEditor, ComposeFileEditor>();
        // V7.6 — keeps the last N revisions of each Compose file under
        // <project>/.stashboard/history/ for the Restore button. The writer
        // snapshots through it on every save (best-effort). Stateless.
        services.AddSingleton<IComposeHistoryStore, ComposeHistoryStore>();
        // V7.1 — atomic, validated save (.next + `compose config -q` + rename)
        // for both transports. Stateless.
        services.AddSingleton<IComposeProjectWriter, ComposeProjectWriter>();
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

        // V6.0 — Proxmox LXC update monitoring. The API client + SSH guest
        // inspector are the two transports; the checker is the pure
        // orchestrator that composes them (mirrors the Docker checker).
        services.AddSingleton<IProxmoxApiClient, ProxmoxApiClient>();
        services.AddSingleton<IProxmoxGuestInspector, ProxmoxSshGuestInspector>();
        services.AddSingleton<IProxmoxUpdateChecker, ProxmoxUpdateChecker>();
        // V6.7.1 — one-click "Update now" applier (streams apt over SSH).
        services.AddSingleton<IProxmoxUpdateApplier, ProxmoxSshUpdateApplier>();
        // V8.6 — server-side VNC relay for the browser VM console: mints the
        // vncproxy session and bridges the raw RFB vncwebsocket to the browser,
        // keeping the API token server-side.
        services.AddSingleton<IProxmoxVncRelay, ProxmoxVncRelay>();
        // V6.11 — webhook-triggered scan queue (the Proxmox analogue of the
        // Docker webhook check queue). Singleton process-local buffer; the
        // webhook token reuses the Docker token generator above.
        services.AddSingleton<IProxmoxScanQueue, ProxmoxScanQueue>();

        return services;
    }
}
