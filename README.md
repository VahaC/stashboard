# Stashboard

**Self-hosted homelab dashboard** — one place for every service you run.

Each service appears as a card showing its favicon (or a custom logo), name, live status indicator, category badge, and tags. Click a card to open a modal with four tabs:

- **General** — URL, display name, categories, tags, custom logo upload
- **Healthcheck** — configure the HTTP check, view history, trigger a manual check
- **Credentials** — store usernames, passwords, API keys, and notes encrypted at rest (AES-256-GCM)
- **Docker** — track container image updates, inspect containers, stream logs and stats, trigger one-click updates

Beyond the dashboard, Stashboard doubles as a **lightweight Docker manager**: a dedicated page lists every container across every connected Docker host (local socket, remote TCP+TLS, or SSH tunnel) with inline start / stop / restart actions, live stats sparklines, and a direct link to the container's log and inspect panels.

Built as **ASP.NET Core 10 Web API + React SPA**, deployed as a **single Docker image** with SQLite — no separate database container, no migrator sidecar.

---

## Stack

- **Backend:** ASP.NET Core 10, controllers MVC, custom auth (PBKDF2-SHA256 passwords, rotating refresh tokens with family reuse-detection, SecurityStamp server-side invalidation), JWT Bearer
- **Frontend:** Vite + React 19 + TypeScript + Tailwind v4 + shadcn-style UI + TanStack Query + react-router-dom v7
- **DB:** SQLite (via EF Core 10 + WAL mode) — a single file on the `stashboard-data` volume; no separate database container
- **Crypto:** AES-256-GCM for credentials at rest, key from env var
- **API docs:** Scalar (`/scalar/v1` in dev)
- **Deployment:** single Docker image — API serves the built React from `wwwroot/` and applies any pending schema migrations on startup

## Features

### Core

- Multi-user JWT auth (register / login / refresh / logout / logout-all)
- Email verification and password reset flows
- Editable SMTP / email-server settings stored in the DB and managed from the **Account** page (password encrypted at rest) — no redeploy to change the mail server
- Service cards with favicon (auto-resolved) or custom uploaded logo, status dot, category badge, tags
- Add / edit modal organised into tabs: **General · Healthcheck · Credentials · Docker**
- Per-user **categories** (with color) and **tags**
- Light / dark / system theme — picker in **Account** + quick toggle in the sidebar; preference saved server-side and synced across devices
- Background healthcheck loop + manual "Check now" button per service
- AES-256-GCM credential encryption at rest
- JSON backup export / import per user (covers full schema: Docker connections + watches + encrypted secrets, service flags, user settings)
- Deep link support for direct navigation to service modals

### Docker container update tracking

- Per-service watch that compares the running container's image digest against the registry
- Surfaces an **Update** badge on the dashboard card; emails the owner once per unique digest (no spam)
- **Supported registries:** Docker Hub, GHCR, and self-hosted registries via Basic auth (Harbor, Nexus, Gitea Packages) or AWS ECR — Quay works via the generic Distribution v2 client
- **Tag-pattern filtering** — regex filter to ignore pre-release tags (e.g. `-rc`, `-beta`); semver-aware preset dropdown
- **Flexible check schedules** — Hourly (1/2/4/6/12/24 h), Daily at a fixed UTC time, or Weekly; the background loop handles missed windows gracefully
- **Docker host types:** local socket, remote TCP+TLS, or SSH-tunnelled daemon (no exposed Docker port required)
- **Webhook receiver** — per-watch token-authenticated `POST` endpoint so registries can push instant update notifications; hybrid fallback to the schedule-driven sweep
- **One-click "Update now"** — pulls the new image and recreates the container in place (Watchtower-style, with a per-click confirmation); every attempt written to an immutable audit log
- **Post-update health verification** — polls the container's health state after recreate; downgrades the audit row to `RecreateFailed` if the container doesn't become healthy within the configured window
- **GitHub Releases enrichment** — for GHCR images, fetches the matching GitHub Release and surfaces the changelog inline in the modal's "What's new" panel and in notification emails

### Docker container diagnostics

- **Inspect panel** — full container config (image digest, command, env, mounts, networks, labels, restart policy, health state, ports); env values matching secret-name heuristics are masked
- **Live logs panel** — real-time NDJSON stream with stdout/stderr toggles, pause/resume/stop, clear, and snapshot download; supports SSH-tunnelled hosts
- **Live stats panel** — per-second CPU/memory/network/block I/O with inline sparklines; no chart library dependency
- **Docker instances page** (`/docker`) — top-down view of every container across every host with inline **Start / Stop / Restart** actions and (gated by `Stashboard:AllowContainerRemoval`) **Remove**; "Open in service" deep link surfaces the Inspect / Logs / Stats panels for tracked containers; auto-refreshes every 10 s

See [DOCKER_UPDATE_MONITORING_GUIDE.md](./DOCKER_UPDATE_MONITORING_GUIDE.md) for the full walkthrough.

---

## Quick start (Docker Compose)

You don't need the source code or a build toolchain — Stashboard ships as a
prebuilt image on Docker Hub (`vahac/stashboard`). All you need is the
`docker-compose.yml` and a `.env` file:

```bash
# Grab the two files (or clone the repo)
curl -O https://raw.githubusercontent.com/VahaC/stashboard/main/docker-compose.yml
curl -O https://raw.githubusercontent.com/VahaC/stashboard/main/.env.example
mv .env.example .env

# Fill in .env:
#   STASHBOARD_ENCRYPTION_KEY   openssl rand -base64 32
#   STASHBOARD_JWT_SECRET       openssl rand -base64 48
#   STASHBOARD_TAG              latest  (or pin a release, e.g. 1.2.0)

docker compose up -d        # pulls vahac/stashboard and starts
```

App is on `http://localhost:8080`. Register the first account, log in, click **+ Add service**.

Everything runs in **one container**: the app stores its data in a SQLite file on the `stashboard-data` volume and applies any pending schema migrations on startup. There is no separate database or migrator container.

> **Build from source instead?** Layer on the build override:
> `docker compose -f docker-compose.yml -f docker-compose.build.yml up -d --build`

## Deployment (production update)

Updating is just pulling a newer image and recreating the container — no source
checkout or local build. Run `deploy.sh` from the directory holding your
`docker-compose.yml`:

```bash
cd /opt/stashboard

# first time only
chmod +x deploy.sh

./deploy.sh
```

The script:
1. `docker compose pull` — fetches the image at the tag set by `STASHBOARD_TAG`
2. (Re)starts the single `app` container
3. Waits for the app to become healthy; on failure, prints the last 50 log lines

To move to a specific version, set `STASHBOARD_TAG=1.2.0` in `.env` first.

**Migrations** are applied automatically by the app on startup — there is no separate migrator step.

**Data is safe** — the SQLite database and uploads live in Docker volumes (`stashboard-data`, `stashboard-uploads`) that persist across container restarts and image updates.

> **Maintainer:** see [PUBLISHING.md](./PUBLISHING.md) for how images are built and published, and how to cut a release.

### Migrating from a previous PostgreSQL deployment

Older versions stored data in PostgreSQL. To move an existing database to the new SQLite single-container layout, use the one-shot copy tool (run from a checkout with the .NET SDK; PostgreSQL is only read, never modified):

```bash
dotnet run --project src/Stashboard.Migrations -- pg-to-sqlite \
  --source "Host=...;Database=stashboard;Username=...;Password=..." \
  --target "Data/app.db"
```

Then mount the resulting `app.db` into the `stashboard-data` volume. **Carry the same `STASHBOARD_Encryption__Key` over** — the copy preserves encrypted values verbatim, so the original key is required to decrypt them.

If the deploy fails:
```bash
docker compose logs app        # full logs
docker compose up -d --no-deps app  # retry without rebuilding
```

---

## Quick start (local dev)

Two processes — API on `:5254`, Vite dev server on `:5173` (proxies `/api` and `/uploads` to the API).

No database container is needed — the API creates and migrates a local SQLite file (`Data/app_dev.db`) on startup.

```bash
# Terminal 1 — API (creates + migrates Data/app_dev.db on startup)
dotnet run --project src/Stashboard.Api

# Terminal 2 — frontend
cd frontend
npm install
npm run dev
```

Open `http://localhost:5173`.

> `appsettings.Development.json` uses an all-zeros encryption key and a weak JWT secret — **never use these values in production**.

API docs in dev: `http://localhost:5254/scalar/v1`.

## Configuration

All settings can be overridden via env vars prefixed with `STASHBOARD_` (use `__` to descend into a section).

| Setting | Env var | Default | Notes |
|---|---|---|---|
| Connection string | `STASHBOARD_ConnectionStrings__DefaultConnection` | `Data Source=Data/app.db` | SQLite. In Docker the file lives on the `stashboard-data` volume |
| Encryption key | `STASHBOARD_Encryption__Key` | — | **Required.** Base64-encoded 32 bytes |
| JWT secret | `STASHBOARD_Jwt__Secret` | — | **Required.** 32+ chars |
| Access-token TTL | `STASHBOARD_Jwt__AccessTokenMinutes` | `15` | Minutes |
| Refresh-token TTL | `STASHBOARD_Jwt__RefreshTokenDays` | `30` | Days |
| Require confirmed email | `STASHBOARD_Jwt__RequireConfirmedEmail` | `false` | When `true`, login is rejected for unconfirmed users |
| Email provider | `STASHBOARD_Email__Provider` | `LogOnly` | `LogOnly` (writes to logs — dev/CI) or `Smtp` (real send) |
| SMTP host | `STASHBOARD_Email__Host` | `smtp.gmail.com` | Required when `Provider=Smtp` |
| SMTP port | `STASHBOARD_Email__Port` | `587` | STARTTLS |
| SMTP username | `STASHBOARD_Email__Username` | — | Gmail address (full email) |
| SMTP password | `STASHBOARD_Email__Password` | — | Gmail **App Password** (NOT account password) |
| From address | `STASHBOARD_Email__FromAddress` | `no-reply@stashboard.local` | Must equal Username for Gmail |
| App base URL | `STASHBOARD_Email__AppBaseUrl` | `http://localhost:5173` | Used to build links inside emails |
| Healthcheck interval | `STASHBOARD_HealthCheck__IntervalSeconds` | `60` | Seconds |
| Healthcheck timeout | `STASHBOARD_HealthCheck__RequestTimeoutSeconds` | `10` | Per-request |
| Docker update scan tick | `STASHBOARD_DockerUpdate__TickIntervalSeconds` | `300` | How often the schedule-driven Docker scan wakes up to look for due watches. Per-watch cadence (default 24 h) is set per service in the UI. Floor: 30 s. Between sweeps the loop also drains the webhook queue every ~5 s. |
| Health verification attempts | `STASHBOARD_DockerUpdate__HealthVerificationMaxAttempts` | `10` | Polls after "Update now" recreate; set to `0` to disable and accept success on container start. |
| Health verification interval | `STASHBOARD_DockerUpdate__HealthVerificationIntervalSeconds` | `3` | Seconds between health polls |
| Allow container removal | `STASHBOARD_Stashboard__AllowContainerRemoval` | `false` | When `true`, the Docker instances page renders the **Remove** action. Off by default — removing a container is irreversible from the UI. |

> **Email settings are stored in the database and editable from the UI** (Account → **Email server (SMTP)**). The `STASHBOARD_Email__*` values above only **seed** the settings row on first startup; after that, manage the provider, host, credentials and from-address from the Account page and changes apply without a restart. The SMTP password is encrypted at rest (AES-256-GCM) and never returned by the API.

### Generating secrets

```bash
openssl rand -base64 32   # encryption key
openssl rand -base64 48   # JWT secret
```

> Losing the encryption key means losing every stored credential. Back it up.

### Local secrets (User Secrets)

`appsettings*.json` files are committed to git and must **never** contain real passwords or keys — even in `appsettings.Development.json`.

For local development .NET's built-in **User Secrets** mechanism stores overrides in `%APPDATA%\Microsoft\UserSecrets\<id>\secrets.json` — completely outside the repository.

#### First-time setup

```bash
# SMTP (Gmail App Password — NOT your account password)
# Create one at: https://myaccount.google.com/apppasswords  (requires 2FA)
dotnet user-secrets set "Email:Provider"     "Smtp"                   --project src/Stashboard.Api
dotnet user-secrets set "Email:Username"     "you@gmail.com"          --project src/Stashboard.Api
dotnet user-secrets set "Email:Password"     "xxxx xxxx xxxx xxxx"    --project src/Stashboard.Api
dotnet user-secrets set "Email:FromAddress"  "you@gmail.com"          --project src/Stashboard.Api
```

If you don't need real email sending locally, skip the above — `appsettings.json` already defaults to `Provider: LogOnly` which writes emails to the log instead.

#### Useful commands

```bash
dotnet user-secrets list   --project src/Stashboard.Api
dotnet user-secrets remove "Email:Password" --project src/Stashboard.Api
dotnet user-secrets clear  --project src/Stashboard.Api
```

#### Production / Docker

Use environment variables (already wired via `STASHBOARD_` prefix):

```bash
STASHBOARD_Email__Provider=Smtp
STASHBOARD_Email__Username=you@gmail.com
STASHBOARD_Email__Password=xxxx xxxx xxxx xxxx
STASHBOARD_Email__FromAddress=you@gmail.com
STASHBOARD_Encryption__Key=<base64-32-bytes>
STASHBOARD_Jwt__Secret=<base64-48-bytes>
```

```yaml
# docker-compose.prod.yml — keep secrets in a .gitignored .env file
services:
  app:
    image: vahac/stashboard:${STASHBOARD_TAG:-latest}
    env_file:
      - .env.prod   # add .env.prod to .gitignore !
```

## Docker container update tracking

Stashboard can mark any service as Docker-backed and periodically compare the running container's image digest against what its registry advertises. When a newer digest is published, the dashboard card shows an **Update** badge and the owner gets an email (once per unique digest — no spam).

**To enable on your deployment**, give the Stashboard container access to a Docker daemon — the local socket on the same host, a remote daemon over TCP+TLS, or a remote daemon reachable over an SSH tunnel (no exposed Docker port required). The local-socket path is one line in `docker-compose.yml`:

```yaml
services:
  app:
    # ...
    volumes:
      - stashboard-uploads:/app/wwwroot/uploads
      # Read-only is enough for digest tracking + notifications.
      # Drop `:ro` if you also want the "Update now" button to
      # pull + recreate containers from the UI.
      - /var/run/docker.sock:/var/run/docker.sock:ro
```

Then in the UI: open any service → **Docker** tab → **+ Add container** → fill in a short label, image reference (e.g. `ghcr.io/owner/repo:tag`), and container name → **Test connection** → **Save**.

Full walkthrough — composite services, TLS for remote hosts, SSH-tunnelled hosts, webhook receivers for instant updates, one-click **Update now**, private registry credentials, rate-limit math, the 9 most common errors with diagnostic commands — in [DOCKER_UPDATE_MONITORING_GUIDE.md](./DOCKER_UPDATE_MONITORING_GUIDE.md).

## API surface

Cookie-less; pass `Authorization: Bearer <accessToken>` on every request.

```
POST   /api/auth/register      { email, password }     → AuthResponse
POST   /api/auth/login         { email, password }     → AuthResponse
POST   /api/auth/refresh       { refreshToken }        → AuthResponse
POST   /api/auth/logout        { refreshToken }        → 204
POST   /api/auth/logout-all                            → 204  (rotates SecurityStamp, revokes all sessions)
GET    /api/auth/me                                    → UserResponse

GET    /api/account/profile                            → ProfileResponse
PATCH  /api/account/profile    { displayName, theme? }→ 204
PUT    /api/account/theme      { theme }               → 204  ("system" | "light" | "dark")
POST   /api/account/change-password { currentPassword, newPassword }  → 204
POST   /api/account/change-email    { newEmail, currentPassword }     → 204  (sends link to new address)
POST   /api/account/confirm-email-change { token }     → 204
DELETE /api/account            { currentPassword }     → 204
GET    /api/account/email-settings                     → EmailSettingsResponse  (app-wide SMTP config; password masked)
PUT    /api/account/email-settings  UpdateEmailSettings → 204  (tri-state password: keep / set / clear)

POST   /api/account/forgot-password      { email }            → 204  (always — no email enumeration)
POST   /api/account/reset-password       { email, token, newPassword } → 204
POST   /api/account/confirm-email        { email, token }     → 204
POST   /api/account/resend-confirmation  { email }            → 204  (always)

GET    /api/services                                   → Service[]
POST   /api/services           ServiceUpsert           → Service
PUT    /api/services/{id}      ServiceUpsert           → Service
DELETE /api/services/{id}                              → 204
POST   /api/services/{id}/check                        → Service (status refreshed)
POST   /api/services/{id}/logo (multipart)             → { path }

GET    /api/services/{id}/docker/watches                                  → DockerWatch[]
POST   /api/services/{id}/docker/watches            DockerWatchUpsert     → DockerWatch    (201)
GET    /api/services/{id}/docker/watches/{watchId}                        → DockerWatch
PUT    /api/services/{id}/docker/watches/{watchId}  DockerWatchUpsert     → DockerWatch
DELETE /api/services/{id}/docker/watches/{watchId}                        → 204
POST   /api/services/{id}/docker/watches/{watchId}/check                  → DockerWatch    (digest comparison refreshed)
POST   /api/services/{id}/docker/watches/test?watchId={id?}               → DockerWatchTestResponse
POST   /api/services/{id}/docker/watches/{watchId}/webhook/rotate         → DockerWatch    (generate or rotate webhook token)
DELETE /api/services/{id}/docker/watches/{watchId}/webhook                → DockerWatch    (disable webhook delivery)
POST   /api/services/{id}/docker/watches/{watchId}/update                 → DockerWatchUpdateResponse  (pull + recreate; returns audit row + refreshed watch)
GET    /api/services/{id}/docker/watches/{watchId}/updates                → DockerUpdateAttempt[]      (newest-first audit history, capped at 50)
GET    /api/services/{id}/docker/watches/{watchId}/inspect                → DockerContainerInspect    (slimmed docker inspect; env values for secret-looking keys masked)
GET    /api/services/{id}/docker/watches/{watchId}/logs?follow=&tail=…    → NDJSON (chunked)          (live container logs; query: follow, tail, since, timestamps, stdout, stderr)
GET    /api/services/{id}/docker/watches/{watchId}/stats?oneShot=         → NDJSON (chunked)          (per-second CPU/mem/net/blkio samples)

# Docker instances page
GET    /api/docker/connections/{id}/instance/containers                   → DockerContainerCard[]
POST   /api/docker/connections/{id}/instance/containers/{name}/start      → DockerContainerActionResponse
POST   /api/docker/connections/{id}/instance/containers/{name}/stop       → DockerContainerActionResponse
POST   /api/docker/connections/{id}/instance/containers/{name}/restart    → DockerContainerActionResponse
DELETE /api/docker/connections/{id}/instance/containers/{name}            → DockerContainerActionResponse  (403 unless AllowContainerRemoval=true)

GET    /api/features                                                      → StashboardFeatures         (server-side feature flags the UI gates against)

# Public webhook receiver (no JWT; the URL token is the auth)
POST   /api/docker/webhooks/{watchToken}     (any body)           → 202 Accepted

GET    /api/categories                                 → Category[]
POST   /api/categories         { name, color }         → Category
PUT    /api/categories/{id}    { name, color }         → Category
DELETE /api/categories/{id}                            → 204

GET    /api/tags                                       → Tag[]
POST   /api/tags               { name }                → Tag
DELETE /api/tags/{id}                                  → 204

GET    /api/backup/export                              → application/json (file)
POST   /api/backup/import      (multipart)             → { imported }
```

## Project structure

```
src/
├── Stashboard.Core/             # Domain entities, enums, abstractions, options (stack-agnostic)
├── Stashboard.Infrastructure/   # AES, favicon resolver, healthcheck client, Docker/SSH/registry/GitHub/AWS clients
├── Stashboard.Migrations/       # One-shot PostgreSQL→SQLite data-migration tool (pg-to-sqlite command)
└── Stashboard.Api/              # Controllers + JWT + DbContext + EF migrations + BackupService + HostedServices
frontend/
├── src/
│   ├── components/              # AppLayout, ProtectedRoute, ServiceModal, DockerWatchSection, ui/*
│   ├── pages/                   # Login, Register, Dashboard, Docker, Categories, Tags, Backup
│   └── lib/                     # api client, auth-store, queries, types
tests/
└── Stashboard.Tests/
```

## Database migrations

The database is **SQLite** and migrations live in `src/Stashboard.Api/Migrations`. The app **applies pending migrations automatically on startup** in every environment.

### Add a new migration

```bash
dotnet ef migrations add <MigrationName> --project src/Stashboard.Api
```

### Apply / roll back manually (optional)

```bash
dotnet ef database update                            --project src/Stashboard.Api   # apply all
dotnet ef database update <PreviousMigrationName>    --project src/Stashboard.Api   # roll back to
```

---

## Planned features (V5+)

These are the next items on the roadmap, ordered from simplest to most complex.

| Phase | Feature | Description |
|---|---|---|
| V5.1 | **Compose-aware recreate** | True `docker compose up -d <service>` recreate that honours lifecycle hooks, `depends_on` ordering, and `env_file` resolution — instead of the current Watchtower-style raw `ContainerInspect` + recreate. Requires the `docker compose` binary and a bind-mounted Compose project path. |
| V5.2 | **Compose project grouping & bulk update** | Group containers on the instances page by `com.docker.compose.project` label; a project-level "Update all" button drives `docker compose pull && up -d` (with V5.1) or falls back to per-container recreate in `depends_on` order. |
| V5.3 | **Image cleanup / prune** | Scheduled background task that calls `ImagesPruneAsync` per host to remove dangling images left behind by "Update now"; "Storage" widget on the instances page + manual **Prune now** button. |
| V5.4 | **Proxmox LXC update monitoring** | Track pending package updates on Proxmox LXC containers via the Proxmox REST API (`GET /nodes/{node}/apt/update` for the host; `exec`-based `apt list --upgradable` inside each LXC). Same Hourly/Daily/Weekly schedule model as Docker watches; same email notification channel. |
| V5.5 | **Container exec (browser terminal)** | `xterm.js` terminal in the modal backed by a SignalR/WebSocket bridge to `docker exec`. Off by default; admin only; every session audited. |
| V5.6 | **Browser-based SSH client for Proxmox LXC** | Closes the loop on V5.4 — `pct enter <vmid>` over the same `xterm.js` component. SSH keys stored encrypted; host-key TOFU on first connect; all V5.5 guardrails apply. |

## License

MIT
