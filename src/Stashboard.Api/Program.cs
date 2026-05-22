using System.IdentityModel.Tokens.Jwt;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;
using Stashboard.Api.Auth;
using Stashboard.Api.Data;
using Stashboard.Api.Mapping;
using Stashboard.Api.Notifications;
using Stashboard.Api.Services;
using Stashboard.Core.Abstractions;
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

        // Keep JWT claims compact ("sub", "uid", "email", "stmp") — disable the legacy
        // XML-namespace remapping that would otherwise inflate every claim name.
        JwtSecurityTokenHandler.DefaultInboundClaimTypeMap.Clear();

        builder.Services.AddControllers()
            .AddJsonOptions(o =>
                o.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
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
        builder.Services.AddSingleton<IStashboardMapper, StashboardMapper>();
        builder.Services.AddScoped<IServiceStatusNotificationService, ServiceStatusNotificationService>();
        builder.Services.AddScoped<IDockerUpdateNotificationService, DockerUpdateNotificationService>();
        builder.Services.Configure<DockerUpdateOptions>(builder.Configuration.GetSection(DockerUpdateOptions.SectionName));
        // V3.5 — top-level app feature flags (Docker instances page,
        // container-removal gate, etc.). Default-off so destructive
        // actions never light up without the operator opting in.
        builder.Services.Configure<StashboardOptions>(builder.Configuration.GetSection(StashboardOptions.SectionName));
        builder.Services.AddHostedService<HealthCheckBackgroundService>();
        builder.Services.AddHostedService<DockerUpdateBackgroundService>();

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
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.Database.Migrate();
        }

        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi();
            app.MapScalarApiReference();
            app.UseSwagger();
            app.UseSwaggerUI();
            app.UseCors();
        }

        app.UseStaticFiles();

        app.UseAuthentication();
        app.UseAuthorization();
        app.UseRateLimiter();

        app.MapControllers();

        // SPA fallback — serve index.html for any non-API GET route so React Router works on refresh.
        app.MapFallbackToFile("index.html");

        app.Run();
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

