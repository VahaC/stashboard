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
> **Status (shipped milestones, V5+):** ✅ V5.0 (disabled card style + one-click removal) · ✅ V5.0.1 (unlink container from service) · ✅ V5.0.2 (editable SMTP / email settings) · ✅ V5.0.3 (dedicated notifications settings page) · ✅ V5.1 (secure key auto-provisioning, image 5.1.0) · ✅ V5.2 (true Compose-aware recreate, image 5.2.0) · ✅ V5.3 (host terminal, image v5.3.0) · ✅ V5.3.1 (tag-pattern filter correctness + version tags, image 5.3.1) · ✅ V5.3.2 (reliable offline alerts, image 5.3.2) · ✅ V5.4 (Compose project grouping & bulk update, image 5.4.0) · ✅ V5.5 (image cleanup / prune, image 5.5.0) · ✅ V5.6 (health-check tuning page, image 5.6.0) · ✅ V5.7 (container exec, image 5.7.0) · ✅ V5.8 (session audit viewer, image 5.8.0) · ✅ V5.9 (Docker instances page redesign, image 5.9.0) · ✅ V6.0 (Proxmox LXC update monitoring, image 6.0.0) · ✅ V6.1 (Proxmox LXC detail modal + Docker-style cards, image 6.1.0) · ✅ V6.2 (LXC Config tab, image 6.2.0) · ✅ V6.3 (LXC Stats + Tasks tabs, image 6.3.0) · ✅ V6.4 (LXC lifecycle actions + real-time stats, image 6.4.0) · ✅ V6.5 (edit LXC parameters, image 6.5.0) · ✅ V6.6 (browser LXC console / Console tab, image 6.6.0) · ✅ V6.7 (per-LXC update monitoring toggle, image 6.7.0) · ✅ V6.7.1 (Proxmox one-click "Update now", image 6.7.1) · ✅ V6.8 (PVE node health card + node modal, image 6.8.0) · ✅ V6.8.1 (PVE node alerting, image 6.8.1) · ✅ V6.8.2 (PVE node deep telemetry / SSH collectors, image 6.8.2) · ✅ V6.9.0 (edit LXC network interfaces & mount points, image 6.9.0) · ✅ V6.10 (Proxmox page Docker-parity redesign, image 6.10.0) · ✅ V6.11 (bulk LXC monitoring & update operations + audit, image 6.11.0) · ✅ V6.12 (LXC live logs / Logs tab, image 6.12.0) · ✅ V6.13 (destroy / remove LXC, image 6.13.0) · ✅ V6.13.1 (create LXC, image 6.13.1) · ✅ V6.14 (VM / QEMU support, image 6.14.0) · ✅ V6.15 (Proxmox connections in backup/restore, image 6.15.0) · ✅ V7.0 (visual Compose viewer, image 7.0.0) · ✅ V7.1 (edit basic service fields, image 7.1.0) · ✅ V7.1.1 (Compose as a per-project modal, image 7.1.1) · ✅ V7.2 (resource constraints UI, image 7.2.0) · ✅ V7.2.1 (PBS disk/SMART fixes, image 7.2.1) · ✅ V7.3 (top-level resources, image 7.3.0) · ✅ V7.4 (create a new service, image 7.4.0) · ✅ V7.4.1 (create a whole project, image 7.4.1) · ✅ V7.5 (service templates, image 7.5.0) · ✅ V7.6 (diff / dry-run / apply, image 7.6.0) · ✅ V7.7 (dependency graph + linter, image 7.7.0) · ✅ V7.8 (container card icons, image 7.8.0) · ✅ V7.9 (link Proxmox guests to services + Docker↔Proxmox cross-link, image 7.9.0) · ✅ V8.0 (clone & snapshot LXC, image 8.0.0) · ✅ V8.1 (restore LXC from backup, image 8.1.0) · ✅ V8.2 (clone & snapshot VM / QEMU, image 8.2.0) · ✅ V8.3 (restore VM from backup, image 8.3.0) · ✅ V8.4 (create VM / QEMU from scratch, image 8.4.0) · ✅ V8.5 (edit VM / QEMU parameters, image 8.5.0) · ✅ V8.6 (browser VM console / noVNC, image 8.6.0) · ✅ V9.0 (MQTT publisher + HA Discovery, image 9.0.0) · ✅ V9.1 (derived-signal MQTT sensors, image 9.1.0) · ✅ V9.2 (self-update via detached helper, image 9.2.0) · ✅ V10.0 (notification channels beyond email/Telegram — Apprise, image 10.0.0) · ✅ V10.1 (uptime history & analytics, image 10.1.0). Shipped V5.x phase detail now lives in [`HISTORY.md`](./HISTORY.md) §14, the V6.x Proxmox parity & LXC/VM phase detail in [`HISTORY.md`](./HISTORY.md) §16, the V7.x visual Compose editor track in [`HISTORY.md`](./HISTORY.md) §17, the V8.x advanced Proxmox provisioning track in [`HISTORY.md`](./HISTORY.md) §18, and the V9.x Home Assistant / MQTT + self-update track in [`HISTORY.md`](./HISTORY.md) §19; V1–V4 historical detail is also in [`HISTORY.md`](./HISTORY.md). End-user documentation: [`DOCKER_UPDATE_MONITORING_GUIDE.md`](./DOCKER_UPDATE_MONITORING_GUIDE.md).

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

> **Archived:** every shipped phase that lived under this section has moved to
> [`HISTORY.md`](./HISTORY.md) §19 — the full **V9.0–V9.2** Home Assistant / MQTT +
> self-update track: the read-only MQTT publisher with HA MQTT-Discovery (V9.0), the
> derived-signal sensors built on it (V9.1), and the detached-helper self-update that
> lets Stashboard update its own container (V9.2). The heading is kept only for external
> references; the active roadmap continues with V10 below.

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

### ✅ Shipped (10.0.0) Phase V10.0 — Notification channels beyond email/Telegram

**Shipped in 10.0.0 — exactly as scoped.** A new **Apprise** channel sits alongside the
existing email + Telegram channels in all four notification services
(`ServiceStatusNotificationService`, `DockerUpdateNotificationService`,
`ProxmoxUpdateNotificationService`, `ProxmoxNodeAlertNotificationService`), reusing the
per-channel toggle + per-channel throttle-key pattern (the digest/signature key is stamped
only after a successful send, so a transient Apprise outage retries on the next tick and
never drops email/Telegram — and vice-versa). App-wide config lives on the **Notifications**
settings page, mirroring the editable-SMTP model: a master switch, an Apprise base URL (the
operator's own Apprise API or the stateless `/notify` endpoint), and one Apprise URL per line
(`discord://`, `ntfy://`, `gotify://`, `slack://`, …) stored DB-backed with the URLs
**encrypted at rest** and never returned (presence flag + non-secret schemes only); changes
apply without a restart. A **Send test** button fires a sample notification through each
configured target and reports per-target success/failure. Per-watch / per-host **Apprise**
toggles surface disabled until the channel is configured (same UX as the Telegram toggle);
service-offline alerts fan out whenever the per-service offline-notification switch is on.
The Apprise config (URLs encrypted) + the per-target toggles round-trip through
`BackupService`. Covered by `AppriseSenderTests`, `AppriseSettingsServiceTests`,
`AppriseBackupRoundTripTests`, and the per-channel cases added to the notification-service
tests. **Out of scope (unchanged):** a built-in Apprise runtime, and per-notification routing
rules — V10.0 fans every notification out to all configured channels. See the
[CHANGELOG](./CHANGELOG.md).

<details><summary>Original phase plan</summary>

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

</details>

---

### ✅ Shipped (10.1.0) Phase V10.1 — Uptime history & analytics

**Shipped in 10.1.0.** The health-check loop now retains a bounded, append-only time-series so the
Healthcheck tab is a real monitor. A new `HealthCheckEventEntity` (per URL: timestamp, status,
response-time-ms, error, keyed by a lean autoincrement id) is written by the scan **and** a manual
"Check now" via a shared `IHealthCheckEventRecorder` — **only on a status transition or past a
sampled cadence** (default 15 min), so a steady service never writes one row per tick. A slow
background `HealthCheckHistoryPruneBackgroundService` (every 6 h) drops rows older than the
configurable retention window (default **90 days**); both knobs live on the **Settings → Health
checks** page alongside the V5.6 tuning and seed from `STASHBOARD_HealthCheck__*`. Per the user's
call, history is kept **per URL — main and additional tracked separately**, each with its own
uptime, sparkline and incidents. A dedicated **Uptime** tab in the service modal
(`UptimeHistorySection`) shows **uptime % over 24 h / 7 d / 30 d**, a **response-time sparkline** (hand-rolled inline SVG, reusing the V3.4
stats-panel approach — no chart library), and an **incident log** (Down→Up spans with durations,
the open span flagged *ongoing*), all from `GET /api/services/{id}/health/metrics`; a paginated,
newest-first `GET /api/services/{id}/health/events` exposes the raw rows. Both endpoints are
owner-scoped (404 for a foreign service). The metrics are pure-computed by
`HealthCheckMetricsCalculator` (Up/NeedsAttention = up, Down = down, Unknown excluded). **Backup:**
the history is runtime-derived telemetry and is **not** exported (consistent with the
"runtime status is re-derived, not exported" rule); the retention/sample knobs are app-wide
health-check settings (DB-backed, env-seeded) and, like the rest of the health-check tuning, are
not part of the per-user backup export. Covered by `HealthCheckMetricsCalculatorTests`,
`HealthCheckEventRecorderTests`, `HealthCheckHistoryPruneServiceTests`, `HealthHistoryEndpointTests`,
and a new transition case in `HealthCheckBackgroundServiceTests`. See the [CHANGELOG](./CHANGELOG.md).

<details><summary>Original phase plan</summary>

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

</details>

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

---

### Phase V10.8 — Additional monitor types (TCP / ping / DNS / keyword)

**Complexity:** Medium
**Value:** Today every health check is HTTP(S) only (`ServiceHealthChecker` over
`WebResourceEntity.Url`, `HealthCheckMethod` = GET/HEAD). That misses the monitors the
self-hosted audience expects from Uptime Kuma: a **TCP port** probe (Jellyfin `:32400`
accepts connections even when no HTTP route answers), an **ICMP ping** (is the box alive at
all), a **DNS resolve** (did the local resolver come back up), and a **keyword / body
assertion** on an HTTP response (catches the "200 OK but it's the reverse-proxy placeholder"
failure). This is purely additive — it extends the existing checker, retry/threshold logic,
status model, and notification path rather than building a parallel one.

**Scope:**

- Extend `HealthCheckMethod` (or add a sibling `MonitorType`) with **`TcpPort`, `Ping`,
  `Dns`, `HttpKeyword`**, defaulting existing rows to today's HTTP behaviour (migration
  backfills, no UX change for current services).
- New optional fields on `WebResourceEntity`, only relevant per type: **target host + port**
  (TCP), **hostname / record type** (DNS), and an **expected keyword** + optional **JSON
  path/value** (HTTP keyword). The Healthcheck tab gains a **"Monitor type"** selector that
  shows only the fields the chosen type needs, each explained inline (consistent with the
  V5.6 Health-checks page tone).
- `ServiceHealthChecker` **dispatches per type** to a small `IMonitor` per kind; all of them
  resolve to the same `ServiceStatus` (Up/Down) + `lastResponseTimeMs` + `lastError`, so the
  dashboard card, the V5.3.2 failure-threshold/offline-alert logic, the V5.6 retry settings,
  and the MQTT **service-health** `binary_sensor` (V9.0) all keep working with **zero**
  downstream change.
- Reuse the existing in-probe retry + consecutive-failure threshold from V5.6 for every type
  (a flaky DNS lookup retries the same way a flaky HTTP probe does).
- **Backup/restore:** the new per-service monitor fields are added to `BackupService`
  export/import and its round-trip test in the same change (Definition-of-Done §10.3).

**Out of scope:** SNMP / IPMI monitors; **push / heartbeat** monitors (the "service calls
Stashboard to say it's alive" inversion — a separate transport); game-server /
protocol-specific probes; multi-step transaction checks. V10.8 adds connection-level +
content-level checks only.

**Tests:** a TCP monitor reports Up when the port accepts and Down when refused/timed-out; a
Ping monitor reflects host reachability; a DNS monitor is Down on NXDOMAIN/timeout and Up on
a successful resolve; an HTTP-keyword monitor is Down when the body is missing the keyword
despite a 2xx and Up when present; each type honours the V5.6 retry + failure-threshold
settings; an offline-alert fires once per transition for every type; the backup round-trip
preserves the monitor type + per-type fields.

**Acceptance bar:** a user can add a service whose health is judged by a raw TCP port, an
ICMP ping, a DNS lookup, or a keyword in the HTTP body — and that service shows a live
Up/Down dot, response time, offline alerts, and an MQTT health sensor exactly like an HTTP
service does today.

---

### Phase V10.9 — TLS certificate-expiry monitoring

**Complexity:** Low–Medium
**Value:** The health-check loop already makes the HTTPS connection on every probe but
throws the certificate away. Capturing the leaf cert's expiry turns "is it up?" into "is it
up **and** not about to break in three days?" — the classic self-hosted footgun (a lapsed
Let's Encrypt renewal silently 502s everything behind the proxy). This also **unblocks the
TLS sensor the V9.1 note deferred** ("Stashboard doesn't collect it yet — needs its own
collection first"): once collected, it feeds Home Assistant for free.

**Scope:**

- During an HTTPS probe, `ServiceHealthChecker` captures the leaf certificate's **`NotAfter`**
  (and subject/issuer for display) and persists **`certNotAfterUtc` / `certDaysRemaining`** on
  `WebResourceEntity` (runtime-derived, refreshed each scan; cheap — it's already on the wire).
- **Warn / crit thresholds** (default e.g. 14 d / 3 d) added to the V5.6 **Settings → Health
  checks** page, DB-backed, applied on the next scan; `STASHBOARD_HealthCheck__*` seeds the
  defaults on first run like the rest.
- **Surfacing:** an expiry badge on the service card + Healthcheck tab ("expires in 9 days"),
  and a **certificate-expiry notification** through the existing channels, reusing the
  established **per-channel toggle + throttle-key stamped only after a successful send**
  pattern (so it fires once per threshold crossing per service, not every tick).
- **Self-signed / untrusted** certs are reported with their expiry but do **not** by
  themselves flip the service Down (TLS-validation failure already surfaces through the normal
  probe error); a self-signed cert is a display state, not an outage.
- **MQTT (V9.1 follow-through):** a per-service **`timestamp` sensor** (cert expiry) flows
  through the existing `MqttEntityStateProvider → MqttDiscoveryBuilder → MqttPublishReconciler`
  pipeline on the service's existing device, retained, under the shared availability/LWT,
  cleared when the service disappears — so HA can render "x days" and automate on it.
- **Backup/restore:** the **thresholds** round-trip with the health-check settings; the
  captured cert data is runtime-derived and **not** exported (consistent with the "runtime
  status is re-derived, not exported" rule). Documented in the change.

**Out of scope:** full chain / intermediate validation reporting; certificate **pinning**;
raw-TCP TLS with STARTTLS (SMTP/IMAP cert checks) — V10.9 reads the cert presented on the
existing HTTPS health probe only; revocation (OCSP/CRL) checking.

**Tests:** a probe against an HTTPS endpoint records `NotAfter` and computes days-remaining
correctly; crossing the warn then crit threshold fires exactly one notification per crossing
per service and re-arms only on the next crossing (throttle key stamped on success); a
self-signed cert records expiry without forcing Down; the MQTT timestamp sensor publishes the
expiry and clears its retained topics when the service is removed; the thresholds survive the
settings backup round-trip; a non-HTTPS service has no cert fields.

**Acceptance bar:** with a service checked over HTTPS, Stashboard shows how long its
certificate is valid, warns (card badge + notification + HA sensor) before it expires at a
configurable threshold, and never treats a deliberately self-signed cert as an outage.

---

### Phase V10.10 — Prometheus `/metrics` exporter

**Complexity:** Low–Medium
**Value:** Raw resource telemetry was deliberately kept **out** of the HA MQTT path (it would
hammer the HA recorder — see the V9.1 note). But the Grafana/Prometheus crowd wants exactly
that graphing, and the signals Stashboard already computes are a perfect scrape target. One
read-only `/metrics` endpoint hands that audience their dashboards **without** Stashboard
owning any charting and without bloating Home Assistant — a single low-churn surface instead
of hundreds of HA entities.

**Scope:**

- A new endpoint emitting **Prometheus text exposition format**, **off by default**, enabled
  at **Settings → Integrations/Metrics**, and protected by a dedicated **scrape token** (the
  endpoint exposes owner-scoped, sensitive estate state, so it is never anonymous — the token
  maps to the owning user, mirroring the encrypted-secret settings model; the token is shown
  once and stored hashed).
- Reuses the **already-computed** snapshot behind the MQTT publisher (`MqttEntityStateProvider`
  / the same checkers/evaluators) — **no new collection**. Gauges, labelled per object:
  - `stashboard_service_up{service}` / `stashboard_service_response_ms{service}` /
    `stashboard_cert_days_remaining{service}` (the last from V10.9 if shipped),
  - `stashboard_container_running{host,container}` /
    `stashboard_container_update_available{host,container}`,
  - `stashboard_docker_updates_pending{host}` / `stashboard_proxmox_updates_pending{node,guest}`,
  - `stashboard_proxmox_node_alert{node}` (the V6.8.1 `ProxmoxNodeAlertEvaluator` verdict) +
    per-category labels,
  - `stashboard_guest_running{node,guest}` / `stashboard_backup_age_seconds{guest}`,
  - and the estate **roll-ups** (`stashboard_containers_running_total`, …
    `stashboard_updates_pending_total`).
- Standard `stashboard_build_info{version}` + scrape-self metrics; values are point-in-time
  from the latest checker pass (the exporter never triggers a fresh expensive scan — it reads
  the cached state, same as MQTT).
- **Backup/restore:** the enabled flag + scrape-token (hashed) follow the existing settings
  round-trip rules; the token, being bearer material, is **not** re-exported in plaintext
  (documented, consistent with V10.4 PATs).

**Out of scope:** OpenMetrics exemplars / histograms / per-second response-time series
(Prometheus does the time-series — Stashboard only exposes the current value); a Pushgateway
client; raw per-container CPU/RAM gauges streamed at high frequency (the live-stats stream
stays UI-only — the exporter publishes the same **sparse, derived** signals the MQTT path
does, not the V9.1-rejected raw telemetry).

**Tests:** the endpoint returns valid Prometheus format with the expected gauges and labels
for a known estate; it is 401/403 without a valid scrape token and 200 with one; values match
the underlying checker state (a down service reads `0`, a pending update increments the
count); the endpoint is owner-scoped (a token never exposes another user's objects); enabling/
disabling the exporter and rotating the token take effect immediately; the settings backup
round-trip preserves the enabled flag.

**Acceptance bar:** a user flips the exporter on, points Prometheus at `/metrics` with a
scrape token, and gets per-service / per-container / per-node / per-guest gauges plus estate
roll-ups — graphable in Grafana with zero new collection load and zero exposure of any object
the token's owner doesn't own.

---

## V11 — Proxmox Backup Server (PBS) backup monitoring

> PBS is currently handled only as a **generic node** (V6.8.3): CPU/RAM/disk/uptime,
> RRD telemetry, disks+SMART, network, apt updates, node alerts, and its datastores
> shown as a storage-pool analogue via `/status/datastore-usage`. Its actual purpose —
> **backups** — is invisible: `ListBackupsAsync` / `ListStorageContentAsync` both
> deliberately **skip `pbs:`** ("needs its own auth/namespace surface — out of scope").
> V11 closes that gap, turning a PBS connection from "another node with a full disk"
> into a real backup monitor, and fixing the V9.1 backup-age signal for guests that
> back up to PBS rather than to a PVE-side vzdump store. Phases build on each other;
> V11.0 is the foundation the rest read from.

---

### Phase V11.0 — Datastore snapshot browser + verify status

**Complexity:** Medium
**Value:** The core PBS screen Stashboard has never had — *what is actually backed up,
when, and is it verified*. Today a PBS card shows only datastore fullness; you can't see
a single snapshot. This phase reads the PBS backup catalogue and surfaces it, including
each snapshot's **verify state**, which is the whole point of PBS (a `failed`/missing
verification is a silently corrupt backup).

**Scope:**

- New `IProxmoxApiClient` reads, branched on `ServerType.Pbs`, against the PBS admin
  surface: `GET /admin/datastore/{store}/groups` → `GET /admin/datastore/{store}/snapshots`
  — backup **groups** (`vm`/`ct`/`host` + backup-id) → **snapshots** (backup-time, size,
  owner, comment, `protected`, files, and the **verify state** `ok`/`failed`/none). These
  are new methods, not a relaxation of the `ListBackupsAsync` skip (that one stays
  vzdump-only for PVE storage).
- The PBS **node modal** (V6.8) gains a **Backups** tab: a datastore picker → grouped,
  newest-first snapshot list with type/id/time/size/owner/verify badge/`protected` flag,
  paginated. Per-datastore roll-up header: total snapshots, **unverified count**,
  **failed-verify count**, newest-backup age.
- Read-only, owner-scoped endpoints under the existing Proxmox connection surface; values
  are live/best-effort like the existing PBS datastore + apt reads (no new persistence
  beyond what later phases need).
- **Backup/restore:** nothing new to export — this is live-read telemetry, consistent with
  the "runtime status is re-derived, not exported" rule. Documented in the change.

**Out of scope:** restoring **from** a PBS snapshot into PVE (heavier, separate — V8 restore
is vzdump-file based); PBS datastore namespaces beyond the default unless trivially listable;
editing/deleting snapshots (V11.4 covers actions, and delete stays out by default).

**Tests:** the PBS client lists groups and snapshots with the correct `PBSAPIToken` auth
scheme; a snapshot's verify state maps to ok/failed/unverified; the per-datastore roll-up
counts (total / unverified / failed / newest-age) are computed correctly over a known
catalogue; the endpoint is owner-scoped and returns empty for a non-PBS connection; a PVE
connection's `ListBackupsAsync` behaviour is unchanged.

**Acceptance bar:** opening a PBS node's **Backups** tab lists every datastore's backup
snapshots with size, age, owner, protected flag, and a verify badge, plus a per-datastore
summary of how many backups are unverified or failed verification.

---

### Phase V11.1 — Backup-to-PBS cross-link on PVE guests (truthful backup-age)

**Complexity:** Medium
**Value:** V9.1 derives a guest's backup age from the newest **vzdump file** on a PVE
storage — but for guests that back up **to a PBS datastore** (the common setup) that source
is empty/stale, so the backup-age signal is wrong. PBS holds the truth. Linking a PVE guest
to its PBS snapshots makes "when was this last backed up — and was it verified?" correct on
the guest's own card, mirroring the V7.9 Docker↔Proxmox cross-link.

**Scope:**

- A guest's PBS backups are matched by backup-id (`vm/<vmid>` / `ct/<vmid>`) across the PBS
  connections the user owns — **auto-correlated** where the vmid + type match, with an
  explicit **link override** (like V7.9) when auto-match is ambiguous (multiple PBS targets)
  or the ids differ.
- On the **PVE guest card / modal**, surface **latest PBS snapshot time + verify state** (and
  a small "N snapshots on `<datastore>`" line). The V9.1 backup-age signal **prefers the PBS
  snapshot time** when a PBS link exists, falling back to the vzdump ctime otherwise — so
  backup-age is finally truthful for PBS-backed guests.
- A tiny link entity (mirroring `WebResourceProxmoxGuestLink` / `ContainerProxmoxLink`)
  persists explicit overrides; auto-matches are computed at read time and need no row.
- **Backup/restore:** explicit PBS link overrides are added to `BackupService` export/import
  and the round-trip test (§10.3); auto-matched links are derived and not exported.

**Out of scope:** cross-cluster identity reconciliation beyond vmid/type+datastore; linking
Docker containers to PBS (PBS backs Proxmox guests, not containers); historical
backup-success charts (V11.3 surfaces the job history instead).

**Tests:** a guest auto-correlates to its PBS snapshots by vmid+type; an explicit override
wins over auto-match and round-trips through backup; the V9.1 backup-age uses the PBS snapshot
time when a link exists and falls back to vzdump otherwise; the guest card shows the latest
snapshot's verify state; a guest with no PBS backup shows the existing vzdump-derived value
unchanged.

**Acceptance bar:** a PVE guest that backs up to PBS shows its real last-backup time and
verify state on its own card, and its MQTT backup-age sensor reflects the PBS snapshot rather
than reading empty.

---

### Phase V11.2 — GC / prune health + datastore alerting

**Complexity:** Medium
**Value:** A PBS datastore that stops running **garbage collection** silently stops
reclaiming space and fills up; a **failed verify** means corrupt backups; a datastore with
**no recent successful backup** means a broken backup job. These are exactly the verdicts the
V6.8.1 alert engine is built for — this phase teaches it PBS-specific categories so PBS
problems page you the same way a hot CPU or a SMART-failing disk already does.

**Scope:**

- Read **GC status** (`GET /admin/datastore/{store}/gc` / the GC task) — last run time,
  reclaimed bytes, success/failure — and the newest **prune** outcome per datastore.
- Extend `ProxmoxAlertCategory` (currently `Cpu|Memory|Storage|Thermal|Smart|Network`) with
  PBS-only categories — **`VerifyFailed`**, **`GcStale`**, **`BackupStale`** — evaluated by
  the existing `ProxmoxNodeAlertEvaluator` only for PBS connections, with thresholds on
  `ProxmoxNodeAlertSettingsEntity` (e.g. GC-stale-after-days, backup-stale-after-days),
  defaulting sensibly and opt-out-able via the existing `CategoryMask` (so a user can mute a
  category without muting the node).
- Notifications reuse the existing node-alert path (`ProxmoxNodeAlertNotificationService`,
  per-channel toggle + signature/throttle), so a PBS verdict fires email/Telegram exactly like
  a PVE node alert does today — no new notification surface.
- **Backup/restore:** the new PBS threshold fields + category mask round-trip with the
  existing node-alert settings export/import and its test (§10.3).

**Out of scope:** alerting on individual snapshot age (the `BackupStale` verdict is per
datastore/group, not per snapshot); configurable GC/prune **schedules** from the UI (read-only
status only — scheduling stays in PBS); per-namespace thresholds.

**Tests:** GC status (last-run, reclaimed, outcome) is read correctly; the evaluator raises
`GcStale` past the configured age and clears when GC runs; `VerifyFailed` raises when a
datastore has a failed-verify snapshot and clears when none remain; `BackupStale` raises when
the newest successful backup exceeds the threshold; a muted category is suppressed via
`CategoryMask`; the notification fires once per signature like the existing node alerts; the
thresholds survive the settings backup round-trip.

**Acceptance bar:** a PBS datastore whose GC hasn't run, whose verification failed, or whose
newest backup is too old raises a node alert (with the category breakdown) and notifies over
the same channels as every other Proxmox node alert.

---

### Phase V11.3 — PBS tasks / jobs feed (backup / verify / sync / GC / prune)

**Complexity:** Medium
**Value:** PBS runs scheduled **verify, sync, GC and prune** jobs (and receives backup
tasks); when one fails you currently learn nothing. Surfacing the recent task history — with
status and log — gives the "why did last night's job fail?" answer, reusing the PVE task
plumbing already in the client.

**Scope:**

- Read `GET /nodes/{node}/tasks` filtered to PBS worker types (`backup`, `verificationjob`/
  `verify`, `syncjob`/`sync`, `garbage_collection`, `prune`) with status + timing, plus the
  task log on demand — reusing the existing `GetLxcTasksAsync` / `GetTaskLogAsync` pattern,
  branched for PBS.
- The PBS node modal gains a **Jobs** tab: recent tasks newest-first with type/status/duration
  badges and an inline **log** viewer (same component as the PVE task log). **Sync jobs** are
  shown here too (status of pull/remote-sync between PBS instances) — no separate surface.
- Owner-scoped read endpoints; live/best-effort, paginated, no persistence.
- **Backup/restore:** nothing to export (live telemetry).

**Out of scope:** triggering jobs (V11.4); editing job schedules/config (stays in PBS);
cross-PBS sync **configuration**; long-term job-history retention/charts.

**Tests:** the PBS task feed lists the expected worker types with correct status mapping; a
failed task is flagged and its log is retrievable; sync-job status surfaces in the feed; the
feed is owner-scoped and paginated; a non-PBS connection is unaffected.

**Acceptance bar:** a PBS node's **Jobs** tab shows recent backup/verify/sync/GC/prune tasks
with pass/fail status and a readable log, so a failed nightly job is visible at a glance.

---

### Phase V11.4 — One-click Verify / GC / Prune (gated + audited)

**Complexity:** Medium
**Value:** Once you can *see* a stale GC or an unverified snapshot (V11.0–V11.2), the natural
next step is to fix it without SSHing into PBS. This adds the small set of safe, idempotent
maintenance actions — **Verify**, **Garbage-collect**, **Prune** — one click, gated and
audited like every other mutating Proxmox surface.

**Scope:**

- POST actions on the PBS admin surface: start a **datastore (or snapshot) verify**, start
  **GC**, run a **prune** (honouring the datastore's configured retention — Stashboard does
  not invent a retention policy). Each returns a UPID tracked to completion via the existing
  task-status polling.
- **Verify now** on a snapshot/datastore and **GC now** / **Prune now** on a datastore, behind
  a per-click confirmation. Gated **off by default** by a server-wide toggle (a
  `Stashboard:Allow…` flag, mirroring `AllowContainerRemoval`) **and** a per-connection opt-in,
  consistent with the host-terminal / removal gating model.
- Every attempt written to an **immutable audit row** (new `ProxmoxMaintenanceAuditEntity`,
  mirroring `ProxmoxCloneAuditEntity` / `ProxmoxRestoreAuditEntity`: who / when / connection /
  datastore / action / UPID / outcome) and surfaced in the **Settings → Audit** viewer as a
  new tab.
- **Backup/restore:** the new gating flag round-trips with the connection settings; audit rows
  are runtime records and not exported (consistent with existing audit handling).

**Out of scope:** **deleting** snapshots / forget (destructive — stays out by default, can be a
later guarded phase); creating/editing verify-or-prune **schedules**; restoring from a PBS
snapshot (separate, V8-style restore track).

**Tests:** a verify/GC/prune POST returns a UPID and is polled to completion; the action is
refused (403) when the server-wide flag or per-connection opt-in is off; every attempt writes
an audit row with the outcome; the audit tab renders the PBS maintenance rows; the gating flag
survives the backup round-trip.

**Acceptance bar:** with the feature enabled and a connection opted in, a user can trigger a
verify, garbage-collection, or prune on a PBS datastore from the UI, watch it run to
completion, and find every attempt in the audit log — with the action refused by construction
when the gate is off.

---

### Phase V11.5 — PBS-derived MQTT sensors

**Complexity:** Low–Medium
**Value:** Everything V11.0–V11.3 collects is exactly the kind of **sparse, derived signal**
the V9.0/V9.1 MQTT pipeline is built for. Publishing it to Home Assistant lets a user automate
over PBS's own conclusions — "alert me if any datastore has a failed verification", "warn if GC
hasn't run in a week", "remind me if a datastore took no backup last night" — without
re-deriving any of it.

**Scope:** builds on the V9.0/V9.1 publisher verbatim
(`MqttEntityStateProvider → MqttDiscoveryBuilder → MqttPublishReconciler → MqttPublisherService`,
same broker/prefixes/per-object device tree/retained topics/shared availability+LWT/lifecycle
cleanup). New entity kinds on the **existing PBS node device** plus per-datastore devices:

1. **Failed-verify count** — a numeric `sensor` per datastore (count of failed/unverified
   snapshots).
2. **Last-GC age** — a `timestamp` `sensor` per datastore (newest successful GC), `device_class:
   timestamp` so HA renders "x days ago".
3. **Last-successful-backup age** — a `timestamp` `sensor` per datastore (and feeding the V11.1
   per-guest backup-age where a link exists).
4. **Datastore alert** — a `problem` `binary_sensor` per datastore carrying the V11.2 verdict
   (VerifyFailed / GcStale / BackupStale) with the category breakdown as `json_attributes`,
   exactly like the V9.1 node-alert sensor.

**Out of scope:** raw PBS metric graphing (dedup ratio time-series, throughput) — deliberately
off, same rationale as the V9.1 raw-telemetry note; control/command topics (deferred).

**Tests:** each PBS sensor publishes the expected value and refreshes on a check cycle; the
datastore `problem` sensor flips with the V11.2 verdict and clears when resolved; all entities
reference the shared availability topic and clear their retained topics when the datastore /
connection disappears; the per-guest backup-age reflects the PBS link from V11.1.

**Acceptance bar:** with the V9.0 integration on, Home Assistant additionally exposes
per-datastore failed-verify counts, last-GC age, last-successful-backup age, and a datastore
alert `problem` sensor — auto-discovered, updated within a check cycle, and going `unavailable`
when Stashboard stops.

---

### Phase V11.6 — PVE→PBS backup jobs (back up *to* PBS from Stashboard)

**Complexity:** Medium–High
**Value:** V11.0–V11.5 make PBS backups **observable**; this phase lets Stashboard
**create** them — the one thing a user genuinely wants once they can see their backup
estate: trigger a backup now, and schedule recurring ones, targeting a PBS datastore,
without leaving Stashboard for the PVE web UI. **Important boundary:** PBS itself never
initiates guest backups — **PVE** runs `vzdump` against a storage that points at a PBS
datastore. So this is a **PVE-side** provisioning feature (a continuation of the V8 line),
placed here only to keep the whole backup story in one section. It requires a **PVE
connection whose storage targets a PBS datastore**; a PBS-only connection cannot run it.

**Scope:**

- **Backup now** on an LXC/VM: `POST /nodes/{node}/vzdump` for the guest into a chosen
  PBS-backed storage, with mode (snapshot / suspend / stop), compression, and an optional
  note — returns a UPID tracked to completion via the existing task-status polling, with the
  log viewable through the V11.3 task feed.
- **Scheduled backup-job CRUD** against `/cluster/backup`: list / create / edit / delete jobs
  selecting guests (or a pool / all), a **PBS-backed storage** as the target, a schedule
  (the same calendar-event grammar Proxmox uses), retention (`prune-backups` / `keep-*`),
  mode, and enable/disable — surfaced as a **Backup jobs** management view (mirroring the LXC
  parameter-editor UX), with the target storage filtered to those that resolve to a PBS
  datastore.
- **Gating + audit:** backup-now and job mutations are **off by default** behind a
  server-wide flag (a `Stashboard:Allow…` flag, mirroring `AllowContainerRemoval`) **and** a
  per-connection opt-in, consistent with the V11.4 / clone / restore gating; every action
  writes an immutable audit row (extending the V11.4 `ProxmoxMaintenanceAuditEntity` or a
  sibling `ProxmoxBackupJobAuditEntity`: who / when / connection / guest-or-job / action /
  UPID / outcome) and shows up in the **Settings → Audit** viewer.
- **Backup/restore:** the gating flag round-trips with the connection settings; **job
  definitions live in PVE, not Stashboard's DB**, so they are read live and not exported
  (Stashboard configures PVE, it doesn't shadow-store the schedule); audit rows are runtime
  records and not exported.

**Out of scope:** restoring **from** a PBS snapshot (separate, V8-style restore track — V11
stays backup-create + monitor); file-level / single-file restore; editing PBS-side **prune
schedules** (those stay in PBS — see V11.4); backup jobs targeting non-PBS storage (V11.6 is
the PBS story; generic vzdump-to-local is not in scope here); cluster-wide job orchestration
beyond what `/cluster/backup` already models.

**Tests:** a backup-now POSTs `vzdump` for the guest into the selected PBS storage, returns a
UPID, and is polled to completion; a created scheduled job appears in `/cluster/backup` with
the chosen guests / PBS storage / schedule / retention / mode and round-trips through
edit/delete; the storage picker offers only PBS-backed storages; backup-now and job mutations
are refused (403) when the server-wide flag or per-connection opt-in is off; every action
writes an audit row; a PBS-only connection cannot reach this surface; the gating flag survives
the backup round-trip.

**Acceptance bar:** with the feature enabled and a PVE connection opted in, a user can trigger
an immediate backup of a guest into a PBS datastore and create/edit/delete a recurring backup
job targeting PBS — entirely from Stashboard, with every action gated and audited, and the
resulting snapshots then visible through V11.0 and fresh on the V11.1 backup-age signal.

---

## V12 — Notification depth, dashboard breadth & convenience

> A grab-bag of independent, higher-value-but-larger-scope items that don't belong to the
> monitoring (V10), HA-integration (V9) or PBS (V11) tracks: smarter notification gating
> (maintenance windows + root-cause suppression + severity routing), automated config
> backups, broadening the dashboard from "monitored services only" to a real homelab launcher
> (link cards + spaces + live widgets), and Wake-on-LAN. Phases are independent and can ship in
> any order; the order below front-loads the items that reduce alert noise.

---

### Phase V12.0 — Maintenance windows + alert suppression

**Complexity:** Medium
**Value:** Every failure notifies immediately today, with no way to say "I'm doing planned
work 02:00–04:00, stay quiet" and no **root-cause suppression** — when a Docker host or a
Proxmox node goes unreachable, the user gets N separate "container X is down" alerts instead
of one "host is down". This phase adds both, layered onto the existing per-channel
toggle + throttle-key model rather than a new pipeline.

**Scope:**

- **Maintenance windows:** a user defines named windows (one-off or recurring, with a
  timezone) scoping **all** notifications, or a subset (a host / connection / tag / service).
  While a window is active, alerts are **suppressed** (not queued-and-flushed — the state is
  re-derived on exit, so a service that recovered during the window doesn't fire a stale
  alert). A small "in maintenance" badge surfaces on affected cards.
- **Root-cause suppression:** when a Docker connection / Proxmox node is itself unreachable,
  the per-child "container/guest down" alerts are **collapsed** into the single
  host/node-down alert — children are marked *suppressed (host down)* rather than each
  notifying. Built on the existing dependency the data model already implies (a container
  belongs to a connection; a guest to a node), so no new dependency graph is needed for the
  common case.
- Reuses the established **per-channel throttle key stamped only after a successful send**, so
  suppression is a gate *before* the send decision, leaving the retry semantics intact.
- **Backup/restore:** maintenance-window definitions + suppression settings are added to
  `BackupService` export/import and its round-trip test (§10.3).

**Out of scope:** arbitrary user-authored **service-dependency graphs** ("Jellyfin depends on
the NAS") beyond the implicit host→child relationship — a possible later phase; SLA/uptime
accounting that *excludes* maintenance windows (V10.1 history records raw state; annotating it
with windows is separate); approval workflows.

**Tests:** an alert inside an active maintenance window is suppressed and the state is
re-derived (not replayed) on exit; a recurring window activates/deactivates on schedule in the
configured timezone; when a connection/node is unreachable the child down-alerts are collapsed
into one host-down alert and re-expand when the host returns; suppression gates before the
throttle stamp so a post-window failure still notifies; the backup round-trip preserves windows
+ settings.

**Acceptance bar:** a user can schedule a maintenance window during which no alerts fire, and a
host/node outage produces a single host-down notification instead of one per child container or
guest — with normal alerting fully restored the moment the window ends or the host recovers.

---

### Phase V12.1 — Scheduled automatic config backups

**Complexity:** Medium
**Value:** Stashboard's own config backup is **manual JSON export per user** today — easy to
forget, and the one thing whose loss (with the encryption key) is unrecoverable. A scheduled,
rotated automatic backup to a mounted path or S3-compatible target closes the classic homelab
"I never took a backup" gap. (Proxmox `vzdump` *scheduling* is a separate concern and lives in
**V11.6** — V12.1 is about backing up **Stashboard itself**.)

**Scope:**

- A **backup schedule** (off by default) at **Settings → Backup**: cadence (daily/weekly at a
  fixed time), destination — a **mounted host path** (a volume the user maps in compose) and/or
  an **S3-compatible** bucket (endpoint / bucket / access key / secret, the secret encrypted at
  rest, never returned) — and a **retention count** (keep last N, prune older).
- A background job produces the **same `BackupService` export artifact** the manual flow does
  (so format and round-trip guarantees are identical), writes it timestamped to the
  destination, and prunes beyond the retention count. Each run is recorded (success/failure +
  bytes + location) and surfaced as a small history list on the Backup page.
- **Failure visibility:** a failed scheduled backup raises a notification through the existing
  channels (reusing the per-channel toggle/throttle), because a silently-failing backup is
  worse than none.
- **Backup/restore:** the schedule + destination config (secret encrypted) round-trips through
  `BackupService` itself (§10.3); the produced backup *files* are the output, not re-exported.

**Out of scope:** backing up the SQLite file / volume at the filesystem level (the JSON export
is the supported, portable artifact — volume backup is the user's infra concern); encryption of
the backup artifact beyond what the transport provides (a possible later phase); restore *from*
a scheduled backup is the existing import flow, not new here.

**Tests:** a scheduled run writes a valid `BackupService` artifact to the mounted path and to
S3; retention prunes beyond the keep-N count and never deletes newer ones; a failed destination
write records a failure row and fires a notification; the S3 secret is encrypted at rest and the
API returns a presence flag only; the schedule + destination survive the config backup
round-trip; the produced artifact imports cleanly through the existing restore path.

**Acceptance bar:** a user points Stashboard at a mounted path or S3 bucket, picks a daily
cadence and a retention count, and finds a fresh, importable backup appearing on schedule with
old ones pruned — and gets notified if a backup ever fails.

---

### Phase V12.2 — Link-only cards + dashboard spaces

**Complexity:** Medium
**Value:** Every dashboard card today is a **monitored `WebResource`** with a health check.
That excludes the many services a user wants on "one place for every service" but doesn't need
to monitor (a bookmark to the router UI, an external SaaS, a doc). A lightweight **link-only
card** plus optional **spaces** (named dashboard groups beyond the existing categories) makes
Stashboard a real homelab launcher, directly competing with Homepage / Heimdall, without
forcing a health check on things that don't have one.

**Scope:**

- A **link-only** card type: name, icon/favicon (reuse the existing favicon resolver + custom
  upload), URL, category/tags — **no health check, no status dot** (or a neutral "not
  monitored" affordance). Modelled either as a flavour of `WebResourceEntity` (a
  `Monitored` flag) or a sibling entity, whichever keeps the dashboard query simplest;
  link cards are skipped by `ServiceHealthChecker`, the MQTT publisher, and uptime history.
- **Spaces:** an optional per-user grouping above categories — a named set of cards (e.g.
  "Media", "Infra", "External") selectable as dashboard tabs/sections. Coexists with the
  existing category grouping and sort modes; a card can live in one space.
- Fully owner-scoped; link cards and spaces are ordinary user content (no new gating).
- **Backup/restore:** link cards + space definitions + membership are added to `BackupService`
  export/import and its round-trip test (§10.3).

**Out of scope:** the live-data widgets on cards (that's V12.5 — V12.2 is a static link with an
icon); importing a Homepage/Heimdall config; per-space access sharing (no role system exists);
nested spaces.

**Tests:** a link-only card renders with icon + name and is excluded from health checks, MQTT,
and uptime history; a space groups its member cards and a card belongs to exactly one space;
spaces coexist with category grouping and the existing sort modes; everything is owner-scoped;
the backup round-trip preserves link cards + spaces + membership.

**Acceptance bar:** a user can add an unmonitored link card with a custom icon and organise both
link and monitored cards into named dashboard spaces — turning the dashboard into a launcher for
everything they run, monitored or not.

---

### Phase V12.3 — Notification routing rules (severity → channel)

**Complexity:** Medium
**Value:** V10.0 (Apprise) explicitly fans **every** notification to **all** configured
channels and defers routing — but the common ask is "criticals to Discord, routine info to
ntfy". This phase adds per-severity / per-source routing on top of the V10.0 channel set, so
the right alerts reach the right place without drowning a chat in low-signal noise.

**Scope:**

- A small **routing-rules** model: match on **severity** (info / warn / crit — derived from the
  existing alert kinds: a service going Down or a node crit = crit; an update-available = info;
  etc.) and optionally **source** (service health / Docker update / Proxmox update / node
  alert / backup failure), → a set of **target channels** (email / Telegram / specific Apprise
  URLs / web-push from V10.6).
- A **default rule** preserves today's behaviour (all → all) so existing setups are unchanged
  until a user adds rules; rules evaluate top-to-bottom, first match wins (or "all matching",
  configurable), surfaced on the **Notifications** page with a clear explanation + a **test**
  that shows which channels a sample severity would hit.
- Reuses the existing **per-channel toggle + throttle key** — routing decides *which* channels
  are eligible, the per-channel send/throttle logic is unchanged.
- **Backup/restore:** routing rules are added to `BackupService` export/import and its
  round-trip test (§10.3).

**Out of scope:** time-of-day routing (that overlaps maintenance windows, V12.0); per-recipient
routing / multiple users' inboxes (single-owner channels); escalation chains ("page me if
unacked in 10 min").

**Tests:** a crit alert routes only to its mapped channels and an info alert to its own; the
default all→all rule preserves current behaviour with no rules defined; first-match (or
all-match) evaluation is correct; routing gates before the per-channel throttle stamp; the test
button reports the channels a given severity/source would hit; the backup round-trip preserves
rules.

**Acceptance bar:** a user can route critical alerts to Discord and routine update notices to
ntfy, see exactly which channels a sample alert would reach, and have existing
all-channels behaviour preserved until they opt into routing.

---

### Phase V12.4 — Wake-on-LAN for physical hosts

**Complexity:** Low–Medium
**Value:** Starting a VM/LXC is already a one-click action, but **waking the physical box** it
runs on is not — a homelabber who powers down a node to save energy has no way to bring it back
from Stashboard. A Wake-on-LAN magic-packet sender closes that loop for any host that supports
it.

**Scope:**

- A **WoL target** model: name, **MAC address**, optional broadcast address / port, and an
  optional association to an existing Docker connection / Proxmox node (so the "Wake" button can
  appear next to an unreachable host).
- A **"Wake" action** that emits the magic packet from the Stashboard container (UDP broadcast
  on the host network — documented compose note, since WoL needs L2 reachability), with a clear
  result toast (packet sent ≠ host definitely up — pair with the existing reachability check to
  confirm).
- Audited like other actions (who / when / target / result); no special gating needed (a magic
  packet is low-risk), though it respects the same owner-scoping.
- **Backup/restore:** WoL targets are added to `BackupService` export/import and its round-trip
  test (§10.3).

**Out of scope:** graceful **shutdown** of a physical host (that's an SSH/IPMI concern, not
WoL); scheduled wake/sleep automation; IPMI/Redfish power control (a heavier, separate phase).

**Tests:** the Wake action emits a correctly-formed magic packet to the target MAC/broadcast; a
target optionally associates with a connection/node and the Wake button surfaces when that host
is unreachable; the action is owner-scoped and audited; the backup round-trip preserves WoL
targets.

**Acceptance bar:** a user can register a host's MAC and wake it from Stashboard with one click,
see the packet was sent, and confirm via the existing reachability check that the host came up.

---

### Phase V12.5 — Live service widgets on cards

**Complexity:** High
**Value:** The biggest differentiator and the biggest scope: pull **live data** from popular
self-hosted services onto their dashboard cards — queue counts from Sonarr/Radarr, active
torrents from qBittorrent, stream count from Jellyfin, etc. — the headline feature of Homepage.
It turns a card from "is it up?" into "what is it doing right now?".

**Scope:**

- A **widget provider** abstraction: per-service-type integrations that, given a base URL + an
  API key (stored with the existing AES-256-GCM credential encryption), fetch a small set of
  **summary metrics** on a polling cadence and render them as compact stats on the card (reuse
  the inline-SVG / no-chart-library approach).
- A **starter set** of the most-requested providers (e.g. Sonarr/Radarr, qBittorrent, Jellyfin/
  Plex) behind a common interface, each opt-in per service and configured from the service
  modal; a service with no widget configured renders exactly as today.
- Polled server-side (so keys never reach the browser and CORS isn't a problem), cached, and
  rate-limited per provider; failures degrade gracefully to "widget unavailable" without
  affecting the health-check status.
- **Backup/restore:** widget config + encrypted keys are added to `BackupService` export/import
  and its round-trip test (§10.3).

**Out of scope:** a generic user-scriptable widget / arbitrary JSON-path scraping (a possible
later phase); writing/controlling the target service from the widget (read-only counters only);
a huge provider catalogue in one go — V12.5 ships a curated starter set and grows incrementally.

**Tests:** a configured Sonarr/qBittorrent widget fetches and renders its summary metrics; keys
are encrypted at rest and never sent to the client; a provider outage degrades to "unavailable"
without changing the service's health status; polling is cached + rate-limited; an unconfigured
service is unchanged; the backup round-trip preserves widget config + keys.

**Acceptance bar:** a user can attach a live widget to a service card — e.g. qBittorrent's
active-torrent count or Sonarr's queue — and see it update on the dashboard, with the API key
held encrypted server-side and the widget never affecting the up/down status.

