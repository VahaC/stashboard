# Stashboard — Product Roadmap

> This document is the forward-looking product roadmap — unshipped phases
> only. The historical detail (Docker-update-checker V1–V3, V4 SQLite
> migration, the original V1 implementation checklist) lives in
> [`HISTORY.md`](./HISTORY.md).
>
> **Numbering note:** the legacy section numbers §1–§13 and §15 live in
> HISTORY.md. §14 keeps its heading number here for external references, but its
> shipped V5.x phase detail (V5.0–V5.9) was archived to [`HISTORY.md`](./HISTORY.md) §14
> once those phases all shipped; only V6.0+ remains in this file.
>
> **Status (shipped milestones, V5+):** ✅ V5.0 (disabled card style + one-click removal) · ✅ V5.0.1 (unlink container from service) · ✅ V5.0.2 (editable SMTP / email settings) · ✅ V5.0.3 (dedicated notifications settings page) · ✅ V5.1 (secure key auto-provisioning, image 5.1.0) · ✅ V5.2 (true Compose-aware recreate, image 5.2.0) · ✅ V5.3 (host terminal, image v5.3.0) · ✅ V5.3.1 (tag-pattern filter correctness + version tags, image 5.3.1) · ✅ V5.3.2 (reliable offline alerts, image 5.3.2) · ✅ V5.4 (Compose project grouping & bulk update, image 5.4.0) · ✅ V5.5 (image cleanup / prune, image 5.5.0) · ✅ V5.6 (health-check tuning page, image 5.6.0) · ✅ V5.7 (container exec, image 5.7.0) · ✅ V5.8 (session audit viewer, image 5.8.0) · ✅ V5.9 (Docker instances page redesign, image 5.9.0) · ✅ V6.0 (Proxmox LXC update monitoring, image 6.0.0) · ✅ V6.1 (Proxmox LXC detail modal + Docker-style cards, image 6.1.0) · ✅ V6.2 (LXC Config tab, image 6.2.0) · ✅ V6.3 (LXC Stats + Tasks tabs, image 6.3.0) · ✅ V6.4 (LXC lifecycle actions + real-time stats, image 6.4.0) · ✅ V6.5 (edit LXC parameters, image 6.5.0) · ✅ V6.6 (browser LXC console / Console tab, image 6.6.0) · ✅ V6.7 (per-LXC update monitoring toggle, image 6.7.0) · ✅ V6.7.1 (Proxmox one-click "Update now", image 6.7.1) · ✅ V6.8 (PVE node health card + node modal, image 6.8.0) · ✅ V6.8.1 (PVE node alerting, image 6.8.1) · ✅ V6.8.2 (PVE node deep telemetry / SSH collectors, image 6.8.2) · ✅ V6.9.0 (edit LXC network interfaces & mount points, image 6.9.0) · ✅ V6.10 (Proxmox page Docker-parity redesign, image 6.10.0) · ✅ V6.11 (bulk LXC monitoring & update operations + audit, image 6.11.0) · ✅ V6.12 (LXC live logs / Logs tab, image 6.12.0) · ✅ V6.13 (destroy / remove LXC, image 6.13.0) · ✅ V6.13.1 (create LXC, image 6.13.1) · ✅ V6.14 (VM / QEMU support, image 6.14.0). Shipped V5.x phase detail now lives in [`HISTORY.md`](./HISTORY.md) §14; V1–V4 historical detail is also in [`HISTORY.md`](./HISTORY.md). End-user documentation: [`DOCKER_UPDATE_MONITORING_GUIDE.md`](./DOCKER_UPDATE_MONITORING_GUIDE.md).

## 14. Post-V4 backlog (V5+) — deferred Docker features

> **Archived:** the V5.x Docker feature phases (V5.0 – V5.9, all shipped) and
> their sequencing rationale moved to [`HISTORY.md`](./HISTORY.md) §14. What
> remains in this file is V6.0 — the first Proxmox phase — followed by the active
> Proxmox parity track in §15.

---

### ✅ Phase V6.0 — Proxmox LXC update monitoring

**Complexity:** Medium–High
**Value:** Stashboard already tracks Docker image updates; the natural next
target is the layer below — the LXC containers those Docker hosts (and other
services) run inside. Proxmox is the most common homelab hypervisor and
exposes a stable REST API.

**Shipped (6.0.0):**

- New top-level **Proxmox** page (`/proxmox`) with per-host blocks and one
  auto-discovered card per node + LXC (pending-update count · running state ·
  last-checked). Hosts modelled as a new `ProxmoxConnection` entity
  (user-scoped, `ProxmoxConnections` + `ProxmoxGuests` tables).
- **Hybrid transport** — the design fork resolved during implementation:
  - REST API (`PVEAPIToken`) → `GET /nodes/{node}/lxc` for discovery and
    `GET /nodes/{node}/apt/update` for the **node's** own update count.
  - SSH → `pct exec <vmid> -- apt list --upgradable` for the **per-LXC**
    count. **Correction to the feasibility note below:** Proxmox VE exposes
    **no** command-exec REST endpoint for LXC (`status/exec` is QEMU-guest-agent
    only), so the API-only path cannot read per-container counts — SSH is
    required for the headline feature.
- Reuses the V2.2 `CheckScheduleEvaluator` (Hourly / Daily / Weekly) via a
  dedicated `ProxmoxUpdateBackgroundService`, and the existing email +
  Telegram channels (throttled by a signature of the pending state).
- Per-host **Test connection** (probes API + SSH independently) and
  **Check now** (immediate scan). Self-signed certs handled via a per-host
  **Skip TLS verification** toggle.
- Out of scope, as planned: triggering `apt upgrade` (V6.6-adjacent) and
  non-Debian templates.

**Original feasibility notes:**

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
  is V6.6-adjacent and should land separately, after the shell story exists.
- Non-Debian LXC templates (Alpine `apk`, Rocky `dnf`) — add as follow-ups
  once the Debian path is stable.

---

## 15. Proxmox page — Docker parity & LXC management (V6.1+)

> A track that brings the **Proxmox page** (`/proxmox`, LXC containers) up to the
> look and capability of the mature **Docker instances** page: Docker-style LXC
> cards, a click-to-open **LXC detail modal** with tabs analogous to the Docker
> container modal, and the ability to **manage and edit LXC containers**. This is
> Proxmox/LXC work — it does not touch the Docker container card or page.
>
> Today's Proxmox backend (`IProxmoxApiClient`) is read-only: it can list LXCs,
> read the node's pending-update count, and look up an LXC's IP. Everything from
> V6.2 on needs new calls against the Proxmox VE REST API (auth: `PVEAPIToken` —
> tokens can POST/PUT without the cookie/CSRF dance). The **Console** tab (V6.6)
> is the browser-SSH client previously catalogued as "V6.1 Proxmox-LXC SSH",
> renumbered to land last in this block as a tab of the same modal.

### ✅ Phase V6.1 — Docker-style LXC cards + detail modal (Overview) (image 6.1.0)

**Complexity:** Low
**Value:** Immediate visual parity and the modal scaffold the rest of the track
hangs off — with zero backend risk.

**Scope (delivered):**

- LXC cards restyled to mirror the Docker container card (`proxmox-cc` classes,
  values copied from `.docker-instances-card` so there's no cross-page CSS
  coupling): name + runtime state badge, amber **Update** badge, monospace
  `CT <vmid>` line, `Up <uptime>` / `Stopped` status line, and the Proxmox
  **resources / IP / uptime** as chips. The Docker card and the Proxmox **node**
  card are untouched.
- Click (or Enter/Space) on an LXC card opens `LxcModal`, shaped like the Docker
  container modal (header + tab nav + body). The **Overview** tab is functional,
  built from data already on the page; **Config / Stats / Tasks / Console** are
  scaffolded (disabled, "coming in a later version") to show the target shape.

---

### ✅ Phase V6.2 — Config tab (read) (image 6.2.0)

**Complexity:** Medium
**Value:** The "Inspect" analogue — see an LXC's real configuration without SSH.

**Scope:** new instance-scoped read endpoint backed by
`GET /nodes/{node}/lxc/{vmid}/config` + `/status/current`; the Config tab renders
cores, memory, swap, hostname, OS template, rootfs, mount points (`mp[n]`),
network interfaces (`net[n]`), features, and `onboot` as a structured snapshot.

---

### ✅ Phase V6.3 — Stats + Tasks tabs (image 6.3.0)

**Complexity:** Medium
**Value:** Live resource view + recent activity, matching the Docker modal's
Stats/Logs tabs.

**Scope:** **Stats** from `GET /nodes/{node}/lxc/{vmid}/rrddata` (CPU / memory /
net / disk sparklines); **Tasks** from the node task log filtered to the guest,
with a task-log viewer.

### ✅ Phase V6.4 — Lifecycle actions + real-time stats (image 6.4.0)

**Complexity:** Medium
**Value:** Start / stop / shutdown / reboot an LXC from the card and modal, the
way the Docker card exposes start/stop/restart. Also upgraded the Stats tab to a
real-time **Live** view (2 s polling of `status/current`, since Proxmox has no
LXC stats stream) alongside the V6.3 RRD **History** view.

**Scope:** `POST /nodes/{node}/lxc/{vmid}/status/{start|stop|shutdown|reboot}`
behind a confirm for destructive transitions; actions recorded in the audit
trail; optimistic state refresh. Requires the API token to hold `VM.PowerMgmt`.

---

### ✅ Phase V6.5 — Edit LXC parameters (image 6.5.0)

**Complexity:** High
**Value:** The headline ask — change an LXC's parameters from the UI.

**Scope (delivered):** the **Config** tab's scalar fields are now editable —
**cores**, **memory** (MiB), **swap** (MiB), **hostname**, and **onboot** (start
at boot). An **Edit** button swaps the read-only snapshot for a form; **Review
changes** shows a per-field confirm that classifies each change as *applies live*
(cores / memory / swap), *needs restart* (hostname), or *next boot* (onboot)
before anything is written. Saving calls a new owner-scoped
`PUT /api/proxmox/connections/{id}/lxc/{vmid}/config` endpoint, which writes
through to the Proxmox `PUT /nodes/{node}/lxc/{vmid}/config` API (only the
changed fields are sent; Proxmox merges them). Requires the token to hold
`VM.Config.*`; a permission / validation rejection is surfaced verbatim.

---

### ✅ Phase V6.6 — Browser-based SSH client for Proxmox LXC (Console tab) (image 6.6.0)

> Renumbered from the old V6.1. Lands as the **Console** tab of the LXC modal,
> after the read/lifecycle/edit phases above and before the V7 Compose editor.

**Shipped (6.6.0):**

- ✓ **Console tab on the LXC modal** (and the console button on each LXC card):
  an interactive `xterm.js` shell *inside* an LXC. The transport is the V5.3 SSH
  PTY (`IHostShellConnector` / `SshHostShellConnector`) opened against the
  Proxmox host's existing SSH credentials, with an initial
  `exec pct exec <vmid> -- <shell>` so the login shell is replaced by a shell in
  the guest — no per-container key management, and the SSH channel closes when
  the inner shell exits. The decision (confirmed with the user) was `pct exec`
  with a per-session command field (default `/bin/bash`, editable to `/bin/sh`
  etc.), mirroring the V5.7 container-exec UX.
- ✓ **Reuses the shared transport verbatim.** A browser `WebSocket` can't carry
  the JWT header, so `POST …/lxc/{vmid}/console/ticket` mints a single-use,
  short-lived ticket bound to `(user, host, vmid, command)` and the socket opens
  at `…/lxc/{vmid}/console/ws?ticket=…&cols=&rows=` (`AllowAnonymous`,
  ticket-authenticated). The byte pump (`HostShellSession`), the WebSocket
  adapter (`WebSocketShellClientTransport`) and the frontend xterm.js panel are
  the same components the host terminal / exec use — the panel is a near-clone of
  `ContainerExecPanel`, reusing the same `host-terminal-*` / `container-exec-*`
  CSS so the surface is identical to Docker, not a parallel one.
- ✓ **Security model — base Docker parity (confirmed with the user), off by
  default, gated three ways** (all required): the server-wide master switch at
  **Settings → LXC console** (DB-backed `ProxmoxConsoleSettingsEntity` /
  `GET|PUT /api/settings/proxmox-console`, surfaced via `FeaturesController`,
  seeded from the optional `Stashboard:AllowProxmoxConsole` flag on first run),
  the per-host `AllowConsole` opt-in, and SSH credentials configured on the host.
  Every session writes a start/stop row to the new `ProxmoxConsoleSessions` table
  (who, when, host / node / guest, command, duration, bytes in / out, end reason)
  and streams to the application log; per-user / per-host concurrency caps + a
  server-side idle timeout (`Stashboard:ProxmoxConsole` options) close idle /
  over-cap sessions regardless of client state. The `AllowRootShell` and
  read-only-mode extras from the original spec were deferred to keep parity with
  the V5.3 / V5.7 model.
- ✓ **Audit viewer parity.** The **Settings → Audit** page gained a fifth tab,
  **LXC console**, backed by a new owner-scoped `GET /api/proxmox/console/sessions`
  read endpoint; the Console panel links there pre-filtered to the host.
- ✓ Pure-additive migration `AddProxmoxConsole` (the per-host `AllowConsole`
  column + the `ProxmoxConsoleSessions` audit table + the DB-backed master-switch
  row). Tests: ticket service (single-use / expiry / vmid+command binding),
  session-registry caps, the settings service (seed / persist), the mapper
  `AllowConsole` round-trip, and the controller's two-way gate + command binding.
  Full backend suite green (1050).

**Caveat (inherited from V5.3):** SSH.NET exposes no live `window-change` on
`ShellStream`, so the PTY is sized at connect and live auto-resize is
unavailable — the console works regardless.

**DoD met:** an operator who turns on **Settings → LXC console** and enables
**Allow LXC console** on an SSH-configured Proxmox host can open an interactive
shell inside a running LXC from the modal's Console tab; the session is audited
start-to-finish and visible on the Audit page; the gates are enforced
server-side; and the WebSocket refuses to open without a valid single-use
ticket. ✅

<details>
<summary>Original plan (pre-implementation)</summary>

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

</details>

---

### ✅ Phase V6.7 — Add Proxmox LXC updates monitoring the same way as for Docker (image 6.7.0)

**Shipped (6.7.0):**

- ✓ **Per-LXC `MonitoringEnabled` flag** on `ProxmoxGuestEntity` (default `true`,
  backfilled to `true` for existing rows via an additive migration). A
  **Monitoring enabled** checkbox in the LXC modal's **Watch** tab toggles it —
  the same checkbox + helper-text pattern as a Docker watch's `Enabled`.
- ✓ **Disabled guests are skipped inside the existing scan pipeline**, not a new
  worker. `ProxmoxScanService` reads the disabled vmids up front and hands them
  to `IProxmoxUpdateChecker.CheckAsync(profile, disabledVmIds, …)`, which lists
  the LXC (resource details stay fresh) but skips the IP lookup + `pct exec`
  count for it. The node row (`vmId 0`) is never disabled.
- ✓ **Notifications exclude disabled guests** — `ProxmoxUpdateNotificationService`
  filters on `MonitoringEnabled && PendingUpdates > 0`, so disabling a guest
  stops repeat alerts immediately; the toggle also clears its stale count.
- ✓ **Discovery reconciliation** preserves the user's choice on rediscovery
  (matched on connection + vmid); the upsert never overwrites the flag, and new
  guests default to enabled.
- ✓ **Docker-style API**: `PUT …/lxc/{vmId}/monitoring` (toggle) and
  `POST …/lxc/{vmId}/check` (per-guest **Check now**) on
  `ProxmoxConnectionsController`. A disabled guest's Check now returns a
  deterministic disabled outcome without scanning; an enabled guest re-scans the
  whole node (Proxmox has no per-container probe — the UI says so explicitly).
- ✓ **Docker-parity UI**: muted (dashed/dimmed) card with a **Disabled** badge
  and no amber "updates pending" emphasis while off, last-checked still visible;
  optimistic toggle with rollback on error (`useSetProxmoxLxcMonitoring`).
- ✓ **Tests**: checker skips the disabled guest; scan passes the disabled set +
  preserves the toggle + excludes it from notifications; new guests default
  enabled; controller toggle persists / clears pending / rejects the node row /
  guards ownership; per-guest Check now scans only when enabled.

<details>
<summary>Original plan (pre-implementation)</summary>

**Complexity:** Medium
**Value:** Proxmox LXC update checks already exist but are host-level; operators need the
same per-container control they already have for Docker watches so noisy or
intentionally unmanaged guests can be excluded without disabling the whole host.
Delivering Docker-parity UX lowers cognitive load, avoids duplicate concepts,
and makes mixed Docker + Proxmox environments consistent.

**Scope:**

- **Per-LXC enable/disable toggle (Docker-watch parity).**
  - Add a persistent `Enabled` flag for each tracked LXC guest (default `true`
    for newly discovered guests to preserve current behaviour).
  - Disabled guests are skipped by scheduled and manual update-count checks,
    exactly like disabled Docker watches are skipped.
  - Node-level row (`vmId = 0`) remains host-controlled and is not treated as an
    LXC watch toggle target.
- **No new monitoring engine. Reuse existing scheduling + scan flow.**
  - Keep `ProxmoxUpdateBackgroundService` and `CheckScheduleEvaluator` as the
    single scheduler path.
  - Integrate the per-guest `Enabled` gate inside the existing Proxmox scan
    pipeline (the same place that currently computes per-guest pending updates),
    rather than introducing a second checker/background worker.
  - Reuse current notification signature/throttle logic; disabled guests are
    excluded from signature calculation so disabling stops repeat alerts for that
    guest immediately.
- **Docker-style API semantics.**
  - Add guest-level endpoints matching Docker-watch intent/shape (`PATCH`/`PUT`
    update settings + `POST check` style action) under the existing Proxmox
    route group.
  - Manual **Check now** on a disabled guest returns a deterministic disabled
    status (same UX principle as Docker), not an opaque error.
- **UI/UX must mirror Docker watch UX as closely as possible.**
  - Reuse existing switch/toggle component variants, disabled-state badge style,
    helper text pattern, and action placement from Docker watch forms/cards.
  - In Proxmox LXC card + modal overview/config surfaces, show a clearly visible
    **Monitoring enabled** control with identical wording and interaction model
    to Docker where possible.
  - Disabled LXC visual treatment mirrors Docker disabled semantics: muted card,
    explicit disabled status, last checked timestamp still visible, and no amber
    "updates pending" emphasis while disabled.
- **Data model & migration (additive only).**
  - Extend the existing persisted Proxmox guest model with a nullable/boolean
    monitoring flag (backfilled to enabled for existing rows).
  - Keep migration additive and backward-compatible; do not change current host
    credentials, discovery contract, or business rules.
- **Discovery reconciliation rules.**
  - Auto-discovery must preserve user-chosen enabled/disabled state when an LXC
    is rediscovered (match by `(connectionId, vmId)`).
  - Newly discovered LXCs default to enabled.
  - Removed LXCs are deleted as today; if they reappear later they are treated
    as new rows (enabled by default unless future history retention is added).
- **Tests (parity-focused).**
  - Backend: disabled guest skipped on scan; enabled guest still checked; manual
    check on disabled guest returns disabled status; discovery preserves toggle;
    notifications exclude disabled guests.
  - Frontend: toggle renders and persists; disabled card styling parity with
    Docker semantics; disabled guest does not show update-warning emphasis;
    optimistic update/error rollback behaviour matches Docker interactions.

**Implementation constraints:**

- **Must reuse existing code paths/components** (scheduler, scan service,
  notification throttling, shared toggle UI primitives, existing query cache
  invalidation strategy). Avoid creating parallel abstractions when an existing
  Docker or Proxmox pattern already fits.
- **No business-logic drift.** This phase adds per-guest enable/disable control
  only; it does not change how update counts are computed for enabled guests.

**Additional ideas (optional follow-ups, not required for DoD):**

- Bulk actions per host: **Enable all / Disable all LXCs** with confirmation.
- Filter chips on Proxmox page: **All / Enabled / Disabled / Updates available**.
- "Temporarily disable until date" (snooze) for maintenance windows.
- Audit row for toggle changes (`who`, `when`, `guest`, `new state`) to match
  operational traceability patterns already present in Docker-related actions.

</details>

---

### ✅ Phase V6.7.1 — Proxmox one-click "Update now" (image 6.7.1)

> The follow-up V6.0 always pointed at and V6.6 unblocked: once you can *see*
> "this LXC has 7 updates pending", apply them in one click. Deferred out of V6.0
> ("triggering `apt upgrade` … should land separately, after the shell story
> exists") — the shell story is V6.6, so this lands now.

**Shipped (6.7.1):**

- ✓ **Apply over SSH, node + LXC.** `IProxmoxUpdateApplier` /
  `ProxmoxSshUpdateApplier` SSH to the host and run
  `apt-get update && apt-get -y -o Dpkg::Options::=--force-confold dist-upgrade`,
  directly on the node (`vmId 0`) or via `pct exec <vmid> -- …` for an LXC.
  Non-interactive; a guard short-circuits non-Debian targets with a distinct
  exit code reported as "nothing to upgrade".
- ✓ **Streamed output.** New `POST …/node/update` and `POST …/lxc/{vmId}/update`
  endpoints stream the combined stdout/stderr as NDJSON; the browser
  (`proxmox-updates.ts` + `ProxmoxUpdateDialog`) renders a confirm → run → result
  dialog with the live apt log. The confirm step spells out that a node run
  upgrades the whole node (1 host = 1 node) and may need a reboot for a kernel.
- ✓ **Triple-gated like the console** (all required, off by default): the
  `AllowProxmoxUpdates` master switch (DB-backed singleton +
  **Settings → Proxmox updates** page + features flag), a per-host
  `AllowUpdates` opt-in (**Allow apply updates** in the host modal), and SSH
  credentials. Gate failures return a deterministic 403/409 before any command.
- ✓ **Audited.** A `ProxmoxUpdateSession` row per run (who / when / host / node /
  guest / exit / bytes / end reason), on the Audit page's new **Proxmox updates**
  tab. Additive migration `AddProxmoxUpdateApply`. No new background worker —
  applying is on-demand only.
- ✓ **Tests.** Controller gating (global off → 403, per-host off → 403, no SSH →
  409, foreign host → 404) + the all-gates-pass path streams output and writes a
  finalised audit row; settings-service seed/persist. Full suite 1070.

**Out of scope (as before):** non-Debian package managers (`apk` / `dnf`),
auto-reboot after a node kernel update, and scheduled/unattended upgrades — all
remain manual follow-ups.

---

### ✅ Phase V6.8 — PVE node card

**Complexity:** Medium-High
**Value:** Give operators a single, high-signal hardware/health view for each
Proxmox node so they can detect saturation, thermal risk, storage pressure,
and hardware degradation early.

**Shipped (6.8.0):**

- The node card on the Proxmox page is now a **live health card**, styled to
  follow the Docker page's **`.host-card`** pattern (full-width host summary
  above the LXC grid) for cross-platform consistency: it reuses the shared status
  dot + `StateBadge` (online/offline) and polls the node's own status (CPU % ·
  RAM % · root-FS %) every 20s, showing colour-coded metric chips (ok / warn /
  crit by sane default thresholds) and a "Refreshed Xs ago" timestamp, while
  keeping the update badge / "Update now" affordance. **Degraded chips expose a
  tooltip with the root reason + suggested action.** Click it to open the modal.
- New **node modal** reusing the Docker/LXC `container-modal-*` shell and the
  shared `StatTile` / `Sparkline` (extracted from the LXC modal so both surfaces
  render identical tiles), with tabs **Overview · CPU/RAM · Storage/SMART ·
  Network · Sensors · Console**:
  - **Overview** — identity/lifecycle (uptime, kernel, PVE version,
    subscription), CPU (model, sockets/cores/threads, frequency, VT-x/AMD-V,
    live %, load avg, IO wait), memory (used/total/free + swap), host root FS.
  - **CPU/RAM** — **Live** real-time view (polls node status every 2s, rolling
    window, Pause/Resume) and a **History** toggle for the RRD sparklines (CPU %,
    load, memory, swap) with hour/day/week timeframes — same Live/History UX as
    the LXC Stats tab.
  - **Console** — an SSH shell **on the node itself** (host login shell, not
    `pct exec`), reusing the V6.6 console transport (ticket + WebSocket + xterm)
    and audited identically; gated by the same global `AllowProxmoxConsole` +
    per-host `AllowConsole` + SSH.
  - **Storage/SMART** — per-pool usage meters + physical disks with SMART
    health/wearout badges; each disk expands to its full SMART attribute table
    (ATA) or NVMe text, loaded on demand.
  - **Network** — node throughput sparkline + the configured interface list
    (type, link state, address/CIDR, gateway, bridge ports / bond slaves).
  - **Sensors** — CPU/board temperatures and fan RPMs parsed from `sensors -j`
    over SSH (the one signal the Proxmox API doesn't expose), with a clear
    "not available / install lm-sensors" state when SSH or lm-sensors is missing.
- **Transport.** Base metrics come from the Proxmox REST API
  (`/nodes/{node}/status`, `/rrddata`, `/storage`, `/disks/list`,
  `/disks/smart`, `/network`, `/subscription`); only temperatures/fans use the
  host-side SSH collector. Every source degrades independently — a missing
  source renders a "not available" marker, never a hard failure.
- New endpoints under the existing controller:
  `GET …/node/status | /node/rrddata | /node/storage | /node/disks |
  /node/disks/smart | /node/network | /node/sensors`. Read-only, owner-scoped,
  no new tables or migrations (the card fans out lightly across nodes).

**Deferred:** threshold **alerting** (per-node persisted toggles, debounce,
notification wiring) moves to **V6.8.1**. The functional-requirement bullets the
Proxmox **API doesn't expose** — per-core CPU % + steal, memory `available`, disk
IO/IOPS/latency, thin-pool warnings, per-interface RX/TX + errors + link
speed/duplex, SMART last-self-test, PSU voltages — need host-side SSH collectors
and move to **V6.8.2** (along with degraded-metric tooltips, per-metric refresh
timestamps, and configurable per-connection polling). The "recommended
additions" (PCI/GPU inventory, Ceph/ZFS summaries, capacity forecast,
top-consumers, NTP drift) remain future follow-ups.

**Functional requirements:**

- Show node identity and lifecycle metadata: node name, cluster role,
  online/offline, uptime, Proxmox version, kernel version, last reboot,
  subscription state.
- Show CPU data:
  - model, sockets/cores/threads,
  - current utilization (% total + per-core),
  - load average (1m/5m/15m),
  - CPU frequency (if available),
  - virtualization flags (VT-x/AMD-V),
  - steal/wait indicators where available.
- Show memory data:
  - total/used/free/available,
  - swap total/used,
  - memory pressure trend (short historical sparkline).
- Show storage data:
  - per-storage pool usage (local-lvm, zfs, ceph, directory, etc.),
  - filesystem usage for host root,
  - disk IO (read/write throughput + IOPS),
  - latency where source supports it,
  - thin-pool / reserved capacity warnings.
- Show thermal and power signals:
  - CPU package temperature,
  - motherboard/system temperature,
  - NVMe/SATA drive temperatures,
  - fan RPM and PSU sensor values when exposed.
- Show disk health (SMART):
  - health status per physical drive,
  - critical SMART counters (reallocated sectors, pending sectors,
    uncorrectable errors, media wearout, power-on hours),
  - last self-test result + age,
  - explicit degraded/failing badges.
- Show network data:
  - per-interface RX/TX throughput,
  - error/drop counters,
  - link speed/duplex/state,
  - bridge/bond/VLAN overview.

**What else to display (recommended additions):**

- PCI/GPU inventory (vendor, model, driver, passthrough capability, health).
- RAID/ZFS status summary (degraded/resilvering/scrub state).
- Ceph node contribution snapshot (OSD up/in, reweight anomalies).
- Node task pressure indicators: running tasks, backup jobs, replication jobs.
- Top consumers lists (top VMs/LXCs by CPU/RAM/disk/network in last interval).
- Alert banner area with severity and first-seen timestamp.
- Capacity forecast (days-to-full estimation for key storages from trend).
- Time drift / NTP sync status.
- Security posture quick facts (secure boot, microcode version, pending updates).

**UX/UI requirements:**

- PVE node card visual style should follow the existing Docker page pattern,
  specifically .host-card in .host-section, to keep cross-platform UI consistency.
- Main card should display only high-level summary (status + key health signals),
  while detailed diagnostics must open in a dedicated modal.
- Detailed modal should be structured with multiple tabs to keep information
  readable (recommended tabs: Overview, CPU/RAM, Storage/SMART,
  Network, Sensors/Temperature, Alerts).
- Card and modal must be mobile-responsive: stacked sections on small screens,
  no horizontal overflow for metric tables.
- Consistent status colors and badges reused from existing dashboard components.
- Each metric must include unit and timestamp of last refresh.
- Degraded metrics must expose tooltip with root reason and suggested action.

**Data/refresh requirements:**

- Pull base metrics from Proxmox API where available; use host-side collectors
  only for gaps (e.g., SMART/sensors) with explicit capability checks.
- Support mixed availability (if one metric source is unavailable, render card
  partially with clear "not available" markers, not hard failure).
- Polling interval configurable per connection (default 15-30s) with backoff
  on failures.
- Keep short retention window for trend mini-graphs (for example 30-120 minutes).

**Alerting requirements:**

- Threshold-based warnings for CPU saturation, memory pressure, storage fullness,
  thermal limits, SMART degradation, and NIC error spikes.
- Per-node threshold overrides with sane global defaults.
- User must be able to enable/disable critical deviation/load notifications
  per node card (with explicit persisted preference state).
- Optional granular toggles by category (CPU, RAM, Storage, Thermal, SMART,
  Network) are recommended for future extension.
- Suppress flapping via debounce/hysteresis.
- Link active alerts to existing outage/notification channels.

**Access/security requirements:**

- Respect existing RBAC: read-only hardware visibility for viewer roles,
  advanced diagnostics only for admin/operator roles.
- Audit access to diagnostic sections and manual refresh actions.
- Never expose raw secrets/tokens in the UI or logs.

**Non-functional requirements:**

- Card API response should be lightweight enough for dashboard fan-out across
  multiple nodes.
- Graceful degradation under partial telemetry failure.
- Stable rendering without layout shifts under frequent refresh.

**Acceptance criteria:**

- User can open any reachable node card and see CPU/RAM/storage/network
  health in under one refresh cycle.
- Thermal/SMART/storage warnings are clearly visible and actionable.
- Card remains usable on mobile viewport.
- Missing telemetry sources do not break rendering.

---

### ✅ Phase V6.8.1 — PVE node alerting (image 6.8.1)

> **Shipped.** Implemented exactly as scoped below. The node modal gains an
> **Alerts** tab (active alerts with severity + first-seen, the enable/disable
> toggle, per-category checkboxes, and per-node threshold overrides). Health
> classification is a shared backend port of the frontend `proxmox-node-health`
> helpers (`ProxmoxNodeHealthClassifier` + `ProxmoxNodeAlertThresholds`); the
> debounce/hysteresis state machine (`ProxmoxNodeAlertEvaluator`) fires only after
> N consecutive breaches and clears only after N consecutive Ok readings.
> Evaluation is folded into `ProxmoxUpdateBackgroundService` but runs on **every
> tick** (not the slow update schedule) for opted-in nodes. New
> `ProxmoxNodeAlertSettings` + `ProxmoxNodeAlertState` tables (additive
> migration, every node opted out by default); alerts route through the existing
> email + Telegram channels with a per-channel signature throttle. NIC error/drop
> spikes are read from `/proc/net/dev` over SSH; an unavailable source is n/a,
> never crit. Backend + frontend tests (boundary classification, debounce,
> hysteresis, override resolution, throttle signature, "unavailable never
> alerts"; Alerts-tab rendering, optimistic toggle, threshold validation).

**Complexity:** Medium-High
**Value:** Turn the V6.8 node card from a *view* into a *watch*. V6.8 ships the
health classification (ok / warn / crit) and the colour-coded surface; V6.8.1
makes those deviations **notify**, with per-node opt-in and the same throttling
discipline as the existing Docker/Proxmox update alerts. Split out from V6.8
deliberately: the read-only card is low-risk and useful on its own, whereas
alerting adds persisted preference state, a DB migration, an evaluation loop,
and notification-channel wiring — a materially larger, separable surface.

**Scope:**

- **Thresholds with sane global defaults + per-node overrides.** Reuse the V6.8
  defaults (CPU 80/95, RAM 85/95, storage 85/95) as the global baseline; allow a
  per-node override row so a deliberately hot node can be tuned without muting
  the fleet. Categories: CPU saturation, memory pressure, storage fullness,
  thermal limit (vs the chip's own high/crit, falling back to defaults), SMART
  degradation (health ≠ PASSED, wearout ≤ thresholds), and NIC error/drop spikes.
- **Per-node enable/disable, persisted.** A toggle on the node card / modal that
  opts a node into critical-deviation notifications, with explicit persisted
  state (the node analogue of a Docker watch's enabled flag). Optional granular
  per-category toggles (CPU / RAM / Storage / Thermal / SMART / Network) are a
  recommended extension, off by default.
- **Evaluation loop.** Fold node-health evaluation into the existing
  `ProxmoxUpdateBackgroundService` cadence (or a sibling timer) so it shares the
  per-connection schedule. Each tick reads the same API/SSH sources the card
  uses and classifies them.
- **Debounce / hysteresis.** Suppress flapping: a deviation must persist across N
  consecutive evaluations (or a time window) before it fires, and must clear
  below a lower band before "recovered" is sent. Persist last-fired signatures
  the way the update notifier already throttles by a state signature.
- **Channel reuse.** Route active alerts through the existing email + Telegram
  channels and the outage/notification surface — no new transport. An alert
  carries severity (warn / crit), the metric + value + threshold, and a
  first-seen timestamp.

**Data / migration:**

- New `ProxmoxNodeAlertSettings` (per-connection: enabled, optional category
  mask, optional threshold overrides) + a small last-state table (or a JSON
  signature column) for debounce/throttle. Additive migration; defaults keep
  every node opted **out** until the user enables it.

**UX/safety requirements:**

- Surface an **Alerts** tab/area in the node modal: current active alerts with
  severity + first-seen, plus the enable/disable + threshold controls.
- Degraded metrics on the card expose a tooltip with the root reason and a
  suggested action (already stubbed by the V6.8 health classification).
- Never notify on a source that's merely *unavailable* (SSH/lm-sensors missing
  is "n/a", not "crit").

**Tests:**

- Backend: threshold classification (boundary values per category), debounce
  (fires only after N breaches, clears with hysteresis), per-node override
  resolution vs global defaults, throttle signature (no duplicate notification
  for an unchanged alert), and "unavailable source never alerts".
- Frontend: enable/disable persistence + optimistic toggle, threshold form
  validation, Alerts tab rendering for warn/crit/recovered states.

---

### ✅ Phase V6.8.2 — PVE node deep telemetry (SSH collectors)

**Status:** Implemented (unreleased) — ships in the next image; see the
[CHANGELOG](./CHANGELOG.md) `[Unreleased]`.
**Complexity:** Medium-High
**Value:** V6.8 shipped every node metric the **Proxmox REST API** exposes
(plus temperatures/fans via `sensors`). This phase closes the remaining
functional-requirement bullets that the API simply does not provide — they need
host-side collectors over the same SSH channel the console/sensors already use.
Split out deliberately: each is an independent SSH parser with its own capability
check and "not available" fallback, so they batch cleanly into one phase rather
than bloating the V6.8 first cut.

**Scope (each = one SSH collector behind a capability check, rendered into the
existing node-modal tabs; missing/unsupported → "not available", never a hard
failure):**

- **CPU — per-core utilization + steal.** Parse `/proc/stat` (two samples) for
  per-core % and the `steal` field. Renders per-core bars on the CPU/RAM tab and
  a steal indicator on Overview. (API gives only aggregate CPU + `wait`.)
- **Memory — `available`.** Read `MemAvailable` from `/proc/meminfo` (the API
  reports `free`, not `available`).
- **Storage — disk IO + IOPS + latency.** Parse `/proc/diskstats` (or a short
  `iostat -x` sample) for per-disk read/write throughput, IOPS, and await/latency
  where the source supports it. Adds an IO section to the Storage/SMART tab.
- **Storage — thin-pool / reserved-capacity warnings.** Read `lvs`
  (data%/meta%) for LVM-thin pools and surface a warning badge as the pool nears
  full; flag reserved/over-provisioned capacity.
- **Network — per-interface throughput + errors + link.** Parse `/proc/net/dev`
  (two samples) for per-interface RX/TX rates and error/drop counters, and
  `ethtool <iface>` (or `/sys/class/net/<iface>/{speed,duplex,operstate}`) for
  link speed/duplex/state. Replaces the current node-aggregate-only throughput on
  the Network tab.
- **SMART — last self-test + age, highlighted critical counters.** `smartctl -l
  selftest` for the last self-test result + age; extract and badge the critical
  counters (reallocated/pending/uncorrectable/power-on hours) instead of leaving
  them in the raw attribute table.
- **Sensors — PSU / voltage / power.** Extend the `sensors -j` parser to also
  emit voltage (`in*`) and power (`power*`) inputs, not just temps/fans.

**Cross-cutting data item deferred from V6.8 (fold in here):**

- **Per-connection polling interval** (configurable, default 15-30s) **with
  failure backoff** — V6.8 uses fixed intervals (card 20s, live 2s, lazy tabs
  30s) and react-query's default retry.

  *(The other two cross-cutting items — degraded-metric tooltips with root
  reason + suggested action, and per-tab "last refresh" timestamps — were
  completed in V6.8 itself and are no longer pending here.)*

**Out of scope (still future, "recommended additions"):** PCI/GPU inventory,
RAID/ZFS status summary, Ceph OSD snapshot, node task-pressure indicators,
top-consumers lists, capacity forecast, NTP drift, secure-boot/microcode posture.

**Note on RBAC:** the roadmap's viewer/admin diagnostic split does not map to
this app — connections are ownership-scoped per user, with no role model. If a
role model is introduced later, gate the advanced tabs + console then.

**Tests:**

- Backend: each collector's parser against representative `/proc/*`, `iostat`,
  `lvs`, `ethtool`, `smartctl -l selftest`, and `sensors -j` (voltage/power)
  fixtures, including the capability-absent path → "not available".
- Frontend: per-core bars, per-interface rows, IO section, self-test badge, and
  the tooltip/refresh-timestamp/poll-config additions render and degrade cleanly.

---

### ✅ Phase V6.9.0 — Edit LXC network interfaces and mount points (image 6.9.0)

**Status:** Shipped in 6.9.0. The LXC **Config** tab's `net<n>` / `mp<n>` /
`rootfs` lines became guided row editors with Edit / Add / Remove, an advanced
raw fallback that preserves unmodelled options, server-side key numbering +
`delete=` generation, a conservative per-change review (live / restart / 
destructive, naming the exact key), and full client + server validation. Backend
codec/validator/payload and frontend parser/editor are covered by tests; the V6.5
scalar editor keeps working unchanged. Operator caveats (possible guest restart;
removing a mount entry does not delete storage content) are surfaced inline and
documented.

**Complexity:** High
**Value:** Completes the remaining editable part of the LXC **Config** tab by
covering the configuration areas intentionally excluded from V6.5: network
interfaces and mount points / `rootfs`. This is the point where the LXC editor
stops being a scalar-only form and becomes a practical day-to-day configuration
surface for real guests.

**Why this is a separate phase:** unlike `cores` / `memory` / `swap` /
`hostname` / `onboot`, the `net<n>` / `mp<n>` / `rootfs` keys are compound
config lines with Proxmox-specific option syntax, key numbering, and destructive
delete semantics. They also carry materially higher risk: a malformed network
entry can cut off connectivity, and a bad mount/rootfs change can orphan data or
leave the guest unbootable. Keeping them out of V6.5 preserved the low-risk
scalar edit path and leaves this higher-risk structured work isolated here.

**Scope:**

- **Network interfaces become editable.** The read-only `net<n>` lines on the
  **Config** tab move to an editor that supports both updating an existing
  interface and adding/removing interfaces.
  - Structured fields for the common case: interface name, bridge, IPv4/IPv6
    mode (`dhcp`, `manual`, static/CIDR), gateway, VLAN tag, firewall flag,
    MTU, rate limit, MAC address, and enable/disable state where Proxmox exposes
    it through the config line.
  - Advanced/raw fallback for options Stashboard does not yet model explicitly,
    so uncommon Proxmox options are not blocked by an incomplete form.
  - New interfaces use the next available `net<n>` key; removals flow through
    Proxmox `delete=` semantics rather than ad-hoc string rewriting.
- **Mount points become editable.** The read-only `mp<n>` lines move to an
  editor that supports updating, adding, and removing secondary mounts.
  - Structured fields for the common case: storage/source, container mount path,
    size, read-only/read-write, backup flag, quota, ACL, shared/replicate flags,
    and mount options where they map cleanly.
  - Add flow supports both storage-backed volumes and bind/device-style mounts
    where the Proxmox API allows them.
  - Remove flow uses Proxmox `delete=mp<n>` and clearly warns that removing the
    config entry does not necessarily delete underlying storage content.
- **`rootfs` gets first-class handling.** `rootfs` is not just another `mp<n>`;
  it should be rendered in a dedicated section with its own editing rules.
  - Editable fields cover the safe/common subset such as size and selected
    storage options that Proxmox accepts on `rootfs`.
  - Dangerous or storage-migration-like operations stay out of scope unless they
    can be validated safely; the initial target is editing, not full storage
    re-provisioning.
  - `rootfs` cannot be removed; only modified.
- **Review/confirm flow, matching V6.5.** Before saving, the user sees a
  per-change review list that classifies each operation conservatively:
  *likely requires restart*, *destructive/remove*, or *safe metadata change*.
  For ambiguous cases, prefer the stricter warning rather than claiming a live
  apply path Proxmox may not honour consistently.
- **Backend/API path.** The owner-scoped LXC config write path grows from the
  V6.5 scalar subset to cover structured network/mount changes as well. The API
  must build the exact Proxmox config payload, including numbered keys and any
  required `delete=` list, instead of trusting the client to craft raw request
  strings.
- **Permission model.** Requires the relevant Proxmox config rights for the
  affected area (at minimum the existing `VM.Config.*` umbrella; in practice
  `VM.Config.Network` and storage/disk config rights where applicable). Any
  permission or validation rejection is surfaced verbatim.

**UX/safety requirements:**

- Show **network interfaces** and **mount points** as row cards/tables with an
  explicit **Edit**, **Add**, and **Remove** affordance instead of a single raw
  textarea.
- Keep the original raw Proxmox option string visible in an advanced/details
  expander so power users can verify the exact generated line.
- Validate on the client before save for common mistakes: duplicate interface
  names, invalid IP/CIDR, invalid gateway, duplicate mount paths, impossible size
  values, and attempts to delete `rootfs`.
- On remove, show a strong confirm dialog naming the exact key (`net1`, `mp2`,
  `rootfs`) and summarising the effect.
- Surface restart guidance inline after save where relevant: if the guest is
  running and the change is not safely live-applied, the success state should say
  that a restart may be required for the guest to fully pick up the new config.
- Preserve mobile usability: rows stack cleanly, long raw config strings wrap,
  and advanced fields do not cause horizontal overflow in the modal.

**Non-goals / intentionally out of scope for the first cut:**

- Full storage migration workflow between backends/nodes.
- Reordering interfaces/mount points purely for cosmetic key renumbering.
- Editing every obscure Proxmox option in structured mode on day one; unknown but
  valid options may remain advanced/raw-only until later.
- Silent destructive fallbacks. If Stashboard cannot safely interpret a config
  line, it should leave it read-only or require advanced mode rather than writing
  a lossy approximation.

**Tests:**

- Backend: builds the correct Proxmox payload for update/add/remove of `net<n>`
  and `mp<n>` / `rootfs`; emits `delete=` correctly; rejects illegal operations
  such as deleting `rootfs`; preserves untouched lines; surfaces Proxmox
  validation/permission failures without rewriting them.
- Frontend: form parses existing rows correctly; add/edit/remove review list is
  accurate; advanced/raw fallback is shown when structured parsing is incomplete;
  confirm dialogs name the exact target; optimistic refresh/error handling matches
  the V6.5 editor.
- Regression: existing scalar edits from V6.5 still work unchanged when network
  and mount editing is introduced.

**Acceptance bar:** a user can open an LXC's **Config** tab, edit `net<n>` and
`mp<n>` / `rootfs` through a guided UI, review the exact effect before saving,
and have Stashboard persist the correct Proxmox config changes without turning
unsupported lines into broken output.

**Implementation checklist:**

- **1. Extend the LXC config contract for structured edits.**
  - Add request/response models for editable network and mount rows.
  - Keep numbered-key identity (`net0`, `mp2`, `rootfs`) explicit instead of
    inferring it from array position.
  - Model add/update/remove as intentful operations, not as a blind full-string
    replace of the whole config block.
- **2. Build a Proxmox config-line parser/formatter layer.**
  - Parse the current raw `net<n>` / `mp<n>` / `rootfs` strings into structured
    fields for the guided editor.
  - Preserve unknown-but-valid options so unsupported lines do not become lossy.
  - Format the edited model back into the exact Proxmox payload shape, including
    numbered keys and `delete=` support.
- **3. Implement backend validation and safety guards.**
  - Reject illegal operations early (`rootfs` removal, duplicate mount paths,
    malformed IP/CIDR, invalid gateways, impossible sizes where they are
    locally checkable).
  - Distinguish between safe structured mode and advanced/raw fallback when a
    line cannot be represented losslessly.
  - Keep untouched config lines untouched.
- **4. Extend the owner-scoped LXC config write endpoint.**
  - Reuse the V6.5 write path and grow it to emit network/mount changes.
  - Translate UI intent into Proxmox request parameters and `delete=` entries.
  - Keep permission and validation errors verbatim.
- **5. Add frontend row editors for `net<n>`.**
  - Render one row per interface with edit/review/remove actions.
  - Support add flow using the next available `net<n>` key.
  - Include advanced/raw mode for uncommon options.
- **6. Add frontend row editors for `mp<n>` and dedicated `rootfs` editing.**
  - Render secondary mounts separately from `rootfs`.
  - Support add/edit/remove for `mp<n>` and edit-only for `rootfs`.
  - Show clear destructive warnings when removing a mount config entry.
- **7. Expand the review/confirm UX.**
  - Reuse the V6.5 review pattern so every pending change is summarised before
    save.
  - Classify changes as conservative impact labels such as restart likely,
    destructive/remove, or metadata-only.
  - Name the exact target key in every confirmation surface.
- **8. Add optimistic refresh and fallback behaviour.**
  - Refresh the LXC detail after save and keep error handling aligned with the
    current scalar editor.
  - If a line cannot be safely mapped into the structured form, render it as
    advanced/raw or keep it read-only instead of risking a lossy write.
- **9. Add backend tests.**
  - Parser/formatter round-trip tests for supported `net<n>` / `mp<n>` /
    `rootfs` shapes.
  - Endpoint tests for add/update/remove, `delete=` generation, rootfs-protect
    rules, and surfaced Proxmox failures.
- **10. Add frontend tests.**
  - Form rendering/parsing tests, validation tests, review-list tests,
    confirmation tests, and regression coverage for the V6.5 scalar editor.
- **11. Document operator caveats.**
  - Note that some changes may require a guest restart.
  - Clarify that removing a mount config entry does not necessarily delete
    underlying storage content.

---

### ✅ Phase V6.10 — Proxmox page Docker-parity redesign (page-level UX) (image 6.10.0)

**Complexity:** Low-Medium
**Value:** The LXC **modal** reached Docker parity in V6.1–V6.7.1, but the Proxmox
**page** itself was still a plain stacked list while the Docker page
([`DockerInstances.tsx`](frontend/src/pages/DockerInstances.tsx)) is a full command
centre. This phase brings the list surface up to the same standard. It is UI-only —
the data is already on the client in `connection.guests` — so it was the cheapest
high-impact parity work left.

> Bundles the page-level gaps that would otherwise be ~6 tiny versions into one
> coherent redesign release, mirroring how the Docker page redesign shipped as the
> single V5.9.

**Shipped (6.10.0):**

- The Proxmox page now wears the Docker page's `dock` shell, **reusing the
  `searchbox`, `segmented`, `dock-summary`, `switcher`/`conn` markup + CSS
  verbatim** — no parallel system.
- **Search box** filtering LXC cards by name (mirrors the Docker name search).
- **State filter** segmented control (`All / Running / Stopped`).
- **Monitoring filter** segmented control (`All / Enabled / Disabled / Updates`)
  driven by the existing `monitoringEnabled` / `pendingUpdates` fields (the optional
  follow-up noted under V6.7) — `Updates` requires monitoring on **and** a positive
  pending count.
- **Summary strip** aggregating totals across hosts: objects, running, stopped,
  pending updates (`objects === running + stopped` always holds).
- **Connection switcher** (`All connections` + a chip per host with running/total
  and update counts); hidden for a single host, like the Docker switcher.
- **Grouping by PVE node** — clarified during implementation: since each Proxmox
  connection already maps to exactly one node, "grouping by node" is the existing
  per-connection section structure (node card as the host summary, LXC cards in the
  grid below). No `GET /pools` backend / migration was needed; the phase stayed
  UI-only.
- **Deep-link** into the LXC modal via query params (`?connection=…&vmid=…`),
  reusing the deep-link `useEffect` pattern from the Docker page (the `vmid` param is
  consumed and stripped after the modal opens).
- The filter/aggregation predicates live in a pure
  [`proxmox-page.ts`](frontend/src/lib/proxmox-page.ts) module with unit tests
  (search/filter/monitoring predicates, summary totals math, switcher stats,
  deep-link resolution).

**Acceptance bar:** a user with multiple Proxmox hosts and many LXCs can search,
filter by state/monitoring, see cross-host totals, switch hosts, and deep-link to a
specific container — the same affordances the Docker page already offers.

---

### ✅ Phase V6.11 — Bulk LXC monitoring & update operations + audit (image 6.11.0)

**Complexity:** Medium
**Value:** Today monitoring and "Update now" are per-LXC only. Operators with many
guests need host-wide controls, maintenance-window snoozing, and traceability —
all explicitly listed as optional follow-ups under V6.7.

**Scope:**

- **Bulk monitoring toggle:** **Enable all / Disable all LXCs** on a host, with
  confirmation. Implemented as a loop over the existing
  `PUT …/lxc/{vmId}/monitoring` endpoint (or a new batch endpoint if the loop is
  too chatty).
- **Bulk "Update now":** apply pending updates across all (or selected) LXCs on a
  host, streaming each guest's apt log in turn via the existing
  `POST …/lxc/{vmId}/update` path. Reuses the V6.7.1
  [`ProxmoxUpdateDialog`](frontend/src/components/proxmox/ProxmoxUpdateDialog.tsx)
  confirm → run → result flow, iterating per guest. Same triple-gate
  (`AllowProxmoxUpdates` + per-host `AllowUpdates` + SSH).
- **Snooze ("temporarily disable until date")** for maintenance windows — a
  nullable `MonitoringSnoozedUntil` on `ProxmoxGuestEntity`; the scan service
  skips snoozed guests until the date passes, then auto-re-enables.
- **Audit row for monitoring toggle changes** (`who / when / guest / new state`)
  to match the operational traceability already present for console/update
  actions; surfaced on the existing **Settings → Audit** page.
- **Webhook trigger for update checks** — the Proxmox analogue of the Docker
  watch webhook (rotate / delete token + an endpoint that kicks off a host scan).
  Reuses the Docker webhook token pattern; gated and off by default since it is an
  external trigger surface.

**Out of scope:** scheduled/unattended bulk upgrades (stays manual, as in V6.7.1).

**Tests:** bulk enable/disable iterates and persists; snoozed guests are excluded
from scheduled + manual checks until expiry then re-included; toggle audit rows are
written with the correct fields; bulk update respects all three gates and finalises
one audit session per guest.

**Acceptance bar:** an operator can disable monitoring for every LXC on a host in
one click, snooze a guest for a maintenance window, apply updates to all guests
from one dialog, and see every monitoring change in the audit trail.

**Shipped (6.11.0):**

- **Bulk monitoring toggle** — chose a **batch endpoint**
  (`PUT …/lxc/monitoring/bulk`) over a client-side loop, so the flip is one
  transaction and one audit row per actually-changed guest. "Enable all" /
  "Disable all" live on each host's section header, behind a confirmation dialog;
  the node row is never touched.
- **Bulk "Update now"** — a dedicated streaming endpoint
  (`POST …/lxc/update/bulk`) iterates the selected guests server-side (extracted a
  shared `RunTargetUpdateAsync` so single + bulk share the session / refresh /
  finalise logic and can never drift). The dialog reuses the V6.7.1 confirm →
  stream → result flow over a **checklist** of eligible targets — the **node**
  and its containers (running, monitored, not snoozed, **with pending updates** —
  pre-checked, uncheck any), framing each one's apt log with `guest-start` /
  `guest-end` and a final `all-done`. The node runs first (vmId 0) and carries
  the reboot caveat. Same triple gate, validated once up front; one finalised
  audit session per target.
- **Maintenance snooze** — nullable `MonitoringSnoozedUntil` on
  `ProxmoxGuestEntity`, set from the LXC **Watch** tab (1h / 6h / 24h / 7d, or
  clear). The scan service folds snoozed guests into the same skip set as
  monitoring-off (excluded from scheduled **and** manual checks) and clears the
  field on the first scan at/after the instant, so the guest auto-re-includes.
  Monitoring stays on; the card shows a **Snoozed** badge and mutes meanwhile.
- **Monitoring audit trail** — a new `ProxmoxMonitoringAuditEntity` (who / when /
  guest / change type / new state / bulk flag) written for every toggle, bulk
  flip, and snooze/unsnooze, surfaced on **Settings → Audit → LXC monitoring**.
- **Update-check webhook** — opt-in, off by default. A host webhook token
  (rotate / remove from the edit modal, unique-indexed) and a public
  `POST /api/proxmox/webhooks/{token}` receiver that enqueues an out-of-band scan,
  drained by the background service's new `IProxmoxScanQueue` (the Proxmox
  analogue of the Docker webhook check queue; token reuses the Docker token
  generator).
- Migration `AddProxmoxBulkMonitoringAndWebhook`. Pure helpers
  (`isSnoozeActive`, `isBulkUpdateEligible`) live in
  [`proxmox-page.ts`](frontend/src/lib/proxmox-page.ts) with unit tests; backend
  tests cover bulk persist + audit, snooze exclusion/expiry, the three gates, the
  per-guest audit sessions, and the webhook receiver + drain.

---

### ✅ Phase V6.12 — LXC live logs (Logs tab) (image 6.12.0)

**Complexity:** Medium
**Value:** The LXC modal has **Tasks** (PVE task history) but no live log tail,
whereas the Docker modal streams container logs. This adds the missing
observability surface so operators can watch a guest's output in real time without
leaving the UI.

**Scope:**

- New **Logs** tab in the LXC modal, positioned and styled like the Docker
  [`ContainerLogsPanel`](frontend/src/components/docker/ContainerLogsPanel.tsx)
  (Pause / Resume / Stop / Download, autoscroll).
- Streams the guest's system journal over the **existing SSH channel** built for
  the V6.6 Console (`pct exec <vmid> -- journalctl -f` / tail of `/var/log`),
  bridged to the browser via the same xterm.js-adjacent WebSocket + ticket
  infrastructure.
- Gated by the same per-host SSH requirement as Console; read-only (no input).

**Why SSH, not the PVE API:** Proxmox exposes task logs via the API but **not** a
live guest-journal stream, so the live tail must go through SSH — the same
constraint the Console tab already lives with.

**Tests:** ticket issuance + gate checks reuse the Console test patterns; stream
starts/stops cleanly; non-running guest shows a clear empty state; SSH-not-
configured shows the same calm hint as the rest of the modal.

**Acceptance bar:** a user can open **Logs** on a running, SSH-configured LXC and
watch its journal stream live with pause/download controls.

**Shipped (6.12.0):** a new **Logs** tab (after **Tasks**) backed by a thin
[`ProxmoxLogsController`](src/Stashboard.Api/Controllers/ProxmoxLogsController.cs)
that reuses the V6.6 console transport *verbatim* — the same ticket service,
concurrency registry, SSH PTY connector, byte pump, and WebSocket adapter — gated
**identically** to the console (global switch + per-host **Allow LXC console** +
SSH + running guest), each gate showing the same calm hint. The remote command is
built server-side (`pct exec <vmid> -- sh -c 'journalctl -f … || tail -F
/var/log/…'`), so it is always the read-only tail with a journald→`/var/log`
fallback; the stream is read-only (no input), runs with **no idle timeout** so a
quiet guest isn't reaped, and writes **no audit row** (nothing runs beyond a
read-only read). The frontend
[`LxcLogsPanel`](frontend/src/components/proxmox/LxcLogsPanel.tsx) renders into the
Docker `docker-logs-*` toolbar/viewport (Pause / Resume / Stop / Stream / Clear /
Copy / Download + autoscroll). No new tables or migrations.

---

### ✅ Phase V6.13 — Destroy / remove LXC

**Complexity:** Medium
**Value:** Docker can `remove` a container from the UI; the LXC modal has no
destroy path (`ProxmoxLxcAction` is `start | stop | shutdown | reboot`). This
closes the last lifecycle gap — deliberately as its own small, high-risk phase.

**Scope:**

- **Destroy** action wired to `DELETE /nodes/{node}/lxc/{vmid}` via
  `IProxmoxApiClient`, surfaced in the modal's Lifecycle section and (optionally)
  the card.
- Mirrors the Docker removal safety model:
  [`RemoveConfirmDialog`](frontend/src/components/docker/atoms/RemoveConfirmDialog.tsx)-
  style **double confirm** that names the exact guest (`CT <vmid> · <name>`),
  behind a **feature flag** + a **per-host opt-in** (e.g. `AllowDestroy`), off by
  default — the same pattern as console/updates.
- Refuses to destroy a **running** guest (require stop first), and audits the
  action (`who / when / host / guest / result`).

**Out of scope:** purging associated backups/storage volumes; bulk destroy.

**Tests:** gate failures return deterministic 403/409 before any API call; running
guest is rejected; confirm dialog names the exact target; successful destroy
removes the guest from the next scan and writes an audit row.

**Acceptance bar:** with the flag and per-host opt-in enabled, a user can destroy a
**stopped** LXC after an explicit double confirmation, and the action is audited.

**Shipped (6.13.0):**

- **Destroy** wired to `DELETE /nodes/{node}/lxc/{vmid}` via a new
  `IProxmoxApiClient.DeleteLxcAsync` (the client's `DeleteAsync` surfaces the
  Proxmox error body verbatim). Endpoint
  `DELETE /api/proxmox/connections/{id}/lxc/{vmId}` on
  `ProxmoxConnectionsController`.
- **Triple gate, the same pattern as console/updates:** a DB-backed server-wide
  master switch (`ProxmoxDestroySettingsEntity` singleton, seeded from
  `Stashboard:AllowProxmoxDestroy`, managed at **Settings → Destroy LXC** /
  `/api/settings/proxmox-destroy`), a per-host opt-in
  (`ProxmoxConnection.AllowDestroy`, "Allow destroy" in the host modal), and a
  **stopped** guest. Gate failures are deterministic and returned **before** any
  Proxmox call — global off ⇒ 403, host opt-in off ⇒ 403, running guest ⇒ 409
  (read from the last scan's persisted state, no API round-trip).
- **Double confirm:** the modal's Lifecycle section shows **Destroy** only for a
  stopped, gated guest; it opens
  [`LxcDestroyDialog`](frontend/src/components/proxmox/LxcDestroyDialog.tsx) — a
  verbatim reuse of the Docker `remove-confirm-*` markup/CSS — naming the exact
  guest (`CT <vmid> · <name>`). On success the guest row is removed immediately
  (the card drops without waiting for the next scan) and the modal closes.
- **Audited:** every attempt that reaches the host writes one
  `ProxmoxDestroyAuditEntity` row (who / when / host / node / guest / success /
  error), surfaced read-only on the Audit page's new **LXC destroy** tab
  (`GET /api/proxmox/destroy/sessions`).
- **Out of scope, as planned:** purging backups / external storage volumes (only
  the container + its root disk are removed); bulk destroy.
- Migration `AddProxmoxDestroy` adds the `AllowDestroy` column, the
  `ProxmoxDestroySettings` singleton table, and the `ProxmoxDestroyAudits` table.

---

### ✅ Phase V6.13.1 — Create LXC

**Complexity:** Medium–High
**Value:** With **edit** (V6.5 / V6.9) and **destroy** (V6.13) shipped, the only
missing leg of full LXC lifecycle from the Stashboard UI is **create**. Proxmox
can already provision a container from a template via the REST API; surfacing it
closes the loop so a user never has to drop to the Proxmox web UI for routine
container management. Deliberately its own phase — creation pulls in template /
storage / network selection and is the highest-touch of the lifecycle verbs.

**Scope:**

- **Create** action wired to `POST /nodes/{node}/lxc` via a new
  `IProxmoxApiClient.CreateLxcAsync(profile, ProxmoxLxcCreate spec)`. Proxmox
  returns a task UPID; the call should poll `GET …/tasks/{upid}/status` to a
  terminal state (or surface the UPID to the existing Tasks tab) so the UI can
  report real success/failure rather than "request accepted".
- **Entry point:** a **New LXC** button on the Proxmox page's per-host block
  header (next to the host's bulk actions), opening a new
  `LxcCreateModal` that reuses the `container-modal-*` / `service-modal-*` form
  styling — **not** a parallel form system (same UX-unification rule as the LXC
  modal).
- **Create form (minimum viable provision):**
  - **Identity:** `vmid` (with a "next free id" default read from
    `GET /cluster/nextid`), `hostname`, optional `description`/tags.
  - **Template:** a dropdown populated from
    `GET /nodes/{node}/storage/{storage}/content?content=vztmpl` across the
    template-capable storages (so the user picks an existing
    `local:vztmpl/…​.tar.zst`, not a free-text path).
  - **Root password / SSH key:** `password` (write-only) or `ssh-public-keys`,
    surfaced as tri-state secret fields like the host's API token / SSH key.
  - **Resources:** cores, memory (MiB), swap (MiB), rootfs storage + size — reuse
    the V6.5 scalar editors and the storage list from
    `GET /nodes/{node}/storage`.
  - **Network:** one `net0` row reusing the **V6.9** structured net editor
    (`name` / `bridge` / `ip` (dhcp|static CIDR) / `gw` / VLAN), formatted by the
    existing `formatNet`.
  - **Options:** `unprivileged` (default on), `onboot`, `start` after create.
- **Validation:** client-side guards mirror the V6.9 editor (valid CIDR/MAC,
  positive sizes, vmid in 100–999999999 and not already present in the host's
  guest list); the server stays authoritative and relays any Proxmox rejection
  verbatim (e.g. storage that can't hold a rootfs, a vmid already in use).
- **Discovery:** on success, trigger the existing host **Check now** scan so the
  brand-new container appears as a card without waiting for the schedule (the new
  guest is added by the scan's upsert).
- **Gating + audit:** behind a server-wide master switch
  (`Stashboard:AllowProxmoxCreate` → `ProxmoxCreateSettingsEntity` singleton,
  **Settings → Create LXC**) **+** a per-host opt-in
  (`ProxmoxConnection.AllowCreate`), both off by default — the same triple-gate
  shape as destroy/updates, **minus** the running-guest check (there is no guest
  yet). Every create attempt that reaches the host writes a
  `ProxmoxCreateAuditEntity` row (who / when / host / node / vmid / hostname /
  template / success / error), surfaced on the Audit page's new **LXC create**
  tab.

**Out of scope:** cloning from an existing container / snapshot (tracked by
**V8.0**); restoring from a backup (`vzdump`) (tracked by **V8.1**); multi-mount-
point / advanced rootfs options at create time (edit them afterwards via the V6.9
Config editor); VM (QEMU) creation (tracked by V6.14).

**Tests:** gate failures return deterministic 403 before any API call; a vmid that
already exists is rejected; a malformed network/size is rejected client- and
server-side; `CreateLxcAsync` POSTs the expected form body to
`/nodes/{node}/lxc`; a successful create writes an audit row and the follow-up
scan surfaces the new card; a Proxmox rejection surfaces as a 502 with the host's
message and a failure audit row.

**Acceptance bar:** with the flag and per-host opt-in enabled, a user can create a
new LXC from a template — choosing id / hostname / template / resources /
network — entirely from the Stashboard UI, the new container appears on the
Proxmox page after the auto-scan, and the action is audited.

**Shipped (6.13.1):**

- **Create** wired to `POST /nodes/{node}/lxc` via a new
  `IProxmoxApiClient.CreateLxcAsync(profile, ProxmoxLxcCreate spec)` that polls
  the returned task UPID (`GET …/tasks/{upid}/status`) to a terminal state and
  throws the host's error / non-`OK` exit verbatim. Endpoints on
  `ProxmoxConnectionsController`: `POST /api/proxmox/connections/{id}/lxc`,
  `GET …/lxc/nextid` (`/cluster/nextid`), `GET …/lxc/templates` (aggregated
  `vztmpl` content across template-capable storages).
- **`LxcCreateModal`** opened from the per-host header's **New LXC** button,
  reusing the Docker `container-modal-*` / `service-modal-*` styling, with
  near-parity to the Proxmox "Create CT" wizard: identity (vmid defaulted from
  next-free id, hostname, description, tags), an **editable template combobox**
  (pick a discovered `vztmpl` or type any volid), root password / SSH key,
  resources (cores / memory / swap / rootfs storage + size), a full `net0` row
  (name / bridge / MAC / VLAN / rate / IPv4 + gw / IPv6 + gw / firewall), **DNS**
  (nameserver + searchdomain), and options — unprivileged (default on), **nesting**
  (`features=nesting=1`), onboot, start, and **Add to HA** (best-effort
  `POST /cluster/ha/resources` after create). Defaults derive from the live
  queries during render (no setState-in-effect).
- **Double gate, the destroy/updates shape minus the running-guest check:** a
  DB-backed server-wide switch (`ProxmoxCreateSettingsEntity`, seeded from
  `Stashboard:AllowProxmoxCreate`, **Settings → Create LXC** /
  `/api/settings/proxmox-create`) and a per-host opt-in
  (`ProxmoxConnection.AllowCreate`, "Allow create"). Gate failures are
  deterministic and returned **before** any Proxmox call (global off ⇒ 403, host
  opt-in off ⇒ 403); a vmid already on the host ⇒ 409, a malformed spec ⇒ 400
  (`ProxmoxLxcCreateValidator`, reusing the V6.9 net rules); a Proxmox rejection
  ⇒ 502 with the host's message.
- **Discovery:** on success the host's `CheckConnectionAsync` scan runs so the new
  card appears immediately; the refreshed host is returned.
- **Audited:** every attempt that reaches the host writes one
  `ProxmoxCreateAuditEntity` row (who / when / host / node / vmid / hostname /
  template / success / error), surfaced read-only on the Audit page's new **LXC
  create** tab (`GET /api/proxmox/create/sessions`).
- **Out of scope, as planned:** cloning / snapshot restore, vzdump restore,
  multi-mount / advanced rootfs at create time, and VM (QEMU) creation (V6.14).
- Migration `AddProxmoxCreate` adds the `AllowCreate` column, the
  `ProxmoxCreateSettings` singleton table, and the `ProxmoxCreateAudits` table.

---

### ✅ Phase V6.14 — VM (QEMU) support (image 6.14.0)

**Complexity:** High
**Value:** Stashboard's Proxmox integration currently covers **LXC + nodes only**
(`ProxmoxGuestType = Node | Lxc`). Many homelabs also run QEMU VMs. This phase adds
VMs as a first-class guest type so the Proxmox page reflects the whole host, not
just its containers.

**Scope (read + lifecycle first cut):**

- **New `ProxmoxGuestType.Qemu`** threaded through the scan service,
  `ProxmoxGuestEntity`, the API responses, and the TS `ProxmoxGuestType` union.
- **Discovery & status:** list VMs via `GET /nodes/{node}/qemu`, status via
  `qemu/{vmid}/status/current`.
- **Lifecycle:** start / stop / shutdown / reboot through
  `qemu/{vmid}/status/{action}` — reuses the LXC action UI.
- **Stats:** live (polling `status/current`) + history (`qemu/{vmid}/rrddata`),
  reusing the existing Stats tab and sparklines.
- **Tasks** tab works unchanged (tasks are node-scoped by upid).
- **Card + modal** reuse the LXC surface; the subtitle reads `VM <vmid>`.

**Explicitly out of scope for the first cut (and why):**

- **APT update monitoring / "Update now"** — VMs are not necessarily Debian and
  may have no SSH/guest-agent; the apt model does not generalise. Update
  monitoring for VMs is a possible later phase, not part of this one.
- **Console** — VM console is SPICE/VNC, a different protocol from the LXC SSH
  shell; out of scope here.
- **Config editing** — VM config (`virtio`, `scsi`, PCI passthrough) is a much
  larger structured-edit surface than LXC; read-only config display only.
- **Create** — VM creation (disk/ISO/firmware/PCI passthrough) is a much larger
  form than the LXC template create; out of scope. (**Destroy** generalises
  cleanly and *is* included — see "What shipped".)

**Tests:** scan maps QEMU guests with the new type; lifecycle actions hit the qemu
endpoints; stats/RRD render; the page groups/filters VMs alongside LXCs without
regressing LXC behaviour.

**Acceptance bar:** a user sees their QEMU VMs on the Proxmox page next to LXCs,
can start/stop/shutdown/reboot them, and can view live + historical stats and
tasks — with update monitoring and console clearly marked as LXC-only for now.

**What shipped (image 6.14.0):** `ProxmoxGuestType.Qemu` (value `2`) threaded
through the enum, the scan service (the upsert + `(connection, vmid)` key were
already type-agnostic), the API responses, and the TS `ProxmoxGuestType` union.
`IProxmoxApiClient` gained `ListQemuAsync` (`GET /nodes/{node}/qemu`) plus
`GetQemuStatusAsync` / `GetQemuRrdDataAsync` / `QemuStatusActionAsync` /
`GetQemuDetailAsync` — the status / rrddata / lifecycle reads share a private
`{kind}` path helper with their LXC twins (one segment differs), and the VM
status / RRD reuse `ProxmoxLxcStatus` / `ProxmoxLxcRrdPoint` verbatim. The
checker lists VMs after LXCs and maps each to a guest row with **no**
pending-update count, IP, or SSH probe (apt monitoring stays LXC-only); a VM
listing failure is treated as a connection-level error just like the LXC probe.
The live-status sync endpoint now reads both guest lists. The controller added
`{id}/qemu/{vmId}/config|rrddata|tasks|status` reads + `…/status/{verb}`
lifecycle (sharing the optimistic-update handler with the LXC path; the Tasks
read is the existing vmid-scoped listing). On the page, VM cards render in the
same guest grid (subtitle **VM `<vmid>`**) and a new **All / LXC / VM** type
filter appears once the host has at least one VM; the modal reuses the LXC shell
but exposes only **Overview · Config (read-only) · Tasks · Stats** — Watch
(apt update monitoring), Logs (`pct`), and Console (SSH) are hidden for VMs, and
the VM's disks / NICs map onto the Config tab's mount / network sections.
**Destroy** is included for VMs: the modal Lifecycle **Destroy** action is reused
for a stopped VM under the same triple gate as LXC destroy (global switch +
per-host **Allow destroy** + stopped), routed to `DELETE …/qemu/{vmid}` (new
`DeleteQemuAsync`) and written to the same destroy audit trail; the confirm
dialog + Lifecycle copy are VM-worded. Two post-ship lifecycle UX fixes also
landed in 6.14.0: a graceful **Shutdown** no longer optimistically marks the card
stopped (it's async — left for the live-sync to reconcile; also fixes the latent
LXC V6.4 behaviour, and the card buttons were relabelled Stop→Shutdown /
Restart→Reboot), and **Stop/Shutdown** now open a confirm dialog explaining the
difference (graceful vs hard power-off). Tests cover the QEMU API parsing + URLs
(incl. `DeleteQemuAsync`), the checker mapping VMs with the new type, the
controller's qemu lifecycle + reads + destroy gates + dual-list sync, the
shutdown-not-optimistic behaviour, the power-confirm dialog, and the page's type
filter.

---

### ✅ Phase V6.15 — Proxmox connections in backup / restore (image 6.15.0)

**Complexity:** Low-Medium
**Value:** Closes a **data-integrity gap**, not a cosmetic one. The config
backup/restore feature
([`BackupService`](src/Stashboard.Api/Services/BackupService.cs), endpoints
`GET /api/backup/export` + `POST /api/backup/import`) exports categories, tags,
**Docker connections**, services, **Docker watches**, and settings — but **omits
Proxmox entirely**. A user who exports a backup and restores it (e.g. migrating
hosts) silently **loses every Proxmox host** and its per-guest settings. Despite
its high phase number this should be prioritised ahead of the other V6.1x work.

**Scope:**

- Add **`ProxmoxConnections`** to the export/import DTOs alongside the existing
  `DockerConnections`, reusing the same **merge-by-name** strategy and the
  encrypted-at-rest handling already applied to credentials / SSH / registry
  material (`Dec(...)` on export, re-encrypt on import).
- Include the connection-level fields (node name, SSH host/user, `AllowUpdates`,
  `AllowConsole`, schedule, enabled flag, encrypted API token + SSH private key).
- Include the **per-guest settings** that are user intent rather than scan output —
  primarily `MonitoringEnabled` (and the V6.11 `MonitoringSnoozedUntil` snooze field) — keyed by
  `(vmId, guestType)` so they re-attach on the next scan. Scan-derived state
  (current status, pending counts, last error) is **not** exported; it repopulates
  on the next scan.
- Update the export file's documented contents and the **Backup** page copy to
  mention Proxmox.

**Out of scope:** exporting transient scan results; backing up the Proxmox guests
themselves (that is PVE `vzdump`, a different concern — see the VM/LXC platform,
not Stashboard config).

**Tests:** round-trip export→import preserves Proxmox connections and
`MonitoringEnabled` flags; encrypted fields survive the round-trip; merge-by-name
does not duplicate an existing host; a backup file without a Proxmox section
imports cleanly (back-compat with pre-V6.15 backups).

**Acceptance bar:** a user can export a backup, wipe/reinstall, import it, and find
their Proxmox hosts and per-LXC monitoring choices restored exactly — the same
guarantee Docker connections already enjoy.

**What shipped (image 6.15.0):** `BackupService` export + import now carry a
`ProxmoxConnections` section (merged by name like `DockerConnections`), covering
the connection-level config and the encrypted API token + SSH key (decrypted on
export, re-encrypted on import). Each host carries the per-guest **monitoring
intent** worth backing up — guests with monitoring turned off or snoozed
(`MonitoringEnabled` / `MonitoringSnoozedUntil`), keyed by VmId and re-seeded so
the next scan re-attaches them; default-monitored guests and all scan-derived
state are not exported. Import is additive — an existing host (by name) isn't
duplicated, a colliding webhook token is dropped, and guest intent is only seeded
for guests not already present; a pre-V6.15 backup with no Proxmox section imports
cleanly. The Backup page copy + the documented export contents
(BUSINESS_REQUIREMENTS §10) now mention Proxmox. Round-trip tests cover the
connection + encrypted fields + monitoring flags, the default-guest exclusion,
merge-by-name (no duplicate host), and the no-Proxmox-section back-compat path.

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

---

## V8 — Proxmox LXC provisioning, advanced (clone / snapshot / restore)

> Explicitly deferred out of **V6.13.1 (Create LXC)** so the create phase stayed
> the "minimum viable provision from a template". These build on the exact same
> machinery V6.13.1 shipped — `IProxmoxApiClient`, the task-UPID polling
> (`PollTaskAsync`), the double gate (`Stashboard:AllowProxmox*` master switch +
> per-host opt-in, both off by default), the deterministic pre-API gate failures,
> the `Check now` re-scan for discovery, and a per-action audit entity surfaced on
> the Audit page — so each is a smaller, well-trodden phase rather than new ground.

### Phase V8.0 — Clone & snapshot LXC

**Complexity:** Medium
**Value:** V6.13.1 creates a container from a *template*. The other two everyday
"new container" paths in the Proxmox UI are **clone** (duplicate an existing
guest) and **snapshot** (point-in-time state you can roll back to). Surfacing them
means a user can stamp out a copy of a known-good container or take/restore a
snapshot before a risky change — without dropping to the Proxmox web UI.

**Scope:**

- **Snapshots** on the LXC modal (a new **Snapshots** tab or a section in
  Config): list (`GET /nodes/{node}/lxc/{vmid}/snapshot`), **create**
  (`POST …/snapshot`, name + optional description + `vmstate` for a running
  guest), **rollback** (`POST …/snapshot/{name}/rollback`, double-confirm — it
  discards newer state), and **delete** (`DELETE …/snapshot/{name}`). Each is a
  task UPID → reuse `PollTaskAsync` for real success/failure.
- **Clone** wired to `POST /nodes/{node}/lxc/{vmid}/clone` via a new
  `IProxmoxApiClient.CloneLxcAsync`, reachable from a guest's action row → a
  `LxcCloneModal` that reuses the `LxcCreateModal` styling: new `vmid` (default
  from `/cluster/nextid`), hostname, target **storage**, **full vs linked** clone,
  and (when the source has snapshots) an optional **source snapshot**. Validation
  mirrors create (vmid range + not already on the host).
- **Discovery:** on success trigger the host **Check now** scan so the new/cloned
  guest appears; snapshots refresh their own list.
- **Gating + audit:** same shape as create/destroy — a master switch
  (`Stashboard:AllowProxmoxClone`, **Settings → Clone/snapshot LXC**) + per-host
  opt-in (`ProxmoxConnection.AllowClone`), both off by default; rollback/delete
  also require the per-guest double-confirm. Every clone / snapshot / rollback /
  delete that reaches the host writes a `ProxmoxCloneAuditEntity` row (who / when /
  host / node / vmid / action / target / success / error) on a new Audit tab.

**Out of scope:** cross-node / cross-cluster clone migration; scheduled
snapshots; snapshot trees beyond a flat list.

**Tests:** gate failures return deterministic 403 before any API call; clone vmid
collision is rejected; `CloneLxcAsync` POSTs the expected body to `…/clone`;
snapshot create/rollback/delete hit the right endpoints and poll the task; a
rollback is double-confirmed; each action writes an audit row; a Proxmox rejection
surfaces as a 502 with the host's message.

**Acceptance bar:** with the flag + per-host opt-in enabled, a user can clone a
container (full or linked, optionally from a snapshot) and take / roll back /
delete snapshots entirely from the Stashboard UI, the result appears after the
auto-scan, and every action is audited.

---

### Phase V8.1 — Restore LXC from backup (vzdump)

**Complexity:** Medium–High
**Value:** The disaster-recovery leg: re-create a container from an existing
`vzdump` backup archive (the Proxmox **Restore** button). Complements V6.13.1
create (from template) and V8.0 clone (from a live guest) — together they cover
every "make a container" path in the Proxmox UI.

**Scope:**

- **Backup discovery:** list restorable archives across the node's
  backup-capable storages — the storages whose content advertises `backup`, then
  `GET /nodes/{node}/storage/{storage}/content?content=backup` filtered to
  `vzdump-lxc-*` volumes — via a new `IProxmoxApiClient.ListBackupsAsync`
  (mirrors V6.13.1 `ListTemplatesAsync`), surfaced in a `LxcRestoreModal` dropdown
  with the backup's guest id / timestamp / size.
- **Restore** wired to the same `POST /nodes/{node}/lxc` endpoint with
  `ostemplate=<backup volid>` + `restore=1` (extend `ProxmoxLxcCreate` /
  `CreateLxcAsync` with a `Restore` flag + a `Force` option for restoring **over**
  an existing vmid), target `vmid` (default next-free, or the archive's original),
  rootfs **storage**, and the **unprivileged / start** toggles. Task UPID →
  `PollTaskAsync`.
- **Overwrite guard:** restoring over an existing vmid (`force=1`) is destructive
  (it replaces that container) — gate it behind the **stopped-guest** check + an
  explicit double-confirm naming the target, reusing the V6.13 destroy-dialog
  pattern.
- **Discovery + gating + audit:** `Check now` re-scan on success; a master switch
  (`Stashboard:AllowProxmoxRestore`, **Settings → Restore LXC**) + per-host
  `ProxmoxConnection.AllowRestore`, both off by default; a `ProxmoxRestoreAudit`
  row (who / when / host / node / vmid / backup volid / overwrote? / success /
  error) on a new Audit tab.

**Out of scope:** restoring from **Proxmox Backup Server** datastores
(`pbs:` volumes need PBS auth/namespaces — a later phase); bandwidth/`--bwlimit`
tuning; live-restore.

**Tests:** gate failures return deterministic 403 before any API call;
`ListBackupsAsync` reads only backup-capable storages; restore POSTs
`restore=1` (+ `force=1` only when overwriting) and polls the task; an overwrite
is double-confirmed and refused for a running target; each attempt writes an audit
row; a Proxmox rejection surfaces as a 502 with the host's message.

**Acceptance bar:** with the flag + per-host opt-in enabled, a user can restore a
container from a `vzdump` archive — to a new vmid or (with an explicit
double-confirm) over an existing stopped one — entirely from the Stashboard UI,
the result appears after the auto-scan, and the action is audited.

