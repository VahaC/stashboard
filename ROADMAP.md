# Stashboard — Product Roadmap

> This document is the forward-looking product roadmap — unshipped phases
> only. The historical detail (Docker-update-checker V1–V3, V4 SQLite
> migration, the original V1 implementation checklist) lives in
> [`HISTORY.md`](./HISTORY.md).
>
> **Numbering note:** the legacy section numbers §1–§13 and §15 live in
> HISTORY.md. §14 keeps its heading number here for external references, but its
> shipped V5.x phase detail (V5.0–V5.9) was archived to [`HISTORY.md`](./HISTORY.md) §14,
> the V6.x Proxmox parity & LXC/VM phase detail (V6.0–V6.15.1) to [`HISTORY.md`](./HISTORY.md) §16,
> the V7.x visual Compose editor track (V7.0–V7.9) to [`HISTORY.md`](./HISTORY.md) §17,
> and the V8.x advanced Proxmox provisioning track (V8.0–V8.6) to [`HISTORY.md`](./HISTORY.md) §18,
> once those phases all shipped; only V9.0+ remains in this file.
>
> **Status (shipped milestones, V5+):** ✅ V5.0 (disabled card style + one-click removal) · ✅ V5.0.1 (unlink container from service) · ✅ V5.0.2 (editable SMTP / email settings) · ✅ V5.0.3 (dedicated notifications settings page) · ✅ V5.1 (secure key auto-provisioning, image 5.1.0) · ✅ V5.2 (true Compose-aware recreate, image 5.2.0) · ✅ V5.3 (host terminal, image v5.3.0) · ✅ V5.3.1 (tag-pattern filter correctness + version tags, image 5.3.1) · ✅ V5.3.2 (reliable offline alerts, image 5.3.2) · ✅ V5.4 (Compose project grouping & bulk update, image 5.4.0) · ✅ V5.5 (image cleanup / prune, image 5.5.0) · ✅ V5.6 (health-check tuning page, image 5.6.0) · ✅ V5.7 (container exec, image 5.7.0) · ✅ V5.8 (session audit viewer, image 5.8.0) · ✅ V5.9 (Docker instances page redesign, image 5.9.0) · ✅ V6.0 (Proxmox LXC update monitoring, image 6.0.0) · ✅ V6.1 (Proxmox LXC detail modal + Docker-style cards, image 6.1.0) · ✅ V6.2 (LXC Config tab, image 6.2.0) · ✅ V6.3 (LXC Stats + Tasks tabs, image 6.3.0) · ✅ V6.4 (LXC lifecycle actions + real-time stats, image 6.4.0) · ✅ V6.5 (edit LXC parameters, image 6.5.0) · ✅ V6.6 (browser LXC console / Console tab, image 6.6.0) · ✅ V6.7 (per-LXC update monitoring toggle, image 6.7.0) · ✅ V6.7.1 (Proxmox one-click "Update now", image 6.7.1) · ✅ V6.8 (PVE node health card + node modal, image 6.8.0) · ✅ V6.8.1 (PVE node alerting, image 6.8.1) · ✅ V6.8.2 (PVE node deep telemetry / SSH collectors, image 6.8.2) · ✅ V6.9.0 (edit LXC network interfaces & mount points, image 6.9.0) · ✅ V6.10 (Proxmox page Docker-parity redesign, image 6.10.0) · ✅ V6.11 (bulk LXC monitoring & update operations + audit, image 6.11.0) · ✅ V6.12 (LXC live logs / Logs tab, image 6.12.0) · ✅ V6.13 (destroy / remove LXC, image 6.13.0) · ✅ V6.13.1 (create LXC, image 6.13.1) · ✅ V6.14 (VM / QEMU support, image 6.14.0) · ✅ V6.15 (Proxmox connections in backup/restore, image 6.15.0) · ✅ V7.0 (visual Compose viewer, image 7.0.0) · ✅ V7.1 (edit basic service fields, image 7.1.0) · ✅ V7.1.1 (Compose as a per-project modal, image 7.1.1) · ✅ V7.2 (resource constraints UI, image 7.2.0) · ✅ V7.2.1 (PBS disk/SMART fixes, image 7.2.1) · ✅ V7.3 (top-level resources, image 7.3.0) · ✅ V7.4 (create a new service, image 7.4.0) · ✅ V7.4.1 (create a whole project, image 7.4.1) · ✅ V7.5 (service templates, image 7.5.0) · ✅ V7.6 (diff / dry-run / apply, image 7.6.0) · ✅ V7.7 (dependency graph + linter, image 7.7.0) · ✅ V7.8 (container card icons, image 7.8.0) · ✅ V7.9 (link Proxmox guests to services + Docker↔Proxmox cross-link, image 7.9.0) · ✅ V8.0 (clone & snapshot LXC, image 8.0.0) · ✅ V8.1 (restore LXC from backup, image 8.1.0) · ✅ V8.2 (clone & snapshot VM / QEMU, image 8.2.0) · ✅ V8.3 (restore VM from backup, image 8.3.0) · ✅ V8.4 (create VM / QEMU from scratch, image 8.4.0) · ✅ V8.5 (edit VM / QEMU parameters, image 8.5.0) · ✅ V8.6 (browser VM console / noVNC, image 8.6.0) · ✅ V9.0 (MQTT publisher + HA Discovery, image 9.0.0). Shipped V5.x phase detail now lives in [`HISTORY.md`](./HISTORY.md) §14, the V6.x Proxmox parity & LXC/VM phase detail in [`HISTORY.md`](./HISTORY.md) §16, the V7.x visual Compose editor track in [`HISTORY.md`](./HISTORY.md) §17, and the V8.x advanced Proxmox provisioning track in [`HISTORY.md`](./HISTORY.md) §18; V1–V4 historical detail is also in [`HISTORY.md`](./HISTORY.md). End-user documentation: [`DOCKER_UPDATE_MONITORING_GUIDE.md`](./DOCKER_UPDATE_MONITORING_GUIDE.md).

## 14. Post-V4 backlog (V5+) — deferred Docker features

> **Archived:** every shipped phase that lived under this section has moved to
> [`HISTORY.md`](./HISTORY.md) — the V5.x Docker feature phases (V5.0–V5.9) to
> HISTORY §14, the V6.x Proxmox parity & LXC/VM phases (V6.0–V6.15.1) to
> HISTORY §16, and the V7.x visual Compose editor track (V7.0–V7.9) to
> HISTORY §17. The section number is kept only for external references; the
> active roadmap continues with V8 below.

---

## V7 — Visual Compose editor (Docker)

> **Archived:** every shipped phase that lived under this section has moved to
> [`HISTORY.md`](./HISTORY.md) §17 — the full **V7.0–V7.9** Compose-editor track
> (read-only viewer → comment-preserving YAML editor → resource constraints →
> top-level networks/volumes/secrets/configs → from-scratch service & project
> creation → ~126-recipe template catalogue → diff/dry-run/apply with history +
> audit → dependency graph + linter → container/guest card icons → Proxmox-guest
> ↔ service links and Docker-container ↔ guest cross-links). The heading is kept
> only for external references; the active roadmap continues with V8 below.

---

## V8 — Proxmox LXC/VM provisioning, advanced (clone / snapshot / restore / create / edit / console)

> **Archived:** every shipped phase that lived under this section has moved to
> [`HISTORY.md`](./HISTORY.md) §18 — the full **V8.0–V8.6** advanced Proxmox
> provisioning track (clone & snapshot for LXC then VMs, restore-from-`vzdump` for
> LXC then VMs, from-scratch VM creation, the writable VM config editor, and the
> browser VM console over noVNC). The heading is kept only for external references;
> the active roadmap continues with V9 below.

---

## V9 — Home Assistant integration via MQTT

>Stashboard already collects everything a
> homelab dashboard in Home Assistant would want — per-container running state
> across Docker hosts **and** Proxmox guests, Docker image-update availability, and
> per-service health-check status — but that data lives only behind Stashboard's own
> auth. Publishing it to an MQTT broker via **Home Assistant MQTT Discovery** lets HA
> auto-create the matching entities with zero manual YAML, turning Stashboard into a
> data source for HA dashboards, automations, and the notification channels the user
> already runs there. This phase is **publish-only** (read): HA observes, it does not
> control — control (start/stop/restart from HA via command topics) is explicitly
> deferred so the first cut adds no externally-driven action surface.

### Phase V9.0 — MQTT publisher + HA Discovery (read-only) ✅ Shipped (9.0.0)

**Shipped in 9.0.0.** Exactly as scoped. A DB-backed, runtime-editable MQTT config
(**Settings → Home Assistant**, mirroring the editable-SMTP model: broker host / port /
TLS / username + encrypted password / client id / discovery prefix / entity prefix,
master switch off by default, password never returned) feeds a background
`MqttPublisherService` (`IHostedService`) holding one long-lived MQTTnet connection.
It publishes **retained** HA-Discovery config topics + retained state topics, grouping
entities into **one HA device per real object** linked by `via_device` to a single
**Stashboard** hub. Three `binary_sensor` families ship: container/guest **running**
state (Docker daemons live + Proxmox guests), Docker **image-update** available (from
`DockerWatch.UpdateStatus`), and service **health** (from `WebResource.CurrentStatus`).
Every entity's node id / object_id / unique_id is prefixed with the configured entity
prefix (default `stashboard`, e.g. `binary_sensor.stashboard_jellyfin_running`); changing
the prefix re-publishes under the new ids and clears the old retained topics. A single
availability topic is registered as the broker **Last Will** and referenced by every
entity (all flip to `unavailable` when Stashboard stops / drops). A periodic reconcile
refreshes state on transitions (no spam on unchanged ticks), clears retained topics for
vanished containers/guests/services, and reconnects after a broker drop. The MQTT config
(password encrypted) round-trips through `BackupService`. **Out of scope (unchanged):**
control / command topics, raw resource telemetry, per-entity publish selection, and
running a broker. Covered by `MqttDiscoveryBuilderTests`, `MqttPublishReconcilerTests`,
`MqttPublisherServiceTests`, `MqttSettingsServiceTests`, and `MqttBackupRoundTripTests`.
See the [CHANGELOG](./CHANGELOG.md).

<details><summary>Original phase plan</summary>

**Complexity:** Medium
**Value:** Surfaces three already-collected signals into Home Assistant as
auto-discovered entities, so a user can build HA dashboards/automations over their
whole estate (e.g. "notify when a container goes down", "alert when a Docker update
appears") without Stashboard re-implementing notification breadth itself. Complements
V10.0 (Apprise) rather than competing with it — MQTT exposes *state* to HA; Apprise
fans *events* out to chat services.

**Scope:**

- **App-wide MQTT config** on the **Notifications** (or a new **Integrations**)
  settings page, mirroring the editable-SMTP / Apprise model: broker host, port,
  TLS toggle, username + password, a client id, a configurable **discovery prefix**
  (default `homeassistant`), and a configurable **entity prefix** (default
  `stashboard`) applied to every published sensor — stored DB-backed, the password
  **encrypted at rest** and never returned (presence flag only). Changes apply
  without a restart.
  A master **"MQTT / Home Assistant integration"** switch, off by default.
- **A background publisher** (`MqttPublisherService`, an `IHostedService` holding one
  long-lived broker connection) that publishes **retained** HA Discovery config
  topics (`<prefix>/<component>/<node>/<object>/config`) with a stable `unique_id`,
  grouping entities into **one HA device per real object** — each container / guest /
  node / service is its **own** device with a handful of entities, linked by
  `via_device` to a single **Stashboard** hub device (which also carries the V9.1 estate
  roll-ups), so HA renders a tidy device tree (`Stashboard → host → container`, ~5–10
  entities each) instead of one monolithic 500-entity device — and retained **state**
  topics the entities point at. **Every published entity is prefixed with the
  configured entity prefix** (default `stashboard`) — the discovery node id, the
  `object_id`/`name`, and the `unique_id` all start with `<prefix>_`
  (e.g. `binary_sensor.stashboard_jellyfin_running`) so the entities are trivial to
  spot, group, and filter in Home Assistant and never collide with other MQTT
  producers. Changing the prefix re-publishes discovery under the new ids and clears
  the old retained topics. Three entity families for the MVP:
  1. **Container state** — a `binary_sensor` (running / not-running) per Docker
     container across hosts **and** per Proxmox LXC/VM guest, sourced from the
     existing instance/guest state already shown on the Docker and Proxmox pages.
  2. **Image-update available** — a `binary_sensor` per Docker container, sourced from
     the existing `DockerUpdateChecker` / `DockerUpdateStatus`.
  3. **Service health** — a `binary_sensor` (online / offline) per `WebResource`
     health check, sourced from `ServiceHealthChecker`'s `currentStatus`.
- **Event-driven + periodic publishing:** states are republished when the underlying
  checker/health loop detects a change, plus a periodic full refresh; because state
  topics are retained, HA gets the last value immediately on (re)connect.
- **Availability / LWT:** a single Stashboard availability topic registered as the
  MQTT **Last Will**, referenced by every discovered entity, so all entities flip to
  `unavailable` (not stale-last-value) when Stashboard stops or the connection drops.
- **Lifecycle cleanup:** when a container/guest/service disappears, its retained
  discovery + state topics are cleared so HA removes the entity rather than leaving an
  orphan.
- **Backup/restore:** the MQTT config (password encrypted) is added to
  `BackupService` export/import and its round-trip test in the same change
  (Definition-of-Done §10.3).

**Out of scope:** **control / command topics** (start/stop/restart a container or
guest from HA) — deferred to a follow-up phase because it introduces an
externally-driven action surface needing its own gating/auth/ACL story; raw CPU/RAM/disk
**resource-telemetry** sensors (deliberately off the roadmap — see the note after V9.1;
V9.0 publishes state + update + health only); per-entity
selection of *which* containers/services publish (V9.0 publishes all the user's
monitored entities); running an MQTT broker (the user points Stashboard at their
existing Mosquitto).

**Transport rationale (MQTT, not a HACS integration):** container / guest / service state
is **sparse and event-driven** — one thing goes down, one retained message flips one
entity — which is precisely MQTT's sweet spot, and HA MQTT Discovery needs **zero HA-side
code** (no Python integration to maintain against HA's fast-moving API, no second
language / repo / release cadence). The estate is large, so entities are deliberately
**not** grouped under one device: per-object `device.identifiers` + `via_device` build a
tidy tree of small devices under a single Stashboard hub (see the publisher bullet above),
so the count stays navigable without a monster device. A HACS **polling** integration is
reconsidered only for the deferred **bidirectional-control** surface, where a coordinator +
`services` / `buttons` genuinely win — not for publishing state.

**Tests:** a discovery config message is published (retained) with the expected
`unique_id`, device grouping, `state_topic`, and `availability_topic`; every
published entity's node id / `object_id` / `unique_id` starts with the configured
entity prefix (defaulting to `stashboard_`), and changing the prefix re-publishes
discovery and clears the old retained topics; a state topic
is republished on a status transition and not spammed on an unchanged tick beyond the
refresh cadence; the LWT/availability topic is registered and entities reference it; a
removed container/service clears its retained discovery + state topics; the broker
password is encrypted at rest and the API returns a presence flag only; the
publisher reconnects after a broker drop; the backup round-trip preserves the MQTT
config.

**Acceptance bar:** with a broker configured and the switch on, Home Assistant
auto-discovers one device per host exposing a `binary_sensor` for each container's
running state, each Docker container's update-available, and each monitored service's
online/offline status — values update within one check cycle, all entities go
`unavailable` when Stashboard stops, and no manual HA YAML is required.

</details>

---

### Phase V9.1 — Derived-signal sensors over MQTT

**Complexity:** Medium
**Value:** Publishes the signals Stashboard **computes** (not raw telemetry) into Home
Assistant — pending-update counts, the V6.8.1 node-alert verdicts, backup freshness, and
whole-estate roll-ups — so HA can automate over Stashboard's own *conclusions* rather than
re-deriving them ("notify when any node raises a crit alert", "warn if a VM hasn't been
backed up in 7 days", "remind me on Sunday if updates are pending"). All of it is already
on hand from the existing checkers / evaluators; **pure publish, no new collection**.

**Scope:** builds on the V9.0 publisher verbatim (same broker, discovery + entity
prefixes, per-object **device** tree, retained topics, shared availability / LWT,
lifecycle cleanup):

1. **Update counts** — a numeric `sensor` for pending updates per Docker host, per
   Proxmox node (apt), and per monitored LXC, sourced from `DockerUpdateChecker` and the
   Proxmox per-guest + node counts already on the cards. (V9.0 ships the boolean "update
   available"; this adds the **number**.)
2. **Node alerts** — a `binary_sensor` (`device_class: problem`) per PVE / PBS node
   carrying the V6.8.1 `ProxmoxNodeAlertEvaluator` verdict (`off` = clear, `on` =
   warn / crit), with the per-category breakdown (CPU / memory / storage / thermal /
   SMART / network) and the worst active severity as attributes — Stashboard's own
   alerting logic surfaced to HA.
3. **Backup freshness** — a `sensor` per guest for the **age** (timestamp) of its most
   recent vzdump archive, from `ListBackupsAsync` (ctime), `device_class: timestamp` so HA
   renders "x days ago" and can alert on a stale backup.
4. **Estate roll-ups** — a single **"Stashboard" HA device** with summary `sensor`s:
   containers running / total, guests running / total, services online / total, hosts
   reachable, and total updates pending across everything — the one-glance + single-trigger
   surface.

**Out of scope:** raw resource telemetry (deliberately off the roadmap — see the note
below); control / command topics (still deferred); TLS-certificate expiry (Stashboard
doesn't collect it yet — needs its own collection first).

**Transport rationale:** derived signals are the **most** event-driven of all — an alert
raises, an update count ticks, a backup completes — which is the strongest case for MQTT's
push model over polling (a coordinator would re-fetch unchanged values every cycle). The
estate **roll-up** sensors live on the **Stashboard hub device** (the `via_device` root),
while per-node alert and per-guest backup-age entities attach to their **existing** node /
guest devices from V9.0 — so this phase adds signals without spawning new devices or
inflating any one of them.

**Tests:** an update-count sensor publishes the expected number and refreshes on a check
cycle; the node-alert binary_sensor flips `on` with the category attributes when the
evaluator raises warn / crit and clears when it resolves; a guest's backup-age sensor
reflects the newest archive's ctime; the roll-up device's counts match the underlying
entities; every entity references the shared availability topic and clears its retained
topics when the host / guest / service disappears.

**Acceptance bar:** with the V9.0 integration on, HA additionally exposes per-host /
per-guest pending-update counts, a per-node alert `problem` sensor reflecting the V6.8.1
verdict, a per-guest backup-age sensor, and a single Stashboard roll-up device — all
auto-discovered, updated within a check cycle, and going `unavailable` when Stashboard
stops.

---

> **Note — raw resource telemetry is deliberately *not* on the roadmap.** Publishing
> per-node and per-container metrics (CPU / RAM / swap / storage / temperatures / SMART /
> network / container stats) to Home Assistant was scoped and **dropped**: the value is
> marginal — HA users who want host / container graphs already run the tools built for it
> (Glances, Prometheus + node-exporter / cAdvisor, the official Proxmox integration) —
> while the cost is real: **hundreds of high-churn entities hammering the HA recorder**
> (disk writes), a fiddly per-object device tree, and recorder-exclude documentation just
> to keep it sane. Stashboard publishes **state + derived signals** (V9.0 / V9.1) —
> sparse, event-driven, high-signal — and leaves raw metric graphing to the purpose-built
> tools.

---

## V10 — Core dashboard & monitoring depth

> The V5–V8 line drove Stashboard deep into infrastructure management (Docker
> manager + Compose editor + Proxmox provisioning). V10 turns back to the product's
> original promise — *"one place for every service"* — and closes the gaps that
> separate Stashboard from a real monitoring + dashboard tool: notification
> breadth, uptime history, a public status page, and the login-security surface
> (2FA / API tokens) appropriate for a product that stores credentials and can
> open a shell on the host. These phases are independent of each other and can ship
> in any order; the suggested order front-loads the highest user-visible value.

---

### Phase V10.0 — Notification channels beyond email/Telegram

**Complexity:** Medium
**Value:** Today every notification (service offline, Docker update available,
Proxmox updates pending, node alert) goes out over exactly two channels — email
and Telegram. The most common request from the self-hosted audience is the rest:
Discord, ntfy, Gotify, Slack, and generic webhooks. Rather than hand-roll each
provider, integrate **Apprise** (one HTTP POST → 90+ services), which covers all
of them at once and keeps Stashboard out of the per-provider-API treadmill.

**Scope:**

- A new **Apprise** channel sitting alongside the existing email + Telegram
  channels in the notification services (`ServiceStatusNotificationService`,
  `DockerUpdateNotificationService`, `ProxmoxUpdateNotificationService`,
  `ProxmoxNodeAlertNotificationService`), reusing the established
  **per-channel toggle + per-channel throttle key** pattern (stamped only after a
  successful send, so a transient failure retries on the next tick and one flaky
  channel never drops another).
- **App-wide Apprise config** on the **Notifications** settings page, mirroring the
  editable-SMTP model: an Apprise base URL (self-hosted Apprise API or the
  stateless `/notify` endpoint) plus one or more **Apprise URLs**
  (`discord://`, `ntfy://`, `gotify://`, `slack://`, …) stored DB-backed, the
  secret parts encrypted at rest and never returned (presence flags only).
  Changes apply without a restart.
- **Per-target opt-in** consistent with the existing toggles: a master
  "Apprise notifications" switch on the user/settings level, and the per-watch /
  per-service / per-host toggles gain an Apprise toggle surfaced disabled until the
  app-wide config exists (same UX as the Telegram toggle today).
- **Test button** on the Notifications page that fires a sample notification
  through the configured Apprise targets and reports per-target success/failure.
- **Backup/restore:** the Apprise config + per-target toggles are added to
  `BackupService` export/import and its round-trip test in the same change
  (Definition-of-Done §10.3).

**Out of scope:** a built-in Apprise runtime (users point at their own Apprise
instance or the public stateless endpoint); per-notification routing rules
(e.g. "criticals to Discord, info to ntfy") — V10.0 fans every notification out to
all configured channels, same as email+Telegram do today.

**Tests:** the Apprise sender posts the expected payload to the configured URL;
a per-channel throttle key is stamped only after a successful send and suppresses
a duplicate within the same digest/state; a transient send failure leaves the key
unstamped so the next tick retries; the channel is independent (an Apprise outage
doesn't drop email/Telegram and vice-versa); secrets are encrypted at rest and the
API returns presence flags only; the backup round-trip preserves config + toggles.

**Acceptance bar:** with an Apprise URL configured, a service going Down, a Docker
update appearing, and a Proxmox node alert each deliver to the configured
Discord/ntfy/Gotify target exactly once per state change, with no regression to the
existing email/Telegram delivery.

---

### Phase V10.1 — Uptime history & analytics

**Complexity:** Medium
**Value:** The health-check loop records only the *current* status per URL
(`currentStatus`, `lastCheckedUtc`, `lastResponseTimeMs`, `lastError`). There is no
retained time-series, so the product can't answer "what's my uptime this week?" or
"when did this flap?" — the questions monitoring is for. This phase adds a bounded
history so the Healthcheck tab becomes a real monitor and unblocks the public
status page (V10.2).

**Scope:**

- A new append-only `HealthCheckEventEntity` (per URL: timestamp, status,
  response-time-ms, error) written by the existing `ServiceHealthChecker` on every
  scan **only when the status changes or on a sampled cadence** (avoid one row per
  60 s per URL forever) — store transitions plus periodic response-time samples.
- **Bounded retention** via a background prune (configurable window, default e.g.
  90 days), matching the project's "never let a table grow unbounded" stance.
- **Derived metrics** on the Healthcheck tab: **uptime % over 24 h / 7 d / 30 d**,
  a **response-time sparkline** (reuse the hand-rolled inline-SVG approach from the
  V3.4 stats panel — no chart library), and an **incident log** (Down → Up spans
  with duration).
- New owner-scoped read endpoint(s) under the service surface for the history +
  rolled-up metrics; paginated, newest-first.
- **Backup/restore:** uptime *history* is runtime-derived telemetry and is **not**
  exported (consistent with the existing "runtime status is re-derived, not
  exported" rule); the retention setting is part of the health-check settings that
  already round-trip. Documented in the change.

**Out of scope:** SLA reports / exports (CSV/PDF); alerting *thresholds* on uptime
% (offline alerting already lives in the V5.6 failure-threshold logic); long-term
downsampling/rollup tables — a single bounded window is enough for V10.1.

**Tests:** an event row is written on a status transition and not on an unchanged
sampled tick beyond the configured cadence; uptime % is computed correctly across a
known up/down sequence including the open (still-down) span; the incident log pairs
Down→Up correctly and reports duration; retention prune deletes only rows older than
the window; the metrics endpoint is owner-scoped (404 for a foreign service).

**Acceptance bar:** opening a service's Healthcheck tab shows its 24 h / 7 d / 30 d
uptime, a response-time sparkline, and a list of past incidents with durations, all
derived from retained events that prune at the configured window.

---

### Phase V10.2 — Public status page

**Complexity:** Medium
**Value:** A shareable, read-only status page is the headline feature of the
monitoring tools Stashboard's audience already knows (Uptime Kuma). It turns
Stashboard from a private admin view into something a homelabber can hand to family
or teammates — "is the service up?" without an account. Builds directly on the
V10.1 history.

**Scope:**

- A user creates one or more **status pages**, each a named selection of their own
  services (`StatusPageEntity` + a join to the chosen `WebResource`s), with a
  title, optional description, and a public **slug**.
- A **public, unauthenticated** read endpoint (`GET /api/status/{slug}`) and a
  matching public SPA route that renders each selected service's current status and
  its V10.1 uptime % + recent-history bar — **owner data only ever exposed through an
  explicitly published page**, never the raw service list. The endpoint returns only
  display fields (name, custom display label, status, uptime %, history sparkline) —
  **never** URLs, credentials, notes, categories, tags, Docker/Proxmox internals.
- **Publish toggle** per page (off by default; an unpublished page 404s publicly),
  and an optional per-service "show on status page" display name so the public view
  doesn't leak internal naming.
- **Rate-limited + cacheable** public endpoint (reuse the existing rate-limiting
  middleware) so a shared page can't be used to hammer the instance.
- Management UI: a **Status pages** section (new settings sub-page) to create /
  edit / publish / delete pages and copy the public link.
- **Backup/restore:** status pages + their service selections + publish state are
  added to `BackupService` export/import and its round-trip test in the same change
  (§10.3).

**Out of scope:** custom domains / white-labelling; subscriber email/RSS digests on
the public page; incident write-ups / scheduled-maintenance banners authored by the
owner (a possible later phase) — V10.2 renders live status + history only.

**Tests:** the public endpoint returns only published pages and only the
whitelisted display fields (a test asserts URLs/credentials/notes never appear in
the payload); an unpublished or unknown slug returns 404; the page renders only the
selected services for that owner; the public endpoint requires no auth but is
rate-limited; the backup round-trip preserves pages + selections + publish state.

**Acceptance bar:** a user can select a subset of their services, publish a status
page at a public slug, and an anonymous visitor sees those services' live status and
uptime history — with zero access to any field not explicitly meant for the public
view, and no way to enumerate unpublished pages.

---

### Phase V10.3 — Two-factor authentication (TOTP)

**Complexity:** Medium
**Value:** Stashboard stores credentials (AES-256-GCM at rest), can open an
**SSH shell on the Docker host** (V5.3) and **exec into containers** (V5.7), and can
recreate/destroy containers and LXCs. That is the highest-risk profile in the
product, yet login is a single password. TOTP closes the most conspicuous gap in
the account-security surface and layers cleanly onto the existing `SecurityStamp`
session model.

**Scope:**

- **Enroll / disable TOTP** from the Account page: server generates a secret,
  renders a QR (otpauth URI) + manual key, and verifies a first code before enabling
  (`TwoFactorSecret` stored encrypted at rest, presence flag only on the wire).
- **Login flow** gains a second step: when TOTP is enabled, `POST /api/auth/login`
  returns a short-lived **2FA-pending** challenge instead of tokens, and a new
  `POST /api/auth/login/2fa` exchanges a valid code for the normal
  `AuthResponse` (access + refresh). Wrong codes are rate-limited and the existing
  account-lockout path applies.
- **Recovery codes:** a one-time-displayed set of single-use backup codes
  (hashed at rest), each consumable once in place of a TOTP code; regenerable from
  the Account page.
- **Disabling 2FA** (and consuming/regenerating recovery codes) is a
  security-sensitive mutation → **rotates the SecurityStamp** and invalidates all
  sessions, consistent with the existing password/email-change behaviour.
- **Backup/restore:** the 2FA enabled-flag + encrypted secret + recovery codes are
  added to `BackupService` export/import and the round-trip test (§10.3), so a
  restored account keeps its 2FA.

**Out of scope:** WebAuthn / passkeys / hardware keys (a heavier, separate phase);
SMS or email OTP; enforcing 2FA org-wide (no role/admin system exists — each
account governs its own).

**Tests:** enrollment requires a valid first code before enabling; a TOTP-enabled
login returns a pending challenge and no tokens; a correct code completes login and
issues tokens; a wrong code is rejected and rate-limited; a recovery code works
exactly once and then fails; disabling 2FA rotates the SecurityStamp and kills
existing sessions; the TOTP secret is encrypted at rest and never returned; the
backup round-trip preserves 2FA state.

**Acceptance bar:** a user can enroll an authenticator app, is required to enter a
TOTP (or recovery) code on every subsequent login, can recover with a backup code if
they lose the app, and disabling 2FA signs out all sessions — with the secret never
leaving the server in plaintext.

---

### Phase V10.4 — API tokens (personal access tokens)

**Complexity:** Medium
**Value:** Every API call today is authenticated with a short-lived JWT obtained
through the interactive login/refresh flow — there is no way to script against
Stashboard or wire it into external automation/monitoring without storing a password
and replaying the login. Long-lived, scoped, revocable **personal access tokens**
fill that gap and are the standard self-hosted expectation.

**Scope:**

- **Create / list / revoke** tokens from the Account page: each token has a name, an
  optional expiry, and a **scope** (at minimum read-only vs. full; finer scopes —
  e.g. services-only — optional). The secret is shown **once** on creation and stored
  **hashed** at rest (`PersonalAccessTokenEntity`: name, hash, scope, expiry,
  `lastUsedUtc`, created/revoked timestamps).
- **Authentication:** the API accepts a PAT (e.g. `Authorization: Bearer
  sb_pat_…`, distinguished from a JWT by prefix) on the same endpoints, resolving to
  the owning user and enforcing the token's scope. A read-only token is rejected on
  mutating endpoints with 403.
- **Revocation is immediate** (a revoked or expired token fails the next request);
  `lastUsedUtc` is stamped for auditability. Tokens are **independent of the
  SecurityStamp** so rotating sessions (password change, logout-all) does not have to
  kill automation — but disabling/deleting the account removes them.
- **Excluded from high-risk surfaces by construction:** PATs do **not** grant the
  host-terminal / container-exec WebSocket (those keep their single-use ticket flow
  and interactive-session gating); a PAT is for the REST data surface only.
- **Backup/restore:** tokens are **not** exported (they're bearer secrets; export
  would either leak them or be useless) — documented explicitly in the change, the
  same way other non-exported runtime/secret material is.

**Out of scope:** OAuth client-credentials / third-party app authorization;
per-endpoint ACLs beyond the coarse scopes; token usage analytics beyond
`lastUsedUtc`.

**Tests:** a created token authenticates subsequent requests as its owner; a
read-only token is accepted on GETs and 403'd on mutations; an expired or revoked
token is rejected immediately; `lastUsedUtc` is stamped on use; a PAT is refused on
the host-shell/exec ticket endpoints; the secret is stored hashed and shown only
once; account deletion removes the user's tokens.

**Acceptance bar:** a user can mint a scoped, optionally-expiring token, use it to
drive the REST API from a script, see when it was last used, and revoke it with
immediate effect — without it ever granting host-shell/exec access.

---

### Phase V10.5 — OIDC / SSO login (optional)

**Complexity:** Medium–High
**Value:** Authentik / Authelia / Keycloak are increasingly the front door of a
homelab, and users running one want Stashboard behind it rather than maintaining a
separate password. This is the heaviest account-surface phase and is explicitly
**optional / lower-priority** than V10.3–V10.4 — sequence it last in the security
group.

**Scope:**

- **OIDC Authorization Code + PKCE** login against a configurable provider
  (issuer / client id / client secret / scopes), config stored DB-backed and
  editable in the UI like SMTP (secret encrypted at rest), surfaced to the frontend
  via the existing `GET /api/features` so the login page can show a
  "Sign in with …" button only when configured.
- **Account linking:** an OIDC identity maps to a Stashboard user by verified email;
  first OIDC login can **provision** a new account (behind a "allow OIDC
  registration" toggle) or require a pre-existing one. A linked account can still use
  its password unless the owner disables local login.
- **Coexists with local auth + 2FA:** OIDC is additive; local login (and V10.3 TOTP)
  keep working when OIDC is off or for non-OIDC accounts. The resulting session is the
  same `AuthResponse` (access + refresh) the rest of the app already uses, so nothing
  downstream changes.
- **Backup/restore:** OIDC provider config (secret encrypted) and the per-user link
  flag are added to `BackupService` export/import and the round-trip test (§10.3).

**Out of scope:** SAML; SCIM user provisioning/de-provisioning; group/role mapping
(no role system exists); enforcing OIDC-only org-wide.

**Tests:** the auth-code+PKCE exchange issues a normal `AuthResponse`; an OIDC
identity links to an existing user by verified email; provisioning is gated by the
registration toggle; local login + 2FA still work with OIDC enabled; the client
secret is encrypted at rest and never returned; the feature flag reflects config
presence; the backup round-trip preserves provider config + link flags.

**Acceptance bar:** with an OIDC provider configured, a user can sign in through it
and land in a normal authenticated session, optionally have an account provisioned on
first login, and local password + 2FA login continue to work unchanged.

---

### Phase V10.6 — PWA, web push & card ordering

**Complexity:** Medium
**Value:** The dashboard is already responsive and phone-tested, but it isn't an
*installable* app and notifications never reach the device that's actually in the
user's hand. Making Stashboard an installable PWA with web-push turns it into a
daily home-screen surface, and manual card ordering lets users lay the dashboard out
the way they think about their services rather than alphabetically.

**Scope:**

- **PWA**: web app manifest + service worker (installable, app-icon, standalone
  display, offline shell for the login/dashboard chrome). No offline data sync —
  the shell loads, data fetches when online.
- **Web push** as another V10.0-style notification channel: the browser subscribes
  (VAPID keys auto-generated + persisted like the existing encryption/JWT secrets),
  subscriptions stored per user/device, and the same status/update/alert
  notifications can fan out to push. Reuses the per-channel toggle + throttle model.
- **Manual card ordering**: a per-user explicit sort mode (`Custom`) added to the
  existing `sortMode` preference, with drag-and-drop on the dashboard persisting an
  order index per service. Coexists with the current name/status/last-checked sorts
  and the category grouping.
- **Backup/restore:** push subscriptions are
  device-bound bearer material and are **not** exported (documented); the custom
  **order** is a user preference and is added to the settings round-trip (§10.3).

**Out of scope:** native app-store apps; offline data caching / write queue;
reordering within Docker/Proxmox pages (dashboard services only).

**Tests:** the manifest + service worker register and the app reports installable in
a headless check; a push subscription is stored and a notification delivers to it,
honouring the per-channel toggle/throttle; an unsubscribed/expired endpoint is pruned
on send failure; custom order persists per user and survives reload; VAPID keys are
auto-generated once and reused on restart; the settings backup round-trip preserves
custom order.

**Acceptance bar:** a user can install Stashboard to their home screen, receive a
push when a service goes Down, and drag their service cards into a custom order that
persists across devices.

---

### Phase V10.7 — Command palette & global search

**Complexity:** Low–Medium
**Value:** The product now spans many pages and entity types (services, Docker
containers across hosts, Compose projects, Proxmox LXCs/VMs, settings sub-pages). A
`Ctrl/⌘-K` command palette that searches across all of them and jumps straight to the
right modal/page is a large navigation-speed win at the scale the UI has reached, and
is purely additive.

**Scope:**

- A global **command palette** (front-end, opened with `Ctrl/⌘-K` and a header
  button) that fuzzy-searches across the user's **services, Docker containers,
  Compose projects, and Proxmox guests**, plus a fixed list of **navigation
  actions** (go to Settings sub-pages, Audit, Notifications, etc.).
- Results deep-link to the existing modals/routes (reuse the deep-link support the
  dashboard already has for service modals; extend the same pattern to the Docker
  container modal and Proxmox guest modal).
- **Backed by existing list endpoints** where possible (services, instances,
  guests are already fetched/cached client-side) so V10.7 needs **no new backend
  surface** in the common case; add a lightweight combined search endpoint only if
  client-side filtering proves insufficient at scale.

**Out of scope:** server-side full-text search infrastructure; searching inside logs
/ audit history; command *actions* that mutate (start/stop a container from the
palette) — V10.7 is navigation/search only, mutations stay on their guarded surfaces.

**Tests:** the palette opens on the shortcut; a query matches services / containers /
guests / nav actions and ranks sensibly; selecting a result navigates to the correct
modal/route with the right entity in focus; results are owner-scoped (a query never
surfaces another user's entities).

**Acceptance bar:** pressing `Ctrl/⌘-K` anywhere opens a search box that finds any of
the user's services, containers, Compose projects, or Proxmox guests (and the main
navigation targets) and jumps straight to it.

