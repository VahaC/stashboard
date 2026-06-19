# Stashboard — Product Roadmap

> This document is the forward-looking product roadmap — unshipped phases
> only. The historical detail (Docker-update-checker V1–V3, V4 SQLite
> migration, the original V1 implementation checklist) lives in
> [`HISTORY.md`](./HISTORY.md).
>
> **Numbering note:** the legacy section numbers §1–§13 and §15 live in
> HISTORY.md. §14 keeps its heading number here for external references, but its
> shipped V5.x phase detail (V5.0–V5.9) was archived to [`HISTORY.md`](./HISTORY.md) §14,
> and the V6.x Proxmox parity & LXC/VM phase detail (V6.0–V6.15.1) to [`HISTORY.md`](./HISTORY.md) §16,
> once those phases all shipped; only V7.0+ remains in this file.
>
> **Status (shipped milestones, V5+):** ✅ V5.0 (disabled card style + one-click removal) · ✅ V5.0.1 (unlink container from service) · ✅ V5.0.2 (editable SMTP / email settings) · ✅ V5.0.3 (dedicated notifications settings page) · ✅ V5.1 (secure key auto-provisioning, image 5.1.0) · ✅ V5.2 (true Compose-aware recreate, image 5.2.0) · ✅ V5.3 (host terminal, image v5.3.0) · ✅ V5.3.1 (tag-pattern filter correctness + version tags, image 5.3.1) · ✅ V5.3.2 (reliable offline alerts, image 5.3.2) · ✅ V5.4 (Compose project grouping & bulk update, image 5.4.0) · ✅ V5.5 (image cleanup / prune, image 5.5.0) · ✅ V5.6 (health-check tuning page, image 5.6.0) · ✅ V5.7 (container exec, image 5.7.0) · ✅ V5.8 (session audit viewer, image 5.8.0) · ✅ V5.9 (Docker instances page redesign, image 5.9.0) · ✅ V6.0 (Proxmox LXC update monitoring, image 6.0.0) · ✅ V6.1 (Proxmox LXC detail modal + Docker-style cards, image 6.1.0) · ✅ V6.2 (LXC Config tab, image 6.2.0) · ✅ V6.3 (LXC Stats + Tasks tabs, image 6.3.0) · ✅ V6.4 (LXC lifecycle actions + real-time stats, image 6.4.0) · ✅ V6.5 (edit LXC parameters, image 6.5.0) · ✅ V6.6 (browser LXC console / Console tab, image 6.6.0) · ✅ V6.7 (per-LXC update monitoring toggle, image 6.7.0) · ✅ V6.7.1 (Proxmox one-click "Update now", image 6.7.1) · ✅ V6.8 (PVE node health card + node modal, image 6.8.0) · ✅ V6.8.1 (PVE node alerting, image 6.8.1) · ✅ V6.8.2 (PVE node deep telemetry / SSH collectors, image 6.8.2) · ✅ V6.9.0 (edit LXC network interfaces & mount points, image 6.9.0) · ✅ V6.10 (Proxmox page Docker-parity redesign, image 6.10.0) · ✅ V6.11 (bulk LXC monitoring & update operations + audit, image 6.11.0) · ✅ V6.12 (LXC live logs / Logs tab, image 6.12.0) · ✅ V6.13 (destroy / remove LXC, image 6.13.0) · ✅ V6.13.1 (create LXC, image 6.13.1) · ✅ V6.14 (VM / QEMU support, image 6.14.0). Shipped V5.x phase detail now lives in [`HISTORY.md`](./HISTORY.md) §14 and the V6.x Proxmox parity & LXC/VM phase detail in [`HISTORY.md`](./HISTORY.md) §16; V1–V4 historical detail is also in [`HISTORY.md`](./HISTORY.md). End-user documentation: [`DOCKER_UPDATE_MONITORING_GUIDE.md`](./DOCKER_UPDATE_MONITORING_GUIDE.md).

## 14. Post-V4 backlog (V5+) — deferred Docker features

> **Archived:** every shipped phase that lived under this section has moved to
> [`HISTORY.md`](./HISTORY.md) — the V5.x Docker feature phases (V5.0–V5.9) to
> HISTORY §14, and the V6.x Proxmox parity & LXC/VM phases (V6.0–V6.15.1) to
> HISTORY §16. The section number is kept only for external references; the
> active roadmap continues with V7 below.

---

## V7 — Visual Compose editor (Docker)

> The visual Compose editor track for the Docker page. V7.0–V7.6 have
> shipped; V7.7+ below are forward-looking.

---

### ✅ Phase V7.0 — Visual Compose viewer (foundation, read-only) (image 7.0.0)

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

**Shipped (7.0.0):** implemented as scoped, plus one scope extension: the
viewer also works for **SSH connections**. There the **Compose project path**
points at a directory on the remote Docker host and `ComposeProjectReader`
fetches the file over the connection's existing SSH credentials in a single
probe-and-`cat` round trip (read-only; SSH failures → 502; the compose-aware
"Update now" recreate stays LocalSocket-only inside the updaters; TcpTls keeps
no path). `GET /api/docker/connections/{id}/compose`
locates the file in the connection's `ComposeProjectPath` (spec precedence
`compose.yaml` → `compose.yml` → `docker-compose.yaml` → `docker-compose.yml`;
`ComposeProjectReader`), parses it with YamlDotNet (`ComposeFileParser`) and
returns the typed `ComposeProjectResponse` (services with image / container
name / restart / ports / mounts / environment / env files / depends_on /
networks / `deploy.resources`, plus top-level networks / volumes / secrets /
configs). Long-form ports/volumes are normalised to short syntax; plain
anchor/alias pairs resolve; `x-*` / `extends` / merge keys are reported in
`unsupportedFeatures` and surface the "Read-only — file uses X" banner. The
page at `/projects/{id}/compose` (entered via a **Compose** button on the
connection header of the Docker page) reuses the `dock` shell, `EntityCard`
and the state-pill family — each service card wears the live state of the
container matched by its compose-service label ("not deployed" otherwise),
expands into the `container-modal-summary` detail list, and renders a
disabled **Edit** button until V7.1. Error envelopes: 400 (no path
configured), 404 (directory / file missing), 422 (unparseable YAML).

---

### ✅ Phase V7.1 — Edit basic service fields (image 7.1.0)

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

**Shipped (7.1.0):** implemented as scoped, with two notable decisions and one
model fix:

- **Round-trip POC outcome
  ([ADR-0001](./docs/adr/0001-compose-yaml-round-trip.md)):** neither
  YamlDotNet's load-and-redump nor SharpYaml can preserve comments (their ASTs
  drop trivia before parsing), so the editor (`ComposeFileEditor`) instead
  **splices the raw file text** at the exact token spans of the edited keys,
  located via YamlDotNet's low-level event stream (whose marks — unlike the
  representation model's — point at alias *use sites*). Per-key diffing means
  an untouched field is a guaranteed zero-diff; block-end and empty-value mark
  quirks are clamped and regression-tested (`ComposeFileEditorTests` asserts
  full output texts). Safety refusals: flow-style service bodies, merge keys,
  anchored values (alias use-sites edit fine). Files with `x-*` / `extends` /
  merge keys stay read-only end-to-end (409 + the V7.0 banner).
- **Atomic save on both transports** (`ComposeProjectWriter`): write
  `<file>.next` → `docker compose -f <file>.next config -q` →
  same-directory rename. Validation is **blocking** (no CLI = no save, by
  decision); failures roll back and surface the raw stderr in the modal
  (422). LocalSocket validates inside the Stashboard container; SSH uploads +
  validates + renames in one round trip over the connection's credentials.
- **V7.0 model fix — per-project discovery from labels.** The V7.0
  single-`ComposeProjectPath`-per-connection model broke on hosts running
  several Compose projects (and the same flaw dated back to the V5.2/V5.4
  compose-aware updaters). Projects are now **discovered from the containers'
  `com.docker.compose.project` / `…project.working_dir` labels**:
  `GET /compose` lists them (project picker page), `GET /compose/{project}` /
  `PUT /compose/{project}/services/{service}` are project-scoped, project
  group headers on the Docker page link straight to their own editor, and
  standalone containers correctly have no Compose affordance. SSH hosts need
  zero config; LocalSocket gets an optional host→container **path mapping**
  on the connection form (replaces `ComposeProjectPath`; migration drops the
  column). `DockerImageUpdater` / `DockerProjectUpdater` now resolve each
  project's directory from the same labels, fixing compose-aware updates on
  multi-project hosts.
- Editor UX mirrors the Docker page (same modal/field family); the image tag
  dropdown lists registry tags anonymously and falls back to free text for
  private registries. Saving never touches running containers — changes apply
  on the next **Update project** (full diff/dry-run/apply lands in V7.6).

---

### ✅ Phase V7.1.1 — Compose as a per-project modal (image 7.1.1)

**Complexity:** Low (front-end only)
**Value:** The V7.0/V7.1 surface was a standalone page reached from a
**whole-host** Compose button and a project picker — the wrong altitude, since
a Docker host runs *many* Compose projects plus standalone containers. This
reworks the surface so the Compose affordance lives next to the thing it acts
on, and the editor opens in place rather than navigating away from the Docker
page.

**Shipped (7.1.1):**

- **Whole-host Compose button removed**, along with the `/projects/{id}/compose`
  and `/projects/{id}/compose/{project}` routes and the project-picker page
  (`ComposeProjectPage` deleted). The viewer/editor is now a **modal**
  (`ComposeProjectModal`) scoped to one discovered project on one connection.
- **Button only where it means something:** every Compose project's **group
  header** opens the modal. Single-container projects are **no longer demoted**
  into the "Other containers" bucket (the v5.4 1-of-1 collapse is lifted), so
  each shows as its own named group with the Compose / Update project actions;
  "Other containers" now holds only genuinely label-less containers, which carry
  no button. A non-compose container shows no button.
- **One tab per service** inside the modal, each wearing the matched live
  container's runtime-state badge, over a **compact project header strip**
  (counts + top-level networks / volumes / secrets / configs). The tab body is
  the same V7.1 editable basic-fields form (extracted into
  `ComposeServiceEditForm`) plus a read-only block for the fields the form
  doesn't cover. Unsupported-construct files stay read-only (details only) with
  the V7.0 banner.
- **No backend / contract / round-trip change** — discovery, per-service edit,
  and the byte-for-byte YAML splicing are untouched; this is a pure front-end
  UX rework reusing the shared `container-modal-*` / `EntityCard` / `StateBadge`
  families.

---

### ✅ Phase V7.2 — Resource constraints UI (Proxmox-style) (image 7.2.0)

**Complexity:** Medium
**Value:** The headline use case the user asked for. Today they either
hand-edit YAML or delegate to Portainer — both lose the per-host context
Stashboard already has. Stashboard knows the target host's total CPU /
memory and which other containers already reserve capacity, so it can
surface "you're allocating 14 of 16 cores" warnings inline instead of
letting the user discover the over-commit at `docker compose up -d` time.

**Shipped (7.2.0):** the resources editor renders below the V7.1 basic-fields
form in each service tab (`ComposeResourcesForm`), folded into the same atomic
save — one `docker compose config -q` validation, one write.

- **All nine fields editable:** `cpus`, `mem_limit`/`memory`,
  `mem_reservation`, `pids_limit`, `cpu_shares`, `ulimits`,
  `oom_kill_disable`, `oom_score_adj`, `shm_size`. cpu/mem/pids follow the
  file's convention — `deploy.resources.limits`/`.reservations` (modern,
  preferred; the default for files declaring none) **or** the legacy
  top-level `cpus`/`mem_limit`/… — detected per file and **never mixed**;
  legacy mode disables CPU reservation (v2 has no such key). `cpu_shares` /
  `oom_*` / `shm_size` / `ulimits` are always top-level and live behind an
  "Advanced" disclosure.
- **Round-trip extended (`ComposeFileEditor`):** the `deploy.resources`
  subtree is rewritten as a unit (sibling `deploy` keys — `replicas`,
  `placement`, … — survive byte-for-byte); top-level scalars and the
  `ulimits` block reuse the existing splice paths; numeric/boolean values
  render unquoted so integer-typed keys stay valid; an untouched field is
  still a guaranteed zero-diff (`yes` doesn't reflow to `true`). Anchored
  `deploy.resources` and GPU device reservations
  (`deploy.resources.reservations.devices`) are refused / flagged read-only
  rather than corrupted.
- **Numeric inputs + sliders bounded by the host's real capacity** — slider
  maxima are the host's actual CPU count and RAM, taken from the V3.5
  `docker stats` stream (`onlineCpus` + `memoryLimitBytes`), **not** a
  `docker info` call (no such endpoint exists; the original roadmap note was
  inaccurate).
- **Companion capacity panel:** *"Host capacity: 16 CPUs, 32 GiB · allocated
  by other containers: 12 CPUs (75 %), 18 GiB (56 %) · this service draft:
  2 CPUs, 4 GiB"* with an inline over-commit warning. The "allocated by
  others" figure is summed from the running containers' `HostConfig`
  (`NanoCpus`/`Memory`) via `inspect` — the edited project's own containers
  excluded — and cached server-side (`IMemoryCache`, ~60 s per connection)
  so re-opening the editor doesn't re-inspect every container on each open.

---

### ✅ Phase V7.2.1 — Proxmox Backup Server disk/SMART fixes (image 7.2.1)

**Complexity:** Low
**Value:** PBS support shipped in 6.8.2, but three bugs only surfaced on real
PBS hardware — all because PBS names a field or parameter differently from PVE,
and the client read only the PVE spelling.

**Shipped (7.2.1):**

- **Per-disk SMART 400.** The SMART read sent the `/dev/`-prefixed path
  (`/dev/sda`) PVE accepts; PBS validates `disk` against its block-device name
  schema and wants the bare name (`sda`), so the regex failed with a 400 shown
  as "host unreachable". The `/dev/` prefix is now stripped for PBS. A genuine
  per-disk `smartctl` failure now surfaces the host's own reason inline under
  that disk instead of a 502 (the host is reachable — only that read failed).
- **Disk type blank + health "UNKNOWN".** `disks/list` was read from the PVE
  keys `health` / `type`; PBS uses `status` (`passed`) / `disk-type`
  (`hdd`/`ssd`). Both spellings are now read, so the badge shows PASSED and the
  type shows on PBS.
- **Stale "API unreachable" banner.** A connection-level scan error lingered
  over a node card that the live status poll had already brought back online;
  the banner is now suppressed while that poll currently succeeds.

---

### ✅ Phase V7.3 — Top-level resources (networks, volumes, secrets, configs) (image 7.3.0)

**Complexity:** Medium
**Value:** Editing services in isolation is half the picture; networks,
named volumes, secrets, and configs are what stitch them together.
Without this phase the V7 editor stays a per-service form filler instead
of a real project editor.

**What shipped:**

- A separate **Shared resources** tab on the Compose modal (alongside the
  per-container tabs), holding four sections — **Networks · Volumes · Secrets ·
  Configs** — each with a plain-language "what is this / when do I need it" line
  at the top. Reuses the existing `container-modal-tabs` shell so the UX matches
  the rest of the editor.
- Each section is a CRUD list backed by the same comment-preserving,
  `docker compose config -q`-validated, atomic YAML writer from V7.1, now
  extended to splice **top-level** entries one at a time (siblings, key order
  and comments survive byte-for-byte; the same anchor / flow-style / merge-key
  refusals apply).
- **Network** editor: driver (`bridge` / `overlay` / `macvlan` / …), subnet,
  gateway, driver opts; warns on **subnet overlap** with networks already
  defined on the host (read via the Docker Engine network list).
- **Volume** editor: driver, driver opts, name override; surfaces the actual
  **on-disk size** from the host's `/system/df` (so the user sees that
  `postgres_data` is 4.2 GiB before they consider deleting it).
- **Secret** / **config** editor: external vs. file path, with a name override.

**Scope notes (deviations from the original proposal):**

- The secrets/configs editor manages the **Compose declarations only** (external
  vs. `file:` path). The proposed *encrypted-at-rest secret store inside
  Stashboard* (writing secret material to the host on save) was **deferred** —
  it is a much larger, security-sensitive subsystem; keeping V7.3 to YAML
  declarations keeps it symmetric with networks/volumes and adds no new attack
  surface.
- Volume size uses `/system/df` (raw Engine API over the connection's transport),
  not `docker volume inspect` — `inspect` does not report usage. It is
  best-effort: when the daemon can't be reached for `df` (e.g. TCP+TLS), the
  editor simply omits the size rather than erroring.

---

### ✅ Phase V7.4 — Create a new service from scratch (image 7.4.0)

**Complexity:** High
**Value:** This is the second half of what the user explicitly asked for:
the editor stops being a YAML-renderer and becomes a project
bootstrapper. A user clicks **Add service**, picks an image, fills the
form, and Stashboard appends a valid block to the project's
`docker-compose.yml`. Combined with V7.2, this is the "Proxmox-like
container creator" the user envisioned.

**Shipped:**

- **Add service** tab on the project modal — a structured wizard built on the
  same shared field controls as the existing-service editor (image + tag
  dropdown against `IRegistryClient`, ports, volumes, env, labels, restart,
  command/entrypoint, user, working_dir, and the V7.2 resource picker). The
  service name is validated client- and server-side for uniqueness and Compose
  key shape (`^[a-zA-Z0-9._-]+$`), and an image is required.
- The new block is appended at the end of the `services:` map by the same
  comment-preserving, `docker compose config -q`-validated, atomic writer the
  editor uses, so the rest of the file survives byte-for-byte. The entry sits at
  the existing services' indentation column (2- vs. 4-space). Adding several
  services in turn (or pasting them) supports **multiple containers in one file**.
- **Save and run** appends the block and then runs `docker compose up -d` against
  the whole project so the new container comes up alongside its siblings —
  **LocalSocket** via the in-container Compose CLI (V5.2 path) **and SSH** on the
  remote host over the connection's existing credentials. The modal then switches
  to the new service's tab, where every field is editable exactly like an
  existing service. **Save only** writes the file without starting anything.
- **Raw YAML** tab — the whole Compose file in a plain-text editor (write or
  paste by hand), with the same validated atomic save and an optional run.
  Available for existing projects too, and the escape hatch for files the
  structured editor marks read-only.

---

### ✅ Phase V7.4.1 — Create a whole project from scratch (image 7.4.1)

**Complexity:** Medium
**Value:** Closes the last gap in the bootstrapper: V7.4 could only add services
to an *already-discovered* project (one with running, labelled containers). You
could not start a brand-new stack from the UI. V7.4.1 lets you create the project
itself.

**Shipped:**

- **New project** button on every host header (hidden for TCP+TLS, which exposes
  no host filesystem). Opens a dialog: project name (validated to Compose's
  lowercase `^[a-z0-9][a-z0-9_-]*$` rule), target **directory** (free-text path
  as the connection sees it, with an opt-in `mkdir -p`), file name (default
  `docker-compose.yml`), and the first service via the same shared field controls
  as the editor.
- The file is built with a top-level `name:` (deterministic project name),
  validated by `docker compose config -q` and written atomically by the V7.1
  writer — local (inside the Stashboard container) or over SSH. The flow refuses
  to overwrite a directory that already holds a Compose file.
- **Create and run** then runs `docker compose up -d` (local or SSH) and opens the
  new project's modal — at which point it's a normal discovered project and the
  V7.4 **Add service** / **Raw YAML** tabs take over. **Create only** writes the
  file without starting anything.

---

### ✅ Phase V7.5 — Service templates / starter recipes (image 7.5.0)

**Complexity:** Low–Medium
**Value:** Most users adding a service want one of ~20 well-known images
(Postgres, Redis, Nginx, MariaDB, Traefik, Caddy, Mosquitto, Grafana,
Prometheus, Pi-hole, AdGuard Home, Vaultwarden, Jellyfin, …). A curated
catalogue turns a 5-minute form-filling exercise into a one-click action
and removes the "what's the right env var for the postgres password
again?" friction.

**Shipped:**

- **From template** tab in the new-project wizard: a searchable,
  category-grouped grid of **~126 starter recipes across 10 categories**
  (Databases & caches, Networking & proxies, Monitoring & dashboards, Media
  servers, Media automation, Files & productivity, Security & identity, Smart
  home & IoT, Developer & Git, Communication) shown with their real
  dashboard-icons logos — including full multi-service stacks (Nextcloud, Immich,
  Paperless-ngx, Authentik, Mattermost, …).
- Picking a template opens the project config panel reduced to the
  per-deployment bits: project name, target directory, and the template's
  declared **variables** (volume host paths, env values, exposed ports) — each
  with a hint and a one-click **generate** for secrets (passwords / tokens).
  Filling them resolves the template's `${KEY}` placeholders and posts to the
  same `create-project` endpoint the from-scratch tab uses (so it's still
  `docker compose config -q`-validated and written atomically).
- `create-project` became **multi-service** (the first service seeds the file,
  the rest are appended through the V7.1 comment-preserving editor), so recipes
  like WordPress + MariaDB bootstrap in one action.
- **Decisions vs. the original proposal:** the catalogue ships as structured,
  schema-validated `templates/*.json` (not `*.yml` + `meta.json`) — the wizard
  needs structured fields and per-variable hints anyway, and JSON deserialises
  straight into the create payload with zero parsing. Icons are pulled from the
  homarr-labs **dashboard-icons** CDN. Templates are served read-only at
  `GET /api/templates`; drop custom `*.json` into a mounted `/app/Data/templates`
  to extend/override the built-ins (same-`id` user recipe wins).
- **Optional follow-up (not done):** pulling community templates from a signed
  Git source. The user-override directory is the extension point for it.

---

### ✅ Phase V7.6 — Diff, dry-run, apply (image 7.6.0)

**Complexity:** Medium
**Value:** "Save" on YAML is scary if you can't see what changes. A
pre-save diff + `docker compose config` validation, with an explicit
**Apply** button to drive the compose-aware recreate, makes the editor
safe to use on production projects. This is the single phase that
separates a toy editor from one a homelabber will trust on their
always-on host.

**Shipped:**

- **Review & save…** on the Raw-YAML tab posts the proposed text to a
  diff endpoint that returns a **unified line diff** vs. the on-disk
  file, a **dry-run** `docker compose config -q` verdict (a throwaway
  `.next` candidate is validated then deleted — the original is never
  touched), and the **changed-services** set (computed by diffing the
  parsed per-service model; services only removed from the file are
  reported separately). The confirm dialog (a unified diff, not
  side-by-side — better in the modal and on phones) shows all three.
- On confirm: write atomically (V7.1 path) via **Save only**, or
  **Save & apply changed** which then runs `docker compose up -d
  <changed services>` (LocalSocket CLI or over SSH) so only the
  recreated containers are touched.
- Every save snapshots the previous file into
  `<project>/.stashboard/history/<stamp>__<file>` (last **20**, oldest
  pruned, duplicate-of-newest skipped; best-effort so it never blocks a
  save). A **History** tab lists revisions with **Restore**, which
  previews the same diff and re-validates + writes the revision back
  (snapshotting the current file first, so a restore is undoable). Each
  save / restore / apply also writes a metadata-only **ComposeChangeAudit**
  row (who / when / project + file / which services / outcome), surfaced
  read-only on the Audit page's **Compose changes** tab.

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

