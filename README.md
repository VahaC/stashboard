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
- **Home Assistant integration via MQTT** (V9.0 – V9.1) — publishes each container's / guest's running state, each Docker container's image-update status, and each service's online/offline health to your MQTT broker as Home Assistant **auto-discovered** entities, plus the signals Stashboard **computes**: pending-update **counts** (per Docker host / Proxmox node / LXC), a per-node **alert** verdict (`problem` sensor with the CPU/memory/storage/thermal/SMART/network breakdown), per-guest **backup freshness**, and estate **roll-ups** on a single Stashboard device; managed from **Settings → Home Assistant** (off by default, broker password encrypted at rest)
- AES-256-GCM credential encryption at rest
- JSON backup export / import per user (covers full schema: Docker connections + watches + Proxmox connections + per-guest monitoring intent + encrypted secrets, service flags, user settings, and the MQTT integration config)
- Deep link support for direct navigation to service modals

### Docker container update tracking

- Per-service watch that compares the running container's image digest against the registry
- Surfaces an **Update** badge on the dashboard card; emails the owner once per unique digest (no spam)
- **Supported registries:** Docker Hub, GHCR, and self-hosted registries via Basic auth (Harbor, Nexus, Gitea Packages) or AWS ECR — Quay works via the generic Distribution v2 client
- **Tag-pattern filtering** — regex filter to ignore pre-release tags (e.g. `-rc`, `-beta`); semver-aware preset dropdown
- **Flexible check schedules** — Hourly (1/2/4/6/12/24 h), Daily at a fixed UTC time, or Weekly; the background loop handles missed windows gracefully
- **Docker host types:** local socket, remote TCP+TLS, or SSH-tunnelled daemon (no exposed Docker port required)
- **Webhook receiver** — per-watch token-authenticated `POST` endpoint so registries can push instant update notifications; hybrid fallback to the schedule-driven sweep
- **One-click "Update now"** — pulls the new image and recreates the container in place (Watchtower-style, with a per-click confirmation); every attempt written to an immutable audit log. Compose-managed containers on a local socket get a **true Compose-aware recreate** (`docker compose pull` + `up -d <service>`) that honours `env_file`, `depends_on` ordering, and profiles — since V7.1 each project's directory is discovered from the container's own compose labels, so it works per project on multi-project hosts. Since V9.2 it can even update **Stashboard itself** — the recreate is offloaded to a detached helper container so the app doesn't kill the process mid-update
- **Post-update health verification** — polls the container's health state after recreate; downgrades the audit row to `RecreateFailed` if the container doesn't become healthy within the configured window
- **GitHub Releases enrichment** — for GHCR images, fetches the matching GitHub Release and surfaces the changelog inline in the modal's "What's new" panel and in notification emails

### Docker container diagnostics

- **Inspect panel** — full container config (image digest, command, env, mounts, networks, labels, restart policy, health state, ports); env values matching secret-name heuristics are masked
- **Live logs panel** — real-time NDJSON stream with stdout/stderr toggles, pause/resume/stop, clear, and snapshot download; supports SSH-tunnelled hosts
- **Live stats panel** — per-second CPU/memory/network/block I/O with inline sparklines; no chart library dependency
- **Docker instances page** (`/docker`) — a **connection-switcher** layout (V5.9) that puts a page-level *Containers / Running / Stopped / Updates* summary strip first, then a horizontal pill row to pick one Docker host as the context (or *All connections* to see them all). Each host renders a compact summary card with the **Storage** widget folded in as a collapsible row, and packed **Compose project groups** sized to exactly fit their cards — several small projects sit side-by-side on one row. The flat container card keeps inline **Start / Stop / Restart** actions and (gated by `Stashboard:AllowContainerRemoval`) **Remove**, a coloured *(healthy)* status, the diagnostic icon row (Inspect / Logs / Stats / Notifications / Exec) toggle-able from the page-level **Diagnostics** preference, and "Open in service" deep links surfacing the Inspect / Logs / Stats panels for tracked containers. Auto-refreshes every 10 s
- **Visual Compose viewer + editor** (V7.0/V7.1, reworked into a modal in V7.1.1) — Compose projects are **auto-discovered** from each container's `com.docker.compose.project` / `…working_dir` labels (no per-project config). A **Compose** button appears on **every Compose project's group header** on the Docker page (single-container projects are no longer collapsed into "Other containers" — each shows as its own named group) and opens a **modal scoped to that one project** (the old whole-host button and the `/projects/{id}/compose…` pages are gone). The modal has **one tab per container** (image, ports, mounts, env, labels, command, restart policy, `deploy.resources` limits — each wearing the live runtime state of its matching container) plus a separate **Shared resources** tab (V7.3) for the Compose top-level elements that are shared across containers. That tab holds four sections — **Networks / Volumes / Secrets / Configs** — each with a plain-language explanation and full CRUD: a **network** editor (driver / subnet / gateway / driver-opts, with a warning when a subnet overlaps one already on the host), a **volume** editor (driver / driver-opts / name, showing each volume's on-disk size from the host's `/system/df`), and a **secret/config** editor (external vs. host `file:` path) — all written through the same surgical YAML writer. Each tab's **Edit** form covers image with a registry **tag dropdown**, ports with **host-port collision checks**, volumes with named-volume suggestions + outside-project-root warnings, env vars with secret masking, labels, restart, command/entrypoint, user, working_dir, and (V7.2) **resource constraints** — CPU / memory limits & reservations, pids, cpu_shares, ulimits, oom_kill_disable, oom_score_adj, shm_size — with **numeric inputs + sliders bounded by the host's real capacity** (CPU count and RAM from the live stats stream), a capacity panel showing what other containers already reserve, and an inline **over-commit warning**; cpu/mem/pids follow the file's convention (`deploy.resources` or legacy top-level) and are never mixed. Saves are **surgical** (comments, key order and formatting elsewhere survive byte-for-byte), validated by `docker compose config -q` and written atomically — running containers are untouched until the next *Update project*. Works for **Local socket** connections (bind-mount the stacks root; optional host→container path mapping) and **SSH** connections (read/written on the remote host over the connection's SSH credentials, zero config). Files using `x-*` / `extends` / YAML merge keys stay read-only with a "file uses X" banner. A **Graph** tab (V7.7) draws a read-only SVG **dependency graph** (services as nodes, `depends_on` as arrows, shared networks as group boxes, named volumes in a legend), and a **linter** runs on every load + save — port collisions, `depends_on` cycles, missing healthchecks behind `service_healthy`, escaping bind mounts, deprecated keys and `:latest` tags — shown inline on each service and rolled up into a **Health** badge next to the project name
- **Host terminal** (V5.3) — a **Host terminal** button in each connection's header on the Docker page opens an interactive `xterm.js` shell *on the Docker host* over SSH (not inside a container), bridged over a WebSocket with single-use ticket auth. It's connection-scoped (a shell on the host), so it lives at the connection level rather than on a per-container tab. **Off by default** and the most dangerous surface in the product: it requires the server-wide toggle at **Settings → Host terminal** **and** a per-connection **Allow host terminal** opt-in **and** an SSH connection. Every session is audited (who / when / host / duration / bytes / end reason) with per-user & per-host caps and a server-side idle timeout
- **Container exec** (V5.7) — an **Exec** tab on the container modal opens an interactive `xterm.js` shell *inside* a running container via the Docker daemon's `exec` API (the "I just need to run one command in this container" case). Reuses the V5.3 WebSocket + single-use-ticket transport but, because it routes through the daemon rather than SSH, it works for **every** connection type (local socket, TCP+TLS, SSH). The command defaults to `/bin/sh` and is editable per session; live terminal resize works. **Off by default**, gated by the **Settings → Container exec** toggle **and** a per-connection **Allow container exec** opt-in. Every session is audited (who / when / container / command / duration / bytes / end reason) with per-user & per-host caps and a server-side idle timeout
- **Image cleanup / prune** (V5.5) — background sweep that runs `docker image prune` on every Docker connection on a configurable interval (default weekly) to reclaim the `<none>:<none>` dangling images container auto-updates leave behind, plus a **Storage** widget on the Docker page showing image counts and a manual **Prune now** button with a dry-run preview. Master toggle + interval at **Settings → Image cleanup**; per-connection opt-out and an opt-in "also prune unused images" aggressive scope on the connection form. Every run is audited (deleted count + bytes reclaimed). Never touches volumes
- **Session audit viewer** (V5.8) — a read-only **Settings → Audit** page with tabbed tables — *Host terminal* (V5.3), *Container exec* (V5.7), *LXC console* (V6.6), *Update attempts* (V2.7) and *Image prune* (V5.5) — surfacing who ran what, against which connection / host / container / guest, for how long, the bytes transferred, and how it ended. An **Active** badge flags sessions still open. Backed by owner-scoped, newest-first, paginated read-only endpoints; the host-terminal dialog, container Exec panel and LXC Console panel each link straight here pre-filtered to that connection

- **Health-check tuning** (V5.6) — a **Settings → Health checks** page to tune the probe schedule and the offline-alert reliability logic from V5.3.2: the check interval (how often every service is probed — default 60 s), the failure threshold (consecutive failed scans before a 🔴 *Service unavailable* alert fires), the in-probe retry count (extra attempts on a transient DNS / timeout / network / TLS failure), and the delay between those retries. Each field is explained inline; values are DB-backed and apply on the next scan without a restart. The `STASHBOARD_HealthCheck__*` env vars now only seed the defaults on first run

### Proxmox LXC update monitoring (V6.0)

- **Proxmox page** (`/proxmox`) — monitors pending package updates one layer below Docker: on the LXC containers and the Proxmox node itself. Add a host once and every scheduled scan **auto-discovers** the node + its LXCs, rendering one card per object with the **pending-update count**, running state, and last-checked time
- **Hybrid transport** — the Proxmox **REST API** (authenticated with a `PVEAPIToken`) lists LXCs and reads the node's own updates via `/nodes/{node}/apt/update`; **SSH** to the host runs `pct exec <vmid> -- apt list --upgradable` for the per-LXC count (Proxmox exposes no command-exec API for LXC, so SSH is required for the per-container counts). Self-signed certs are handled by a per-host **Skip TLS verification** toggle
- **Familiar schedule + alerts** — the same Hourly / Daily / Weekly cadence as Docker watches, with email + Telegram notifications when new updates appear (throttled so the same un-applied updates aren't re-sent every tick). Per-host **Test connection** (probes API + SSH independently) and **Check now** buttons. Secrets are encrypted at rest. *Triggering `apt upgrade` from the UI and non-Debian templates are planned follow-ups.*

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

To move to a specific version, set `STASHBOARD_TAG=6.11.0` in `.env` first.

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

> **The MQTT / Home Assistant integration is stored in the database and editable from the UI** at **Settings → Home Assistant** (V9.0): broker host / port / TLS, username + password, client id, discovery prefix, entity prefix, and the master switch (**off by default**). Optional `STASHBOARD_Mqtt__*` values only **seed** the settings row on first startup; after that, manage everything from the page and changes apply without a restart. The broker password is encrypted at rest (AES-256-GCM) and never returned by the API. Point Stashboard at your **existing** broker (e.g. Mosquitto) — it doesn't run one.

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
      # Optional (V5.2/V7.1): bind-mount your Compose stacks root — ideally at
      # the SAME path on both sides — so "Update now" runs `docker compose
      # pull` + `up -d <service>` instead of the raw recreate, and the visual
      # Compose viewer/editor can read + edit the files. Projects are
      # discovered from container labels; if the in-container path differs,
      # set the connection's "Compose path mapping" (host → container prefix).
      # - /opt/stacks:/opt/stacks
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

# LXC console (V6.6) — gated: AllowProxmoxConsole flag + per-host AllowConsole opt-in + SSH configured
POST   /api/proxmox/connections/{id}/lxc/{vmid}/console/ticket  { command? } → ProxmoxConsoleTicketResponse  (single-use, short-TTL ticket binding the LXC + command; 403/404 when not eligible)
GET    /api/proxmox/connections/{id}/lxc/{vmid}/console/ws?ticket=&cols=&rows= → WebSocket upgrade   (ticket-authenticated SSH PTY running `pct exec`; binary = stdin/stdout, text = resize)
GET    /api/proxmox/console/sessions             ?skip=&take=&connectionId= → ProxmoxConsoleSession[]    (owner-scoped LXC-console audit trail, newest first)

# Destroy LXC (V6.13) — gated: AllowProxmoxDestroy flag + per-host AllowDestroy opt-in + stopped guest
DELETE /api/proxmox/connections/{id}/lxc/{vmid}                           → 204 / 403 / 409 / 502     (destroy a stopped LXC; 403 when not eligible, 409 when running, 502 relays a host rejection)
GET    /api/proxmox/destroy/sessions             ?skip=&take=&connectionId= → ProxmoxDestroyAudit[]     (owner-scoped LXC-destroy audit trail, newest first)
GET    /api/settings/proxmox-destroy                                      → ProxmoxDestroySettings     (destroy-LXC master switch — Settings page)
PUT    /api/settings/proxmox-destroy            { enabled }              → 204                        (toggle destroy LXC server-wide)

# Restore guest (V8.1 LXC / V8.3 VM) — gated: AllowProxmoxRestore flag + per-host AllowRestore opt-in (+ stopped target & double-confirm to overwrite)
GET    /api/proxmox/connections/{id}/lxc/backups                          → ProxmoxBackup[]            (restorable vzdump-lxc-* archives across backup-capable storages; PBS datastores excluded)
POST   /api/proxmox/connections/{id}/lxc/restore  { vmId, backupVolid, storage?, force?, … } → ProxmoxConnection / 403 / 409 / 502  (restore an LXC from a vzdump archive; 409 on a vmid collision or running overwrite target, 502 relays a host rejection)
GET    /api/proxmox/connections/{id}/qemu/backups                         → ProxmoxBackup[]            (V8.3 — restorable vzdump-qemu-* archives; PBS datastores excluded)
POST   /api/proxmox/connections/{id}/qemu/restore { vmId, backupVolid, storage?, force?, … } → ProxmoxConnection / 403 / 409 / 502  (V8.3 — restore a VM via POST …/qemu with archive=; same gates/guards as the LXC path)
GET    /api/proxmox/restore/sessions             ?skip=&take=&connectionId= → ProxmoxRestoreAudit[]    (owner-scoped guest-restore audit trail, LXC + VM, newest first)
GET    /api/settings/proxmox-restore                                      → ProxmoxRestoreSettings     (guest-restore master switch — Settings page)
PUT    /api/settings/proxmox-restore            { enabled }              → 204                        (toggle guest restore server-wide)

GET    /api/features                                                      → StashboardFeatures         (server-side feature flags the UI gates against)
GET    /api/settings/host-shell                                           → HostShellSettings          (host-terminal master switch — Settings page)
PUT    /api/settings/host-shell                  { enabled }              → 204                        (toggle the host terminal server-wide)
GET    /api/settings/container-exec                                       → ContainerExecSettings      (container-exec master switch — Settings page)
PUT    /api/settings/container-exec              { enabled }              → 204                        (toggle container exec server-wide)
GET    /api/settings/proxmox-console                                      → ProxmoxConsoleSettings     (LXC-console master switch — Settings page)
PUT    /api/settings/proxmox-console            { enabled }              → 204                        (toggle the LXC console server-wide)
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

✅ **V5.2 — True Compose-aware recreate** _(shipped in 5.2.0)_ — when a local-socket connection has a bind-mounted **Compose project path**, "Update now" runs `docker compose pull` + `up -d <service>` (honouring `env_file`, `depends_on` ordering, and profiles) instead of the raw recreate. The image now ships the `docker compose` binary; falls back to the raw recreate when not configured. _(Reworked in 7.1.0: the per-connection path is gone — project directories are discovered from container labels, so this works per project on multi-project hosts.)_ See the [CHANGELOG](./CHANGELOG.md) and [guide §5.1a](./DOCKER_UPDATE_MONITORING_GUIDE.md).

✅ **V5.3 — Host terminal (browser SSH shell to the Docker host)** _(shipped in 5.3.0)_ — opens an interactive `xterm.js` shell on the **host** of an SSH connection, bridged over a WebSocket with single-use ticket auth (the transport later shell phases reuse). Off by default and gated three ways (global flag + per-connection opt-in + SSH connection); every session is audited with per-user/host caps and an idle timeout. _(Relocated from the container modal to a **Host terminal** button in the connection header in 5.7.0.)_ See the [CHANGELOG](./CHANGELOG.md).

✅ **V5.3.1 — Tag-pattern filter correctness + version tags** _(shipped in 5.3.1)_ — fixes the per-watch tag-pattern filter so it resolves the genuinely newest matching tag (semver outranks non-semver, full-match regex, paginated registries fully scanned) instead of getting stuck on a phantom *Update available*, and the UI now shows the resolved version tag next to each `sha256` digest. See the [CHANGELOG](./CHANGELOG.md).

✅ **V5.3.2 — Reliable offline alerts (no false positives)** _(shipped in 5.3.2)_ — health checks now retry connection-level failures within a probe and require N consecutive failed scans before marking a service Down, so a single transient blip (DNS hiccup, timeout, network/TLS glitch on the monitoring host) no longer fires a false "Service unavailable" Telegram alert. Tunable via `STASHBOARD_HealthCheck__FailureThreshold` / `RetryCount` / `RetryDelayMs`. See the [CHANGELOG](./CHANGELOG.md).

✅ **V5.4 — Compose project grouping & bulk update** _(shipped in 5.4.0)_ — containers on the instances page are now grouped by their `com.docker.compose.project` label under a project header card with a *"N of M tracked services have updates available"* counter and an **Update project** button. With V5.2 set up the button shells out one `docker compose pull` + `up -d` against the project root (so Compose honours `depends_on` ordering); otherwise it falls back to per-service raw recreate sorted by the `com.docker.compose.depends_on` labels. One aggregate audit row + one child row per service. See the [CHANGELOG](./CHANGELOG.md) and [guide §5.1b](./DOCKER_UPDATE_MONITORING_GUIDE.md).

✅ **V5.5 — Image cleanup / prune** _(shipped in 5.5.0)_ — background sweep runs `docker image prune` on every Docker connection on a configurable interval (default weekly) to reclaim dangling `<none>:<none>` images left behind by container auto-updates. A **Storage** widget on the Docker page shows image counts and a manual **Prune now** button with a dry-run preview; **Settings → Image cleanup** holds the master toggle + interval. Per-connection opt-out (`AllowImagePrune`) and an opt-in aggressive scope (`PruneUnusedImages`) on the connection form. Every run is audited (deleted count + bytes reclaimed). Volumes are never touched. See the [CHANGELOG](./CHANGELOG.md).

✅ **V5.6 — Health-check tuning page** _(shipped in 5.6.0)_ — a **Settings → Health checks** page to tune the probe schedule and the V5.3.2 offline-alert reliability logic from the UI: the check interval (how often every service is probed — default 60 s), the failure threshold (consecutive failed scans before a service is marked Down and a 🔴 *Service unavailable* alert fires), the in-probe retry count (extra attempts on a transient DNS / timeout / network / TLS failure — real HTTP responses are never retried), and the delay between those retries. Each field is explained inline. Values are DB-backed and apply on the next scan without a restart; the `STASHBOARD_HealthCheck__*` env vars now only seed the defaults on first run. See the [CHANGELOG](./CHANGELOG.md).

✅ **V5.7 — Container exec (browser terminal into a container)** _(shipped in 5.7.0)_ — an **Exec** tab on the container modal (and the shell button on each container card) opens an interactive `xterm.js` shell *inside* a running container via the Docker daemon's `exec` API. Reuses the V5.3 WebSocket + single-use-ticket transport, but because it routes through the daemon (not SSH) it works for **every** connection type. The command defaults to `/bin/sh` and is editable per session; live resize works. Off by default, gated by the **Settings → Container exec** toggle **and** a per-connection **Allow container exec** opt-in; every session is audited (connection / container / command / duration / bytes / end reason) with per-user & per-host caps and an idle timeout. This release also **moved the host terminal to the connection header** (it's host-scoped, not per-container) and **retargeted the card's shell button to Exec**. See the [CHANGELOG](./CHANGELOG.md).

✅ **V5.8 — Session audit viewer** _(shipped in 5.8.0)_ — a read-only **Settings → Audit** page surfacing the audit rows the product already records but previously kept write-only. Four tabbed tables: *Host terminal* (V5.3) and *Container exec* (V5.7) sessions — who ran it, against which connection / host / container, the command, start / end, duration, bytes in / out, end reason — plus *Update attempts* (V2.7) and *Image prune* (V5.5) for convenience. An **Active** badge flags open sessions. Backed by four owner-scoped, newest-first, paginated `GET` endpoints under `/api/docker` (`?skip=&take=`, capped page size, optional `?connectionId=` filter); no write/delete verbs. The host-terminal dialog and container Exec panel each link here pre-filtered to that connection. No new auditing — purely a read path over existing rows. See the [CHANGELOG](./CHANGELOG.md).

✅ **V5.9 — Docker instances page redesign** _(shipped in 5.9.0)_ — replaces the V3.5–V5.8 vertical, one-section-per-host layout with a **connection-switcher** layout: a page-level summary strip (Containers / Running / Stopped / Updates), a horizontal connection switcher with per-host running-/-total counts and amber update badges, and a compact per-host summary card with the V5.5 **Storage** widget folded inline as a collapsible row. **Compose project groups** are now packed — each group is exactly as wide as its cards, so several small projects share a row. The container card itself was refined: coloured `(healthy)` segment, bottom-aligned divider so action rows align across cards in a row, a small external-link affordance on the *service:* chip, and a page-level **Diagnostics** toggle that hides the Inspect / Logs / Stats / Notifications / Exec icon row. New device-local **Display preferences** (density · storage style · diagnostics on/off), persisted in `localStorage`. Frontend-only — same V3.5+ API surface. See the [CHANGELOG](./CHANGELOG.md).

✅ **V6.0 — Proxmox LXC update monitoring** _(shipped in 6.0.0)_ — a new top-level **Proxmox** page (`/proxmox`) tracks pending package updates on Proxmox LXC containers and the node itself. A **hybrid transport** does the work: the REST API (`PVEAPIToken`) lists LXCs + reads the node's `apt/update` count, and **SSH** runs `pct exec <vmid> -- apt list --upgradable` for the per-LXC count (Proxmox has no command-exec API for LXC, so the API-only path can't read per-container counts). Hosts are a new `ProxmoxConnection` entity with auto-discovered node + LXC cards, the same Hourly/Daily/Weekly schedule model as Docker watches, email + Telegram notifications, per-host Test/Check-now, and a Skip-TLS-verify toggle for self-signed certs. See the [CHANGELOG](./CHANGELOG.md).

✅ **V6.1 — Proxmox LXC detail modal + Docker-style cards** _(shipped in 6.1.0)_ — first step toward Proxmox/Docker parity. LXC cards are restyled to mirror the Docker container card (runtime state badge, amber **Update** badge, monospace `CT <vmid>` line, `Up <uptime>` / `Stopped` status, and resources / IP / uptime chips), and clicking one opens an **LXC detail modal** shaped like the Docker container modal. The **Overview** tab is live (VMID, node, host, IP, status, uptime, resources, tags, pending updates, last-checked); **Config / Stats / Tasks / Console** tabs are scaffolded for V6.2–V6.6. The Docker container card is unchanged; presentation-only, no backend changes. See the [CHANGELOG](./CHANGELOG.md).

✅ **V6.2 — LXC Config tab** _(shipped in 6.2.0)_ — the Proxmox container modal's **Config** tab now reads an LXC's full configuration straight from the Proxmox REST API (`/lxc/{vmid}/config` + `/status/current`) via a new owner-scoped endpoint. It shows **Resources** (configured cores / memory / swap, plus live CPU %, memory and disk used / max, uptime), **System** (hostname, OS type, arch, start-at-boot, unprivileged, features), and the raw **Mount point** (`rootfs` / `mp<n>`) and **Network** (`net<n>`) lines. The scalar fields became editable in V6.5. See the [CHANGELOG](./CHANGELOG.md).

✅ **V6.3 — LXC Stats + Tasks tabs** _(shipped in 6.3.0)_ — two more Proxmox modal tabs go live. **Stats** renders RRD sparklines for CPU, memory, network (in/out) and disk I/O (read/write) with an Hour / Day / Week switch (auto-refreshing). **Tasks** lists the recent node tasks scoped to the container (type, OK / running / error status, start, duration), each expandable to a per-task log viewer. Both read straight from the Proxmox API — no schema changes. See the [CHANGELOG](./CHANGELOG.md).

✅ **V6.4 — LXC lifecycle + real-time stats** _(shipped in 6.4.0)_ — **Start / Stop / Shutdown / Reboot** an LXC from a new **Lifecycle** section on the modal's Overview tab and the card's lifecycle buttons (`POST …/lxc/{vmid}/status/{action}`; needs the API token to hold `VM.PowerMgmt`). The Stats tab now defaults to a **Live** real-time view that polls `status/current` every 2 s with a rolling CPU / memory / network sparkline window and Pause/Resume (Proxmox has no stats stream for LXC, so it's polling); a **History** toggle keeps the V6.3 RRD Hour/Day/Week view. See the [CHANGELOG](./CHANGELOG.md).

✅ **V6.5 — Edit LXC parameters** _(shipped in 6.5.0)_ — the Config tab's scalar fields are now editable. An **Edit** button turns **Cores**, **Memory (MiB)**, **Swap (MiB)**, **Hostname** and **Start at boot** into a form; **Review changes** shows a per-field confirm that notes whether each change applies live (cores / memory / swap), needs a restart (hostname), or takes effect on next boot (onboot), then writes through a new owner-scoped `PUT …/lxc/{vmid}/config` endpoint over the Proxmox config API (needs the API token to hold `VM.Config.*`). Only the changed fields are sent. Network interfaces and mount points stay read-only for now. See the [CHANGELOG](./CHANGELOG.md).

✅ **V6.6 — Browser LXC console** _(shipped in 6.6.0)_ — the LXC modal's **Console** tab (and the console button on each LXC card) opens an interactive `xterm.js` shell *inside* an LXC by SSHing to the Proxmox host and running `pct exec <vmid> -- /bin/bash` — the Proxmox analogue of the Docker **Exec** tab and the natural follow-up to the per-LXC update count. Reuses the V5.3/V5.7 transport verbatim (SSH PTY connector + WebSocket + single-use ticket); the command defaults to `/bin/bash` and is editable per session. **Off by default**, gated three ways: the **Settings → LXC console** master switch, a per-host **Allow LXC console** opt-in, and SSH credentials on the host. Every session is audited (host / node / guest / command / duration / bytes / end reason) — surfaced on the Audit page's new **LXC console** tab — with per-user & per-host caps and a server-side idle timeout. Live resize is unavailable over SSH (same as the V5.3 host terminal). See the [CHANGELOG](./CHANGELOG.md).

✅ **V6.7 — Per-LXC update monitoring toggle** _(shipped in 6.7.0)_ — each discovered LXC now has its own **Monitoring enabled** switch (in the LXC modal's **Watch** tab), the Proxmox analogue of pausing a Docker watch. Turn it off to skip a noisy or intentionally unmanaged container without disabling the whole host: a disabled guest is skipped by scheduled and manual update-count checks (its expensive `pct exec` is never run) and excluded from email/Telegram notifications immediately. Disabled cards are muted with a **Disabled** badge and drop their amber "updates pending" emphasis, mirroring the disabled-Docker-watch treatment; the toggle survives auto-rediscovery, and newly discovered LXCs default to enabled. The Watch tab also gains a **Check now** — but note Proxmox has no per-container probe, so it re-scans the **whole node** (every container at once), not just one. Additive migration, no new background worker. See the [CHANGELOG](./CHANGELOG.md).

✅ **V6.7.1 — Proxmox "Update now"** _(shipped in 6.7.1)_ — the Docker analogue of one-click **Update now**, now for Proxmox. A button on the node card and in each LXC's **Watch** tab *applies* pending package updates by SSHing to the host and running `apt-get update && apt-get -y dist-upgrade` — directly on the node, or via `pct exec <vmid>` inside an LXC — with the apt output **streaming live** into a confirm → run → result dialog (NDJSON over fetch). Because a check is a single SSH sweep, a node update upgrades the **whole node** (a new kernel may need a reboot); the dialog says so before you confirm. Triple-gated exactly like the LXC console — the **Settings → Proxmox updates** master switch, a per-host **Allow apply updates** opt-in, and SSH credentials — and **off by default**. Every run is audited (who / when / host / node / guest / exit status / bytes / outcome) on the Audit page's new **Proxmox updates** tab. Non-Debian guests are detected and reported as "nothing to upgrade" rather than failing. See the [CHANGELOG](./CHANGELOG.md).

✅ **V6.8 — PVE node card** _(shipped in 6.8.0)_ — the node row on the Proxmox page becomes a **live hardware/health card** and gains a detailed multi-tab modal (the node analogue of the LXC card, reusing the same modal shell + stat tiles). The card polls the node's own status (~20s) and shows **CPU % · RAM % · root-FS %** as colour-coded chips (ok / warn / crit) plus a worst-of health dot; click it to open the modal with **Overview** (CPU model/topology/frequency/virtualization, live %, load avg, IO wait, memory + swap, uptime, kernel, PVE version, subscription), **CPU/RAM** (a **Live** real-time view polled every 2s plus a **History** RRD toggle), **Storage/SMART** (per-pool usage meters + physical-disk SMART health/wearout, each disk expandable to its full attribute table), **Network** (throughput sparkline + configured interfaces), **Sensors** (CPU/board temperatures + fan RPMs), and **Console** (an SSH shell on the node itself, reusing the V6.6 console transport + audit). Base metrics come from the Proxmox REST API; temperatures/fans — the one signal the API doesn't expose — are parsed from `sensors -j` over SSH, with a clear "install lm-sensors / add SSH" state when unavailable. Every source degrades independently (a missing source shows "not available", never a hard failure). Read-only, no new tables. Threshold **alerting** lands in V6.8.1. See the [CHANGELOG](./CHANGELOG.md).

✅ **V6.8.1 — PVE node alerting** _(shipped in 6.8.1)_ — turns the V6.8 node card from a *view* into a *watch*. An **Alerts** tab on the node modal opts a node into critical-deviation notifications (the node analogue of a Docker watch's enabled flag — **off by default**), with optional per-category toggles (**CPU / RAM / Storage / Thermal / SMART / Network**) and per-node threshold overrides over the V6.8 global defaults (CPU 80/95, RAM 85/95, storage 85/95) so a deliberately hot node can be tuned without muting the fleet. A background pass folded into the existing Proxmox tick evaluates each opted-in node **every ~5 minutes** (independent of the slow update schedule), reading the same API/SSH sources the card uses. **Debounce + hysteresis** suppress flapping — a deviation must persist across N consecutive evaluations before it fires, and must read normal for N before "recovered" is sent — and a per-channel **state signature** throttle means a steady deviation never re-pings. Alerts route through the **existing email + Telegram channels** (no new transport), carrying severity (warn / crit), the metric + value + threshold, and a first-seen timestamp; the Alerts tab lists them live. A source that's merely unavailable (no SSH / lm-sensors) is **n/a, never crit**. See the [CHANGELOG](./CHANGELOG.md).

✅ **V6.8.2 — PVE node deep telemetry (SSH collectors)** _(shipped in 6.8.2)_ — the node modal gains the host-side metrics the Proxmox REST API doesn't expose, each read by an independent SSH collector that degrades to "not available" rather than failing. **CPU/RAM** adds **per-core utilisation bars + steal** (two `/proc/stat` samples) and Overview shows **MemAvailable** + a **steal** indicator; **Storage/SMART** adds a per-disk **IO** table (throughput · IOPS · await from `/proc/diskstats`) and **LVM-thin pool** fill warnings (`lvs`), and each disk row badges its **last SMART self-test + critical counters** (`smartctl -l selftest`); **Network** replaces the aggregate-only throughput with **per-interface RX/TX rate, errors/drops, and link speed/duplex/state** (`/proc/net/dev` + `/sys/class/net`); **Sensors** now also shows **voltage and power** rails (`sensors -j`). Each host gets a **configurable telemetry refresh interval** (default 20s, 5–300s) with **failure backoff** when unreachable. See the [CHANGELOG](./CHANGELOG.md).

✅ **V6.9.0 — Edit LXC network interfaces & mount points** _(shipped in 6.9.0)_ — completes the editable LXC **Config** tab that V6.5 started: the read-only `net<n>` / `mp<n>` / `rootfs` lines become **guided row editors** with explicit **Edit / Add / Remove** affordances, the same surface as the Docker container modal. Network rows expose structured fields (**name, bridge, IPv4/IPv6 (dhcp/manual/CIDR), gateway, VLAN tag, firewall, MTU, rate, MAC, link-down**); mount rows expose **storage/source, mount path, size, ro/backup/quota/acl/shared/replicate, mount options** and support both storage-backed and bind mounts; **rootfs** gets a dedicated edit-only section (size + safe flags — it cannot be removed). Options Stashboard doesn't model are **preserved verbatim** and any line is editable as **raw** via an advanced expander that always shows the exact generated config line. New interfaces/mounts take the **next free key**, removals go through Proxmox **`delete=`**, and a **per-change review** classifies each edit conservatively (*applies live* / *restart likely* / *destructive — names the exact `net1`/`mp2` key*) before a single write. The server builds the exact Proxmox payload and validates IP/CIDR, gateways, MACs, sizes, duplicates and the rootfs-protect rule up front; permission/validation rejections are surfaced verbatim. **Caveats:** some changes need a guest **restart** to fully apply (flagged inline after save when the guest is running), and removing a mount entry **does not delete the underlying storage content**. See the [CHANGELOG](./CHANGELOG.md).

✅ **V6.10 — Proxmox page Docker-parity redesign** _(shipped in 6.10.0)_ — the **page** itself catches up to the modal: the Proxmox page now wears the Docker instances page's `dock` shell, **reusing its `searchbox`, `segmented`, `dock-summary`, and connection-`switcher` markup + CSS verbatim** (no parallel system). A homelab with many LXCs across several hosts gets the same command-centre affordances as the Docker page: a **search box** filtering LXC cards by name, a **state filter** (All / Running / Stopped), a **monitoring filter** (All / Enabled / Disabled / Updates — `Updates` needs monitoring on **and** a positive pending count), a cross-host **summary strip** (objects · running · stopped · pending updates), a **connection switcher** (running/total + update counts per host, hidden for a single host), and a **deep-link** into a specific LXC modal via `?connection=…&vmid=…`. Grouping by **PVE node** is the existing per-connection structure — each connection already maps to one node, so the node card is the host summary with its LXC cards in the grid below. UI-only — the data was already on the client, so no backend or database migration. See the [CHANGELOG](./CHANGELOG.md).

✅ **V6.11 — Bulk LXC monitoring & update operations + audit** _(shipped in 6.11.0)_ — host-wide controls for operators with many guests, all built on the existing per-LXC plumbing. **Enable all / Disable all** flips update monitoring for every LXC on a host in one click (server-side, one transaction, with a confirmation step). **Update all** opens the V6.7.1 `ProxmoxUpdateDialog` flow over a **checklist** of eligible targets — the **node** and its containers (running, monitored, not snoozed, with pending updates — pre-checked, uncheck any) — and streams each one's `apt` log in turn under the same triple gate (global switch + per-host **Allow updates** + SSH), finalising one audit session per guest. A per-LXC **maintenance snooze** ("skip for 1h/6h/24h/7d") temporarily excludes a container from scheduled **and** manual checks, then **auto-re-includes** it once the window passes — monitoring stays on, no need to remember to turn it back. Every monitoring change (toggle, bulk, snooze) is written to an **audit trail** surfaced on **Settings → Audit → LXC monitoring** (who / when / guest / new state). And an opt-in, off-by-default **update-check webhook** (the Proxmox analogue of the Docker watch webhook) lets an external trigger POST a rotatable URL to kick off an immediate host scan. See the [CHANGELOG](./CHANGELOG.md).

✅ **V6.12 — LXC live logs (Logs tab)** _(shipped in 6.12.0)_ — the LXC modal gains a **Logs** tab (after **Tasks**) that tails a guest's system journal live, the observability surface the Docker modal already had. It reuses the V6.6 console transport *verbatim* — same ticket + concurrency registry + SSH PTY + WebSocket — but SSHes in and runs `pct exec <vmid> -- journalctl -f` (falling back to `tail -F /var/log/syslog` when the guest has no journald), built server-side and **read-only** (no input). Gated **identically** to the console (global switch + per-host **Allow LXC console** + SSH + running guest), each blocked state showing the same calm hint. The panel reuses the Docker logs toolbar/viewport — **Pause / Resume / Stop / Stream / Clear / Copy / Download** with autoscroll — and the tail runs with **no idle timeout** so a quiet guest isn't reaped, writing **no audit row** and needing no new tables. See the [CHANGELOG](./CHANGELOG.md).

✅ **V6.13 — Destroy / remove LXC** _(shipped in 6.13.0)_ — the LXC modal's **Lifecycle** section gains a **Destroy** action, the container analogue of Docker's "Remove container" and the last missing LXC lifecycle verb. It calls `DELETE /nodes/{node}/lxc/{vmid}` via a new `IProxmoxApiClient.DeleteLxcAsync` and is **triple-gated, off by default — the same pattern as the console / "Update now"**: a server-wide master switch (**Settings → Destroy LXC**), a per-host **Allow destroy** opt-in, and a **stopped** guest. Gate failures are deterministic and returned *before* any Proxmox call (global off ⇒ 403, host opt-in off ⇒ 403, running guest ⇒ 409 — stop it first). The **Destroy** button appears only for a stopped, gated container and opens a **double-confirm** dialog — a verbatim reuse of the Docker `remove-confirm-*` UI — naming the exact guest (`CT <vmid> · <name>`); on success the card disappears immediately and the modal closes. Every attempt that reaches the host is **audited** (who / when / host / node / guest / result) on **Settings → Audit → LXC destroy**. **Out of scope:** purging backups / external storage volumes (only the container + its root disk are removed) and bulk destroy. See the [CHANGELOG](./CHANGELOG.md).

✅ **V6.13.1 — Create LXC** _(shipped in 6.13.1)_ — the Proxmox page's per-host block header gains a **New LXC** button, closing the last leg of full LXC lifecycle from Stashboard (edit shipped in V6.5/V6.9, destroy in V6.13). It provisions a container from a template via a new `IProxmoxApiClient.CreateLxcAsync` (`POST /nodes/{node}/lxc`) that polls the returned task UPID to a terminal state so the UI reports real success/failure, not "request accepted". A guided **`LxcCreateModal`** reuses the Docker `container-modal-*` / `service-modal-*` styling (not a parallel form): identity (vmid defaulted from `/cluster/nextid`, hostname, description, tags), a **template dropdown** (the `vztmpl` content of template-capable storages), root password / SSH key, resources (cores / memory / swap / rootfs storage + size), one structured `net0` row (name / bridge / ip / gw / VLAN), and options (unprivileged default on, onboot, start). **Double-gated, off by default — the destroy/updates shape minus the running-guest check** (there's no guest yet): a server-wide switch (**Settings → Create LXC**) + a per-host **Allow create** opt-in; gate failures return 403 *before* any Proxmox call, a vmid already on the host ⇒ 409, a malformed spec ⇒ 400, and Proxmox's own rejection ⇒ 502 verbatim. On success the host's **Check now** scan runs so the new card appears immediately, and every attempt is **audited** on **Settings → Audit → LXC create**. **Out of scope:** cloning / snapshot or backup restore, advanced multi-mount rootfs at create time, and VM (QEMU) creation. See the [CHANGELOG](./CHANGELOG.md).

✅ **V6.14 — VM (QEMU) support** _(shipped in 6.14.0)_ — Stashboard's Proxmox integration covered **LXC + nodes only**; many homelabs also run QEMU VMs. This phase adds VMs as a first-class guest type (`ProxmoxGuestType.Qemu`) so the Proxmox page reflects the whole host, not just its containers — **reusing the LXC surface** so the experience is one and the same. VMs are **discovered** via a new `IProxmoxApiClient.ListQemuAsync` (`GET /nodes/{node}/qemu`) and appear as cards in the same guest grid (subtitle **VM `<vmid>`**), with a new **All / LXC / VM** type filter on the toolbar (shown once a host has at least one VM). **Lifecycle** (start / stop / shutdown / reboot via `qemu/{vmid}/status/{action}`) reuses the LXC action UI; **Stats** (live `status/current` + history `rrddata`) and **Tasks** reuse the existing tabs + sparklines verbatim (identical sample shape; the status/rrd/lifecycle reads share a private `{kind}` path helper with their LXC twins). The modal exposes only the VM-applicable tabs — **Overview · Config (read-only) · Tasks · Stats** — surfacing the VM's disks / NICs on a read-only Config tab. **Destroy** works for VMs too — the modal's Lifecycle **Destroy** action is reused for a stopped VM under the same triple gate (global **Destroy LXC** switch + per-host **Allow destroy** + stopped guest), routed to `DELETE /nodes/{node}/qemu/{vmid}` and audited like an LXC destroy. **Clearly marked LXC-only for now:** APT update monitoring / "Update now" (a VM isn't necessarily Debian and may have no SSH/guest-agent — the **Watch** tab is hidden), the **Console** (SPICE/VNC, not the LXC SSH shell), **Logs** (`pct`-backed), config **editing**, and **create**. See the [CHANGELOG](./CHANGELOG.md).

✅ **V6.15 — Proxmox connections in backup / restore** _(shipped in 6.15.0)_ — closes a **data-integrity gap**: the JSON config backup (`GET /api/backup/export` / `POST /api/backup/import`) exported categories, tags, Docker connections, services, Docker watches and settings — but **omitted Proxmox entirely**, so a user migrating hosts silently lost every Proxmox host and its per-guest monitoring choices. `ProxmoxConnections` now travel in the backup alongside `DockerConnections` with the same **merge-by-name** import strategy, covering the connection-level config (node, API/SSH transport, lifecycle/notification toggles, schedule, webhook token) with the encrypted **API token secret + SSH key** decrypted on export and re-encrypted on import (portable across instances with different encryption keys). Each host also carries the **per-guest monitoring intent** worth backing up — guests with monitoring turned off or snoozed, keyed by VmId so the next scan re-attaches them; scan-derived state is not exported and repopulates. Import stays additive (no duplicate hosts, colliding webhook tokens dropped, guest intent seeded only for unknown guests), and a pre-V6.15 backup imports cleanly. See the [CHANGELOG](./CHANGELOG.md).

✅ **V6.15.1 — Idempotent service import + connection-delete diagnostics** _(shipped in 6.15.1)_ — restoring a backup onto an instance that already held the same services (e.g. staging → prod) **duplicated every service**, because services — unlike categories, tags and connections — were always created fresh on import. Services are now **merged by name + main URL** (an existing match is reused for watch links; same name + different URL still imports as new), so a re-import is idempotent. And deleting a Docker connection still referenced by a service now returns a 409 that **names the blocking services** instead of a count-only "1 service(s) use this connection" that left the user guessing — the assignment lives on the service (modal → Docker tab), not on the container links the Docker page shows. See the [CHANGELOG](./CHANGELOG.md).

✅ **V7.0 — Visual Compose viewer (foundation, read-only)** _(shipped in 7.0.0)_ — the first slice of the **V7 visual Compose editor**, deliberately shipped without a write path so the YAML model and UI layout can be validated against real-world projects before any edit risk is taken. A connection with a **Compose project path** gains a **Compose** button on the Docker page that opens `/projects/{id}/compose`: a **card-per-service grid** showing image, ports, mounts, env summary, restart policy and `deploy.resources` limits, plus the top-level **networks / volumes / secrets / configs**. **Two read transports:** for **Local socket** connections the path is the V5.2 in-container bind mount; for **SSH** connections the path field is now available too and points at a directory **on the remote Docker host** — the viewer fetches the file over the connection's existing SSH credentials (read-only; the compose-aware "Update now" stays LocalSocket-only). The backend (`GET /api/docker/connections/{id}/compose`) parses with **YamlDotNet** into a typed `ComposeProjectResponse`; the cards **reuse the same `EntityCard` / state-pill family as the Docker page** and wear the live runtime state of the container matched by compose-service label. **Hard fail-safe:** files using `x-*` extension fields, `extends` or YAML merge keys surface a **"Read-only — file uses X"** banner naming each construct instead of silently dropping data (plain anchors/aliases resolve fine). **Edit** buttons are rendered but disabled until V7.1 grows round-trip support — no `docker compose` invocation, no writes, no entity-model changes. _(The per-connection path model was replaced in 7.1.0 by label-based project discovery — see V7.1.)_ See the [CHANGELOG](./CHANGELOG.md).

✅ **V7.1 — Visual Compose editor: basic service fields** _(shipped in 7.1.0)_ — the V7.0 viewer grows its **write path**, plus a model fix it exposed. **Editing:** every service card's **Edit** button opens a modal covering image (registry **tag dropdown** + free text), ports (host/container/protocol rows with **collision checks** against the rest of the project), volumes (named-volume suggestions, warning on host paths outside the project root), env vars (secret-style masking for `*_KEY`/`*_TOKEN`/`*_PASSWORD`/`*_SECRET`), labels, restart policy, command/entrypoint, user and working_dir. **Round-trip fidelity:** edits are applied by splicing the raw YAML at the changed keys' exact token spans ([ADR-0001](./docs/adr/0001-compose-yaml-round-trip.md)) — comments, key order and quoting elsewhere survive **byte-for-byte**. **Atomic save:** write `<file>.next` → `docker compose config -q` (blocking — no CLI, no save) → atomic rename; validation failures roll back and surface the raw stderr. Works on **LocalSocket and SSH**. **Model fix:** Compose projects are now **discovered per project from container labels** (`com.docker.compose.project` + `working_dir`) instead of one path per connection — a host header **Compose** button opens the project picker, each project group header links to its own editor, and the V5.2/V5.4 compose-aware updaters resolve each project's directory from the same labels (fixing updates on multi-project hosts). ⚠️ Breaking: the `ComposeProjectPath` field/column is gone; LocalSocket deployments mount the stacks root (same path on both sides, or set the new **Compose path mapping**). See the [CHANGELOG](./CHANGELOG.md).

✅ **V7.1.1 — Compose as a per-project modal** _(shipped in 7.1.1)_ — pure front-end UX rework of the V7.0/V7.1 surface. The **whole-host Compose button** and the `/projects/{id}/compose` + `/projects/{id}/compose/{project}` pages are **removed** (project-picker page deleted); the viewer/editor is now a **modal** scoped to one discovered project. A **Compose** button appears on **every Compose project's group header**; single-container projects are no longer demoted into "Other containers" (the v5.4 1-of-1 collapse is lifted), so they show as their own named groups — non-compose containers have none. The modal shows a compact project header strip and **one tab per service**, each wearing the matched container's live state, with the same V7.1 edit form (now `ComposeServiceEditForm`) plus a read-only block for non-editable fields; unsupported-construct files stay read-only with the V7.0 banner. No backend/contract/round-trip change. See the [CHANGELOG](./CHANGELOG.md).

✅ **V7.2 — Resource constraints editor** _(shipped in 7.2.0)_ — each service tab gains a **resource-constraints** section below the basic-fields form (`ComposeResourcesForm`), folded into the same atomic save. Nine fields are editable — `cpus`, `mem_limit`/`memory`, `mem_reservation`, `pids_limit`, `cpu_shares`, `ulimits`, `oom_kill_disable`, `oom_score_adj`, `shm_size` — with **numeric inputs + sliders bounded by the host's real capacity** (CPU count and RAM from the V3.5 `docker stats` stream) and a companion panel — *"Host capacity … · allocated by other containers … · this service draft …"* — plus an inline **over-commit warning**. cpu/mem/pids follow the file's convention (`deploy.resources.limits`/`.reservations` or legacy top-level `cpus`/`mem_limit`/…), detected per file and **never mixed** (legacy has no CPU reservation). The "allocated by others" figure sums the running containers' `HostConfig` (`NanoCpus`/`Memory`) via `inspect`, cached server-side (~60 s). Round-trip is preserved — the `deploy.resources` subtree is rewritten as a unit leaving sibling `deploy` keys byte-for-byte, untouched fields stay zero-diff, and anchored resources / GPU device reservations are refused rather than corrupted. See the [CHANGELOG](./CHANGELOG.md).

✅ **V7.2.1 — Proxmox Backup Server disk/SMART fixes** _(shipped in 7.2.1)_ — three PBS bugs, all from PBS naming a field or parameter differently than PVE. The per-disk **SMART read** sent `/dev/sda` (PVE-style) where PBS validates `disk` against its block-device name schema and wants the bare name `sda` → a 400 that showed as "host unreachable"; the `/dev/` prefix is now stripped for PBS so SMART attributes load (and a genuine `smartctl` failure now shows the host's own reason under that disk, not a 502). `disks/list` **health/type** were read from the PVE keys `health`/`type`, but PBS uses `status` (`passed`) / `disk-type` (`hdd`/`ssd`) — so the health badge showed **UNKNOWN** and the type was blank; both spellings are now read (badge → PASSED, type → HDD/SSD). And a **stale "API unreachable" banner** that lingered next to a green/online node card is now hidden while the live node-status poll succeeds. See the [CHANGELOG](./CHANGELOG.md).

✅ **V7.3 — Top-level resources editor** _(shipped in 7.3.0)_ — the Compose modal gains a separate **Shared resources** tab (alongside the per-container tabs) holding four sections — **Networks · Volumes · Secrets · Configs**, each with a plain-language explanation — so the editor manages the Compose top-level elements that stitch containers together, not just the containers. Each tab is a CRUD list on the same comment-preserving, `docker compose config -q`-validated, atomic writer from V7.1 — extended to splice **top-level** map entries one at a time, so editing/adding one entry leaves sibling entries, key order and comments **byte-for-byte** (anchors / flow-style / merge-key sections are refused, not corrupted). **Network** editor: driver (`bridge`/`overlay`/`macvlan`/…), subnet, gateway, driver-opts, name override, with a **subnet-overlap warning** against the networks already on the host (read live from the Docker Engine network list). **Volume** editor: driver, driver-opts, name override, and each named volume's **on-disk size** from the host's `/system/df` (best-effort — omitted when unreachable). **Secret/config** editor: external vs. host `file:` path. The parser now reads these sections' **full options** (was name-only); a network with multiple `ipam.config` blocks is flagged unsupported. The encrypted-at-rest secret store from the original proposal was deferred (declarations only this phase). See the [CHANGELOG](./CHANGELOG.md).

✅ **V7.4 — Create a new service from scratch** _(shipped in 7.4.0)_ — the Compose modal gains an **Add service** tab that turns the editor into a project bootstrapper. It reuses the exact field controls of the existing-service editor (image with the registry **tag dropdown**, ports, volumes, env, labels, restart, command/entrypoint, user, working_dir, and the V7.2 resource picker) plus a **service name** validated for uniqueness and Compose key shape (`^[a-zA-Z0-9._-]+$`); an image is required. The new block is appended at the end of the `services:` map by the same comment-preserving, `docker compose config -q`-validated, atomic writer from V7.1 — the rest of the file survives **byte-for-byte** and the entry lands at the existing services' indentation column (2- vs. 4-space). **Save and run** then runs `docker compose up -d` across the whole project so the new container comes up next to its siblings — **LocalSocket** via the in-container Compose CLI **and SSH** on the remote host — and the modal switches to the new service's tab where every field is editable like an existing one (**Save only** writes without starting). A new **Raw YAML** tab edits the whole file by hand (write or paste a ready stack — **multiple containers in one file**), with the same validated atomic save, and is also the escape hatch for files the structured editor marks read-only. See the [CHANGELOG](./CHANGELOG.md).

✅ **V7.4.1 — Create a whole project from scratch** _(shipped in 7.4.1)_ — completes the bootstrapper: a **New project** button on each host header opens a dialog that writes a brand-new `docker-compose.yml` from nothing. Name the project (validated to Compose's lowercase rule), give a target **directory** (free-text path as the connection sees it, with an opt-in `mkdir -p`), and define the first service with the same shared controls as the editor. The file is written with a top-level `name:` (deterministic project name), `docker compose config -q`-validated and atomic, **local or over SSH** — refusing to clobber a directory that already holds a Compose file. **Create and run** then `docker compose up -d`s it and opens the new project's modal, where the V7.4 **Add service** / **Raw YAML** tabs take over; **Create only** just writes the file. See the [CHANGELOG](./CHANGELOG.md).

✅ **V7.5 — Service templates / starter recipes** _(shipped in 7.5.0)_ — the New-project dialog gains a **From template** tab: a searchable, category-grouped catalogue of **~126 well-known self-hosted images across 10 categories** (Databases & caches, Networking & proxies, Monitoring & dashboards, Media servers, Media automation, Files & productivity, Security & identity, Smart home & IoT, Developer & Git, Communication) shown with their real **dashboard-icons** logos — Postgres, Redis, Nginx, Traefik, Pi-hole, Jellyfin, the full *arr stack, Nextcloud, Immich, Paperless-ngx, Vaultwarden, Home Assistant, Gitea, and many more, including full multi-service stacks (app + database + cache). Picking one opens the project config panel reduced to the per-deployment bits — project name, directory, and the template's declared **variables** (volume host paths, env values, exposed ports), each with a hint and a one-click **generate** for secrets (passwords / tokens). Filling them resolves the template's `${KEY}` placeholders and posts to the same `create-project` endpoint the from-scratch tab uses (so it's still `docker compose config -q`-validated and atomic). `create-project` is now **multi-service** (first service seeds the file, the rest are appended by the V7.1 editor), so recipes like WordPress + MariaDB come up in one action. Templates ship as schema-validated `templates/*.json` baked into the image and served read-only at `GET /api/templates`; drop your own into a mounted `/app/Data/templates` to extend or override the built-ins. See the [CHANGELOG](./CHANGELOG.md).

✅ **V7.6 — Diff, dry-run & apply** _(shipped in 7.6.0)_ — makes the editor safe on always-on hosts. **Review & save…** computes a **unified diff** of the file on disk vs. your edit, runs `docker compose config -q` as a **dry-run** (a throwaway candidate, the original is never touched), and shows which services the change touches — before anything is written. Confirm with **Save only**, or **Save & apply changed** to fire a compose-aware `docker compose up -d` for **only the changed services** (new + modified; a service merely removed from the file is flagged, never silently stopped). Every save also snapshots the previous file into `<project>/.stashboard/history/` (last **20**, with a **History** tab + **Restore** that previews the same diff and is itself undoable), and writes a metadata-only **Compose changes** audit row (who / when / which services) surfaced read-only on the Audit page. Local or over SSH. See the [CHANGELOG](./CHANGELOG.md).

✅ **V7.7 — Dependency graph + linter** _(shipped in 7.7.0)_ — completes the V7 editor. A new **Graph** tab draws a lightweight, read-only **SVG dependency graph** of the project: services as nodes (with their live state pill), `depends_on` as arrows (dependencies layered below their dependents), shared networks as translucent **group boxes**, and named volumes in a side legend — click a node to jump to its tab. No graph-library dependency; it's hand-built SVG to match the rest of the UI. Alongside it, a pure **linter** runs on **every load and every save**, with findings shown **inline on each service card** and aggregated into a **Health** badge next to the project name: **port collisions**, **`depends_on` cycles**, a **missing healthcheck** behind a `condition: service_healthy` dependency (errors), and **bind mounts escaping the project root**, **deprecated keys** (`links` / `volumes_from` / top-level `version:`), and **`:latest` image tags** (warnings — pinning `latest` is a common, deliberate homelab choice paired with V2 update monitoring). See the [CHANGELOG](./CHANGELOG.md).

✅ **V7.8 — Container card icons** _(shipped in 7.8.0)_ — every container card on the Docker page (and inside `ServiceModal → Docker`) now leads with a service icon, so the page scans at a glance instead of as a wall of identical cards. The avatar resolves per card: a **custom image** you upload (from the container modal's Overview tab — preview, upload, **Reset to auto**), otherwise the **official icon** derived from the image reference and pulled from the [homarr-labs **dashboard-icons**](https://github.com/homarr-labs/dashboard-icons) set (24h-cached, misses included), otherwise a **placeholder** with the container's initials. Custom icons are keyed by `(connection, container name)` so they survive a recreate. The same treatment is wired onto **Proxmox LXC / VM** cards too — a custom upload per guest, else the official **OS icon** auto-resolved from the guest's `ostype` (`debian` / `ubuntu` / `alpine` / … for containers, `linux` / `windows` for VMs), else a placeholder. See the [CHANGELOG](./CHANGELOG.md).

🧩 **Proxmox Backup Server (PBS) support** _(shipped in 6.8.2)_ — a Proxmox host now has a **server type** (PVE / PBS). Point a connection at a PBS appliance (port 8007) and it's monitored with the same node card, modal, and node-health alerting as a PVE node — CPU/RAM/swap/root, RRD history, disks + SMART, network, `apt` updates, sensors, **Update now**, and the node console — just without LXC guests, and with its **datastores** (usage + dedup) shown in the Storage tab. The client uses the correct token scheme per product (`PBSAPIToken` vs `PVEAPIToken`), which is the exact mismatch that returns **401** when a PBS host is added as PVE.

✅ **V7.9 — Link Proxmox guests to services + Docker↔Proxmox cross-link** _(shipped in 7.9.0)_ — a service on the dashboard can now link **Proxmox LXCs and VMs** alongside the Docker containers it already tracks. The `ServiceModal` gains a **Proxmox** section next to the Docker one: pick a connection, add guests from a picker fed by the already-scanned guests (each with its live state pill + pending-update count), and the service card shows a **Proxmox update badge** — together with the Docker badge when both are linked. The link is a many-to-many join, **not** ownership: guests stay owned by their connection, deleting a service drops its links while the guest lives on, and the link is owner-scoped (foreign connection/guest refused, the node row rejected). On the Docker page, a container can be marked as **running on** a specific Proxmox guest — a **"Runs on"** picker in the container modal sets it and the card shows an **"on `<guest>`"** chip that deep-links to that guest's modal (works for any container, watched or not, and survives a recreate). Both link sets survive a backup/restore, keyed by `(connection, vmid)`. The Proxmox badge is read-only here — one-click "Update now" stays on the Proxmox page (V6.7.1). See the [CHANGELOG](./CHANGELOG.md).

✅ **V8.0 — Clone & snapshot LXC** _(shipped in 8.0.0)_ — the two everyday "new container" paths missing from V6.13.1's create-from-template are now in the UI. A stopped or running guest's **Lifecycle** row gains a **Clone** button (→ `LxcCloneModal`, reusing the create-modal styling): a new `vmid` defaulted from `/cluster/nextid`, hostname, target storage, **full vs linked** clone, and — when the source has snapshots — an optional **source snapshot**. The LXC modal also gains a **Snapshots** tab to **list / take / roll back / delete** snapshots (rollback and delete double-confirm because a rollback discards newer state), plus an **Audit** tab. Each action calls the Proxmox API (`…/clone`, `…/snapshot[/{name}[/rollback]]`) and polls the task UPID for real success/failure. It's double-gated exactly like create — the `Stashboard:AllowProxmoxClone` master switch (**Settings → Clone/snapshot**) + a per-host **Allow clone/snapshot** opt-in, both off by default, with deterministic `403`s before any host call and a clean `409` on a vmid collision. A successful clone re-scans the host so the new card appears; every action is audited (who / when / host / node / vmid / action / target / result) and a host rejection surfaces verbatim as a `502`. See the [CHANGELOG](./CHANGELOG.md).

✅ **V8.1 — Restore LXC from backup (vzdump)** _(shipped in 8.1.0)_ — the disaster-recovery leg that completes the "make a container" trio (create-from-template V6.13.1, clone V8.0, restore V8.1). A Proxmox host's header menu gains a **Restore LXC** button (→ `LxcRestoreModal`, reusing the create-modal styling): pick a `vzdump-lxc-*` archive from a dropdown listing the node's backup-capable storages (each backup shown with its guest id / timestamp / size), a target `vmid` (default next-free, one click to **Use original**), an optional root-FS **storage** override, and the **unprivileged / start** toggles. Restore reuses the create endpoint — `POST …/lxc` with `ostemplate=<backup volid>` + `restore=1` — and polls the task UPID for real success/failure. Restoring **over** an existing container (`force=1`) requires it **stopped** and an explicit double-confirm naming the target (the destroy-dialog pattern). It's double-gated exactly like create — the `Stashboard:AllowProxmoxRestore` master switch (**Settings → Restore LXC**) + a per-host **Allow restore** opt-in, both off by default, with deterministic `403`s before any host call and a clean `409` on a vmid collision / running overwrite target. A successful restore re-scans the host so the card appears; every attempt is audited (who / when / host / node / vmid / backup / overwrote? / result) on the Audit page's **LXC restore** tab, and a host rejection surfaces verbatim as a `502`. Restoring from a **Proxmox Backup Server** datastore is out of scope. See the [CHANGELOG](./CHANGELOG.md).

✅ **V8.2 — Clone & snapshot VM (QEMU)** _(shipped in 8.2.0)_ — the VM analogue of V8.0: the **clone** and **snapshot** workflows now work for QEMU/KVM virtual machines, reusing the exact V8.0 surfaces (gating, audit, modals, double-confirm dialogs) rather than a parallel system. On a **VM** card the **Clone** button and the **Snapshots** + **Audit** tabs appear once the feature is enabled. The five V8.0 `IProxmoxApiClient` methods became kind-aware (shared private helpers behind thin `lxc`/`qemu` wrappers, mirroring `GetLxc`/`GetQemuStatusAsync`), each routed to `…/qemu/{vmid}/clone` or `…/qemu/{vmid}/snapshot[/{name}[/rollback]]` and polling the task UPID via `PollTaskAsync`. A VM clone POSTs the new name as `name` (not `hostname`) and a full clone offers an optional disk **format** (`raw` / `qcow2` / `vmdk`); a running VM's snapshot can additionally save the live **RAM state** (`vmstate`) via a kind-gated **Include running memory state (RAM)** toggle (the LXC path never sends it). The controller routes both kinds through shared `qemu`-flag handlers with `/qemu/...` routes (mirroring `DestroyLxc`/`DestroyQemu`); no new gate, no new audit table — `ProxmoxCloneAuditEntity` records the action irrespective of guest kind. It's gated identically to V8.0 (`Stashboard:AllowProxmoxClone` + per-host **Allow clone/snapshot**, both off by default), with `403`/`409`/`502` semantics unchanged and the running-guest clone guard kept kind-aware. See the [CHANGELOG](./CHANGELOG.md).

✅ **V8.3 — Restore VM from backup (vzdump)** _(shipped in 8.3.0)_ — the VM analogue of V8.1, completing the disaster-recovery leg for **both** guest kinds: a host's header menu now offers **Restore VM** alongside **Restore LXC**. Re-create a QEMU/KVM machine from a `vzdump-qemu-*` archive — the reused `LxcRestoreModal` (now `isVm`-aware) lists the node's VM backups (guest id / timestamp / size), a target `vmid` (default next-free, one click to **Use original**), an optional target **storage**, and a **Name** field, with the LXC-only **unprivileged** option hidden. `IProxmoxApiClient.ListBackupsAsync` became **kind-aware** (`qemu` flag → `vzdump-qemu-*`), and a new `RestoreQemuAsync` POSTs **`archive=<volid>`** (+ `force=1` only when overwriting, optional `storage=`/`name=`) to `POST …/qemu` and polls the task UPID — the QEMU restore shape, distinct from the LXC's `ostemplate=…` + `restore=1`. The controller routes both kinds through a shared `qemu`-flag handler (`/qemu/restore` + `/qemu/backups`). It reuses **all** of V8.1's gating (`Stashboard:AllowProxmoxRestore` + per-host **Allow restore**, both off by default), the overwrite double-confirm (target must be **stopped**), and the audit row — no new table, with the Audit tab generalised to **Guest restore** (CT/VM derived from the archive). `403`/`409`/`502` semantics unchanged; PBS datastores stay out of scope. See the [CHANGELOG](./CHANGELOG.md).

✅ **V8.4 — Create a VM (QEMU) from scratch** _(shipped in 8.4.0)_ — the VM analogue of V6.13.1, completing the **create / clone / restore** matrix for **both** guest kinds. A host's header menu now offers **New VM** alongside **New LXC**: a `QemuCreateModal` (reusing the exact Docker `container-modal-*` / `service-modal-*` shell) provisions a brand-new QEMU/KVM machine from **hardware** — identity (`vmid` defaulted from `/cluster/nextid`, name, tags), an **Installation media** dropdown (the node's `iso`-capable storages, or a custom volid / no media), a SCSI **system disk** on a chosen images-capable storage, **resources** (cores / sockets / memory), a virtio **NIC** (bridge / VLAN / MAC / firewall), and the **firmware** (SeaBIOS / OVMF) + **chipset** (q35 / i440fx) + **OS type**. A new `ListIsoImagesAsync` (the ISO twin of `ListTemplatesAsync`, sharing one storage-content helper) feeds the dropdown; a new `CreateQemuAsync` + `ProxmoxQemuCreate` spec + `ProxmoxQemuCreateValidator` POST the hardware form (`scsi0` / `scsihw` / `net0` / `ide2` / `bios` / `machine` / boot order) to `POST /nodes/{node}/qemu` and poll the task UPID for real success/failure — OVMF auto-adds its `efidisk0` EFI vars disk on the same storage. It's **double-gated, off by default — the same switch as LXC create**: `Stashboard:AllowProxmoxCreate` (renamed **Settings → Create guest**) + a per-host **Allow create** opt-in, with deterministic `403`s before any host call, a `409` on a vmid collision, a `400` on a malformed spec, and a host rejection surfaced verbatim as `502`. On success the host re-scans so the new card appears ready to boot into its installer; every attempt is **audited** in the guest-kind-agnostic `ProxmoxCreateAudits` (Audit page's **Create** tab). **Out of scope:** passthrough, multiple disks / NICs at create time, cloud-init, and importing an existing disk image. See the [CHANGELOG](./CHANGELOG.md).

✅ **V9.2 — Stashboard can update itself** _(shipped in 9.2.0)_ — fixes **Update now** on the Stashboard container itself, which previously **broke the instance**: a container can't recreate itself in process (the `stop` → `remove` step kills the very process doing the work, so `create` + `start` never run and the container vanishes). 9.2 detects when an "Update now" targets our own container — over **any** transport (local socket, SSH tunnel or TCP); self is decided by matching the running container's **id** against the watch target, so a container on a genuinely remote daemon is correctly never "self" — via a new `ISelfUpdateLauncher`, and offloads the recreate to a **detached one-shot helper container** (the same Stashboard image run as `dotnet Stashboard.Api.dll self-update`) that inherits Stashboard's mounts + the decrypted connection profile, performs the normal pull + recreate from the outside (raw **or** Compose-aware, same as any update), and survives the parent restarting; the helper is always auto-removed when it exits so it never lingers. Covers **both** **Update now** (single container) and **Update project** (a Compose project that includes Stashboard — the whole project recreate is offloaded). The attempt is logged with a new **`Scheduled`** status carrying the target digest, and the **Update now** dialog shows a *"Self-update scheduled"* banner (the UI is briefly unavailable while it restarts); on the next startup a reconciler reads the container's **actual** digest and compares it to that target — match flips the row to **Success** and clears the "Update available" badge, mismatch flips it to **RecreateFailed** and keeps the badge (confirmed, never guessed, registry-independent). Requirements are the same as any "Update now" on that connection (a writable local socket, or an SSH/TCP connection that can recreate the container). Also bumps the bundled Docker Compose binary baked into the image **5.1.4 → 5.2.0**. See the [CHANGELOG](./CHANGELOG.md).

✅ **V9.1 — Derived-signal MQTT sensors** _(shipped in 9.1.0)_ — builds on the V9.0 publisher (same broker, prefixes, per-object device tree, retained topics, shared availability / Last Will, lifecycle cleanup) to publish the signals Stashboard **computes**, not just raw state. Four new entity families flow through the same reconcile / lifecycle path: **pending-update counts** as numeric `sensor`s — per **Docker host** (a new `Docker host` device), per **Proxmox node** (apt), and per **monitored LXC** — the number behind the V9.0 boolean; a **node-alert** `binary_sensor` (`device_class: problem`) per PVE/PBS node reflecting the V6.8.1 `ProxmoxNodeAlertEvaluator` verdict (`on` = warn/crit), with the per-category breakdown (CPU / memory / storage / thermal / SMART / network) + worst active severity carried as JSON **attributes**; per-guest **backup freshness** as a `timestamp` `sensor` (newest vzdump ctime via `ListBackupsAsync`, cached briefly so it isn't re-listed every cycle); and **estate roll-ups** on the single **Stashboard** hub device (containers / guests / services running-vs-total, hosts reachable, total updates pending). Per-node / per-guest entities attach to their node / guest devices, roll-ups ride the hub. Still **publish-only** and **state + derived signals** only (raw resource telemetry stays out of scope). Node-alert sensors need node alerting enabled for that host; backup-age sensors need the PVE host reachable. See the [CHANGELOG](./CHANGELOG.md).

✅ **V9.0 — MQTT publisher + Home Assistant Discovery** _(shipped in 9.0.0)_ — publishes the signals Stashboard already collects to your existing MQTT broker (e.g. Mosquitto) as Home Assistant **auto-discovered** entities — no HA YAML, no HACS add-on. Three `binary_sensor` families: each Docker container's and Proxmox guest's **running** state, each Docker container's **image-update available**, and each monitored service's **online/offline** health. **Publish-only** (read): HA observes, it can't control anything. An app-wide MQTT config lives on **Settings → Home Assistant** (mirroring the editable-SMTP model: broker host / port / TLS / username + **encrypted** password / client id / discovery prefix / entity prefix, master switch **off by default**, password never returned, changes apply without a restart, plus a **Test connection** button). A background `MqttPublisherService` holds one long-lived broker connection and publishes **retained** HA-Discovery config + state topics, grouping entities into **one HA device per real object** (each container / guest / service is its own device; a container and an LXC sharing a name stay separate, and a linked service's health joins its container/guest device) linked by `via_device` to a single **Stashboard** hub. Every entity's node id / object_id / unique_id is prefixed with the configured entity prefix (e.g. `binary_sensor.stashboard_jellyfin_running`); changing it re-publishes under new ids and clears the old retained topics. A single availability topic registered as the broker **Last Will** flips all entities to `unavailable` when Stashboard stops or the link drops, and a removed container / guest / service has its retained topics cleared so HA drops the entity. State is republished on transitions (no spam on unchanged ticks) with a periodic full refresh, and the publisher reconnects after a broker drop; the MQTT config (password encrypted) round-trips through backup/restore. **Out of scope:** control / command topics, raw resource telemetry, per-entity publish selection, and running a broker. See the [CHANGELOG](./CHANGELOG.md).

✅ **V8.6 — Browser VM console (noVNC)** _(shipped in 8.6.0)_ — the VM analogue of the V6.6 LXC console, closing the last LXC-only diagnostic gap. A QEMU/KVM VM has no `pct exec` and no guaranteed SSH / guest-agent, so the SSH-`pct exec` PTY transport can't be reused; instead the VM's **Console** tab renders the VM's **built-in VNC screen** with **noVNC** — the same screen the Proxmox web UI opens — with full keyboard / mouse control and a fit-to-window canvas. Stashboard **relays the VNC server-side**, exactly like the V6.6 console: it calls `POST …/qemu/{vmid}/vncproxy` (`websocket=1`), opens the Proxmox `vncwebsocket` from the **backend** (API token in the `Authorization` header, TLS to the host), and bridges the raw **RFB** stream to the browser **byte-for-byte** — the browser only ever sees the ephemeral one-time *vncproxy* ticket (the RFB password), **never the Proxmox API token**. It **reuses the V6.6 scaffold verbatim** — the same global switch (**Settings → LXC console**) + per-host **Allow LXC console** opt-in, the single-use ticket / WebSocket-upgrade dance, the shared concurrency caps + idle timeout, and the same `ProxmoxConsoleSessions` audit table (Audit → **Console**) — only the transport changes (a VNC relay instead of an SSH PTY) and the client renders noVNC instead of xterm. For a VM the **SSH requirement is dropped** (VNC uses the API token); the gate is the global switch + per-host opt-in + a **running** VM. Feasibility-gated: a host that refuses token-auth `vncwebsocket` relay (or a VM with no VGA console) shows a clear *"console unavailable on this host"* message instead of a broken canvas. **Out of scope:** SPICE, audio / clipboard / USB redirection, and the serial (`termproxy`) console. See the [CHANGELOG](./CHANGELOG.md).

✅ **V8.5 — Edit VM (QEMU) parameters** _(shipped in 8.5.0)_ — a VM's **Config** tab was **read-only** since V6.14 — every guest-config change beyond create / clone / restore meant opening the Proxmox web UI. This phase makes it **writable**, reusing the LXC config-editor scaffolding (V6.5 scalars + V6.9 structured network) rather than a parallel system: the per-field "null = leave untouched" merge, the structured row editors, a single **Save** that commits only the changed keys, `PUT …/qemu/{vmid}/config`, and a change-audit. **Scalars** — name, cores / sockets, memory (+ optional balloon minimum), `onboot`, `ostype`, the QEMU **guest-agent** toggle, boot order, description / tags — via a new `ProxmoxQemuConfigUpdate` spec + `UpdateQemuConfigAsync` + `ProxmoxQemuConfigValidator`. **NICs** — `net<n>` add / update / remove through a new **QEMU net codec** (`ProxmoxQemuConfigCodec`, alongside the LXC one — a VM NIC's first token is the device model carrying the MAC), with model / bridge / VLAN / MAC / firewall / rate / MTU / queues / link-down and a raw escape hatch. **CD-ROM** — swap or eject the `ide2` install media, reusing the V8.4 ISO dropdown. **Disks** — **grow** a disk (`…/resize`, grow-only — the size is a `+NG` increment so a shrink can't reach the host), **move** a disk to another storage (`…/move_disk`, task-polled like a clone), and toggle the safe flags (discard / SSD / cache) on Save; adding / removing a whole disk is deferred. The read-only tab gains the same inline-edit affordances the LXC modal has, with client-side guards mirroring the server validator — no second Save button, no auto-apply. Proxmox stays authoritative (any host rejection is surfaced **verbatim** as a `502`) and **every applied change is audited** on the Audit page's new **Guest config** tab via a new `ProxmoxConfigAudits` table — into which the **LXC config edit was retrofitted too**, so container and VM config edits share one history. See the [CHANGELOG](./CHANGELOG.md).

The roadmap continues in **[ROADMAP.md](./ROADMAP.md)** — the V7 visual Compose editor track is **complete** (V7.0–V7.9), the V8 create/clone/snapshot/restore track now covers both LXC **and** VMs (V8.0–V8.4 — create / clone / restore mirrored for both guest kinds), makes the VM Config tab writable (V8.5 — edit parameters) and adds the **browser VM console** (V8.6), and the **Home Assistant integration via MQTT** now covers both read-only state/update/health publishing (V9.0) **and** the derived-signal sensors — update counts, node-alert verdicts, backup freshness, estate roll-ups (V9.1). The active roadmap continues with the **V10 monitoring/notification depth track**.
## License

MIT
