# Stashboard — Product Roadmap

> This document is the forward-looking product roadmap — unshipped phases
> only. The historical detail (Docker-update-checker V1–V3, V4 SQLite
> migration, the original V1 implementation checklist) lives in
> [`HISTORY.md`](./HISTORY.md).
>
> **Numbering note:** the legacy section numbers §1–§13 and §15 now live
> in HISTORY.md; §14 remains in this file at its original heading number
> to avoid breaking external references from PRs and commit messages.
>
> **Status (shipped milestones, V5+):** ✅ V5.0 (disabled card style + one-click removal) · ✅ V5.0.1 (unlink container from service) · ✅ V5.0.2 (editable SMTP / email settings) · ✅ V5.0.3 (dedicated notifications settings page) · ✅ V5.1 (secure key auto-provisioning, image 5.1.0) · ✅ V5.2 (true Compose-aware recreate, image 5.2.0) · ✅ V5.3 (host terminal, image v5.3.0) · ✅ V5.3.1 (tag-pattern filter correctness + version tags, image 5.3.1) · ✅ V5.3.2 (reliable offline alerts, image 5.3.2) · ✅ V5.4 (Compose project grouping & bulk update, image 5.4.0) · ✅ V5.5 (image cleanup / prune, image 5.5.0) · ✅ V5.6 (health-check tuning page, image 5.6.0) · ✅ V5.7 (container exec, image 5.7.0) · ✅ V5.8 (session audit viewer, image 5.8.0) · ✅ V5.9 (Docker instances page redesign, image 5.9.0). All shipped V5.x phases keep their detail in §14 below; V1–V4 historical detail is in [`HISTORY.md`](./HISTORY.md). End-user documentation: [`DOCKER_UPDATE_MONITORING_GUIDE.md`](./DOCKER_UPDATE_MONITORING_GUIDE.md).

## 14. Post-V4 backlog (V5+) — deferred Docker features

> The Docker items here were previously catalogued as **V3.6 – V3.11**. They are
> sequenced **after the V4 SQLite migration** and renumbered **V5.2 – V5.8**.
> The interactive-shell phases — **V5.3** host terminal, **V5.7** container-exec
> and **V6.1** Proxmox-LXC SSH — share one xterm.js + WebSocket + ticket
> transport, which **V5.3 introduces**. **V5.0–V5.1** are
> shipped cross-cutting work that landed in the same window: V5.0/V5.0.x are the
> instances-page + notifications refinements, and **V5.1** is secure key
> auto-provisioning. Most Docker phases are ordered simplest → most complex —
> earlier phases reuse infrastructure already in place (Docker client, audit
> log, permission gate) before the phases that need new transports (WebSocket),
> new external integrations (Proxmox API, SSH) or new security surfaces
> (interactive shells). **Exception:** V5.3 (host terminal) is a high-complexity
> shell phase pulled to the front by priority — it also establishes the shared
> WebSocket / ticket transport the later shell phases reuse.
>
> *(Note: the "first-class containers" decoupling that the codebase tags `V3.6`
> in comments is a separate, already-shipped piece of work — not the
> Compose-aware recreate below.)*

---

### Phase V5.0 — Disabled card style + one-click removal for exited/dead containers

**Complexity:** Low
**Value:** After migrations or stack restructuring (e.g. moving from a
`postgres:16-alpine` sidecar to SQLite), Docker hosts are often left with
containers that are permanently stopped and will never be restarted. These
containers still appear on the V3.5 instances page with a plain `EXITED` badge
alongside healthy running containers, making it hard to distinguish "stopped on
purpose, will restart" from "orphaned, can be deleted". A distinct disabled
visual treatment and a first-class Remove affordance let the user clean up the
docker host without leaving Stashboard.

**Scope:**

- **Disabled card visual.** Cards whose container state is `exited`, `dead`, or
  `removing` render in a visually suppressed style: muted text colour, reduced
  opacity (e.g. `opacity: 0.6`), a subtle dashed or dimmed border, and the
  state badge using a neutral grey rather than the animated red that currently
  applies to `dead` / `removing`. Running containers remain visually prominent
  and unaffected.
- **Remove button always present for exited/dead containers.** The existing
  Remove button is gated by the server-side `Stashboard:AllowContainerRemoval`
  feature flag and was designed for the general case. Exited/dead containers get
  a dedicated **Remove container** button that is always rendered when the
  container is in one of those terminal states, independently of the flag. The
  button is positioned in the card's action row (beside the existing Start
  button) and uses a destructive colour (red) to signal irreversibility.
  - `running` / `restarting` / `created` / `paused` containers are not affected
    — the "always visible" treatment applies only to terminal states.
- **Confirmation dialog.** Clicking Remove pops a modal (not `window.confirm`)
  naming the exact container and image before any action is taken:
  ```
  Remove container?
  ┌───────────────────────────────────────────────────┐
  │  Container: stashboard-db                         │
  │  Image:     postgres:16-alpine                    │
  │  Status:    Exited (0) 17 minutes ago             │
  │                                                   │
  │  This will permanently remove the container.      │
  │  The image will NOT be deleted.                   │
  │                                                   │
  │              [Cancel]   [Remove container]        │
  └───────────────────────────────────────────────────┘
  ```
  The confirmation button is labelled "Remove container" (not just "OK") so the
  action is unambiguous even if the user glances away and refocuses.
- **API reuse.** The existing `DELETE /api/docker/connections/{id}/instance/containers/{name}`
  endpoint (shipped in V3.5) handles the actual removal. No new backend work
  is needed beyond verifying it handles the exited-container case correctly
  (Docker accepts `DELETE /containers/{id}?force=false` for stopped containers
  without the `force` flag — confirm this is what the current controller sends).
- **Post-remove feedback.** On success the card animates out of the grid and a
  toast confirms `"stashboard-db removed"`. On failure (daemon error, container
  was in a different state by the time the request arrived) the dialog stays open
  and surfaces the error inline.
- **Filter interaction.** The existing "running / stopped / all" filter radio
  correctly groups exited containers under "stopped" — no changes needed.
  The disabled-card style applies in both the "stopped" and "all" views.

**Tests:**

- Frontend: confirm the disabled CSS class is applied exactly when state is
  `exited`, `dead`, or `removing`; confirm the button renders for those states
  regardless of the `AllowContainerRemoval` flag; confirm the confirmation
  dialog contains the correct container name + image; confirm the card is
  removed from the list on success and the error is shown on failure.
- Backend (regression): `DeleteContainer` endpoint returns `204` for a stopped
  container without `?force=true`; returns `404` for a container name that no
  longer exists (daemon already removed it).

---

### ✅ Phase V5.0.1 — Unlink container from service

**Complexity:** Low
**Value:** After linking a tracked container to a service there was no way to
unlink it from the service modal — the user had to navigate to the Docker page,
open the watch form, and change the "Linked service" dropdown to standalone.
This phase adds a one-click "Unlink" button next to each linked container in
the service modal's Docker section, making the link reversible without leaving
the modal.

**Shipped:**

- ✓ New endpoint `DELETE /api/docker/connections/{connectionId}/watches/{watchId}/service-link`
  on `DockerConnectionWatchesController`. Sets `WebResourceId = null` on the
  watch and returns the updated `DockerWatchResponse`. Idempotent — calling it
  on an already-standalone watch is a no-op 200. Owner-scoped via the parent
  Docker connection.
- ✓ `useUnlinkConnectionWatch` mutation in `queries.ts`. Parameterless hook
  (connection and watch IDs are passed per-call) so it can be used from the
  service modal context where a single connection ID isn't available up-front.
  Invalidates the services query on success so the linked-watch list refreshes.
- ✓ "Unlink" button (with `Unlink` icon) in the `LinkedContainersSummary`
  component inside the service modal's Docker tab. Positioned next to the
  existing "Open container" link. Tooltip explains that the container keeps
  being tracked on the Docker page.
- ✓ Tests: 3 new `ConnectionWatchEndpointTests` — unlink clears WebResourceId
  (persisted), already-standalone returns OK, foreign connection returns 404.

**DoD met:** a container linked to a service can be unlinked with one click
from the service modal, verified by
`ConnectionWatchEndpointTests.UnlinkService_ClearsWebResourceId`. ✅

---

### ✅ Phase V5.0.2 — Editable SMTP / email settings

**Complexity:** Low
**Value:** The mail-server configuration previously lived only in `appsettings.json`
/ `STASHBOARD_Email__*` env vars, so changing the SMTP host, switching a Gmail
App Password, or turning real sending on/off meant editing config and restarting
the container. This phase moves the email settings into the database — the same
DB-backed, UI-editable mechanism already used for the per-user Telegram bot token
— so an operator can manage them from the **Account** page without a redeploy.

**Shipped:**

- ✓ New single-row entity `EmailSettingsEntity` (fixed `SingletonId`) holding the
  full `Email` section: `Provider`, `Host`, `Port`, `UseStartTls`, `Username`,
  `PasswordEncrypted`, `FromAddress`, `FromName`, `AppBaseUrl`. Pure-additive EF
  migration `20260522113017_AddEmailSettings` (new table only — no data
  conversion). The row is created lazily on first access, seeded from the bound
  `EmailOptions` so an existing deployment keeps its configured values until an
  operator edits them.
- ✓ The SMTP password is encrypted at rest with the existing `IEncryptionService`
  (AES-256-GCM) — never persisted or returned in plaintext. The API exposes only
  a `HasPassword` flag; edits use the tri-state `SecretValueUpsert`
  (Keep / Set / Clear) so saving the form without retyping the password preserves it.
- ✓ `IEmailSettingsService` + `EmailSettingsService` (scoped) resolve and decrypt
  the settings. The two startup-bound senders (`SmtpEmailSender` / `LogOnlyEmailSender`)
  are replaced by a single `DbEmailSender` that reads the current settings per
  send: `Provider=Smtp` with a host configured sends via MailKit, otherwise the
  message is written to the logger (the old LogOnly behaviour). `AccountNotificationService`
  now sources `AppBaseUrl` from the same DB-backed settings.
- ✓ Two owner endpoints: `GET /api/account/email-settings` → `EmailSettingsResponse`
  (masked) and `PUT /api/account/email-settings` (`UpdateEmailSettingsRequest`).
- ✓ Frontend: `EmailSettings` / `EmailSettingsUpdate` types, `getEmailSettings` /
  `updateEmailSettings` API client functions, and an **Email server (SMTP)** card
  on the Account page mirroring the Telegram settings form (provider select,
  conditional SMTP fields, reveal-toggle password with a "leave blank to keep"
  placeholder).
- ✓ Tests: 4 new `AccountControllerTests` (seeded defaults, persist + encrypt,
  keep-password, clear-password) and the existing `AccountNotificationServiceTests`
  rewired onto a stubbed `IEmailSettingsService`. All 793 backend tests pass.

**DoD met:** an operator can change the SMTP host / credentials / provider from the
Account page and the next email is sent with the new settings without a restart;
the password is stored encrypted and never returned over the wire — verified by
`AccountControllerTests.UpdateEmailSettings_PersistsValuesAndEncryptsPassword`. ✅

---

### ✅ Phase V5.0.3 — Dedicated notifications settings page

**Complexity:** Low
**Value:** After V5.0.2 introduced DB-backed SMTP settings, both Telegram and
SMTP configuration lived on the Account page together with profile and security
actions. This phase separates notification-channel management into a dedicated
page so operational notification setup is easier to find and maintain.

**Shipped:**

- ✓ Added a dedicated **Notifications** page in the frontend and moved both
  Telegram and Email server (SMTP) forms there.
- ✓ Simplified the **Account** page to profile, appearance, and security
  actions only (password/email/account lifecycle).
- ✓ Added sidebar navigation entry (`/notifications`) and protected route
  wiring in the SPA.
- ✓ Updated documentation to reflect the new settings location.
- ✓ Validation: frontend build passes and backend account-related tests remain
  green (no API contract changes).

**DoD met:** Telegram and SMTP settings are managed from a dedicated page,
without changing backend notification semantics or restart behavior. ✅

---

### ✅ Phase V5.1 — Secure key auto-provisioning

**Complexity:** Low
**Value:** Self-hosters pulling the image from Docker Hub previously had to
hand-generate `STASHBOARD_ENCRYPTION_KEY` / `STASHBOARD_JWT_SECRET` and set them
in `.env` before the container would even start (the compose file hard-failed
without them). This phase makes a fresh `docker compose up -d` work with **zero
key management** while guaranteeing the encryption key stays stable across image
updates — so credentials encrypted at rest never become undecryptable after an
upgrade.

**Shipped (5.1.0):**

- ✓ `PersistedSecretProvider` (Infrastructure) — file-backed get-or-create: reads
  a secret from a file, or generates and persists one on first run. Atomic
  temp-file-then-move write; owner-only (`0600`) permissions on Unix; regenerates
  if the stored file is empty/corrupt.
- ✓ `SecretProvisioning` (Api) — on startup, if `Encryption:Key` / `Jwt:Secret`
  are unset, generates a 32-byte AES key and a 48-byte JWT secret and persists
  them under `.secrets/` next to the SQLite database (on the `stashboard-data`
  volume). An explicitly supplied value always wins and disables auto-generation
  for that secret, so existing deployments, external secret managers, and key
  rotation are unaffected. Secrets dir overridable via
  `STASHBOARD_Stashboard__SecretsPath`.
- ✓ `docker-compose.yml` / `.env.example`: keys are now optional (dropped the
  `:?` hard-fail guards), and `env_file` is marked `required: false` so `.env`
  itself is optional — `docker compose up -d` works from just the compose file.
  `deploy.sh` warns instead of failing on a missing `.env`. `.gitignore` ignores
  `.secrets/`.
- ✓ Docs: README "Secrets" section + config table, new `CHANGELOG.md`, new
  step-by-step `INSTALL.md`, and `PUBLISHING.md` end-user flow updated.
- ✓ Tests: 4 new `PersistedSecretProviderTests`; full suite green (797). Manual
  end-to-end verified: first run generates + persists; second run loads the
  identical keys unchanged.

**DoD met:** a fresh deployment with no keys configured starts successfully,
auto-generating and persisting them; a subsequent restart/update reuses the same
keys without overwriting them. ✅

---

### ✅ Phase V5.2 — True Compose-aware recreate 

**Complexity:** Medium
**Value:** Preserves the full Compose lifecycle (env-file resolution, profile
membership, network / volume ordering, inter-service dependencies) that the
current raw `Docker.DotNet` recreate cannot replicate.

The V2.7 "Update now" path uses a Watchtower-style raw recreate: it reads the
running container's config via `ContainerInspect`, pulls the new image, stops +
removes the container, calls `CreateContainerAsync` with the preserved config,
and calls `StartContainerAsync`. This works for the common case and faithfully
copies labels (so the container stays Compose-managed), but it bypasses Docker
Compose entirely:

- **Compose lifecycle hooks** (`pre_start`, `post_stop` etc.) are not executed.
- **`depends_on` ordering** is ignored — if the recreated container has
  runtime dependencies on other services, they are not restarted in the correct
  order.
- **`env_file` resolution** happens at `docker compose up` time, not at
  `CreateContainerAsync` time — any environment variables sourced from files
  on the host are baked into the `ContainerInspect` snapshot, which may be
  stale if the file was updated after the container last started.
- **`profiles`** — a container inside a non-default profile may not be
  expected to be recreated in isolation.
- **Network subnet allocation** — Compose manages its own subnet pool; a
  plain `CreateContainerAsync` reattaches to the same named networks, but
  does not use Compose's IP-allocation logic.

**What true Compose-aware recreate requires:**

1. The `docker compose` CLI binary must be present inside the Stashboard
   container (`/usr/local/bin/docker-compose` or the `docker compose` plugin).
2. The `docker-compose.yml` (and any `.env` or `env_file` paths it references)
   must be accessible from inside the Stashboard container — they live on the
   host, not inside the container, so they need to be bind-mounted in.
3. The correct working directory (the project root) must be known at call time.
4. If Stashboard monitors containers on a *remote* host (TCP+TLS or SSH), the
   CLI would need to run against that remote context, which requires either
   Docker contexts or separate SSH exec — significantly more complexity.

**Proposed approach:**

- New optional field on `DockerConnection` (or per-watch): `ComposeProjectPath`
  — the absolute path on the *host* to the compose file's directory, mounted
  read-only into the Stashboard container at a well-known path (e.g.
  `/compose-projects/<connection-id>/`).
- `DockerImageUpdater` checks for the presence of the `docker compose` binary
  and a non-null `ComposeProjectPath`; if both are present it shells out
  `docker compose -f <path> up -d <service-name>` instead of the raw recreate.
- The service name to pass to Compose must be inferred from the running
  container's `com.docker.compose.service` label (already preserved in V2.7).
- Local-socket connections only — SSH/TCP remote hosts remain on the raw
  recreate path until a separate piece of work tackles remote Compose shelling.

**Shipped:**

- ✓ New optional `ComposeProjectPath` column on `DockerConnectionEntity`
  (`varchar(500)`, nullable) — the absolute path *inside* the Stashboard
  container to the host's Compose project directory (the bind-mount target).
  Pure-additive migration `20260523172259_AddDockerConnectionComposeProjectPath`.
  Cleared by the mapper when the host type isn't `LocalSocket` so a stale path
  can't shadow a switch to a remote transport.
- ✓ New `IComposeCommandRunner` + `ComposeCommandRunner` (Infrastructure).
  Detects the Compose CLI form (`docker compose` plugin → `docker-compose`
  standalone), caches the result, and shells out `pull <service>` + `up -d
  <service>` (no `--no-deps`, so `depends_on` ordering is honoured). Never
  throws — every failure maps to a typed `ComposeRunnerStatus`
  (`Success` / `CliNotAvailable` / `ProjectPathNotFound` / `CommandFailed`).
  The process launcher and directory probe are settable seams so the runner is
  fully unit-testable without spawning a process.
- ✓ `DockerImageUpdater` gained a Compose dispatch: when the connection is
  `LocalSocket`, `ComposeProjectPath` is set, the container carries the
  `com.docker.compose.service` label, **and** the CLI is available, it delegates
  to `IComposeCommandRunner` instead of the raw recreate, then re-inspects the
  container by name to resolve the new digest and runs the same V3.2 health
  verification. A missing CLI degrades gracefully to the raw recreate; a missing
  project directory is surfaced as `RecreateFailed` (no destructive fallback).
  `ComposeProjectPath` was threaded through `DockerUpdateProfile` +
  `DockerWatchMapper.BuildUpdateProfile`.
- ✓ Contracts: `DockerConnectionResponse` / `DockerConnectionUpsertRequest`
  expose `ComposeProjectPath`; `DockerConnectionMapper` round-trips it (and
  clears it for non-local hosts). `BackupService` export/import covers the new
  field (V4.3 standing DoD requirement for any persisted field).
- ✓ The runtime image bundles the standalone `docker compose` v2 binary
  (`Dockerfile`, pinned `COMPOSE_VERSION`) so the feature works without a custom
  image. Pull auth uses the host's Docker login state (documented limitation).
- ✓ Frontend: `composeProjectPath` on the `DockerConnection` /
  `DockerConnectionUpsert` types and a "Compose project path" field on the
  connection form, shown only for Local socket hosts, with a bind-mount hint.
- ✓ Tests: 8 `ComposeCommandRunnerTests` (plugin / standalone detection,
  detection caching, pull/up arg shape, pull-fail short-circuit, up-fail,
  missing project dir, CLI absent), 7 new `DockerImageUpdaterTests` (compose
  dispatch + raw-path skip, compose failure → RecreateFailed without touching
  the container, project-path-missing hint, health verification on the recreated
  container, graceful raw fallback when CLI absent, raw path for
  non-compose-managed + remote hosts), 4 new `DockerConnectionMapperTests`
  (persist / clear-on-remote / blank-normalises / response), 1 new
  `DockerWatchMapperTests` (profile carries the path), and a `BackupServiceTests`
  round-trip assertion. Full suite green (831).

**DoD met:** clicking **Update now** on a Compose-managed, local-socket watch
whose connection has a Compose project path bind-mounted in runs
`docker compose pull` + `up -d <service>` (honouring `env_file` / `depends_on` /
profiles), the watch's inline re-check flips it back to **Up to date**, and the
attempt is audited — verified by
`DockerImageUpdaterTests.Update_ComposeConfigured_LocalSocket_RecreatesViaComposeAndSkipsRawRecreate`.
✅ Remote hosts and bulk/project-level Compose updates remain deferred (V5.4).

---

### ✅ Phase V5.3 — Host terminal (browser SSH shell to the Docker host) 

**Complexity:** High (pulled ahead of the medium-complexity V5.4–V5.7 phases by
priority — see the §14 note above).
**Value:** The "I need a shell on the box itself, not inside a container" case —
today the user has to leave Stashboard and SSH to the host manually. Complements
the container-exec phase (V5.7): exec drops you *inside* a workload; this drops
you onto the *host* running it. For **SSH-type** connections Stashboard already
holds everything required (V2.5), so the host shell is largely a transport-and-UX
exercise rather than new plumbing.

**Shipped (5.3.0):**

- ✓ **SSH connections only.** Reuses the V2.5 material (`SshHost` / `SshPort` /
  `SshUsername` / `SshPrivateKeyEncrypted` / passphrase, decrypted through
  `DockerConnectionMapper.BuildTransport`) and the extracted `SshPrivateKeyLoader`
  for PEM parsing. A new `IHostShellConnector` (`SshHostShellConnector`) opens an
  interactive PTY via SSH.NET's `CreateShellStream("xterm-256color", cols, rows, …)`
  instead of the tunnel's `docker system dial-stdio` exec channel. `LocalSocket`
  / `TcpTls` have no host-shell channel by construction.
- ✓ **Transport.** First interactive-shell phase — introduces the `xterm.js` +
  WebSocket bridge later shell phases (V5.7 / V6.1) will reuse. `Program.cs` now
  enables `UseWebSockets()` (the first WebSocket in the app). The browser can't
  send the JWT header on a `WebSocket`, so an authenticated
  `POST .../host-shell/ticket` mints a **short-lived, single-use ticket** bound to
  `(user, connection)` (`IHostShellTicketService`) and the socket opens at
  `.../host-shell/ws?ticket=…&cols=&rows=` (ticket-authenticated, `AllowAnonymous`).
- ✓ A transport-agnostic byte pump (`HostShellSession`) bridges the WebSocket ↔
  PTY, counting bytes both ways, dispatching resize, and enforcing the idle
  timeout — unit-tested against in-memory fakes (the same split that keeps
  `SshDockerTunnel` testable). `WebSocketShellClientTransport` adapts the real
  socket (binary frames = stdin/stdout, text frames = `{"type":"resize",…}`).
- ✓ **UX — the Terminal tab is always present** on the container modal for every
  host type. SSH connections render the live `xterm.js` terminal (Connect /
  Disconnect, audit warning, status dot); `LocalSocket` / `TcpTls` show the
  disabled explainer **"Available only for SSH tunnel connections"**, and an
  SSH connection without the opt-in (or with the server flag off) shows the
  matching hint.
- ✓ **Security model — off by default, gated three ways** (all required): the
  server-wide master switch at **Settings → Host terminal** (a DB-backed setting
  — `HostShellSettingsEntity` / `GET|PUT /api/settings/host-shell` —
  managed in the UI like the editable SMTP settings, *not* an env var; surfaced
  to the frontend via `FeaturesController`, seeded from the optional
  `Stashboard:AllowHostShell` config flag on first run), the per-connection
  `AllowHostShell` opt-in, and ownership of an **SSH** connection. *(There is no
  role system in Stashboard — every connection is owned by exactly one user — so
  the spec's "admin only" reduces to "the owner, with the toggle + opt-in on".)*
  Every session writes a start/stop row to the new `HostShellSessions` table
  (who, when, connection / host, duration, bytes in / out, end reason) and
  streams to the application log. Per-user / per-host concurrent caps + a
  server-side idle timeout (`HostShellSessionRegistry` + `Stashboard:HostShell`
  options) close idle / over-cap sessions regardless of client state.
- ✓ Pure-additive migrations `AddHostShell` (the per-connection `AllowHostShell`
  column + the `HostShellSessions` audit table) and `AddHostShellSettings` (the
  DB-backed master-switch row). A dedicated **Settings → Host terminal** page
  hosts the toggle plus a full write-up of the conditions and risks. Tests:
  mapper opt-in (set / cleared / surfaced), ticket service (single-use / expiry /
  binding), session registry caps, the byte-pump end reasons + counts + resize,
  the settings service (seed / persist), and the controller's three-way gate.

**Caveat (confirmed):** SSH.NET 2024.2.0 exposes no PTY `window-change` on
`ShellStream`, so `TryResize` is a no-op — the shell is created at the browser's
reported size on connect and live auto-resize is unavailable. The terminal works
regardless.

**DoD met:** an operator who turns on the **Settings → Host terminal** toggle and
enables **Allow host terminal** for an SSH connection can open an interactive host
shell from the container modal's Terminal tab; the session is audited start-to-finish;
non-SSH connections and non-opted-in connections show the disabled states; and
the WebSocket refuses to open without a valid single-use ticket. ✅

---

### ✅ Phase V5.3.1 — Tag-pattern filter correctness + version tags

**Complexity:** Low (correctness fix on the existing Docker update-check pipeline).
**Value:** The per-watch **tag pattern** filter could resolve the wrong "latest"
tag, pinning a watch on a phantom *Update available* it could never clear — and
the UI showed only the opaque `sha256` digest, so users couldn't tell which
version a watch had actually resolved to.

**Shipped (5.3.1):**

- ✓ **`TagVersionComparer`** — semver-shaped tags always outrank non-semver ones
  (the **Stable only** preset no longer picks `nightly` over `1.28.0`), prerelease
  identifiers compare per semver §11 (numeric vs. alphanumeric, `rc.2` < `rc.10`),
  and build metadata (`+…`) is ignored for precedence.
- ✓ **`OciRegistryClient.ListTagsAsync`** follows the `Link: rel="next"` header so
  repositories with more tags than a single page still surface their newest tag
  (bounded by a page-count + total-tag cap).
- ✓ **`DockerUpdateChecker`** uses full-match regex semantics — an un-anchored
  pattern like `v\d+\.\d+\.\d+` no longer accepts `v1.2.3-rc1`.
- ✓ **`DockerWatchStatusWriter`** clears a stale latest-tag pointer on a definitive
  `UpToDate` / `UpdateAvailable` result while preserving last-known-good on a
  transient `Error`.
- ✓ The **Stable only** preset is case-insensitive and covers more
  pre-release / rolling markers.
- ✓ **UI** shows the resolved version tag next to each `sha256` digest. Tests:
  comparer precedence (semver vs. non-semver, prerelease ordering, build
  metadata), paginated tag listing, full-match pattern semantics, and the status
  writer's pointer clear/preserve behaviour.

**DoD met:** the **Stable only** / custom patterns resolve the genuinely newest
matching tag (no phantom updates), multi-page repositories are fully scanned, and
each watch shows its resolved version tag in the modal. ✅

---

### ✅ Phase V5.3.2 — Reliable offline alerts (no false positives)

**Complexity:** Low (reliability fix on the existing health-check loop).
**Value:** Users were getting **🔴 Service unavailable** Telegram alerts for
services that were actually up. Every alert traced to a single transient probe
failure on the monitoring host's own network — a momentary DNS miss
(`Name or service not known`), a `Timeout`, a `Network is unreachable`, or a TLS
framing glitch. The old loop flipped a service to **Down** and fired the alert on
the *first* failed probe, with no retry and no confirmation, so each blip became a
false alarm.

**Shipped (5.3.2):**

- ✓ **In-probe retries (`ServiceHealthChecker`).** A probe that fails for a
  connection-level reason (DNS / timeout / network / TLS handshake) is retried up
  to `HealthCheck:RetryCount` times (default 2) with `HealthCheck:RetryDelayMs`
  (default 1000 ms) between attempts, before being treated as a failure. A real
  HTTP response — including a 5xx — is **never** retried (the target answered), so
  genuine outages are still detected on the first scan. The transient/non-transient
  distinction is carried by an internal `ProbeOutcome` and never leaks to the
  public `HealthCheckResult`.
- ✓ **Consecutive-failure threshold (`HealthCheckStatusEvaluator`).** A service is
  only marked Down — and an offline notification only fires — after
  `HealthCheck:FailureThreshold` (default 3, floor 1) consecutive failed scans.
  Below the threshold the previous confirmed status and error are kept untouched,
  so neither the dashboard card nor Telegram reacts to a one-off blip; a single
  success resets the counter. The decision logic is extracted into a pure,
  unit-tested evaluator so the background service stays thin.
- ✓ Pure-additive migration `AddHealthCheckFailureCounters` adds
  `MainUrlConsecutiveFailures` / `AdditionalUrlConsecutiveFailures` (INTEGER,
  default 0) to `WebResources`. New config keys on `HealthCheckOptions`
  (`FailureThreshold`, `RetryCount`, `RetryDelayMs`). Tests: retry-then-success,
  retry-exhaustion, no-retry-on-HTTP-error; threshold flip / pending-window
  status-keep / counter-reset / flapping / independent additional-URL counter.

**DoD met:** a single transient probe failure no longer changes a service's status
or sends a notification; an alert fires only after the failure is confirmed across
the configured number of scans (with retries inside each), and clears state resets
both counters. ✅

---

### ✅ Phase V5.4 — Compose project grouping & bulk update

**Complexity:** Medium
**Value:** Real-world Docker hosts run *stacks*, not isolated containers.
Updating Postgres without also restarting the API that depends on it is the
class of mistake V5.2 is designed to prevent — but even with V5.2, the user
still has to click "Update now" once per service. Grouping makes this one
operation.

**Shipped (5.4.0):**

- ✓ **Project grouping on the V3.5 instances page.** Containers carrying the
  `com.docker.compose.project` label collapse into a project group with a
  header card showing the project name (clickable to filter the page to just
  that project) and a *"N of M tracked services have updates available"*
  counter, where N is computed against the user's `DockerWatch`es. Standalone
  containers (no Compose project label) keep their previous ungrouped layout.
- ✓ **Update project button.** New `IDockerProjectUpdater` orchestrator
  dispatches one of two paths:
  - Local socket + `ComposeProjectPath` set + Compose CLI available → shells
    out a single `docker compose pull` + `docker compose up -d` against the
    project root via the new `IComposeCommandRunner.RecreateProjectAsync`
    (Compose handles `depends_on` ordering itself); per-service post-state is
    harvested by re-inspecting each container.
  - Everything else (remote host, no project path, no CLI) → per-service raw
    recreate, ordered by a best-effort topological sort of the
    `com.docker.compose.depends_on` labels Compose v2 writes on every
    container. Each service goes through the existing `IDockerImageUpdater`
    so the V2.7 pull + recreate + V3.2 health-verification contract is
    preserved.
- ✓ **Audit log treats the bulk operation as one unit.** Pure-additive
  migration `AddDockerUpdateAttemptComposeProject` adds two nullable columns
  to `DockerUpdateAttempts` (`ComposeProject`, `ParentAttemptId`) + a
  self-FK + index. A bulk update writes one aggregate parent row
  (`ActionType = UpdateProject`) plus one child row per service
  (`ActionType = Update`, linked via `ParentAttemptId`). The per-watch
  *Update history* panel keeps surfacing child rows so the per-watch audit
  story is unchanged.
- ✓ **New endpoint** `POST /api/docker/connections/{connectionId}/instance/projects/{projectName}/update`
  returns `{ parent, services: […], mode }` with `mode = "Compose" |
  "Recreate"` so the UI can show which path ran. Failures land on `502` but
  still carry the parent + child rows so the UI can render exactly what
  happened.
- ✓ **Untracked containers** participate in the bulk update too — the
  fallback path attempts an anonymous pull; private images need a watch to
  supply credentials.
- ✓ **Auto re-check tracked watches after a successful bulk update.** Mirrors
  the per-watch *Update now* behaviour: for each service that succeeded and is
  tracked by a watch, the controller runs `IDockerUpdateChecker.CheckAsync` +
  `DockerWatchStatusWriter` so the dashboard's *Update available* badge clears
  without forcing the user to click *Check now* per container. Re-check
  failures land on `Watch.LastError` but never undo the recreate.
- ✓ **Confirmation + 3-phase progress dialog.** Clicking *Update project* opens
  a modal that transitions Confirm → Running → Done/Error. Confirm lists every
  service in the project with its image, an "untracked — anonymous pull only"
  hint where relevant, the V5.2 raw-vs-Compose dispatch warning, and a
  brief-downtime caveat. Running spins each service row. Done shows ✓ Updated /
  ✗ Failed per service with the real per-service error inline; finished rows
  dim and lock.
- ✓ **Unified per-container and per-project update dialogs.** The per-watch
  *Update now* button reuses the same `UpdateProgressDialog` with a single
  target, so the confirm → progress → outcome flow is identical for one
  container or many. The per-watch endpoint always returns `200` with
  `attempt.status` carrying the outcome; the dialog renders a failed recreate
  as a ✗ row instead of the error phase.
- ✓ **Real per-service errors surfaced on `502`.** The bulk endpoint returns
  the same structured `DockerProjectUpdateResponse` body on both `200` (full
  success) and `502` (any service failed). The mutation hook now catches the
  Axios error, returns the body when it matches the response shape, and the
  dialog flows into the *done* phase as if it were a normal response — each
  row shows Updated / Failed with its actual error instead of a generic
  "An error occurred." and frozen "Aborted" rows.
- ✓ **Single-service Compose projects are not wrapped in a group header.**
  Compose stamps `com.docker.compose.project` on every container, even on
  one-liner stacks. The grouping logic now demotes a compose group with
  exactly one container back to a standalone card (the compose-project badge
  on the card itself still flags it as Compose-managed). The group shell +
  *Update project* button only appears once ≥2 services share a project name.
- ✓ **Docker instances page layout polish.** Tightened container-card layout
  and surfaced the resolved image tag on the card even when the container
  hasn't been redeployed since the watch resolved a newer tag (previously
  only the digest was shown for un-redeployed containers).
- ✓ Tests: 4 new project-mode `ComposeCommandRunnerTests` (pull + up arg
  shape, pull-fail short-circuit, missing project dir, CLI absent), 9
  `DockerProjectUpdaterTests` (compose-aware happy path / aggregate failure /
  health downgrade; raw fallback dispatch + depends-on ordering + partial
  failure; empty-services short-circuit; topological sort + cycle guard
  units), 7 `DockerInstancesControllerTests` (happy path with parent + child
  rows, watch + service linkage, partial failure → 502 with rows persisted,
  404 for unknown project, 404 for foreign connection, plus 2 covering the
  post-success re-check firing for tracked services and being skipped for
  failed / untracked ones). Full suite green (916).

**DoD met:** clicking **Update project** on a Compose project grouping runs
one bulk update — `docker compose pull` + `docker compose up -d` on the
local-socket / Compose-configured path, per-service raw recreate ordered by
`depends_on` otherwise — and the per-watch audit history shows one parent +
N child rows for the operation. ✅ Remote Compose shell-out remains deferred.

---

### ✅ Phase V5.5 — Image cleanup / prune

**Complexity:** Medium
**Value:** Auto-update without cleanup is the fastest way to fill a disk.
The V2.7 recreate leaves the previous image tagged `<none>:<none>` once the
new one is in use; over months these dangling images can grow to many GB.

**Shipped (5.5.0):**

- ✓ **Scheduled image-prune sweep.** New
  `DockerImagePruneBackgroundService` ticks every 30 min; for each enabled
  connection where the master switch is on, `AllowImagePrune = true` and
  the configured interval has elapsed since the last successful run, it
  invokes the orchestrator and persists a `DockerPruneRunEntity` audit
  row. Default interval 168 h (weekly), minimum 1 h.
- ✓ **Settings → Image cleanup page** (DB-backed
  `ImagePruneSettingsEntity` singleton; seeded from the
  `Stashboard:ImagePrune` config block on first run, then managed in the
  UI). Two fields only: master enable + interval in hours.
- ✓ **Per-connection participation flags.** Two new columns on
  `DockerConnections`: `AllowImagePrune` (default `true`; opt-out) and
  `PruneUnusedImages` (default `false`; opt-in to the aggressive scope
  that also removes images not referenced by any running or stopped
  container). The connection form exposes both. `LastImagePruneUtc`
  spaces scheduled runs out per host.
- ✓ **Orchestrator (`IDockerPruneRunner` / `DockerPruneRunner`).**
  Stateless wrapper around the host client; maps daemon outcomes onto a
  small `DockerPruneStatus` enum (`Success` / `NothingToPrune` /
  `HostUnreachable` / `Failed`). Persistence is the caller's job — the
  controller and background service each own their own audit-row write.
- ✓ **Host client methods.** `IDockerHostClient.GetImageStorageAsync`
  returns total image count, dangling count + bytes, and unused count +
  bytes for the storage widget. `PruneImagesAsync(includeUnused)` calls
  `ImagesPruneAsync` with the correct `dangling=true/false` filter and
  surfaces images deleted + bytes reclaimed.
- ✓ **Storage widget on the V3.5 instances page.** Renders the four
  counts plus the last prune timestamp, refreshable on demand. **Prune
  now** opens a dialog that previews how many images / how many bytes
  will be removed in the chosen scope, with a one-off *"also prune
  unused"* checkbox (defaults from the connection's persisted opt-in).
- ✓ **API endpoints.**
  - `GET /api/docker/connections/{id}/instance/images/storage` — counts
    + recent prune-run rows.
  - `POST /api/docker/connections/{id}/instance/images/prune` — manual
    run; ignores `AllowImagePrune` for the owner, returns the persisted
    audit row on both 200 and 502 (host unreachable / daemon error).
  - `GET|PUT /api/settings/image-prune` — master toggle + interval.
- ✓ **Audit log + UI history.** Every scheduled and manual run writes a
  `DockerPruneRunEntity` row (trigger, scope, deleted count, reclaimed
  bytes, error). `LastImagePruneUtc` only advances on a successful run
  so a host-unreachable doesn't push the next attempt out by the full
  interval. The storage widget surfaces the 5 most recent rows for the
  connection.
- ✓ **Backup round-trip** carries the two new connection fields
  (`AllowImagePrune` / `PruneUnusedImages`) so export/import stays
  lossless.
- ✓ **Never touches volumes** — volume cleanup is intentionally out of
  scope.
- ✓ Pure-additive migration `AddImagePrune` (new `ImagePruneSettings`
  table + new `DockerPruneRuns` audit table + three columns on
  `DockerConnections`).
- ✓ Tests: 5 `DockerPruneRunnerTests` (success / nothing-to-prune /
  host-unreachable / include-unused passthrough / crash mapping), 4 new
  `DockerInstancesControllerTests` (storage shape + recent runs,
  cross-user 404, prune happy path + LastImagePruneUtc advancement,
  502 with audit row on failure, cross-user 404), 5
  `DockerImagePruneBackgroundServiceTests` (master switch off, opt-out
  skip, interval-not-elapsed skip, due-connection happy path with
  LastImagePruneUtc advancement, host-unreachable does *not* advance
  LastImagePruneUtc, PruneUnusedImages flag forwarded to the runner).
  Full suite green (932).

**DoD met:** dangling images left behind by auto-updates are reclaimed on
a configurable schedule without operator intervention; an operator can
run an ad-hoc prune from the Docker page with a dry-run preview; every
run lands in the audit log. ✅ Per-connection opt-out and the aggressive
"unused" scope are independently configurable. Volume cleanup remains
intentionally deferred.

---

### ✅ Phase V5.6 — Health-check tuning page

**Complexity:** Low (UI + DB-backed settings on top of the V5.3.2 reliability fix).
**Value:** V5.3.2 killed the false-positive **🔴 Service unavailable** alerts by
adding in-probe retries (`RetryCount` / `RetryDelayMs`) and a consecutive-failure
threshold (`FailureThreshold`) — but those three knobs lived only in
`appsettings.json` / `STASHBOARD_HealthCheck__*` env vars, so changing how
forgiving the offline detection is meant editing config and restarting the
container. Different homelabs want different trade-offs (a flaky Wi-Fi bridge
needs more retries; a critical service wants to alert on the first failure).
This phase moves the three knobs into the database — the same DB-backed,
UI-editable mechanism used for the editable SMTP settings, the host-terminal
switch (V5.3) and the image-prune settings (V5.5) — so an operator can tune them
from a dedicated **Settings → Health checks** page, with an inline explanation of
what each one does, and the change applies on the next scan without a redeploy.

**Shipped (5.6.0):**

- ✓ New single-row entity `HealthCheckSettingsEntity` (fixed `SingletonId`)
  holding `IntervalSeconds` / `FailureThreshold` / `RetryCount` / `RetryDelayMs`.
  Pure-additive EF migrations `AddHealthCheckSettings` (new table) +
  `AddHealthCheckInterval` (the interval column). The row is created lazily
  on first access, seeded from the bound `HealthCheckOptions` config block, so an
  existing deployment that set the env vars keeps those values until an operator
  edits them.
- ✓ `IHealthCheckSettingsService` + `HealthCheckSettingsService` (scoped) read /
  write the row, flooring `FailureThreshold` at 1 and the retry knobs at 0 the
  same way the runtime logic does. Two owner endpoints on the existing
  `SettingsController`: `GET /api/settings/health-check` →
  `HealthCheckSettingsResponse` and `PUT /api/settings/health-check`
  (`UpdateHealthCheckSettingsRequest`, `[Range]`-validated).
- ✓ The live values now flow from the DB, not config: the
  `HealthCheckBackgroundService` reads the settings once per scan and passes the
  retry knobs into `IServiceHealthChecker.CheckAsync` (new optional
  `HealthCheckRetrySettings` parameter — it still falls back to the bound
  `HealthCheckOptions` defaults when omitted) and the threshold into the
  `HealthCheckStatusEvaluator`. The manual **Check now** path
  (`WebResourcesController`) reads the same settings, so ad-hoc checks honour the
  configured retries. The `STASHBOARD_HealthCheck__*` env vars now only seed the
  row on first run. The **scan interval** (`IntervalSeconds`) is editable on the
  page too — the background loop reads it each cycle, so a change applies on the
  next sweep. `RequestTimeoutSeconds` stays config-bound (out of scope).
- ✓ Frontend: `HealthCheckSettings` type, `getHealthCheckSettings` /
  `updateHealthCheckSettings` API client, and a **Settings → Health checks** page
  (sidebar entry + protected route `/health-checks`) with three labelled number
  fields. Each field carries a description of what it controls and how it trades
  alert latency for fewer false alarms, plus a "how they work together" write-up.
- ✓ Tests: 4 new `HealthCheckSettingsServiceTests` (seed-from-config, persist,
  persistence across instances, defensive flooring). The existing
  `ServiceHealthCheckerTests` retry coverage is unchanged — the new parameter is
  optional and defaults to the config-bound values. Full suite green (938).

**DoD met:** an operator can change the scan interval, failure threshold,
in-probe retry count and retry delay from the **Settings → Health checks** page
and the next scan uses the new values without a restart; the values persist across restarts and seed
from the existing env vars on first run — verified by
`HealthCheckSettingsServiceTests.Update_PersistsAcrossNewServiceInstances`. ✅

---

### ✅ Phase V5.7 — Container exec (browser terminal into a Docker container)
<!-- V5.5 / V5.6 / V5.7 above are shipped; V6+ remain backlog. -->


**Complexity:** High
**Value:** The "I just need to run one command in this container" use case
that today forces the user to SSH to the Docker host first. Pairs naturally
with V3.3 (logs) and V3.5 (instances page).

**Shipped (5.7.0):**

- ✓ **Docker API exec.** A new `IContainerExecConnector`
  (`DockerContainerExecConnector`) creates a TTY exec instance
  (`ExecCreateContainerAsync`) and upgrades it to a hijacked bidirectional
  stream (`StartAndAttachContainerExecAsync`), exposed as the same duplex
  `IHostShellChannel` the host terminal uses. Resolved through the shared
  `IDockerClientFactory`, so exec works for **every** host type
  (`LocalSocket` / `TcpTls` / `Ssh`) — unlike the SSH-only host terminal, it
  routes through the daemon rather than an SSH login.
- ✓ **Transport reused from V5.3.** An authenticated
  `POST .../containers/{name}/exec/ticket` mints a short-lived, single-use
  ticket bound to `(user, connection, container, command)`; the socket opens
  at `.../containers/{name}/exec/ws?ticket=…&cols=&rows=` (ticket-authenticated,
  `AllowAnonymous`). The byte pump (`HostShellSession`) and WebSocket adapter
  (`WebSocketShellClientTransport`) are shared verbatim with the host terminal.
- ✓ **Per-session command.** Defaults to `/bin/sh`; the Exec panel exposes a
  command field so the user can pick `/bin/bash` etc. The command is bound to
  the ticket server-side, never on the query string.
- ✓ **Live resize works** (the daemon exposes `ResizeContainerExecTtyAsync`),
  so `IHostShellChannel.TryResize` is honoured rather than a no-op — the one
  capability the SSH host terminal lacks.
- ✓ **UX — the Exec tab is always present** on the container modal, and the
  shell button on each container card opens it. It renders the live `xterm.js`
  terminal when the global switch is on, the connection has opted in, and the
  container is running; otherwise it shows the matching disabled explainer.
- ✓ **Host terminal relocated to the connection level.** Since the host terminal
  is host-scoped (a shell on the host, not inside any one container), the V5.3
  per-container **Terminal** tab was removed and replaced by a **Host terminal**
  button in the connection header on the Docker page (shown for SSH connections).
  The host-shell API / gating / audit are unchanged — only the UI entry point
  moved. The card's old terminal button now opens **Exec**.
- ✓ **Security model — off by default, gated two ways** (both required): the
  server-wide master switch at **Settings → Container exec** (DB-backed —
  `ContainerExecSettingsEntity` / `GET|PUT /api/settings/container-exec` —
  seeded from the optional `Stashboard:AllowContainerExec` config flag on first
  run, surfaced via `FeaturesController`) and the per-connection `AllowExec`
  opt-in. *(There is no role system in Stashboard — every connection is owned by
  exactly one user — so the spec's "admin only" reduces to "the owner, with the
  switch + opt-in on".)* Every session writes a start/stop row to the new
  `DockerExecSessions` table (who, when, connection / container, command,
  duration, bytes in / out, end reason) and streams to the application log.
  Per-user / per-host concurrent caps + a server-side idle timeout
  (`ContainerExecSessionRegistry` + `Stashboard:ContainerExec` options) close
  idle / over-cap sessions regardless of client state.
- ✓ Pure-additive migration `AddContainerExec` (the per-connection `AllowExec`
  column, the `DockerExecSessions` audit table, the DB-backed master-switch
  row). A dedicated **Settings → Container exec** page hosts the toggle plus a
  full write-up of the conditions and risks. Tests: ticket service (single-use /
  expiry / container+command binding), session registry caps, the byte-pump end
  reasons (shared with V5.3), the settings service (seed / persist), the mapper
  opt-in (every host type), and the controller's two-way gate.

**DoD met:** an operator who turns on the **Settings → Container exec** toggle and
enables **Allow container exec** for any connection can open an interactive shell
inside a running container from the container modal's Exec tab; the session is
audited start-to-finish; the gate is enforced server-side; the WebSocket refuses
to open without a valid single-use ticket; and live resize works. ✅

---

### ✅ Phase V5.8 — Session audit viewer (surface the shell / exec audit trail)

**Complexity:** Low–Medium (read-only UI + list endpoints on top of audit tables
that already exist).
**Value:** V5.3 (host terminal) and V5.7 (container exec) each write a complete
start/stop audit row per session to `HostShellSessions` / `DockerExecSessions`
(who, when, which connection / host / container, command, duration, bytes in /
out, end reason, error) — but those tables were **write-only**: the rows were
persisted and streamed to the application log, yet there was no API endpoint
or UI to read them back. The most dangerous surfaces in the product (a root shell
on the host, arbitrary commands inside a workload) left a trail that an operator
could only inspect with direct SQL against `app.db`. This phase closes that gap by
surfacing the existing audit data in the frontend — no new auditing, just a read
path over what V5.3 / V5.7 already record.

**Shipped (5.8.0):**

- ✓ **Four read-only, owner-scoped list endpoints** (`DockerAuditController`,
  routed under `api/docker`) returning the audit rows newest-first, scoped to
  connections the caller owns — the stretch goal (fold in the update + prune
  trails) shipped too, so all four trails live behind one page:
  - `GET /api/docker/host-shell/sessions` → host-terminal sessions
    (`HostShellSessionEntity`, V5.3).
  - `GET /api/docker/container-exec/sessions` → container-exec sessions
    (`DockerExecSessionEntity`, V5.7).
  - `GET /api/docker/update-attempts` → per-container "Update now" history
    (`DockerUpdateAttempts`, V2.7).
  - `GET /api/docker/prune-runs` → scheduled / manual image-prune runs
    (`DockerPruneRuns`, V5.5).
  Each supports simple paging (`?skip=&take=`, page size capped at 200) and an
  optional `?connectionId=` filter. Responses are flat DTOs — denormalised
  connection name / host / container are already captured on the row, so deleting
  a connection doesn't orphan the history. No write/delete verbs (audit rows are
  immutable from the UI).
- ✓ **Audit page in the SPA.** A new **Settings → Audit** page with four tabbed
  tables — *Host terminal*, *Container exec*, *Update attempts*, *Image prune* —
  showing per row: user, connection / host (or container + command), started /
  ended, duration, bytes in / out, and end reason (with the error inline when the
  session ended on one). An **Active** badge for rows whose `EndedUtc` is still
  null. Sidebar entry + protected route, consistent with the other settings pages.
- ✓ **Cross-links from the existing surfaces.** The host-terminal dialog and the
  container modal's **Exec** panel each gained a "View session history" link that
  opens the Audit page pre-filtered to the connection they were opened from.
- ✓ **No new auditing, no schema change.** Purely a read path over the rows
  V5.3 / V5.7 / V2.7 / V5.5 already persist — no new tables, no migration.
- ✓ Tests: the list endpoints return rows newest-first, page correctly, are
  scoped to the owner (a foreign connection's sessions are not returned), and
  surface a still-open session (`EndedUtc == null`) as well as a finalised one;
  frontend coverage for the Active badge, duration / bytes / end-reason
  formatting, and the per-connection filter link.

**DoD met:** an operator can open the **Settings → Audit** page and see every
host-terminal and container-exec session that has been recorded — who ran it,
against what, for how long, how it ended — without touching the database directly;
the data is the same rows V5.3 / V5.7 already persist, now read back over an
owner-scoped API. ✅ The stretch *Update attempts* / *Image prune* tabs shipped in
the same release.

---

### ✅ Phase V5.9 — Docker instances page redesign (connection switcher layout)

**Complexity:** Low–Medium (frontend-only).
**Value:** The V3.5 `/docker` page packed one section per Docker host into a
single long scroll. Once a homelab has 3–4 hosts, the page becomes a long
nested list where each Compose project header claims the full page width and
small projects waste horizontal space, and the *page-level state of the
fleet* (how many containers across all hosts? how many updates pending?) is
nowhere to be seen until you scroll through every section. This phase
restructures the page around a **connection switcher** so picking a host is
an action up-front, not a scroll, and packs Compose project groups so
several small projects can share a row.

**Shipped (5.9.0):**

- ✓ **Page-level summary strip** — *Containers · Running · Stopped ·
  Updates* tiles aggregated across every Docker host so the overall fleet
  state is the first thing on the page. Driven by the same
  `/api/docker/connections/:id/instance/containers` +
  `/api/docker/connections/:id/watches` endpoints the per-host sections
  already use (one shared `useQueries` fan-out, so the cache is shared with
  every other consumer on the page).
- ✓ **Horizontal connection switcher** — one pill per host plus an
  *All connections* pill that keeps the cross-host view available. Each
  host pill shows a status dot, host name, the running-/-total container
  count, and an amber update badge when any tracked watch on that host has
  a pending update. Selecting a pill filters the rendered sections; the
  active pill gets a primary-tinted border.
- ✓ **Compact per-host summary card** — status dot, host name, mono
  transport + endpoint line, container count, and right-aligned Terminal
  (SSH-only) / Edit buttons. The V5.5 **Storage** widget is folded inline
  as a collapsible row: chevron + label + inline mono summary
  (`N images · M dangling · K unused`) + Refresh / Prune, expanding to the
  same 4-metric grid the panel layout used.
- ✓ **Packed Compose project groups** — each group is exactly as wide as
  the cards it contains; groups flow with `flex-wrap` so several small
  projects sit on one row when there's room. Card width and column count
  are derived from the viewport (`base = 300px comfortable / 258px
  compact`; `gap = 14px / 10px`), recomputed on resize. Standalone
  containers and single-service Compose projects (where a group header
  would be a meaningless 1-of-1) collect into a trailing **Other
  containers** group.
- ✓ **Container card refinements** — the trailing `(healthy)` segment of
  the status line is colored green (and `(unhealthy)` red, `(starting)`
  amber); a 1px divider with `margin-top: auto` pushes the action row to
  the bottom so action rows of all cards in a row line up; the *service:*
  compose chip gains a small external-link icon. Diagnostic icon row
  (Inspect / Logs / Stats / Notifications / Exec) and the lifecycle row
  (Stop / Restart, or Start / Remove) keep their existing handlers — same
  per-watch audit trail.
- ✓ **Display preferences** — page-level toggles for density (comfortable /
  compact), storage style (collapsed / panel), and diagnostics on/off.
  Persisted per device in `localStorage` (key
  `stashboard.dockerInstances.prefs.v1`). Defaults: cards · comfortable ·
  collapsed · diagnostics on.
- ✓ **Responsive** — column count adapts via the formula above; ≤520px the
  grid collapses to a single full-width column and the toolbar stacks
  (full-width search + equal-width segmented buttons); the page works
  cleanly down to 320px.
- ✓ **Frontend-only, no backend changes.** Same V3.5+ API surface; the
  container card still calls the existing
  `useDockerContainerAction` / `useDockerProjectUpdate` /
  `useDeleteConnectionWatch` mutations, so audit history is unchanged.

**Known mapping gaps flagged for follow-up:**

- The *host online* dot is derived best-effort from whether the per-host
  container list query succeeded — there's no first-class host
  reachability signal in `DockerConnection`. A future phase could surface
  a dedicated probe (e.g. extend the V5.3 storage refresh path).
- The handoff describes the *service:* chip as "open the linked Stashboard
  service". The chip is rendered as a styled link, but it currently only
  carries the existing `onComposeClick` filter behavior (no-op on the new
  page where the group header already names the project). Routing into the
  Service modal would require resolving `card.webResourceId` → service
  URL; left for a follow-up that touches the dashboard routing layer too.
- Display preferences are device-local (`localStorage`) rather than
  folded into the server-side `DashboardPreferences` blob — the existing
  blob has a fixed `{ sortMode, groupByCategory }` shape and extending it
  to carry the docker-page prefs would have added a migration unrelated to
  the redesign.

**DoD met:** the redesigned page renders the same data the V3.5 page did,
but starts with a fleet-level summary and a connection switcher; Compose
project groups pack tightly so several small projects share a row; the
container card matches the design's status / divider / action-row
treatment; display preferences persist; the layout works from 320px up to
the 1320px page max in both light and dark themes. ✅

---

### Phase V6.0 — Proxmox LXC update monitoring

**Complexity:** Medium–High
**Value:** Stashboard already tracks Docker image updates; the natural next
target is the layer below — the LXC containers those Docker hosts (and other
services) run inside. Proxmox is the most common homelab hypervisor and
exposes a stable REST API.

**Feasibility notes:**

- Proxmox VE has a REST API at `https://<host>:8006/api2/json/` authenticated
  via API tokens (`PVEAPIToken=USER@REALM!TOKENID=SECRET`). No password
  scraping required.
- Endpoint `GET /nodes/{node}/lxc` lists LXC containers; `GET /nodes/{node}/
  lxc/{vmid}/status/current` returns runtime state.
- Update detection — Proxmox does not expose "apt has N upgradable packages"
  directly. Two viable paths:
  1. Use `POST /nodes/{node}/lxc/{vmid}/status/exec` (or `pct exec` via SSH)
     to run `apt list --upgradable 2>/dev/null | wc -l` inside each
     container. Cheap, Debian-only, requires the LXC to be running.
  2. Read `/var/lib/apt/periodic/update-success-stamp` and the
     `update-notifier-common` "available updates" file via the same exec —
     same caveats.
- For the Proxmox **host** itself, `GET /nodes/{node}/apt/update` returns the
  list of pending package updates and is purpose-built for this.
- Schedule: same cadence model as Docker watches (Hourly / Daily / Weekly),
  reusing `IDockerUpdateChecker`'s background loop pattern.
- Surfacing: a new "Proxmox" section on the dashboard with one card per LXC
  (host + container) showing pending-update count and last-checked timestamp.
  Notifications reuse the existing email + Telegram channels.

**Out of scope for the first cut:**

- Triggering the actual `apt upgrade` inside the LXC from Stashboard — this
  is V6.1-adjacent and should land separately, after the shell story exists.
- Non-Debian LXC templates (Alpine `apk`, Rocky `dnf`) — add as follow-ups
  once the Debian path is stable.

---

### Phase V6.1 — Browser-based SSH client for Proxmox LXC

**Complexity:** High
**Value:** Closes the loop on V6.0: once the user sees "LXC `pihole` has 7
package updates pending", they can `apt upgrade` it without leaving the
browser. Also useful for any non-Docker host Stashboard is asked to monitor.

**Feasibility notes:**

- A managed SSH client (`SSH.NET` / Renci) opens a session to the Proxmox
  host. To reach a specific LXC, the channel runs `pct enter <vmid>` (or
  `pct exec <vmid> -- /bin/bash`) on the host. No direct SSH into the LXC
  required, no per-container key management.
- Transport: the same xterm.js + WebSocket + ticket bridge the earlier shell
  phases introduce (V5.3 host terminal, V5.7 container exec); the browser side
  reuses the same `xterm.js` component, just pointed at a different endpoint.
- Credential storage: SSH keys live encrypted in the database (ASP.NET Core
  Data Protection — the same approach the existing `DockerConnection` SSH
  path uses in V2.5). Passwords are not supported.
- Host-key verification: `known_hosts` is mandatory; TOFU prompt on first
  connect, stored against the connection record.

**Security model:**

- All the V5.3 / V5.7 guardrails apply (off by default, admin only, audited,
  per-user / per-host caps, idle timeout).
- An extra setting `AllowRootShell = false` blocks `pct enter` when the
  default user inside the LXC is root, forcing the user to specify an
  explicit non-root account first.
- Optional read-only mode: the hub can be configured to drop any keystroke
  outside an allowlist (useful for demo / shared-screen scenarios) — opt-in
  only.

---

### Phase V7.0 — Visual Compose viewer (foundation, read-only)

**Complexity:** Medium
**Value:** Establishes the visual surface for the V7 editor without risking
an existing project. The user opens a Compose project Stashboard already
knows about (V5.2 bind-mounted Compose projects) and sees it rendered as a
card-per-service grid: image, ports, mounts, env summary, restart policy,
resource limits. No write path yet — pure parse + render so the YAML model
and UI layout can be validated against real-world projects before any edit
risk is taken.

**Proposed approach:**

- New view `/projects/{id}/compose` listing services from a parsed
  `docker-compose.yml`. Backend parses with `YamlDotNet`; exposes a typed
  `ComposeProjectResponse` mirroring the subset of the Compose v3.x spec
  Stashboard cares about (services, top-level networks / volumes / secrets /
  configs, `deploy.resources`).
- Front-end renders one collapsible card per service with the same status
  pill family the dashboard already uses; **Edit** buttons are disabled
  (read-only first pass).
- Hard fail-safe: if the file contains unsupported Compose extensions
  (`x-*`, `extends`, anchors that aren't simple aliases), surface a
  "Read-only — file uses extension X" banner rather than silently dropping
  data. Users can still view, just not edit, until V7.1 grows the
  round-trip support.
- No `docker compose` invocation, no write path, no changes to the entity
  model — V7.0 is purely additive on the read side.

---

### Phase V7.1 — Edit basic service fields

**Complexity:** Medium
**Value:** Covers the 80 % of Compose edits users reach for daily — image
tag, ports, env, volumes, labels, restart policy, command/entrypoint —
without leaving the browser and **without losing comments or key order**
in the YAML file. The round-trip fidelity is the make-or-break
correctness bar of the whole V7 line: if a single edit reorders unrelated
keys, the feature is useless on real projects under version control.

**Proposed approach:**

- POC up front to pick the YAML library: `YamlDotNet` with explicit
  `YamlStream` traversal, or switch to a CST library (`SharpYaml`) — the
  one that round-trips a representative sample of real compose files with
  zero diff outside the edited keys wins. Decision documented in an ADR
  before V7.1 implementation starts.
- Per-field forms with inline validation:
  - **Image**: dropdown of tags from the existing
    `IRegistryClient.ListTagsAsync` (V2.1) for the current image, plus
    free-text override.
  - **Ports**: row editor with host port / container port / protocol,
    collision-checked against the rest of the project on every keystroke.
  - **Volumes**: dropdown of existing named volumes + free-form bind
    mounts; warns on host paths outside the project root.
  - **Env vars**: key/value table with a secret-style mask toggle for
    keys matching `*_KEY`, `*_TOKEN`, `*_PASSWORD`, `*_SECRET`.
  - **Labels**, **restart**, **command**, **entrypoint**, **user**,
    **working_dir**.
- Atomic save: backend writes to `docker-compose.yml.next`, validates by
  running `docker compose -f … config -q` in a subprocess, then renames
  atomically over the original. Rollback on validation failure with the
  raw stderr surfaced in the UI.

---

### Phase V7.2 — Resource constraints UI (Proxmox-style)

**Complexity:** Medium
**Value:** The headline use case the user asked for. Today they either
hand-edit YAML or delegate to Portainer — both lose the per-host context
Stashboard already has. Stashboard knows the target host's total CPU /
memory (from `docker info`, polled since V3) and which other containers
already reserve capacity, so it can surface "you're allocating 14 of 16
cores" warnings inline instead of letting the user discover the
over-commit at `docker compose up -d` time.

**Proposed approach:**

- Editor for `deploy.resources.limits` / `deploy.resources.reservations`
  (preferred, portable to Compose v3.8+ and Swarm-shaped tooling) with
  fallback to top-level `cpus` / `mem_limit` / `mem_reservation` for
  older files. Detection is per-file: never mix conventions in the same
  document.
- Numeric inputs + sliders bounded by the target host's actual capacity,
  not arbitrary maxima.
- Companion read-only panel: *"Host capacity: 16 CPUs, 32 GiB · already
  allocated by other containers: 12 CPUs (75 %), 18 GiB (56 %) · this
  service draft: 2 CPUs, 4 GiB"*. Live values pull from the
  `docker stats` stream the V3.5 instances page already established.
- Fields covered: `cpus`, `cpu_shares`, `mem_limit`, `mem_reservation`,
  `pids_limit`, `ulimits`, `oom_kill_disable`, `oom_score_adj`, `shm_size`.

---

### Phase V7.3 — Top-level resources (networks, volumes, secrets, configs)

**Complexity:** Medium
**Value:** Editing services in isolation is half the picture; networks,
named volumes, secrets, and configs are what stitch them together.
Without this phase the V7 editor stays a per-service form filler instead
of a real project editor.

**Proposed approach:**

- Side-tab on the project view: **Networks · Volumes · Secrets · Configs**.
- Each tab is a CRUD table backed by the same comment-preserving YAML
  writer from V7.1.
- **Network** editor: driver (`bridge` / `overlay` / `macvlan`), subnet,
  gateway, driver opts; warns on subnet overlap with networks already
  defined on the host.
- **Volume** editor: driver, driver opts, name override; surfaces the
  actual on-disk size from `docker volume inspect` for context (so the
  user sees that `postgres_data` is 4.2 GiB before they consider
  deleting it).
- **Secret** / **config** editor: external vs. file; for file-based, an
  encrypted-at-rest store inside Stashboard (reuses `IEncryptionService`).

---

### Phase V7.4 — Create a new service from scratch

**Complexity:** High
**Value:** This is the second half of what the user explicitly asked for:
the editor stops being a YAML-renderer and becomes a project
bootstrapper. A user clicks **Add service**, picks an image, fills the
form, and Stashboard appends a valid block to the project's
`docker-compose.yml`. Combined with V7.2, this is the "Proxmox-like
container creator" the user envisioned.

**Proposed approach:**

- **Add service** button on the project view opens a wizard:
  1. Image picker with autocomplete against the registries the project
     already references (reuses `IRegistryClient` from V2).
  2. Required fields: service name (validated for uniqueness + Compose
     key shape `^[a-zA-Z0-9._-]+$`), image tag, restart policy.
  3. Optional collapsible sections: ports, volumes, env, depends_on,
     resources (V7.2 picker), healthcheck, labels.
  4. Review pane shows the YAML block that will be appended.
- Appends to the existing file at the end of the `services:` map;
  honours the user's indentation style by detecting it from the current
  file (2-space vs. 4-space, list-dash position).
- Post-save offers a **Start service** CTA that runs
  `docker compose up -d <name>` via the V5.2 compose-aware path. If V5.2
  isn't configured for the connection, falls back to the raw
  `docker run` recreate the existing "Update now" path uses.

---

### Phase V7.5 — Service templates / starter recipes

**Complexity:** Low–Medium
**Value:** Most users adding a service want one of ~20 well-known images
(Postgres, Redis, Nginx, MariaDB, Traefik, Caddy, Mosquitto, Grafana,
Prometheus, Pi-hole, AdGuard Home, Vaultwarden, Jellyfin, …). A curated
catalogue turns a 5-minute form-filling exercise into a one-click action
and removes the "what's the right env var for the postgres password
again?" friction.

**Proposed approach:**

- New `templates/` folder shipped with the image, one `*.yml` per
  template plus a sibling `meta.json` per template (name, icon,
  description, recommended resources, required env vars with hints,
  required volumes).
- **Add from template** tab in the V7.4 wizard. Picking a template
  pre-fills the wizard; the user only fills in the per-deployment bits
  (volume host path, env values, exposed ports).
- Optional follow-up: pull community templates from a signed Git source
  (opt-in, validated against a JSON schema). Off by default for the
  initial cut to avoid the supply-chain question.

---

### Phase V7.6 — Diff, dry-run, apply

**Complexity:** Medium
**Value:** "Save" on YAML is scary if you can't see what changes. A
pre-save diff + `docker compose config` validation, with an explicit
**Apply now** button to drive the V5.2 compose-aware recreate, makes the
editor safe to use on production projects. This is the single phase that
separates a toy editor from one a homelabber will trust on their
always-on host.

**Proposed approach:**

- **Save** → backend computes a textual diff between the on-disk file
  and the proposed file, runs `docker compose -f … config -q` for
  validation, and returns both to the UI.
- UI shows a side-by-side diff; the user confirms before any write.
- On confirm: write file atomically (V7.1 path), then offer **Apply
  now** which fires V5.2's compose-aware recreate for **only the
  changed services** (compute the set by diffing service keys + their
  serialised YAML).
- Previous file revisions are kept in `<project>/.stashboard/history/`
  (last N, default 20) with a **Restore** button. Pairs with V4's
  SQLite to also store a metadata-only audit row per change
  (who / when / which services touched).

---

### Phase V7.7 — Dependency graph + linter

**Complexity:** Medium–High
**Value:** Once a project has a dozen services, the
`depends_on` / network / volume relationships become hard to reason
about. A small DAG view plus an inline linter catches the issues that
"works on my machine" hides — and is the kind of thing Stashboard can
do that a raw text editor structurally cannot.

**Proposed approach:**

- `react-flow`-based DAG view: nodes = services, edges = `depends_on`,
  shared networks rendered as group boxes, shared volumes shown as a
  side legend.
- Lint rules (run on every load + every save):
  - Port collisions across services on the same host.
  - `depends_on` cycles.
  - Missing healthcheck on a service other services depend on with
    `condition: service_healthy`.
  - Bind mounts pointing outside the project root.
  - Deprecated Compose keys (`links`, `volumes_from`, top-level
    `version:`).
  - Image tags pinned to `latest` (warning, not error — many homelab
    users intentionally pin to `latest` + use V2 to monitor digests).
- Findings render inline on the service card and aggregate into a
  project-level **Health** badge next to the project name.

---

### V7 effort estimate (rough)

| Phase | Effort |
|---|---|
| V7.0 — Compose viewer (foundation, read-only) | 2 days |
| V7.1 — Edit basic service fields | 3 days |
| V7.2 — Resource constraints UI (Proxmox-style) | 2 days |
| V7.3 — Top-level resources (networks / volumes / secrets / configs) | 2 days |
| V7.4 — Create a new service from scratch | 3 days |
| V7.5 — Service templates / starter recipes | 1.5 days |
| V7.6 — Diff, dry-run, apply | 2 days |
| V7.7 — Dependency graph + linter | 2.5 days |
| **Total** | **~18 days** |

> **Scope note.** V7.0 → V7.4 cover the user's original ask (visual editor for
> existing compose files + create new containers via UI). V7.5 / V7.6 / V7.7
> are additions: they don't extend the surface much but they're
> the difference between "a YAML form" and "a safe project editor".

