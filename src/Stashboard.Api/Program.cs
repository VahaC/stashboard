using System.IdentityModel.Tokens.Jwt;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;
using Stashboard.Api.Auth;
using Stashboard.Api.Data;
using Stashboard.Api.Mapping;
using Stashboard.Api.Notifications;
using Stashboard.Api.Services;
using Stashboard.Api.Services.ContainerExec;
using Stashboard.Api.Services.HealthCheckSettings;
using Stashboard.Api.Services.HostShell;
using Stashboard.Api.Services.ImagePrune;
using Stashboard.Api.Services.ProxmoxConsole;
using Stashboard.Core.Abstractions;
using Stashboard.Core.Enums;
using Stashboard.Core.Options;
using Stashboard.Infrastructure;

namespace Stashboard.Api;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // STASHBOARD_* env vars override appsettings (use __ to descend into a section).
        builder.Configuration.AddEnvironmentVariables(prefix: "STASHBOARD_");

        // First-run secret provisioning: if no encryption key / JWT secret were
        // supplied, generate strong ones and persist them on the data volume so
        // they survive image updates. Must run before the options below are read.
        var secretProvisioningNotes = SecretProvisioning.AddPersistedSecrets(builder);

        // Keep JWT claims compact ("sub", "uid", "email", "stmp") — disable the legacy
        // XML-namespace remapping that would otherwise inflate every claim name.
        JwtSecurityTokenHandler.DefaultInboundClaimTypeMap.Clear();

        builder.Services.AddControllers()
            .AddJsonOptions(o =>
            {
                o.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
                // Emit all UTC DateTimes with a trailing 'Z' — see UtcDateTimeConverter.
                o.JsonSerializerOptions.Converters.Add(new Stashboard.Api.Serialization.UtcDateTimeConverter());
            });
        builder.Services.AddOpenApi();
        builder.Services.AddSwaggerGen();

        // Database — SQLite only (single-container, self-hosted). Migrations live in
        // this assembly. WAL + busy_timeout are applied per-connection by the
        // interceptor so the background scanners don't trip "database is locked".
        var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");
        builder.Services.AddDbContext<ApplicationDbContext>(opts =>
            opts.UseSqlite(connectionString).AddInterceptors(new SqlitePragmaInterceptor()));

        // Custom auth — no ASP.NET Core Identity.
        builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.SectionName));
        builder.Services.TryAddSingletonTimeProvider();
        builder.Services.AddSingleton<IPasswordHasher, Pbkdf2PasswordHasher>();
        builder.Services.AddScoped<IUserService, UserService>();
        builder.Services.AddScoped<ITokenService, TokenService>();
        builder.Services.AddHostedService<RefreshTokenCleanupHostedService>();

        // Email / notifications. Settings live in the DB (single row) and are editable from the
        // UI; the bound EmailOptions only seeds the row on first access so an existing deployment
        // keeps its configured values. DbEmailSender resolves them per send — "Smtp" sends via
        // MailKit, anything else writes the email to the logger (default; safe for dev/CI).
        builder.Services.Configure<EmailOptions>(builder.Configuration.GetSection(EmailOptions.SectionName));
        builder.Services.AddScoped<IEmailSettingsService, EmailSettingsService>();
        builder.Services.AddScoped<IEmailSender, DbEmailSender>();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<IAccountNotificationService, AccountNotificationService>();

        var jwtOptions = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>()
            ?? throw new InvalidOperationException("Jwt section not configured.");
        builder.Services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(opts =>
            {
                opts.MapInboundClaims = false;
                opts.TokenValidationParameters = TokenService.BuildValidationParameters(jwtOptions);
                opts.Events = JwtBearerEventHandlers.Build();
            });
        builder.Services.AddAuthorization();

        // Rate-limit policies for endpoints that send email or accept account-recovery
        // tokens — protects against email-bombing and credential-stuffing.
        builder.Services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            // 5 requests per 15 minutes per (IP + email) — applied to forgot-password
            // and resend-confirmation. Partition by email so one attacker can't burn the
            // window for a victim by spamming from many IPs.
            o.AddPolicy("account-email", httpContext =>
            {
                var email = httpContext.Request.Headers["X-Email-Hint"].ToString();
                var ip = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                var key = $"{ip}|{email}";
                return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 5,
                    Window = TimeSpan.FromMinutes(15),
                    QueueLimit = 0,
                });
            });

            // 10 attempts per 15 minutes per IP for token-redemption endpoints
            // (reset-password, confirm-email, confirm-email-change) — slows brute-force
            // guessing of the 256-bit token (still cryptographically infeasible, but
            // keeps logs and notifications quiet).
            o.AddPolicy("account-token", httpContext =>
            {
                var ip = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                return RateLimitPartition.GetFixedWindowLimiter(ip, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 10,
                    Window = TimeSpan.FromMinutes(15),
                    QueueLimit = 0,
                });
            });
        });

        // Stashboard infra (encryption, favicon, healthcheck client)
        builder.Services.AddStashboardInfrastructure(builder.Configuration);
        builder.Services.AddHttpClient<ITelegramSender, TelegramBotSender>(c =>
        {
            c.Timeout = TimeSpan.FromSeconds(10);
            c.DefaultRequestHeaders.UserAgent.ParseAdd("Stashboard/1.0 (+telegram)");
        });

        // Api-side services
        builder.Services.AddScoped<IBackupService, BackupService>();
        builder.Services.AddScoped<IWebResourceMapper, WebResourceMapper>();
        builder.Services.AddScoped<IDockerWatchMapper, DockerWatchMapper>();
        builder.Services.AddScoped<IDockerConnectionMapper, DockerConnectionMapper>();
        builder.Services.AddScoped<IProxmoxConnectionMapper, ProxmoxConnectionMapper>();
        builder.Services.AddSingleton<IStashboardMapper, StashboardMapper>();
        builder.Services.AddScoped<IServiceStatusNotificationService, ServiceStatusNotificationService>();
        // V7.5 — starter-recipe catalogue, loaded once from templates/ (+ optional
        // user override dir) at startup.
        builder.Services.AddSingleton<Services.Templates.IServiceTemplateCatalog, Services.Templates.ServiceTemplateCatalog>();
        builder.Services.AddScoped<IDockerUpdateNotificationService, DockerUpdateNotificationService>();
        builder.Services.Configure<DockerUpdateOptions>(builder.Configuration.GetSection(DockerUpdateOptions.SectionName));
        // V3.5 — top-level app feature flags (Docker instances page,
        // container-removal gate, etc.). Default-off so destructive
        // actions never light up without the operator opting in.
        builder.Services.Configure<StashboardOptions>(builder.Configuration.GetSection(StashboardOptions.SectionName));
        // V5.3 — host terminal tunables (concurrency caps, idle timeout, ticket
        // TTL). The master on/off switch lives on StashboardOptions.AllowHostShell.
        builder.Services.Configure<HostShellOptions>(builder.Configuration.GetSection(HostShellOptions.SectionName));
        // Single-use ticket store + concurrency tally + per-session SSH PTY glue.
        builder.Services.AddSingleton<IHostShellTicketService, HostShellTicketService>();
        builder.Services.AddSingleton<IHostShellSessionRegistry, HostShellSessionRegistry>();
        // DB-backed master switch (managed from the Settings page), seeded from
        // the optional Stashboard:AllowHostShell config flag on first run.
        builder.Services.AddScoped<IHostShellSettingsService, HostShellSettingsService>();
        // V5.7 — container-exec terminal: shells *inside* a container via the
        // daemon's exec API. Reuses the V5.3 WebSocket bridge + byte pump. The
        // connector itself (DockerContainerExecConnector) is registered in
        // Infrastructure; here we wire the ticket store, concurrency tally, the
        // tunables, and the DB-backed master switch (seeded from
        // Stashboard:AllowContainerExec on first run).
        builder.Services.Configure<ContainerExecOptions>(builder.Configuration.GetSection(ContainerExecOptions.SectionName));
        builder.Services.AddSingleton<IContainerExecTicketService, ContainerExecTicketService>();
        builder.Services.AddSingleton<IContainerExecSessionRegistry, ContainerExecSessionRegistry>();
        builder.Services.AddScoped<IContainerExecSettingsService, ContainerExecSettingsService>();
        // V5.5 — image-prune background sweep + on-demand "Prune now" button.
        // The runner itself is registered in Infrastructure/DependencyInjection.cs.
        builder.Services.Configure<ImagePruneOptions>(builder.Configuration.GetSection(ImagePruneOptions.SectionName));
        builder.Services.AddScoped<IImagePruneSettingsService, ImagePruneSettingsService>();
        // V5.6 — DB-backed health-check tuning (failure threshold + in-probe retries),
        // managed from the Settings → Health checks page. Seeded from the HealthCheck
        // config block (bound in Infrastructure) on first read.
        builder.Services.AddScoped<IHealthCheckSettingsService, HealthCheckSettingsService>();
        // V6.0 — Proxmox LXC update monitoring. The API client + SSH inspector
        // + checker live in Infrastructure; here we wire the scan service (the
        // per-connection unit of work, shared by the loop and the "Check now"
        // endpoint), the notification channel, the tick options, and the
        // background loop.
        builder.Services.Configure<ProxmoxUpdateOptions>(builder.Configuration.GetSection(ProxmoxUpdateOptions.SectionName));
        builder.Services.AddScoped<IProxmoxUpdateNotificationService, ProxmoxUpdateNotificationService>();
        builder.Services.AddScoped<Services.Proxmox.IProxmoxScanService, Services.Proxmox.ProxmoxScanService>();
        // V6.8.1 — node-health alerting: the per-tick evaluation service + its
        // email/Telegram notifier. Both scoped; the background loop resolves the
        // service per tick.
        builder.Services.AddScoped<IProxmoxNodeAlertNotificationService, ProxmoxNodeAlertNotificationService>();
        builder.Services.AddScoped<Services.Proxmox.IProxmoxNodeAlertService, Services.Proxmox.ProxmoxNodeAlertService>();
        // V6.7.1 — DB-backed master switch for one-click "Update now".
        builder.Services.AddScoped<Services.Proxmox.IProxmoxUpdateApplySettingsService, Services.Proxmox.ProxmoxUpdateApplySettingsService>();
        // V6.13 — DB-backed master switch for destroy-LXC.
        builder.Services.AddScoped<Services.Proxmox.IProxmoxDestroySettingsService, Services.Proxmox.ProxmoxDestroySettingsService>();
        // V6.13.1 — DB-backed master switch for create-LXC.
        builder.Services.AddScoped<Services.Proxmox.IProxmoxCreateSettingsService, Services.Proxmox.ProxmoxCreateSettingsService>();
        // V8.0 — DB-backed master switch for clone/snapshot.
        builder.Services.AddScoped<Services.Proxmox.IProxmoxCloneSettingsService, Services.Proxmox.ProxmoxCloneSettingsService>();
        // V6.6 — browser LXC console: an interactive shell *inside* an LXC,
        // reached by SSHing to the Proxmox host and running `pct exec`. Reuses
        // the V5.3 SSH PTY connector (IHostShellConnector) + the shared
        // WebSocket bridge / byte pump. Here we wire the ticket store,
        // concurrency tally, the tunables, and the DB-backed master switch
        // (seeded from Stashboard:AllowProxmoxConsole on first run).
        builder.Services.Configure<ProxmoxConsoleOptions>(builder.Configuration.GetSection(ProxmoxConsoleOptions.SectionName));
        builder.Services.AddSingleton<IProxmoxConsoleTicketService, ProxmoxConsoleTicketService>();
        builder.Services.AddSingleton<IProxmoxConsoleSessionRegistry, ProxmoxConsoleSessionRegistry>();
        builder.Services.AddScoped<IProxmoxConsoleSettingsService, ProxmoxConsoleSettingsService>();
        builder.Services.AddHostedService<HealthCheckBackgroundService>();
        builder.Services.AddHostedService<DockerUpdateBackgroundService>();
        builder.Services.AddHostedService<DockerImagePruneBackgroundService>();
        builder.Services.AddHostedService<ProxmoxUpdateBackgroundService>();

        // CORS — allow the Vite dev server (5173) in development.
        builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
            .WithOrigins("http://localhost:5173")
            .AllowAnyHeader()
            .AllowAnyMethod()));

        var app = builder.Build();

        // Apply pending migrations on startup — the single-container deployment
        // has no separate migrator step.
        using (var scope = app.Services.CreateScope())
        {
            var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
            foreach (var note in secretProvisioningNotes)
                logger.LogInformation("{SecretProvisioningNote}", note);

            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            RecoverStaleSqliteMigrationLock(db, logger);
            db.Database.Migrate();
            FinaliseOrphanedSessions(db, logger);
        }

        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi();
            app.MapScalarApiReference();
            app.UseSwagger();
            app.UseSwaggerUI();
            app.UseCors();
        }

        // Cache policy for the SPA. Vite fingerprints every JS/CSS bundle
        // (e.g. index-a1b2c3.js), so those are immutable and cached for a year.
        // index.html references those hashed names and changes every build, so it
        // must never be cached — otherwise a browser keeps loading a stale entry
        // point (and stale bundles) after a release. Always revalidate it.
        var staticFileOptions = new StaticFileOptions
        {
            OnPrepareResponse = ctx =>
            {
                var headers = ctx.Context.Response.Headers;
                if (ctx.File.Name.Equals("index.html", StringComparison.OrdinalIgnoreCase))
                {
                    headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
                    headers["Pragma"] = "no-cache";
                    headers["Expires"] = "0";
                }
                else if (ctx.Context.Request.Path.StartsWithSegments("/assets"))
                {
                    headers["Cache-Control"] = "public, max-age=31536000, immutable";
                }
            },
        };

        app.UseStaticFiles(staticFileOptions);

        app.UseAuthentication();
        app.UseAuthorization();
        app.UseRateLimiter();

        // V5.3 — host terminal upgrades GET .../host-shell/ws to a WebSocket.
        // No WebSocket / SignalR was wired before this phase, so enable it here.
        // KeepAliveInterval sends periodic pings so idle shells survive proxies
        // that drop quiet connections; the app's own idle timeout still closes
        // genuinely inactive sessions server-side.
        app.UseWebSockets(new WebSocketOptions
        {
            KeepAliveInterval = TimeSpan.FromSeconds(30),
        });

        app.MapControllers();

        // SPA fallback — serve index.html for any non-API GET route so React Router
        // works on refresh. Reuses the cache policy above so the fallback-served
        // index.html is also marked no-cache.
        app.MapFallbackToFile("index.html", staticFileOptions);

        app.Run();
    }

    /// <summary>
    /// A freshly started process cannot own any live terminal / exec session, so any
    /// audit row still marked <see cref="HostShellSessionEndReason.Active"/> is an
    /// orphan left by a crash / restart / redeploy that never reached its finalise
    /// step. Close them as <see cref="HostShellSessionEndReason.Interrupted"/> so the
    /// Audit viewer stops showing phantom "Active" sessions. EndedUtc is set to
    /// StartedUtc (the real end time is unknowable) — the Interrupted reason tells
    /// the story. The graceful-shutdown link in the terminal/exec controllers handles
    /// clean stops; this sweep is the backstop for everything else.
    /// </summary>
    private static void FinaliseOrphanedSessions(ApplicationDbContext db, ILogger logger)
    {
        try
        {
            var active = (int)HostShellSessionEndReason.Active;
            var interrupted = (int)HostShellSessionEndReason.Interrupted;

            var closed =
                db.Database.ExecuteSqlRaw(
                    $"UPDATE \"HostShellSessions\" SET \"EndedUtc\" = \"StartedUtc\", \"EndReason\" = {interrupted} WHERE \"EndReason\" = {active};") +
                db.Database.ExecuteSqlRaw(
                    $"UPDATE \"DockerExecSessions\" SET \"EndedUtc\" = \"StartedUtc\", \"EndReason\" = {interrupted} WHERE \"EndReason\" = {active};") +
                db.Database.ExecuteSqlRaw(
                    $"UPDATE \"ProxmoxConsoleSessions\" SET \"EndedUtc\" = \"StartedUtc\", \"EndReason\" = {interrupted} WHERE \"EndReason\" = {active};");

            if (closed > 0)
                logger.LogWarning(
                    "Closed {Count} orphaned terminal/exec session(s) left Active by a previous run (marked Interrupted).",
                    closed);
        }
        catch (Exception ex)
        {
            // Best-effort housekeeping — never block startup over it.
            logger.LogError(ex, "Failed to finalise orphaned terminal/exec audit rows on startup.");
        }
    }

    private static void RecoverStaleSqliteMigrationLock(ApplicationDbContext db, ILogger logger)
    {
        if (!db.Database.IsSqlite())
            return;

        try
        {
            // EF Core keeps a single-row lock table for migrations on SQLite.
            // If a process is terminated mid-migration, the lock row can remain
            // and block every next startup indefinitely. In Stashboard's
            // single-instance deployment model it is safe to clear stale rows
            // before retrying migrations.
            var cleared = db.Database.ExecuteSqlRaw("DELETE FROM \"__EFMigrationsLock\";");
            if (cleared > 0)
                logger.LogWarning("Recovered startup by clearing {Count} stale EF migration lock row(s).", cleared);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1 && ex.Message.Contains("no such table", StringComparison.OrdinalIgnoreCase))
        {
            // First startup before EF has created the lock table.
        }
    }
}

internal static class TimeProviderRegistration
{
    public static IServiceCollection TryAddSingletonTimeProvider(this IServiceCollection services)
    {
        if (!services.Any(s => s.ServiceType == typeof(TimeProvider)))
            services.AddSingleton<TimeProvider>(TimeProvider.System);
        return services;
    }
}

