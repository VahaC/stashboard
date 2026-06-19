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
> and the V7.x visual Compose editor track (V7.0–V7.9) to [`HISTORY.md`](./HISTORY.md) §17,
> once those phases all shipped; only V8.0+ remains in this file.
>
> **Status (shipped milestones, V5+):** ✅ V5.0 (disabled card style + one-click removal) · ✅ V5.0.1 (unlink container from service) · ✅ V5.0.2 (editable SMTP / email settings) · ✅ V5.0.3 (dedicated notifications settings page) · ✅ V5.1 (secure key auto-provisioning, image 5.1.0) · ✅ V5.2 (true Compose-aware recreate, image 5.2.0) · ✅ V5.3 (host terminal, image v5.3.0) · ✅ V5.3.1 (tag-pattern filter correctness + version tags, image 5.3.1) · ✅ V5.3.2 (reliable offline alerts, image 5.3.2) · ✅ V5.4 (Compose project grouping & bulk update, image 5.4.0) · ✅ V5.5 (image cleanup / prune, image 5.5.0) · ✅ V5.6 (health-check tuning page, image 5.6.0) · ✅ V5.7 (container exec, image 5.7.0) · ✅ V5.8 (session audit viewer, image 5.8.0) · ✅ V5.9 (Docker instances page redesign, image 5.9.0) · ✅ V6.0 (Proxmox LXC update monitoring, image 6.0.0) · ✅ V6.1 (Proxmox LXC detail modal + Docker-style cards, image 6.1.0) · ✅ V6.2 (LXC Config tab, image 6.2.0) · ✅ V6.3 (LXC Stats + Tasks tabs, image 6.3.0) · ✅ V6.4 (LXC lifecycle actions + real-time stats, image 6.4.0) · ✅ V6.5 (edit LXC parameters, image 6.5.0) · ✅ V6.6 (browser LXC console / Console tab, image 6.6.0) · ✅ V6.7 (per-LXC update monitoring toggle, image 6.7.0) · ✅ V6.7.1 (Proxmox one-click "Update now", image 6.7.1) · ✅ V6.8 (PVE node health card + node modal, image 6.8.0) · ✅ V6.8.1 (PVE node alerting, image 6.8.1) · ✅ V6.8.2 (PVE node deep telemetry / SSH collectors, image 6.8.2) · ✅ V6.9.0 (edit LXC network interfaces & mount points, image 6.9.0) · ✅ V6.10 (Proxmox page Docker-parity redesign, image 6.10.0) · ✅ V6.11 (bulk LXC monitoring & update operations + audit, image 6.11.0) · ✅ V6.12 (LXC live logs / Logs tab, image 6.12.0) · ✅ V6.13 (destroy / remove LXC, image 6.13.0) · ✅ V6.13.1 (create LXC, image 6.13.1) · ✅ V6.14 (VM / QEMU support, image 6.14.0) · ✅ V6.15 (Proxmox connections in backup/restore, image 6.15.0) · ✅ V7.0 (visual Compose viewer, image 7.0.0) · ✅ V7.1 (edit basic service fields, image 7.1.0) · ✅ V7.1.1 (Compose as a per-project modal, image 7.1.1) · ✅ V7.2 (resource constraints UI, image 7.2.0) · ✅ V7.2.1 (PBS disk/SMART fixes, image 7.2.1) · ✅ V7.3 (top-level resources, image 7.3.0) · ✅ V7.4 (create a new service, image 7.4.0) · ✅ V7.4.1 (create a whole project, image 7.4.1) · ✅ V7.5 (service templates, image 7.5.0) · ✅ V7.6 (diff / dry-run / apply, image 7.6.0) · ✅ V7.7 (dependency graph + linter, image 7.7.0) · ✅ V7.8 (container card icons, image 7.8.0) · ✅ V7.9 (link Proxmox guests to services + Docker↔Proxmox cross-link, image 7.9.0). Shipped V5.x phase detail now lives in [`HISTORY.md`](./HISTORY.md) §14, the V6.x Proxmox parity & LXC/VM phase detail in [`HISTORY.md`](./HISTORY.md) §16, and the V7.x visual Compose editor track in [`HISTORY.md`](./HISTORY.md) §17; V1–V4 historical detail is also in [`HISTORY.md`](./HISTORY.md). End-user documentation: [`DOCKER_UPDATE_MONITORING_GUIDE.md`](./DOCKER_UPDATE_MONITORING_GUIDE.md).

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

---

## V9 — Core dashboard & monitoring depth

> The V5–V8 line drove Stashboard deep into infrastructure management (Docker
> manager + Compose editor + Proxmox provisioning). V9 turns back to the product's
> original promise — *"one place for every service"* — and closes the gaps that
> separate Stashboard from a real monitoring + dashboard tool: notification
> breadth, uptime history, a public status page, and the login-security surface
> (2FA / API tokens) appropriate for a product that stores credentials and can
> open a shell on the host. These phases are independent of each other and can ship
> in any order; the suggested order front-loads the highest user-visible value.

---

### Phase V9.0 — Notification channels beyond email/Telegram

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
(e.g. "criticals to Discord, info to ntfy") — V9.0 fans every notification out to
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

### Phase V9.1 — Uptime history & analytics

**Complexity:** Medium
**Value:** The health-check loop records only the *current* status per URL
(`currentStatus`, `lastCheckedUtc`, `lastResponseTimeMs`, `lastError`). There is no
retained time-series, so the product can't answer "what's my uptime this week?" or
"when did this flap?" — the questions monitoring is for. This phase adds a bounded
history so the Healthcheck tab becomes a real monitor and unblocks the public
status page (V9.2).

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
downsampling/rollup tables — a single bounded window is enough for V9.1.

**Tests:** an event row is written on a status transition and not on an unchanged
sampled tick beyond the configured cadence; uptime % is computed correctly across a
known up/down sequence including the open (still-down) span; the incident log pairs
Down→Up correctly and reports duration; retention prune deletes only rows older than
the window; the metrics endpoint is owner-scoped (404 for a foreign service).

**Acceptance bar:** opening a service's Healthcheck tab shows its 24 h / 7 d / 30 d
uptime, a response-time sparkline, and a list of past incidents with durations, all
derived from retained events that prune at the configured window.

---

### Phase V9.2 — Public status page

**Complexity:** Medium
**Value:** A shareable, read-only status page is the headline feature of the
monitoring tools Stashboard's audience already knows (Uptime Kuma). It turns
Stashboard from a private admin view into something a homelabber can hand to family
or teammates — "is the service up?" without an account. Builds directly on the
V9.1 history.

**Scope:**

- A user creates one or more **status pages**, each a named selection of their own
  services (`StatusPageEntity` + a join to the chosen `WebResource`s), with a
  title, optional description, and a public **slug**.
- A **public, unauthenticated** read endpoint (`GET /api/status/{slug}`) and a
  matching public SPA route that renders each selected service's current status and
  its V9.1 uptime % + recent-history bar — **owner data only ever exposed through an
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
owner (a possible later phase) — V9.2 renders live status + history only.

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

### Phase V9.3 — Two-factor authentication (TOTP)

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

### Phase V9.4 — API tokens (personal access tokens)

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

### Phase V9.5 — OIDC / SSO login (optional)

**Complexity:** Medium–High
**Value:** Authentik / Authelia / Keycloak are increasingly the front door of a
homelab, and users running one want Stashboard behind it rather than maintaining a
separate password. This is the heaviest account-surface phase and is explicitly
**optional / lower-priority** than V9.3–V9.4 — sequence it last in the security
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
- **Coexists with local auth + 2FA:** OIDC is additive; local login (and V9.3 TOTP)
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

### Phase V9.6 — PWA, web push & card ordering

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
- **Web push** as another V9.0-style notification channel: the browser subscribes
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

### Phase V9.7 — Command palette & global search

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
  guests are already fetched/cached client-side) so V9.7 needs **no new backend
  surface** in the common case; add a lightweight combined search endpoint only if
  client-side filtering proves insufficient at scale.

**Out of scope:** server-side full-text search infrastructure; searching inside logs
/ audit history; command *actions* that mutate (start/stop a container from the
palette) — V9.7 is navigation/search only, mutations stay on their guarded surfaces.

**Tests:** the palette opens on the shortcut; a query matches services / containers /
guests / nav actions and ranks sensibly; selecting a result navigates to the correct
modal/route with the right entity in focus; results are owner-scoped (a query never
surfaces another user's entities).

**Acceptance bar:** pressing `Ctrl/⌘-K` anywhere opens a search box that finds any of
the user's services, containers, Compose projects, or Proxmox guests (and the main
navigation targets) and jumps straight to it.

