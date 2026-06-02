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
- Editable SMTP / email-server settings stored in the DB and managed from the dedicated **Notifications** page (password encrypted at rest) — no redeploy to change the mail server
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
- **One-click "Update now"** — pulls the new image and recreates the container in place (Watchtower-style, with a per-click confirmation); every attempt written to an immutable audit log. Compose-managed containers on a local socket can opt into a **true Compose-aware recreate** (`docker compose pull` + `up -d <service>`) that honours `env_file`, `depends_on` ordering, and profiles
- **Post-update health verification** — polls the container's health state after recreate; downgrades the audit row to `RecreateFailed` if the container doesn't become healthy within the configured window
- **GitHub Releases enrichment** — for GHCR images, fetches the matching GitHub Release and surfaces the changelog inline in the modal's "What's new" panel and in notification emails

### Docker container diagnostics

- **Inspect panel** — full container config (image digest, command, env, mounts, networks, labels, restart policy, health state, ports); env values matching secret-name heuristics are masked
- **Live logs panel** — real-time NDJSON stream with stdout/stderr toggles, pause/resume/stop, clear, and snapshot download; supports SSH-tunnelled hosts
- **Live stats panel** — per-second CPU/memory/network/block I/O with inline sparklines; no chart library dependency
- **Docker instances page** (`/docker`) — top-down view of every container across every host with inline **Start / Stop / Restart** actions and (gated by `Stashboard:AllowContainerRemoval`) **Remove**; "Open in service" deep link surfaces the Inspect / Logs / Stats panels for tracked containers; auto-refreshes every 10 s
- **Host terminal** (V5.3) — a **Host terminal** button in each connection's header on the Docker page opens an interactive `xterm.js` shell *on the Docker host* over SSH (not inside a container), bridged over a WebSocket with single-use ticket auth. It's connection-scoped (a shell on the host), so it lives at the connection level rather than on a per-container tab. **Off by default** and the most dangerous surface in the product: it requires the server-wide toggle at **Settings → Host terminal** **and** a per-connection **Allow host terminal** opt-in **and** an SSH connection. Every session is audited (who / when / host / duration / bytes / end reason) with per-user & per-host caps and a server-side idle timeout
- **Container exec** (V5.7) — an **Exec** tab on the container modal opens an interactive `xterm.js` shell *inside* a running container via the Docker daemon's `exec` API (the "I just need to run one command in this container" case). Reuses the V5.3 WebSocket + single-use-ticket transport but, because it routes through the daemon rather than SSH, it works for **every** connection type (local socket, TCP+TLS, SSH). The command defaults to `/bin/sh` and is editable per session; live terminal resize works. **Off by default**, gated by the **Settings → Container exec** toggle **and** a per-connection **Allow container exec** opt-in. Every session is audited (who / when / container / command / duration / bytes / end reason) with per-user & per-host caps and a server-side idle timeout
- **Image cleanup / prune** (V5.5) — background sweep that runs `docker image prune` on every Docker connection on a configurable interval (default weekly) to reclaim the `<none>:<none>` dangling images container auto-updates leave behind, plus a **Storage** widget on the Docker page showing image counts and a manual **Prune now** button with a dry-run preview. Master toggle + interval at **Settings → Image cleanup**; per-connection opt-out and an opt-in "also prune unused images" aggressive scope on the connection form. Every run is audited (deleted count + bytes reclaimed). Never touches volumes
- **Session audit viewer** (V5.8) — a read-only **Settings → Audit** page with four tabbed tables — *Host terminal* (V5.3), *Container exec* (V5.7), *Update attempts* (V2.7) and *Image prune* (V5.5) — surfacing who ran what, against which connection / host / container, for how long, the bytes transferred, and how it ended. An **Active** badge flags sessions still open. Backed by owner-scoped, newest-first, paginated read-only endpoints; the host-terminal dialog and container Exec panel link straight here pre-filtered to that connection

- **Health-check tuning** (V5.6) — a **Settings → Health checks** page to tune the probe schedule and the offline-alert reliability logic from V5.3.2: the check interval (how often every service is probed — default 60 s), the failure threshold (consecutive failed scans before a 🔴 *Service unavailable* alert fires), the in-probe retry count (extra attempts on a transient DNS / timeout / network / TLS failure), and the delay between those retries. Each field is explained inline; values are DB-backed and apply on the next scan without a restart. The `STASHBOARD_HealthCheck__*` env vars now only seed the defaults on first run

See [DOCKER_UPDATE_MONITORING_GUIDE.md](./DOCKER_UPDATE_MONITORING_GUIDE.md) for the full walkthrough.

---

## Quick start (Docker Compose)

You don't need the source code, a build toolchain, or even a config file —
Stashboard ships as a prebuilt image on Docker Hub (`vahac/stashboard`). All you
need is the `docker-compose.yml`. For a detailed, beginner-friendly walkthrough
(prerequisites, updating, backups, troubleshooting) see **[INSTALL.md](./INSTALL.md)**.

```bash
# Grab the compose file (or clone the repo)
curl -O https://raw.githubusercontent.com/VahaC/stashboard/main/docker-compose.yml

docker compose up -d        # pulls vahac/stashboard and starts
```

App is on `http://localhost:8080`. Register the first account, log in, click **+ Add service**.

> **No keys to set.** On first start the app generates a strong encryption key and JWT secret automatically and persists them under `/app/Data/.secrets` on the `stashboard-data` volume — so they're reused on every restart and never overwritten by an image update. Just **back up the `stashboard-data` volume**: losing the encryption key makes every stored credential undecryptable.

> **A `.env` file is optional.** Add one only to override a default — change the host port, pin a version, set your own keys, or configure SMTP. Grab the template when you need it:
> ```bash
> curl -O https://raw.githubusercontent.com/VahaC/stashboard/main/.env.example
> mv .env.example .env   # then edit
> ```
> You only need to set keys yourself if you manage secrets externally or are migrating an existing deployment (see [Secrets](#secrets-auto-generated-and-persisted)).

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

To move to a specific version, set `STASHBOARD_TAG=5.8.0` in `.env` first.

**Migrations** are applied automatically by the app on startup — there is no separate migrator step.

**Data is safe** — the SQLite database, uploads, and auto-generated secrets live in Docker volumes (`stashboard-data`, `stashboard-uploads`) that persist across container restarts and image updates. The encryption key and JWT secret are read back from the `stashboard-data` volume on each start, so an update never re-keys your data.

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
| Encryption key | `STASHBOARD_Encryption__Key` | _auto-generated_ | Base64-encoded 32 bytes. Optional — auto-generated and persisted on first run if unset. An explicit value wins and disables auto-generation. |
| JWT secret | `STASHBOARD_Jwt__Secret` | _auto-generated_ | 32+ chars. Optional — auto-generated and persisted on first run if unset. An explicit value wins. |
| Secrets directory | `STASHBOARD_Stashboard__SecretsPath` | next to the DB (`.secrets/`) | Where auto-generated secrets are stored. Defaults to a `.secrets` folder beside the SQLite file so they live on the persisted volume. |
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
| Healthcheck failure threshold | `STASHBOARD_HealthCheck__FailureThreshold` | `3` | Consecutive failed scans before a service is marked Down and an offline alert fires. Guards against false alerts from a single transient blip. Floor `1` = notify on first failure. |
| Healthcheck retry count | `STASHBOARD_HealthCheck__RetryCount` | `2` | Extra retries within one probe on a connection-level failure (DNS, timeout, network, TLS). Real HTTP responses (incl. 5xx) are never retried. |
| Healthcheck retry delay | `STASHBOARD_HealthCheck__RetryDelayMs` | `1000` | Milliseconds between in-probe retries. |
| Docker update scan tick | `STASHBOARD_DockerUpdate__TickIntervalSeconds` | `300` | How often the schedule-driven Docker scan wakes up to look for due watches. Per-watch cadence (default 24 h) is set per service in the UI. Floor: 30 s. Between sweeps the loop also drains the webhook queue every ~5 s. |
| Health verification attempts | `STASHBOARD_DockerUpdate__HealthVerificationMaxAttempts` | `10` | Polls after "Update now" recreate; set to `0` to disable and accept success on container start. |
| Health verification interval | `STASHBOARD_DockerUpdate__HealthVerificationIntervalSeconds` | `3` | Seconds between health polls |
| Allow container removal | `STASHBOARD_Stashboard__AllowContainerRemoval` | `false` | When `true`, the Docker instances page renders the **Remove** action. Off by default — removing a container is irreversible from the UI. |
| Host shell — max sessions/user | `STASHBOARD_Stashboard__HostShell__MaxSessionsPerUser` | `3` | Concurrent host-terminal sessions a single user may hold. |
| Host shell — max sessions/host | `STASHBOARD_Stashboard__HostShell__MaxSessionsPerHost` | `5` | Concurrent host-terminal sessions against one connection. |
| Host shell — idle timeout | `STASHBOARD_Stashboard__HostShell__IdleTimeoutSeconds` | `600` | Server-side inactivity timeout; closes idle sessions regardless of client state. `0` disables. |
| Host shell — ticket TTL | `STASHBOARD_Stashboard__HostShell__TicketTtlSeconds` | `30` | Lifetime of the single-use connect ticket between the authenticated POST and the WebSocket upgrade. |

> **The host terminal's master switch is *not* an env var** — it is a DB-backed toggle managed in the UI at **Settings → Host terminal**, alongside an explanation of every condition and the risks (mirrors the editable SMTP settings). `Stashboard:AllowHostShell` exists only as an optional **first-run seed** for that toggle. The `HostShell__*` rows above are advanced tuning that still apply once the feature is enabled.

> **Email settings are stored in the database and editable from the UI** (Notifications → **Email server (SMTP)**). The `STASHBOARD_Email__*` values above only **seed** the settings row on first startup; after that, manage the provider, host, credentials and from-address from the Notifications page and changes apply without a restart. The SMTP password is encrypted at rest (AES-256-GCM) and never returned by the API.

> **The healthcheck schedule + reliability knobs are editable from the UI** at **Settings → Health checks** (V5.6): `IntervalSeconds`, `FailureThreshold`, `RetryCount` and `RetryDelayMs`. The `STASHBOARD_HealthCheck__*` values above only **seed** the settings row on first startup; after that, manage them from the page and changes apply on the next scan without a restart. `RequestTimeoutSeconds` remains config-only.

### Secrets: auto-generated and persisted

By default you don't manage the encryption key or JWT secret at all. On first
start, if either is unset, the app generates a cryptographically strong value
(AES-256 key / 48-byte signing secret) and writes it to the secrets directory —
by default `.secrets/` next to the SQLite database, which in Docker is on the
`stashboard-data` volume. On every later start the same file is read back, so:

- **First deploy** → fresh keys generated and saved.
- **Updates / restarts** → existing keys loaded, never overwritten — encrypted data stays decryptable.

> **Back up the `stashboard-data` volume.** Losing the encryption key means losing every stored credential. The secret files live under `/app/Data/.secrets` (owner-only permissions).

**Supplying your own keys** (external secret manager, or migrating an existing
deployment) — set the env vars and they take precedence; the app then won't
generate or touch the persisted files for that secret:

```bash
openssl rand -base64 32   # encryption key  -> STASHBOARD_ENCRYPTION_KEY
openssl rand -base64 48   # JWT secret       -> STASHBOARD_JWT_SECRET
```

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
      # Optional (V5.2): bind-mount a Compose project directory read-only and
      # set the connection's "Compose project path" to make "Update now" run
      # `docker compose pull` + `up -d <service>` instead of the raw recreate.
      # - /srv/my-stack:/compose-projects/home-server:ro
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

# Host terminal (V5.3) — gated: AllowHostShell flag + per-connection opt-in + SSH connection
POST   /api/docker/connections/{id}/host-shell/ticket                     → HostShellTicketResponse    (single-use, short-TTL ticket; 403/400/404 when not eligible)
GET    /api/docker/connections/{id}/host-shell/ws?ticket=&cols=&rows=     → WebSocket upgrade          (ticket-authenticated interactive SSH PTY; binary = stdin/stdout, text = resize)

# Container exec (V5.7) — gated: AllowContainerExec flag + per-connection AllowExec opt-in (any host type)
POST   /api/docker/connections/{id}/containers/{name}/exec/ticket  { command? } → ContainerExecTicketResponse  (single-use, short-TTL ticket binding the container + command; 403/404 when not eligible)
GET    /api/docker/connections/{id}/containers/{name}/exec/ws?ticket=&cols=&rows= → WebSocket upgrade   (ticket-authenticated interactive exec PTY; binary = stdin/stdout, text = resize)

GET    /api/features                                                      → StashboardFeatures         (server-side feature flags the UI gates against)
GET    /api/settings/host-shell                                           → HostShellSettings          (host-terminal master switch — Settings page)
PUT    /api/settings/host-shell                  { enabled }              → 204                        (toggle the host terminal server-wide)
GET    /api/settings/container-exec                                       → ContainerExecSettings      (container-exec master switch — Settings page)
PUT    /api/settings/container-exec              { enabled }              → 204                        (toggle container exec server-wide)
GET    /api/settings/health-check                                         → HealthCheckSettings        (offline-alert tuning — Settings page)
PUT    /api/settings/health-check   { intervalSeconds, failureThreshold, retryCount, retryDelayMs } → 204  (tune the probe schedule + reliability knobs)

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

✅ **V5.1 — Secure key auto-provisioning** _(shipped in 5.1.0)_ — the encryption key and JWT secret are generated and persisted automatically on first run, and preserved across updates. See the [CHANGELOG](./CHANGELOG.md).

✅ **V5.2 — True Compose-aware recreate** _(shipped in 5.2.0)_ — when a local-socket connection has a bind-mounted **Compose project path**, "Update now" runs `docker compose pull` + `up -d <service>` (honouring `env_file`, `depends_on` ordering, and profiles) instead of the raw recreate. The image now ships the `docker compose` binary; falls back to the raw recreate when not configured. See the [CHANGELOG](./CHANGELOG.md) and [guide §5.1a](./DOCKER_UPDATE_MONITORING_GUIDE.md).

✅ **V5.3 — Host terminal (browser SSH shell to the Docker host)** _(shipped in 5.3.0)_ — opens an interactive `xterm.js` shell on the **host** of an SSH connection, bridged over a WebSocket with single-use ticket auth (the transport later shell phases reuse). Off by default and gated three ways (global flag + per-connection opt-in + SSH connection); every session is audited with per-user/host caps and an idle timeout. _(Relocated from the container modal to a **Host terminal** button in the connection header in 5.7.0.)_ See the [CHANGELOG](./CHANGELOG.md).

✅ **V5.3.1 — Tag-pattern filter correctness + version tags** _(shipped in 5.3.1)_ — fixes the per-watch tag-pattern filter so it resolves the genuinely newest matching tag (semver outranks non-semver, full-match regex, paginated registries fully scanned) instead of getting stuck on a phantom *Update available*, and the UI now shows the resolved version tag next to each `sha256` digest. See the [CHANGELOG](./CHANGELOG.md).

✅ **V5.3.2 — Reliable offline alerts (no false positives)** _(shipped in 5.3.2)_ — health checks now retry connection-level failures within a probe and require N consecutive failed scans before marking a service Down, so a single transient blip (DNS hiccup, timeout, network/TLS glitch on the monitoring host) no longer fires a false "Service unavailable" Telegram alert. Tunable via `STASHBOARD_HealthCheck__FailureThreshold` / `RetryCount` / `RetryDelayMs`. See the [CHANGELOG](./CHANGELOG.md).

✅ **V5.4 — Compose project grouping & bulk update** _(shipped in 5.4.0)_ — containers on the instances page are now grouped by their `com.docker.compose.project` label under a project header card with a *"N of M tracked services have updates available"* counter and an **Update project** button. With V5.2 set up the button shells out one `docker compose pull` + `up -d` against the project root (so Compose honours `depends_on` ordering); otherwise it falls back to per-service raw recreate sorted by the `com.docker.compose.depends_on` labels. One aggregate audit row + one child row per service. See the [CHANGELOG](./CHANGELOG.md) and [guide §5.1b](./DOCKER_UPDATE_MONITORING_GUIDE.md).

✅ **V5.5 — Image cleanup / prune** _(shipped in 5.5.0)_ — background sweep runs `docker image prune` on every Docker connection on a configurable interval (default weekly) to reclaim dangling `<none>:<none>` images left behind by container auto-updates. A **Storage** widget on the Docker page shows image counts and a manual **Prune now** button with a dry-run preview; **Settings → Image cleanup** holds the master toggle + interval. Per-connection opt-out (`AllowImagePrune`) and an opt-in aggressive scope (`PruneUnusedImages`) on the connection form. Every run is audited (deleted count + bytes reclaimed). Volumes are never touched. See the [CHANGELOG](./CHANGELOG.md).

✅ **V5.6 — Health-check tuning page** _(shipped in 5.6.0)_ — a **Settings → Health checks** page to tune the probe schedule and the V5.3.2 offline-alert reliability logic from the UI: the check interval (how often every service is probed — default 60 s), the failure threshold (consecutive failed scans before a service is marked Down and a 🔴 *Service unavailable* alert fires), the in-probe retry count (extra attempts on a transient DNS / timeout / network / TLS failure — real HTTP responses are never retried), and the delay between those retries. Each field is explained inline. Values are DB-backed and apply on the next scan without a restart; the `STASHBOARD_HealthCheck__*` env vars now only seed the defaults on first run. See the [CHANGELOG](./CHANGELOG.md).

✅ **V5.7 — Container exec (browser terminal into a container)** _(shipped in 5.7.0)_ — an **Exec** tab on the container modal (and the shell button on each container card) opens an interactive `xterm.js` shell *inside* a running container via the Docker daemon's `exec` API. Reuses the V5.3 WebSocket + single-use-ticket transport, but because it routes through the daemon (not SSH) it works for **every** connection type. The command defaults to `/bin/sh` and is editable per session; live resize works. Off by default, gated by the **Settings → Container exec** toggle **and** a per-connection **Allow container exec** opt-in; every session is audited (connection / container / command / duration / bytes / end reason) with per-user & per-host caps and an idle timeout. This release also **moved the host terminal to the connection header** (it's host-scoped, not per-container) and **retargeted the card's shell button to Exec**. See the [CHANGELOG](./CHANGELOG.md).

✅ **V5.8 — Session audit viewer** _(shipped in 5.8.0)_ — a read-only **Settings → Audit** page surfacing the audit rows the product already records but previously kept write-only. Four tabbed tables: *Host terminal* (V5.3) and *Container exec* (V5.7) sessions — who ran it, against which connection / host / container, the command, start / end, duration, bytes in / out, end reason — plus *Update attempts* (V2.7) and *Image prune* (V5.5) for convenience. An **Active** badge flags open sessions. Backed by four owner-scoped, newest-first, paginated `GET` endpoints under `/api/docker` (`?skip=&take=`, capped page size, optional `?connectionId=` filter); no write/delete verbs. The host-terminal dialog and container Exec panel each link here pre-filtered to that connection. No new auditing — purely a read path over existing rows. See the [CHANGELOG](./CHANGELOG.md).

These are the next items on the roadmap; they're ordered roughly simplest → most complex.

| Phase | Feature | Description |
|---|---|---|
| V6.0 | **Proxmox LXC update monitoring** | Track pending package updates on Proxmox LXC containers via the Proxmox REST API (`GET /nodes/{node}/apt/update` for the host; `exec`-based `apt list --upgradable` inside each LXC). Same Hourly/Daily/Weekly schedule model as Docker watches; same email notification channel. |
| V6.1 | **Browser-based SSH client for Proxmox LXC** | Closes the loop on V6.0 — `pct enter <vmid>` over the same `xterm.js` component. SSH keys stored encrypted; host-key TOFU on first connect; all V5.3 / V5.7 guardrails apply. |

## License

MIT
