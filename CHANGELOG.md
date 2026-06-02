# Changelog

All notable changes to Stashboard are recorded here. The format is loosely based
on [Keep a Changelog](https://keepachangelog.com/), and the project follows
[semantic versioning](https://semver.org/) — released as Docker image tags
`vahac/stashboard:X.Y.Z` (see [PUBLISHING.md](./PUBLISHING.md)).

## [5.9.0] — 2026-05-30

### Changed
- **Docker instances page redesign (V5.9).** The `/docker` page moves from
  the vertical, one-section-per-host layout to a **connection-switcher**
  layout:
  - A page-level **summary strip** (Containers · Running · Stopped · Updates)
    aggregates every host so the overall state of your fleet is the first
    thing you see.
  - A horizontal **connection switcher** picks one host as the context
    instead of scrolling through nested sections — *All connections* keeps
    the cross-host view available. Each pill shows the host's
    running-/-total count and an amber **updates** badge when any tracked
    watch on that host has a pending update.
  - Each host renders a compact **summary card** (status dot · transport ·
    endpoint · count · Terminal/Edit actions) with the **Storage** widget
    folded inline as a collapsible row (inline summary always visible;
    expand to see the 4-metric grid).
  - **Compose project groups** are now packed: each group is sized to
    exactly fit the cards it contains, so several small projects can sit
    side-by-side on one row instead of every group claiming a full page
    width. Column count recomputes on resize.
  - The **container card** itself gets a refresh: the `(healthy)` segment
    of the status line is colored green, the divider sits at the bottom so
    action rows align across cards in a row, the *service:* compose chip
    gets a small external-link affordance, and the diagnostics row
    (Inspect / Logs / Stats / Notifications / Exec) can be toggled off via
    the new **Diagnostics** display preference.
  - **Display preferences** live on the page (density: *comfortable* /
    *compact*; storage: *collapsed* / *panel*; diagnostics on/off),
    persisted per device in `localStorage`.
- The shared `StorageWidget` gained a `variant="collapsed"` mode used by
  the new layout; the V5.5–V5.8 always-expanded panel remains available
  via `variant="panel"`.

### Notes
- **No backend changes.** The redesign is frontend-only; the same
  `/api/docker/connections/*` endpoints from V3.5–V5.8 drive every value
  on the page. The container cards still drive the existing
  start / stop / restart / remove handlers, so the per-watch audit trail
  is unchanged.

## [5.8.0] — 2026-05-30

### Added
- **Session audit viewer (V5.8).** A new read-only **Audit** page (sidebar →
  **Settings → Audit**) surfaces the audit rows the product already records but
  previously kept write-only — you had to query `app.db` directly to read the
  shell / exec history. Four tabbed tables:
  - **Host terminal** — every browser SSH shell opened to a Docker host (V5.3):
    connection / host, user, start / end, duration, bytes in / out, and how it
    ended (with the error inline when it ended on one).
  - **Container exec** — every shell opened *inside* a container (V5.7), plus
    the command that was run.
  - **Update attempts** — the per-container "Update now" history (V2.7).
  - **Image prune** — scheduled and manual prune runs and how much was reclaimed
    (V5.5).

  Sessions still open show an **Active** badge (`EndedUtc == null`). Backed by
  four owner-scoped, newest-first, paginated `GET` endpoints under `/api/docker`
  (`host-shell/sessions`, `container-exec/sessions`, `update-attempts`,
  `prune-runs`), each accepting `?skip=&take=` (page size capped at 200) and an
  optional `?connectionId=` filter. No write/delete verbs — audit rows are
  immutable from the UI.
- **"View session history" cross-links.** The host-terminal dialog and the
  container **Exec** panel each link straight to the Audit page, pre-filtered to
  the connection you opened them from.

### Notes
- **No new auditing** — this release is purely a read path over the rows
  V5.3 / V5.7 / V2.7 / V5.5 already persist. No new tables, no migration.

## [5.7.0] — 2026-05-29

### Added
- **Container exec — a browser terminal *inside* a container.** A new **Exec**
  tab on the container modal opens an interactive shell in a running container
  via the Docker daemon's `exec` API (`POST /containers/{id}/exec` +
  `POST /exec/{id}/start`). This covers the "I just need to run one command in
  this container" case without SSHing to the host first. Unlike the V5.3 host
  terminal (SSH-only — it lands on the host), exec routes through the daemon, so
  it works for **every** connection type: local socket, TCP+TLS, and SSH tunnel.
  - **Per-session command.** The Exec panel has a command field defaulting to
    `/bin/sh`; change it to `/bin/bash` or anything the image ships before
    connecting. The chosen command is bound to the connect ticket server-side.
  - **Live resize.** The PTY follows the panel size in real time (the daemon
    exposes an exec-resize endpoint), unlike the host terminal where resize is a
    no-op.
- **Settings → Container exec page** with the app-wide master switch and a full
  write-up of the conditions and risks, mirroring the Host terminal page.

### Changed
- **The host terminal (V5.3) moved from the container modal to the connection
  level.** It opens a shell on the *host*, not inside any one container, so
  surfacing it as a per-container **Terminal** tab was misleading. It is now a
  **Host terminal** button in the connection header on the Docker page (shown for
  SSH connections); the per-container Terminal tab is removed. The host-shell
  API, gating, and audit are unchanged — only the UI entry point moved.
- **The shell button on each container card now opens the Exec tab** (a shell
  *inside* that container) instead of the host terminal — the container-scoped
  action that actually matches a per-container button.

### Security
- **Off by default, gated two ways** (both required): the server-wide master
  switch at **Settings → Container exec** (DB-backed —
  `ContainerExecSettingsEntity` / `GET|PUT /api/settings/container-exec` —
  seeded from the optional `Stashboard:AllowContainerExec` config flag on first
  run, surfaced to the frontend via `/api/features`) and the per-connection
  `AllowExec` opt-in (set in the connection's Edit form). The connection must be
  owned by the requesting user.
- **Audited.** Every session writes a start/stop row to the new
  `DockerExecSessions` table (who, when, connection / container, command,
  duration, bytes in / out, end reason) and streams to the application log.
- **Token-less WebSocket.** Reuses the V5.3 transport: an authenticated
  `POST …/exec/ticket` mints a short-lived, single-use ticket (binding the
  container + command); the socket opens at
  `…/containers/{name}/exec/ws?ticket=…` (ticket-authenticated). The byte pump
  (`HostShellSession`) and WebSocket adapter (`WebSocketShellClientTransport`)
  are shared with the host terminal.
- **Concurrency caps + idle timeout.** Per-user and per-host limits plus a
  server-side inactivity timeout (default 10 min) close idle / over-cap sessions
  regardless of client state, tunable via `STASHBOARD_Stashboard__ContainerExec__*`.

### Notes
- Pure-additive migration `AddContainerExec`: the per-connection `AllowExec`
  column, the `DockerExecSessions` audit table, and the `ContainerExecSettings`
  master-switch row. No data conversion; existing connections default `AllowExec`
  to off.
- Tests: ticket service (single-use / expiry / container+command binding),
  session registry caps, the settings service (seed / persist), the mapper
  opt-in (set / cleared / surfaced, every host type), and the controller's
  two-way gate including "works for every host type".

## [5.6.0] — 2026-05-29

### Added
- **Settings → Health checks page.** The probe schedule plus the three
  offline-alert reliability knobs introduced in 5.3.2 — failure threshold,
  in-probe retry count, and retry delay — are now editable from a dedicated
  Settings page instead of only `appsettings.json` / `STASHBOARD_HealthCheck__*`
  env vars. Each field has an inline explanation of what it controls and how it
  trades alert latency for fewer false alarms.
  - **Check interval** — how often Stashboard probes every monitored service
    (default 60 s / once a minute; minimum 10 s).
  - **Failure threshold** — how many consecutive failed scans are required
    before a service is marked Down and a 🔴 *Service unavailable* alert fires
    (default 3; set to 1 to alert on the first failure).
  - **In-probe retries** — extra attempts within a single probe on a
    connection-level failure (DNS / timeout / network / TLS); real HTTP
    responses (incl. 5xx) are never retried (default 2).
  - **Retry delay** — milliseconds to wait between in-probe retries
    (default 1000).
- Values are stored in the database and applied on the next scan **without a
  restart**. The `STASHBOARD_HealthCheck__*` env vars now only **seed** the
  settings row on first startup; after that, manage them from the page.

### Changed
- The scheduled scan and the manual **Check now** action both source the retry
  and threshold values from the DB-backed settings, so ad-hoc checks honour the
  same tuning as the background loop.

### Notes
- Pure-additive migrations `AddHealthCheckSettings` (new `HealthCheckSettings`
  table) and `AddHealthCheckInterval` (the `IntervalSeconds` column). No data
  conversion; existing deployments keep their configured env-var values until an
  operator edits them. `HealthCheck:RequestTimeoutSeconds` remains config-only.

## [5.5.0] — 2026-05-28

### Added
- **Scheduled image-prune sweep.** A background task runs `docker image prune`
  on every Docker connection on a configurable interval (default 168 h /
  weekly), removing the `<none>:<none>` dangling images container updates
  leave behind. Stops months of auto-updates from quietly filling the disk.
  - Master toggle and interval live on a dedicated **Settings → Image
    cleanup** page. Default enabled; minimum 1 hour interval.
  - Per-connection opt-out (`AllowImagePrune`, default `true`) on the
    Docker connection form so individual hosts can be excluded.
  - Per-connection opt-in to the aggressive **"also prune unused images"**
    scope (`PruneUnusedImages`, default `false`). Off by default because
    removing non-dangling unused images can break a
    rollback-to-previous-tag workflow.
- **Storage widget on the V3.5 Docker instances page.** Shows total image
  count, dangling count + size, unused count + size, and the last prune's
  timestamp. A **Prune now** button opens a dry-run preview dialog with a
  one-off *"also prune unused"* override before committing.
  - **Clickable stats → per-image drill-down.** Clicking *Total* / *Dangling* /
    *Unused* opens a modal listing exactly which images make up that count —
    repo tag (or `<none>` for dangling), short id, creation date, size, and
    dangling / unused badges — so the user can see what's actually on the host.
  - **Prune dialog lists the images that will be removed**, not just a count,
    so it's clear what's about to be deleted (scrolls when there are many). The
    storage endpoint now returns the full image inventory to power both.
  - **Explains dangling images that can't be pruned.** Docker never deletes an
    image still referenced by a container (running or stopped), so a
    dangling-but-in-use image survives a prune. The dialog now counts only the
    genuinely removable images and calls out any in-use dangling image by name
    of the container holding it ("used by `frigate` — remove that container
    first"), instead of promising to free space the prune then can't.
- **Audit table for prune runs.** Every scheduled and manual run records
  trigger, scope (dangling-only vs. + unused), images deleted, bytes
  reclaimed, and any error. The most recent rows surface in the storage
  widget so an operator can see at a glance when each host was last
  cleaned up and how much was freed.
- New API endpoints:
  - `GET /api/docker/connections/{connectionId}/instance/images/storage`
  - `POST /api/docker/connections/{connectionId}/instance/images/prune`
  - `GET|PUT /api/settings/image-prune`
- New `IDockerHostClient.GetImageStorageAsync` and `PruneImagesAsync`
  methods on the Docker host client. Pure-additive migration
  `AddImagePrune` (new `ImagePruneSettings` table + `DockerPruneRuns`
  audit table + three columns on `DockerConnections`).

### Changed
- **Scheduled prune no longer fires immediately on a freshly added
  connection.** A connection becomes due for its first scheduled prune one
  full interval after it was *created* (the clock is `LastImagePruneUtc ??
  CreatedUtc`), instead of treating "never pruned" as instantly due — so a
  brand-new connection isn't pruned within seconds of being added, and *Last
  prune* no longer shows a timestamp the operator never triggered. The manual
  **Prune now** button is always available for an immediate cleanup.

### Out of scope (intentionally)
- **Volume pruning.** Volume cleanup is too easy to get wrong and is
  explicitly out of scope.

## [5.4.0] — 2026-05-28

### Added
- **Compose project grouping on the Docker instances page.** Containers carrying
  the `com.docker.compose.project` label are now bucketed under a project header
  card that shows the project name plus a *"N of M tracked services have updates
  available"* counter. Standalone containers (no Compose project label) continue
  to render in a trailing ungrouped row. Click the project badge to filter the
  page to that project only.
- **One-click "Update project" button.** Pulls every image in the project and
  recreates the services in dependency order without making the user click
  **Update now** once per container.
  - On a Local socket connection with a bind-mounted Compose project (V5.2) and
    `docker compose` available inside the Stashboard container, the bulk update
    shells out a single `docker compose pull` + `docker compose up -d` against
    the project root, so Compose honours `depends_on` ordering itself.
  - Otherwise the updater falls back to per-service raw recreate, ordered by a
    best-effort topological sort of the `com.docker.compose.depends_on` labels
    Compose v2 writes on every container.
- **Audit log treats the bulk operation as a single auditable unit.** One
  aggregate parent row (`ActionType = UpdateProject`) is written alongside one
  child row per service (linked via `ParentAttemptId`), so the per-watch history
  view and any future per-project activity view share the same trail.
- New API endpoint:
  `POST /api/docker/connections/{connectionId}/instance/projects/{projectName}/update`.
  Returns the parent + child audit rows and a `mode` field (`"Compose"` or
  `"Recreate"`) describing which dispatch path the backend took.
- **Auto re-check tracked watches after a successful bulk update.** The
  per-watch *Update now* flow already did this; the bulk endpoint now mirrors
  it — for every service that succeeded *and* is tracked by a watch, the
  backend runs `IDockerUpdateChecker.CheckAsync` + `DockerWatchStatusWriter` so
  the dashboard's *Update available* badge clears without forcing the user to
  click *Check now* on each container. Re-check failures are recorded on
  `Watch.LastError` but never undo the recreate.
- **Confirmation + progress modal for the bulk run.** Clicking *Update project*
  opens a 3-phase dialog: Confirm (lists every service with its image and an
  "untracked — anonymous pull only" hint where relevant, plus the V5.2
  raw-vs-Compose dispatch warning and a brief-downtime caveat) → Running (each
  service row spins) → Done (each row shows ✓ Updated or ✗ Failed with the real
  per-service error inline) / Error.
- **Unified per-container and per-project update dialogs.** The per-watch
  *Update now* button now opens the same 3-phase modal as *Update project* (with
  a single target instead of N), so the user sees a consistent
  confirm → progress → outcome flow regardless of which button they clicked.

### Changed
- **Single-service Compose projects no longer get a group header.** Compose
  always stamps `com.docker.compose.project` on every container — even on
  one-liner stacks with a single service — which previously produced a noisy
  *"0 of 1 tracked services have updates available"* header whose *Update
  project* button did the same thing as the container's own *Update now*. A
  compose group with exactly one container is now demoted to a standalone card
  (the compose-project badge on the card itself still tells the user the
  container is Compose-managed). The group shell + *Update project* button only
  appears once ≥2 services share a project name.
- **Surface real per-service errors when bulk update returns 502.** The bulk
  endpoint returns the same structured `DockerProjectUpdateResponse` body on
  both `200` (full success) and `502` (any service failed); the UI now reads
  the per-service rows from the 5xx response instead of falling into a generic
  *"An error occurred."* state, so failures like *"Container started but did not
  become healthy within 30 s."* land on the failing service's row.
- **Docker instances page layout rework + per-card resolved image tag.** The
  container card layout was tightened, and each card now surfaces the resolved
  image tag even when the container hasn't been redeployed since the watch
  resolved a newer tag (previously only digests were shown for un-redeployed
  containers).

### Notes
- Pure-additive migration `AddDockerUpdateAttemptComposeProject` adds two
  nullable columns to `DockerUpdateAttempts` (`ComposeProject`,
  `ParentAttemptId`) plus a self-FK + index. Existing rows are untouched. No
  action needed on upgrade.
- For an untracked container participating in a bulk update the fallback path
  attempts an anonymous pull only — credential storage requires the container
  to be tracked by a watch.
- Remote (TCP+TLS / SSH) connections always take the raw fallback path —
  remote compose shell-out is still deferred.

## [5.3.2] — 2026-05-27

### Fixed
- **False "Service unavailable" Telegram alerts from transient blips.** A single
  failed probe — a momentary DNS hiccup (`Name or service not known`), a
  `Timeout`, a `Network is unreachable`, or a TLS framing glitch on the
  Stashboard host's own network — used to flip a service straight to **Down** and
  fire an offline notification, even though the service was actually up. The
  health check now confirms a failure before acting on it.

### Added
- **In-probe retries.** When a probe fails for a connection-level reason (DNS,
  timeout, network, TLS handshake), it is retried a few times within the same
  scan before being treated as a failure. A real HTTP response — including a 5xx
  — is never retried, so genuine outages are still detected immediately.
  Configurable via `STASHBOARD_HealthCheck__RetryCount` (default `2`) and
  `STASHBOARD_HealthCheck__RetryDelayMs` (default `1000`).
- **Consecutive-failure threshold.** A service is only marked Down (and an alert
  sent) after N consecutive failed scans; until then the previous status is kept,
  so neither the dashboard card nor Telegram reacts to a one-off blip. A single
  success resets the counter. Configurable via
  `STASHBOARD_HealthCheck__FailureThreshold` (default `3`, floor `1` = legacy
  notify-on-first-failure behaviour).

### Notes
- Pure-additive migration `AddHealthCheckFailureCounters` adds two integer
  columns (`MainUrlConsecutiveFailures`, `AdditionalUrlConsecutiveFailures`) to
  `WebResources`; existing rows default to `0`. No action needed on upgrade.
- With the defaults, a service must fail ~3 consecutive scans before alerting —
  at the default 60 s interval that is roughly a 2–3 minute sustained outage.
  Lower `FailureThreshold`/`RetryCount` to alert faster, raise them for noisier
  networks.

## [5.3.1] — 2026-05-25

### Fixed
- **Tag-pattern filter could pick the wrong "latest" tag**, leaving a watch stuck
  on a phantom *Update available*. Found while auditing the feature:
  - `TagVersionComparer`: semver-shaped tags now always outrank non-semver ones
    (so the **Stable only** preset no longer picks `nightly` over `1.28.0`),
    prerelease identifiers compare per semver §11 (numeric vs. alphanumeric,
    `rc.2` < `rc.10`), and build metadata (`+…`) is ignored for precedence.
  - `OciRegistryClient.ListTagsAsync` now follows the `Link: rel="next"` header,
    so repositories with more tags than one page still surface their newest tag
    (capped by page count + total tags).
  - `DockerUpdateChecker` uses full-match regex semantics, so an un-anchored
    pattern like `v\d+\.\d+\.\d+` no longer accepts `v1.2.3-rc1`.
  - `DockerWatchStatusWriter` clears a stale latest-tag pointer on a definitive
    `UpToDate` / `UpdateAvailable` result while preserving last-known-good on a
    transient `Error`.
  - The **Stable only** preset is now case-insensitive and covers more
    pre-release / rolling markers.

### Improved
- **Surface version tags in the UI.** Each container update now shows the human
  version tag next to its `sha256` digest, so you can see *which* version a
  watch resolved to instead of only the digest.

## [5.3.0] — 2026-05-25

### Added
- **Host terminal (browser SSH shell to the Docker host).** A new **Terminal**
  tab on the container modal opens a full interactive shell *on the Docker host*
  (not inside a container) for **SSH** connections — no more leaving Stashboard
  to `ssh` to the box. Built on `xterm.js` in the browser and an SSH PTY
  (`CreateShellStream`) on the server, bridged over a WebSocket.
- The Terminal tab is **always present** so the affordance is discoverable. For
  LocalSocket / TCP+TLS connections it shows a disabled explainer ("Available
  only for SSH tunnel connections"); only SSH connections render the live shell.
- **WebSocket transport with single-use ticket auth.** A browser `WebSocket`
  can't send the JWT header, so an authenticated `POST .../host-shell/ticket`
  mints a short-lived, single-use ticket bound to the connection, and the socket
  opens with `?ticket=…`. `Program.cs` now enables `UseWebSockets()` (the first
  WebSocket in the app — reused by the later shell phases on the roadmap).

### Security
- **Off by default, gated three ways** (all required): the server-wide toggle at
  **Settings → Host terminal** (a DB-backed setting managed in the UI, *not* an
  env var — mirrors the editable SMTP settings), a per-connection **Allow host
  terminal** opt-in, and ownership of an **SSH** connection. This is the most
  dangerous surface in the product (host-level RCE), so it never lights up
  implicitly. The Settings page spells out every condition and the risks.
- **Audited.** Every session writes a start/stop row (who, when, connection /
  host, duration, bytes in / out, end reason) to a new `HostShellSessions`
  table and streams to the application log.
- **Caps + idle timeout.** Per-user and per-host concurrent-session caps and a
  server-side inactivity timeout close idle sessions regardless of client state,
  tunable under `Stashboard:HostShell` (`MaxSessionsPerUser`,
  `MaxSessionsPerHost`, `IdleTimeoutSeconds`, `TicketTtlSeconds`).

### Notes
- **Live resize caveat.** SSH.NET 2024.2.0 does not expose a PTY `window-change`
  request, so the shell is created at the browser's reported size on connect but
  later auto-resize is a no-op. The terminal works regardless.
- **Existing deployments are unaffected.** The feature stays dark until an
  operator turns it on at **Settings → Host terminal** *and* enables it on a
  specific SSH connection. Pure-additive migrations `AddHostShell` (the
  per-connection `AllowHostShell` column + the `HostShellSessions` audit table)
  and `AddHostShellSettings` (the DB-backed master-switch row). The optional
  `Stashboard:AllowHostShell` config flag only seeds the master switch on first
  run.

## [5.2.0] — 2026-05-23

### Added
- **True Compose-aware "Update now" recreate.** When a Docker connection
  (LocalSocket only) has a **Compose project path** configured and the tracked
  container is Compose-managed (carries the `com.docker.compose.service` label),
  the "Update now" button now shells out to the bundled `docker compose` CLI
  (`pull` + `up -d <service>`) instead of the raw `Docker.DotNet` recreate. This
  preserves the full Compose lifecycle the raw recreate can't replicate:
  `env_file` resolution, `depends_on` ordering, profiles, and Compose's own
  network / subnet allocation. Post-recreate health verification (V3.2) still
  applies, and every attempt is still audited.
- New optional **`ComposeProjectPath`** field on a Docker connection — the
  absolute path, *inside* the Stashboard container, to the host's Compose
  project directory (the bind-mount target). Surfaced in the connection form
  for LocalSocket hosts. Included in backup export/import.
- The runtime image now bundles the standalone **Docker Compose v2** binary so
  the feature works without a custom image.
- **"Update now" confirmation dialog.** The button now opens a proper dialog
  (replacing the old browser `confirm()`) that names the image + container,
  warns that recreating a running container isn't risk-free, and links to the
  documentation. The recreate runs only after you confirm there.

### Changed
- The Stashboard image gained the `docker compose` CLI (≈ one self-contained
  binary). It is only invoked when a connection has a Compose project path set;
  otherwise behaviour is unchanged.

### Notes for upgraders
- **Existing deployments are unaffected.** Without a Compose project path, the
  "Update now" button keeps using the V2.7 raw recreate exactly as before.
- To opt in: bind-mount the host's Compose project directory read-only into the
  container (e.g. `- /srv/my-stack:/compose-projects/home-server:ro`), keep the
  Docker socket **writable**, and set the connection's "Compose project path" to
  the in-container path. See
  [DOCKER_UPDATE_MONITORING_GUIDE.md](./DOCKER_UPDATE_MONITORING_GUIDE.md) §5.2.
- Remote (TCP+TLS / SSH) connections stay on the raw recreate — remote Compose
  shelling is out of scope for this release.

## [5.1.0] — 2026-05-22

### Added
- **Secure key auto-provisioning.** On first start, if no encryption key or JWT
  signing secret is supplied, Stashboard now generates cryptographically strong
  values (AES-256 key / 48-byte signing secret) and persists them under
  `.secrets/` next to the SQLite database — on the `stashboard-data` volume. They
  are read back on every later start, so **image updates and container recreation
  never re-key your data**. New deployments need zero manual key management; just
  back up the `stashboard-data` volume. See the [installation guide](./INSTALL.md)
  and the README "Secrets" section.
- New `STASHBOARD_Stashboard__SecretsPath` setting to override where the
  persisted secrets are stored (defaults to a `.secrets` folder beside the
  database).
- New [INSTALL.md](./INSTALL.md) — a detailed, step-by-step guide for bringing up
  the container from scratch.

### Changed
- `docker-compose.yml` and `.env.example`: the encryption key and JWT secret are
  now **optional**. The previous hard-fail guards that refused to start without
  them have been removed. An explicitly supplied value still takes precedence and
  disables auto-generation for that secret — so existing deployments, external
  secret managers, and key rotation keep working unchanged.
- **The `.env` file is now optional.** `docker-compose.yml` marks its `env_file`
  as `required: false` (needs Docker Compose v2.24+), so `docker compose up -d`
  works from just the compose file — every value has a built-in default. Add a
  `.env` only to override the port, pin a tag, set SMTP, or supply your own keys.
  `deploy.sh` now warns instead of failing when no `.env` is present.

### Notes for upgraders
- **Existing deployments are unaffected**: if you already set
  `STASHBOARD_ENCRYPTION_KEY` / `STASHBOARD_JWT_SECRET`, those values continue to
  be used exactly as before.
- If you were relying on the startup error to remind you to set keys, note that a
  blank key now triggers safe auto-generation instead of a failure.
