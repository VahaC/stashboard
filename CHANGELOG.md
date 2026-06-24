# Changelog

All notable changes to Stashboard are recorded here. The format is loosely based
on [Keep a Changelog](https://keepachangelog.com/), and the project follows
[semantic versioning](https://semver.org/) — released as Docker image tags
`vahac/stashboard:X.Y.Z` (see [PUBLISHING.md](./PUBLISHING.md)).

## [8.4.0] — 2026-06-21

### Added
- **Create a VM from scratch (V8.4).** The VM analogue of V6.13.1 (LXC create):
  provision a brand-new QEMU/KVM virtual machine entirely from the Proxmox page. With
  guest-create enabled for a host, a **New VM** item joins **New LXC** in the host's
  actions menu. Together with clone (V8.2) and restore (V8.3), every "make a guest"
  path — create / clone / restore — is now mirrored for VMs as well as containers.
- **ISO discovery (V8.4).** A new `IProxmoxApiClient.ListIsoImagesAsync` lists the
  installable **`iso`** images across the node's iso-capable storages (the VM analogue
  of `ListTemplatesAsync`, sharing one storage-content helper); the controller exposes a
  `GET …/qemu/isos` read for the create modal's **Installation media** dropdown.
- **QEMU create endpoint (V8.4).** A new `CreateQemuAsync` + `ProxmoxQemuCreate` spec +
  `ProxmoxQemuCreateValidator` POST VM **hardware** to `POST /nodes/{node}/qemu` — a SCSI
  system disk (`scsi0=<storage>:<GiB>` on `scsihw=virtio-scsi-pci`), a virtio NIC
  (`net0`), firmware (`bios`) + chipset (`machine`), an OS-type hint, an optional install
  CD-ROM (`ide2=<volid>,media=cdrom`) and a boot order — then poll the task UPID for real
  success/failure. OVMF (UEFI) auto-provisions its EFI vars disk (`efidisk0`) on the same
  storage.
- **New VM UI (V8.4).** A `QemuCreateModal` reusing the **exact** Docker container modal
  shell (`container-modal-*` / `service-modal-*`) — identity, installation media, system
  disk + storage, resources, NIC bridge, and firmware / machine / OS type — with
  client-side guards mirroring the server validator.

### Changed
- **Create gating generalised to guests (V8.4).** The existing
  `Stashboard:AllowProxmoxCreate` master switch and per-host `AllowCreate` opt-in now gate
  **both** LXC and VM create (no new switch); **Settings → Create LXC** is renamed
  **Settings → Create guest**. The `ProxmoxCreateAuditEntity` / `ProxmoxCreateAudits`
  table stays guest-kind-agnostic — VM and LXC creates share one audit history, surfaced
  on the Audit page's **Create** tab.

## [8.3.0] — 2026-06-21

### Added
- **Restore a VM from a backup (V8.3).** The VM analogue of V8.1 (LXC restore):
  re-create a QEMU/KVM virtual machine from an existing `vzdump` backup archive,
  entirely from the Proxmox page. With guest-restore enabled for a host, a **Restore
  VM** item joins **Restore LXC** in the host's actions menu. Together with create
  (V6.13.1) and clone (V8.0/V8.2), every "make a guest" path is now mirrored for VMs
  as well as containers.
- **Kind-aware backup discovery (V8.3).** `IProxmoxApiClient.ListBackupsAsync` gained a
  `qemu` flag selecting **`vzdump-qemu-*`** archives (VM) vs `vzdump-lxc-*` (LXC) from
  the node's backup-capable storages; the controller exposes a `GET …/qemu/backups`
  read alongside `…/lxc/backups`. PBS datastores (`pbs:`) stay out of scope.
- **QEMU restore endpoint (V8.3).** A new `RestoreQemuAsync` POSTs **`archive=<volid>`**
  (+ `force=1` only when overwriting an existing — necessarily stopped — vmid, optional
  `storage=` / `name=`) to `POST /nodes/{node}/qemu` and polls the task UPID — the QEMU
  restore shape, distinct from the LXC's `ostemplate=…` + `restore=1`. The controller
  routes both kinds through a shared `qemu`-flag `RestoreGuestAsync` handler with a
  `/qemu/restore` route (mirroring the V8.2 clone/snapshot split); the kind-aware
  `ProxmoxLxcRestoreValidator` checks the expected archive marker.
- **Reused restore UI (V8.3).** `LxcRestoreModal` takes an `isVm` prop (exactly like
  `LxcCloneModal`): VM wording, the `vzdump-qemu-*` archive list, `images` storage, a
  **Name** field (posts as `name`), and the LXC-only **unprivileged** option hidden.
  The overwrite double-confirm names the target VM. The central Audit tab is generalised
  to **Guest restore** (CT/VM derived from the archive name).

### Notes
- Gating is unchanged and shared with V8.1: the same `Stashboard:AllowProxmoxRestore`
  master switch (**Settings → Restore guest**) + per-host `ProxmoxConnection.AllowRestore`
  opt-in (both off by default) gate VMs too — deterministic `403`s before any host call,
  a `409` on a vmid collision or an overwrite of a running/absent target, and a host
  rejection surfaced verbatim as a `502`. No new database table — `ProxmoxRestoreAudit`
  records the vmid / archive irrespective of guest kind.
- Out of scope (deferred), same exclusions as V8.1: restoring from Proxmox Backup
  Server datastores, `--bwlimit` tuning, and live-restore.

## [8.2.0] — 2026-06-20

### Added
- **Clone & snapshot QEMU VMs (V8.2).** The V8.0 **clone** and **snapshot**
  workflows now work for QEMU/KVM virtual machines, reusing the exact V8.0 surfaces
  (gating, audit, modals, double-confirm dialogs) rather than a parallel system. On a
  **VM** card the **Clone** button (Lifecycle row) and the **Snapshots** + **Audit**
  tabs appear once the clone/snapshot feature is enabled for the host.
- **Kind-aware API client (V8.2).** The five V8.0 `IProxmoxApiClient` methods became
  kind-aware via shared private helpers behind thin `lxc`/`qemu` wrappers (mirroring
  `GetLxc`/`GetQemuStatusAsync`): `CloneLxc`/`CloneQemuAsync`,
  `ListLxc`/`ListQemuSnapshotsAsync`, `CreateLxc`/`CreateQemuSnapshotAsync`,
  `RollbackLxc`/`RollbackQemuSnapshotAsync`, `DeleteLxc`/`DeleteQemuSnapshotAsync` —
  each routed to `…/qemu/{vmid}/clone` or `…/qemu/{vmid}/snapshot[/{name}[/rollback]]`
  and polling the task UPID via the existing `PollTaskAsync`. A VM clone POSTs the new
  name as `name` (not `hostname`) and a full clone accepts an optional disk `format`
  (`raw` / `qcow2` / `vmdk`).
- **Running-memory (`vmstate`) for VM snapshots (V8.2).** Unlike an LXC snapshot
  (whose endpoint rejects it), a QEMU snapshot can save the live RAM state. The
  modal re-introduces an **Include running memory state (RAM)** toggle, shown only
  for a **running VM**, that sends `vmstate=1`; the LXC path still never sends it.
- **Shared controller handlers + routes (V8.2).** Clone/snapshot now flow through
  shared `qemu`-flag handlers with `/qemu/...` routes (mirroring the
  `DestroyLxc`/`DestroyQemu` split). No new gate and no new audit table —
  `ProxmoxCloneAuditEntity` records the action irrespective of guest kind, and the
  `clone-audit` read serves both kinds. The frontend threads `kind` through the six
  V8.0 hooks and drops the `!isVm` guards; `LxcCloneModal` / `SnapshotConfirmDialog`
  are reused with VM wording (`Name` vs `Hostname`, "VM" vs "container") and the
  kind-gated disk-format and `vmstate` controls.

### Notes
- Gating is unchanged: the same `Stashboard:AllowProxmoxClone` master switch
  (**Settings → Clone/snapshot**) + per-host `ProxmoxConnection.AllowClone`
  opt-in (both off by default) gate VMs too — deterministic `403`s before any host
  call, a `409` on a vmid collision, and a host rejection surfaced verbatim as a `502`.
- The running-guest clone guard is kept kind-aware: Proxmox won't clone a running
  guest from its live state, so the modal requires a source snapshot (or a stopped
  guest) first.
- Out of scope (deferred), same exclusions as V8.0: cross-node clone migration,
  scheduled snapshots, and nested snapshot trees beyond a flat list.

## [8.1.0] — 2026-06-20

### Added
- **Restore an LXC from a backup (V8.1).** A Proxmox host's header menu gains a
  **Restore LXC** button that opens an `LxcRestoreModal` (reusing the
  `LxcCreateModal` styling) to re-create a container from a `vzdump` archive. A new
  `IProxmoxApiClient.ListBackupsAsync` lists the restorable `vzdump-lxc-*` archives
  across the node's **backup-capable** storages — the storages whose content
  advertises `backup`, then `GET …/storage/{storage}/content?content=backup` filtered
  to LXC archives (PBS datastores are out of scope and skipped) — surfaced in a
  dropdown with each backup's guest id / timestamp / size.
- **Restore reuses the create path (V8.1).** `CreateLxcAsync` branches on a new
  `Restore` flag to `POST /nodes/{node}/lxc` with `ostemplate=<backup volid>` +
  `restore=1`, emitting the default `storage` override rather than a `rootfs` spec
  (the archive carries the rootfs sizes) and skipping the template-only fields
  (password / SSH keys / net / DNS). The target `vmid` defaults to the next-free id
  (with a one-click **Use original** for the archive's own id), and the
  **unprivileged / start** toggles are honoured. The returned task UPID is polled via
  the existing `PollTaskAsync`, so success/failure is real rather than "accepted".
- **Overwrite guard (V8.1).** Restoring **over** an existing vmid (`force=1`) is
  destructive — it replaces that container — so it is gated behind the
  **stopped-guest** check and an explicit double-confirm naming the target (reusing
  the V6.13 destroy-dialog pattern). A new-vmid restore over an existing id is a clean
  `409`; an overwrite of a running (or missing) target is refused before any host call.
- **Gating, audit + Audit tab (V8.1).** Restore is double-gated exactly like create —
  the `Stashboard:AllowProxmoxRestore` master switch (**Settings → Restore LXC**) plus
  the per-host `ProxmoxConnection.AllowRestore` opt-in, both off by default, with
  deterministic `403`s returned **before** any Proxmox call. A successful restore
  re-scans the host so the card appears immediately. Every attempt that reaches the
  host writes a `ProxmoxRestoreAuditEntity` row (who / when / host / node / vmid /
  backup volid / overwrote? / success / error), surfaced on the Audit page's new
  **LXC restore** tab; a host rejection surfaces verbatim as a `502`.

### Notes
- Out of scope (deferred): restoring from **Proxmox Backup Server** datastores
  (`pbs:` volumes need PBS auth/namespaces), bandwidth/`--bwlimit` tuning, and
  live-restore.

## [8.0.0] — 2026-06-19

### Added
- **Clone an LXC (V8.0).** A guest's **Lifecycle** action row gains a **Clone**
  button that opens an `LxcCloneModal` reusing the `LxcCreateModal` styling: a new
  `vmid` (defaulted from `/cluster/nextid`), hostname, optional target storage,
  **full vs linked** clone, and — when the source has snapshots — an optional
  **source snapshot**. It calls a new `IProxmoxApiClient.CloneLxcAsync`
  (`POST /nodes/{node}/lxc/{vmid}/clone`) and polls the returned task UPID via the
  existing `PollTaskAsync`, so success/failure is real rather than "accepted".
  Validation mirrors create (vmid range + not already on the host → a clean 409);
  on success the host is re-scanned so the cloned card appears immediately.
- **Snapshots tab on the LXC modal (V8.0).** A new **Snapshots** tab lists an LXC's
  snapshots (`GET …/snapshot`, the synthetic `current` pseudo-entry filtered out,
  newest first) and can **take** one (`POST …/snapshot`, name + optional
  description — an LXC snapshot has no running-memory option), **roll back** to one
  (`POST …/snapshot/{name}/rollback` — double-confirmed, it discards newer state),
  and **delete** one (`DELETE …/snapshot/{name}`). Each is a task UPID polled to a
  terminal state. The two destructive actions go through a `SnapshotConfirmDialog`
  mirroring the destroy double-confirm.
- **Gating, audit + Audit tab (V8.0).** Clone and the three snapshot writes are
  double-gated exactly like create/destroy — the `Stashboard:AllowProxmoxClone`
  master switch (**Settings → Clone/snapshot LXC**) plus the per-host
  `ProxmoxConnection.AllowClone` opt-in, both off by default, with deterministic
  `403`s returned **before** any Proxmox call. Every clone / snapshot / rollback /
  delete that reaches the host writes a `ProxmoxCloneAuditEntity` row (who / when /
  host / node / vmid / action / target / success / error), surfaced on a new
  per-guest **Audit** tab; a host rejection surfaces verbatim as a `502`.

### Notes
- Out of scope (deferred): cross-node / cross-cluster clone migration, scheduled
  snapshots, and snapshot trees beyond a flat list.

## [7.9.0] — 2026-06-19

### Added
- **Link Proxmox guests to a service (V7.9).** A service on the dashboard can now
  link **Proxmox LXCs and VMs** alongside the Docker containers it already tracks.
  The `ServiceModal` gains a **Proxmox** section parallel to the Docker one: pick a
  Proxmox connection, then add one or more guests from a picker fed by the
  already-scanned guests (each shown with its live state pill and pending-update
  count); linked guests list with an unlink action. The link is a many-to-many
  join (`WebResourceProxmoxGuestLinkEntity`, keyed on
  `(WebResourceId, ProxmoxConnectionId, VmId)`) — **not** ownership: guests stay
  auto-discovered and owned by their connection, deleting a service drops its links
  while the guest lives on, and the link is owner-scoped (a foreign connection or
  guest is rejected, the `VmId == 0` node row is refused). No new guest
  discovery — it only links what a scan already found.
- **Proxmox update badge on the service card (V7.9).** `WebResourceMapper` gains an
  `AggregateProxmoxStatus` sibling to `AggregateDockerStatus` that reduces the
  linked guests' state to one service-level `ProxmoxUpdateStatus` (per guest:
  **updates available** when `PendingUpdates > 0`, else up-to-date / disabled /
  unknown / error for monitoring-off-or-snoozed / never-checked / probe-failed),
  with the same actionable-first precedence (`UpdateAvailable > Error > UpToDate >
  Disabled > Unknown`). The DTO carries it **independently** of the existing
  `DockerUpdateStatus`, so a service that links both shows **both badges at once** —
  reusing the existing badge component family, no new visual system. The badge is
  read-only here; one-click "Update now" stays on the Proxmox page (V6.7.1).
- **Docker container → Proxmox guest cross-link (V7.9).** A container can record the
  **Proxmox guest it physically runs inside** (the common homelab case — Docker
  living in an LXC or VM). A **"Runs on"** picker in the `ContainerModal` sets it,
  and the container card shows a small **"on `<guest>`"** chip that deep-links to
  that Proxmox guest's modal. Stored on a `ContainerProxmoxLinkEntity` keyed on
  `(UserId, DockerConnectionId, ContainerName)` — the same shape
  `ContainerIconEntity` uses, so it works for **any** container (watched or not)
  and survives container churn. `GET containers` builds a `linkByContainer` map; a
  link whose target guest is missing yields no chip rather than an error.
- **Backup/restore covers both link sets (V7.9).** The service↔guest and
  container↔guest link tables are now included in the JSON export/import. Links
  reference guests by their stable `(connection, vmid)` natural key, so they survive
  a restore even though guest rows are re-discovered rather than exported.

## [7.8.0] — 2026-06-17

### Added
- **Container card icons (V7.8).** Every container card on `/docker` (and inside
  `ServiceModal → Docker`) now leads with a service icon, so the page reads at a
  glance instead of as a wall of identical cards. The avatar resolves per card,
  highest priority first:
  - **Custom upload** — set your own image for any container from the container
    modal's **Overview** tab (preview + upload + **Reset to auto**). The image is
    read in the browser and sent as a base64 data URI (JSON, no file written to
    disk), stored on a row keyed by `(connection, container name)` so it survives
    a recreate.
  - **Official icon (auto)** — otherwise the backend derives a slug from the image
    reference (e.g. `lscr.io/linuxserver/jellyfin:latest` → `jellyfin`, with a
    small alias table such as `postgres → postgresql` and `pihole → pi-hole`) and
    resolves it against the [homarr-labs **dashboard-icons**](https://github.com/homarr-labs/dashboard-icons)
    set. Results are cached for 24h — **misses too**, so the CDN isn't re-hit on
    every refresh.
  - **Placeholder** — when neither resolves, a box glyph with the container's
    initials.

  The icon slot lives on the shared `EntityCard`, reused across both the Docker
  and Proxmox cards.
- **Proxmox card icons (V7.8).** The same treatment lands on Proxmox **LXC / VM**
  cards: a **custom upload** per guest (from the LXC modal's Overview tab), else
  the **official OS icon** auto-resolved from the guest's `ostype`
  (`debian` / `ubuntu` / `alpine` / … for containers, generic `linux` / `windows`
  for VMs), else the placeholder. The OS type is read from the guest config once
  and cached on the guest row, so the scheduled scan stays cheap. Custom icons are
  keyed by `(connection, vmid)` so they survive re-discovery. The leading avatar
  also sits **under** the card header (left of the image / status lines), and
  cards in a row now share one height.
- **Backup/restore covers custom icons (V7.8).** Custom container/guest icon
  uploads are now included in the JSON export and restored on import (re-attached
  to their connection by container name / VmId). Auto-resolved official icons are
  not exported — they re-resolve on the target instance.

## [7.7.0] — 2026-06-17

### Added
- **Dependency graph (V7.7).** The Compose modal gains a **Graph** tab: a
  lightweight, read-only SVG diagram of the project. Nodes are services (with
  their live runtime state pill), directed edges are `depends_on` (the arrow
  points to the dependency, drawn with dependencies layered below their
  dependents), shared networks are rendered as translucent **group boxes** behind
  the services that share them, and named volumes are listed in a side legend with
  the services that mount each one. Clicking a node jumps straight to that
  service's tab. Built with hand-rolled SVG (no graph-library dependency) so it
  matches the rest of the UI; a simple layered layout that stays readable at the
  "a dozen services" scale the feature targets, and tolerates cycles without
  hanging.
- **Compose linter (V7.7).** Every project load **and every save** now runs a
  pure linter over the file, with findings rendered **inline on each service
  card** and aggregated into a **Health badge** next to the project name
  (Healthy / N warnings / N errors). The rules:
  - **Port collisions** (error) — two services publish the same host
    port/protocol on the same interface (distinct specific host IPs and
    different protocols don't clash; container-only ports are ignored).
  - **`depends_on` cycles** (error) — a cycle in the dependency graph, reported
    on each service in the loop.
  - **Missing healthcheck** (error) — a service is depended on with
    `condition: service_healthy` but declares no (enabled) healthcheck.
  - **Bind mounts escaping the project root** (warning) — a relative bind source
    that climbs above the root (or a `~` home path); absolute system mounts like
    `/etc/localtime` are intentionally left alone.
  - **Deprecated keys** (warning) — `links:` / `volumes_from:` on a service, or a
    top-level `version:`.
  - **`:latest` image tags** (warning, not error — many homelab setups pin
    `latest` deliberately and use update monitoring to watch digests) — also
    flags an image with no tag at all, and a variable tag that *defaults* to
    latest (`${VAR:-latest}`); a bare `${VAR}` or a variable defaulting to a real
    version is left alone.

## [7.6.0] — 2026-06-16

### Added
- **Diff, dry-run & apply (V7.6).** Saving the Raw-YAML editor is no longer
  blind. **Review & save…** first computes a **unified diff** between the file on
  disk and your edit, runs `docker compose config -q` as a **dry-run validation**
  (writing a temp candidate, never touching the original), and shows which
  services the change touches — all in a confirm dialog. From there you can
  **Save only** or **Save & apply changed**, the latter firing a compose-aware
  `docker compose up -d <changed services>` so only the services whose definition
  actually changed are recreated (computed by diffing the parsed per-service
  model; services merely *removed* from the file are flagged but never silently
  stopped). Works on LocalSocket (in-container CLI) and Ssh (remote host).
- **Revision history + restore.** Every save snapshots the previous file into
  `<project>/.stashboard/history/<timestamp>__<file>` (last **20** kept, oldest
  pruned, duplicate-of-newest skipped) — best-effort, so a history failure never
  blocks the save. A new **History** tab lists the revisions; **Restore** previews
  exactly what would change (the same diff + dry-run dialog) and, on confirm,
  re-validates and writes the revision back — snapshotting the current file first,
  so a restore is itself undoable.
- **Compose-change audit trail.** Each save / restore / apply writes a
  metadata-only row (who, when, which project + file, which services, success /
  error) to SQLite, surfaced read-only on the Audit page's new **Compose changes**
  tab. No file content is stored in the database — that lives in the on-disk
  history.

## [7.5.0] — 2026-06-15

### Added
- **Service templates / starter recipes (V7.5).** The New-project wizard gains a
  **From template** tab: a searchable, category-grouped catalogue of **~126
  well-known self-hosted images across 10 categories** (Databases & caches,
  Networking & proxies, Monitoring & dashboards, Media servers, Media automation,
  Files & productivity, Security & identity, Smart home & IoT, Developer & Git,
  Communication) shown with their real dashboard-icons logos — Postgres, Redis,
  Nginx, Traefik, Pi-hole, Jellyfin, the full *arr stack, Nextcloud, Immich,
  Paperless-ngx, Vaultwarden, Home Assistant, Gitea, and many more, including
  full multi-service stacks (app + database + cache). Picking one opens a project config panel pre-filled and
  reduced to the per-deployment bits: project name, target directory, and the
  template's declared **variables** (volume host paths, env values, exposed
  ports) — each with a hint, and a one-click **generate** for secrets such as
  database passwords and admin tokens. Filling them resolves the template's
  `${KEY}` placeholders and posts the result to the same `create-project`
  endpoint the from-scratch tab uses, so the file is still validated by
  `docker compose config -q` and written atomically.
- **Multi-service project bootstrap.** `create-project` now writes a list of
  services (the first seeds the file, the rest are appended through the same
  comment-preserving editor), so a template like WordPress + MariaDB comes up in
  one action. The from-scratch tab is unchanged (it sends a single service).
- **Extensible catalogue.** Templates ship as validated `templates/*.json` files
  baked into the image; drop your own `*.json` into a mounted
  `/app/Data/templates` to extend or override the built-ins (a recipe with the
  same `id` wins). Malformed files are skipped, never fatal. Served read-only at
  `GET /api/templates`.

## [7.4.1] — 2026-06-14

### Added
- **Create a whole project from scratch (V7.4.1).** Completes the V7.4
  bootstrapper: a **New project** button on every (non-TCP+TLS) host header opens
  a dialog that writes a brand-new `docker-compose.yml` from nothing. You name
  the project (validated to Compose's lowercase `^[a-z0-9][a-z0-9_-]*$` rule),
  give a target **directory** (free-text path as the connection sees it — inside
  the Stashboard container for a local socket, on the remote host for SSH, with
  an opt-in `mkdir -p`), and define the first service with the same shared field
  controls as the editor. The file is written with a top-level `name:` so the
  project name is deterministic, validated by `docker compose config -q` and
  written atomically. **Create and run** then runs `docker compose up -d`
  (local or over SSH) and opens the new project's modal — where you can keep
  adding services (V7.4); **Create only** just writes the file. The flow refuses
  to clobber a directory that already holds a Compose file (open it and use
  Add service instead).

## [7.4.0] — 2026-06-14

### Added
- **Create a new service from scratch (V7.4).** The Compose modal gains an
  **Add service** tab — a structured wizard that turns the editor from a
  YAML-renderer into a project bootstrapper. It reuses the exact field controls
  of the existing-service editor (image with the registry tag dropdown, ports,
  volumes, env, labels, restart, command/entrypoint, user, working_dir, and the
  V7.2 resource picker), plus a service-name field validated for uniqueness and
  Compose key shape (`^[a-zA-Z0-9._-]+$`); an image is required. The new block is
  appended at the end of the `services:` map by the same comment-preserving,
  `docker compose config -q`-validated, atomic writer from V7.1 — so the rest of
  the file survives byte-for-byte and the entry lands at the existing services'
  indentation column (2- vs. 4-space). Adding/pasting several services supports
  **multiple containers in one file**.
- **Save and run.** After appending the block, the wizard runs
  `docker compose up -d` against the whole project so the new container comes up
  alongside its siblings — **LocalSocket** via the in-container Compose CLI (the
  V5.2 path) **and SSH** on the remote host over the connection's existing
  credentials. The modal then switches to the new service's tab, where every
  field is editable just like an existing service. A **Save only** action writes
  the file without starting anything.
- **Raw YAML tab.** A plain-text editor for the project's whole Compose file —
  write or paste by hand (handy for bulk edits or pasting a ready stack), with
  the same validated, atomic save and an optional run. Available on existing
  projects too, and the escape hatch for files the structured editor marks
  read-only.

## [7.3.0] — 2026-06-14

### Added
- **Top-level resources editor (V7.3).** The Compose modal gains a separate
  **Shared resources** tab (alongside the per-container tabs) holding
  four sections — **Networks · Volumes · Secrets · Configs** — each with a
  plain-language "what is this / when do I need it" line at the top. This turns
  the V7 editor from a per-container form filler into a real project editor.
  Each section is a CRUD list backed by the same comment-preserving,
  `docker compose config -q`-validated, atomic YAML writer introduced in V7.1,
  now extended to splice **top-level** map entries one at a time: editing or
  adding one network/volume/secret/config rewrites only that entry's lines, so
  sibling entries, key order and comments survive byte-for-byte. The same safety
  refusals apply (YAML anchors, flow-style or merge-key sections are left to
  manual editing).
  - **Network** editor — driver (`bridge` / `overlay` / `macvlan` / …), subnet,
    gateway and driver options, plus an optional name override. Warns when a new
    subnet **overlaps a network already defined on the host** (read live from the
    Docker Engine network list, cached ~60 s).
  - **Volume** editor — driver, driver options and name override, and surfaces
    each named volume's **actual on-disk size** from the host's `/system/df`
    (so `postgres_data` shows as 4.2 GiB before you consider deleting it).
    Best-effort: the size is simply omitted when the daemon can't be reached for
    `df`.
  - **Secret / config** editor — external vs. host `file:` path, with a name
    override.
- The Compose viewer/parser now reads the **full options** of top-level
  networks/volumes/secrets/configs (driver, driver_opts, ipam subnet/gateway,
  external, name, file), where it previously surfaced only their names. A network
  with more than one `ipam.config` entry is reported as an unsupported feature so
  the read-only banner shows instead of the editor silently dropping subnets.

### Notes
- The secrets/configs editor manages the **Compose declarations** (external vs.
  `file:` path); the file material itself is not stored in Stashboard. A built-in
  encrypted-at-rest secret store was considered for this phase and deferred as a
  larger, security-sensitive change.

## [7.2.1] — 2026-06-14

### Fixed
- **Proxmox Backup Server (PBS) disk SMART + health (V7.2.1).** Three PBS-only
  bugs surfaced on real PBS hardware, all rooted in PBS naming a field or
  parameter differently from PVE:
  - **Per-disk SMART returned 400 ("host unreachable").** The SMART read sent the
    `/dev/`-prefixed path (`/dev/sda`) PVE accepts, but PBS validates `disk`
    against its block-device name schema and wants the bare name (`sda`) — the
    mismatch failed PBS's regex with a 400 that surfaced as a misleading "Proxmox
    host unreachable" banner. The `/dev/` prefix is now stripped for PBS, so SMART
    attributes load. As defence-in-depth, a genuine per-disk `smartctl` failure
    (USB bridge, a disk that can't report SMART) now surfaces the host's own
    reason inline under that disk instead of a 502 — the host is reachable, only
    that one disk's read failed.
  - **Disk type blank + SMART health shown as "UNKNOWN".** `disks/list` health
    and type were read from the PVE keys `health` / `type`, but PBS names the same
    columns `status` (`passed`) / `disk-type` (`hdd`/`ssd`). Both key spellings
    are now read, so the health badge shows **PASSED** and the disk type shows on
    PBS.
  - **Stale "API unreachable" banner over an online card.** A connection-level
    scan error (e.g. a brief "No route to host" from a scan that ran while the
    host was momentarily down) lingered until the next successful scan, so the
    node card could show the host green/online while a red "unreachable" banner
    sat above it. The banner is now suppressed whenever the live node-status poll
    currently succeeds.

## [7.2.0] — 2026-06-11

### Added
- **Compose resource constraints editor (V7.2).** Each service tab now has a
  resource-constraints section below the basic-fields form, folded into the
  same atomic save (`ComposeResourcesForm`):
  - **Nine fields editable:** `cpus`, `mem_limit`/`memory`, `mem_reservation`,
    `pids_limit`, `cpu_shares`, `ulimits`, `oom_kill_disable`, `oom_score_adj`,
    `shm_size`. cpu/mem/pids follow the file's convention —
    `deploy.resources.limits`/`.reservations` (modern, default for new) **or**
    legacy top-level `cpus`/`mem_limit`/… — detected per file and **never
    mixed**; legacy mode disables CPU reservation (v2 has no such key). The
    other knobs are always top-level, behind an "Advanced" disclosure.
  - **Numeric inputs + sliders bounded by the host's real capacity** (CPU count
    and RAM from the V3.5 `docker stats` stream — `onlineCpus` /
    `memoryLimitBytes`), with a capacity panel — *"Host capacity … · allocated
    by other containers … · this service draft …"* — and an inline over-commit
    warning. The "allocated by others" figure sums the running containers'
    `HostConfig` (`NanoCpus`/`Memory`) via `inspect` (the edited project's own
    containers excluded), cached server-side (`IMemoryCache`, ~60 s per
    connection).
  - **Round-trip preserved (`ComposeFileEditor`):** the `deploy.resources`
    subtree is rewritten as a unit, leaving sibling `deploy` keys (replicas /
    placement / …) byte-for-byte; numeric/boolean values render unquoted;
    untouched fields stay zero-diff. Anchored `deploy.resources` and GPU device
    reservations are refused / flagged read-only rather than corrupted.

## [7.1.1] — 2026-06-10

### Changed
- **Compose viewer/editor is now a modal, scoped to one project (V7.1.1).** The
  V7.0/V7.1 surface was a standalone page reached from a whole-host **Compose**
  button — which made little sense, since a host runs *many* projects plus
  standalone containers. It's reworked into a focused modal:
  - The **whole-host Compose button is gone**, and so are the
    `/projects/{id}/compose` and `/projects/{id}/compose/{project}` routes (and
    the project-picker page) — `ComposeProjectPage` is deleted.
  - A **Compose** button now appears on **every Compose project's group header**.
    Single-container projects are **no longer collapsed** into the "Other
    containers" bucket (the v5.4 1-of-1 demotion is lifted) — each shows as its
    own named group with the Compose / Update project actions. The "Other
    containers" bucket is now only genuinely label-less containers, which carry
    no Compose button. A non-compose container shows no button.
  - The modal carries a **compact project header strip** (service / network /
    volume / secret counts + the top-level networks / volumes / secrets / configs)
    and then **one tab per service**, each tab wearing the matched live
    container's runtime-state badge. The tab body is the same V7.1 editable
    basic-fields form (image / ports / volumes / env / labels / restart /
    command / entrypoint / user / working_dir) plus a read-only block for the
    fields the form doesn't cover (depends_on / networks / limits / …). Files
    using unsupported constructs stay **read-only** with the same "file uses X"
    banner and a details-only view.
  - No backend, contract, or round-trip change — the discovery endpoint, the
    per-service edit endpoint, and the byte-for-byte YAML splicing are
    untouched. This is a pure front-end UX rework.

## [7.1.0] — 2026-06-10

### Added
- **Visual Compose editor — edit basic service fields (V7.1).** The V7.0 viewer
  grows its write path: an **Edit** button on every service card opens a modal
  covering the 80 % of Compose edits reached for daily — **image** (with a
  registry **tag dropdown** + free text), **ports** (host / container /
  protocol rows with live **collision checks** against the rest of the
  project), **volumes** (named-volume suggestions + a warning on host paths
  outside the project directory), **environment** (key/value table with a
  password-style mask for `*_KEY` / `*_TOKEN` / `*_PASSWORD` / `*_SECRET`
  names), **labels**, **restart policy**, **command / entrypoint** (string or
  `["exec", "form"]`), **user** and **working_dir**.
  - **Round-trip fidelity (the make-or-break bar).** Edits are applied by
    *splicing the raw YAML text* at the exact token spans of the changed keys
    (located via YamlDotNet's event stream) — comments, key order, quoting and
    blank lines everywhere else survive **byte-for-byte**, and an untouched
    field is a guaranteed zero-diff. Decision + alternatives documented in
    [docs/adr/0001-compose-yaml-round-trip.md](./docs/adr/0001-compose-yaml-round-trip.md).
  - **Atomic, validated save.** The backend writes `<file>.next`, runs
    `docker compose -f <file>.next config -q`, and only on success renames it
    over the original (same-directory rename — atomic). Validation is
    **blocking**: no Compose CLI means the save is refused; a validation
    failure rolls back and surfaces the CLI's raw stderr in the modal. Works
    on **both transports** — inside the Stashboard container for LocalSocket
    connections and over the connection's SSH credentials (upload + validate +
    rename in one round trip) for SSH connections.
  - **Safety refusals instead of silent damage:** files using `x-*` /
    `extends` / YAML merge keys stay read-only (409 + the V7.0 banner);
    flow-style service bodies and anchored (`&name`) values are refused with a
    typed error (editing an `*alias` use-site is fine). Saving never touches
    running containers — apply via **Update project** afterwards.
  - `PUT /api/docker/connections/{id}/compose/{project}/services/{service}`
    (422 + stderr on validation failure) and
    `GET …/compose/image-tags?image=` (anonymous registry tag listing via the
    V2.1 `IRegistryClient`).

### Changed
- **⚠️ Compose projects are now discovered per project from container labels
  (V7.1) — the V7.0 per-connection "Compose project path" is gone.** One Docker
  host runs many Compose projects plus standalone containers, so a single path
  per connection was simply the wrong model. Stashboard now reads each
  container's standard `com.docker.compose.project` +
  `…project.working_dir` labels: the **Compose** button on a host header opens
  a **project picker** (`/projects/{id}/compose`) listing every discovered
  project, each project group header on the Docker page links straight to its
  own viewer/editor (`/projects/{id}/compose/{project}`), and standalone
  containers — which have no Compose file — correctly show no Compose
  affordance at all.
  - **SSH connections need zero configuration** — the label path is used on
    the host as-is.
  - **LocalSocket connections** get an optional **Compose path mapping**
    (host prefix → container prefix) on the connection form for when the
    stacks root is bind-mounted at a different path inside Stashboard
    (e.g. `/opt/stacks` → `/compose`); mount it at the same path on both
    sides and no mapping is needed.
  - **Migration note:** the `ComposeProjectPath` column is dropped (its value
    was one project's in-container path — wrong as a host-side prefix, so it
    is not carried over). LocalSocket operators who relied on V5.2's
    compose-aware updates should set the new mapping once (or re-mount at the
    same path); pre-7.1 backups import cleanly with the old field ignored.

### Fixed
- **Compose-aware "Update now" / "Update project" now work per project
  (V7.1).** V5.2/V5.4 resolved the project directory from the connection's
  single configured path, so on a host with several Compose projects the
  compose-aware update only ever worked for one of them (and could even run
  against the wrong project root). Both updaters now resolve the directory
  from the target containers' own `working_dir` labels (translated through
  the connection's path mapping), so every project updates against its own
  compose file.

## [7.0.0] — 2026-06-10

### Added
- **Visual Compose viewer — read-only (V7.0).** The foundation of the V7 visual
  Compose editor, with **no edit risk**: a Compose project Stashboard already
  knows about (a V5.2 bind-mounted project directory on a Docker connection) can
  now be **viewed** as a card-per-service grid. A new **Compose** button on the
  connection's header on the Docker page opens `/projects/{id}/compose`.
  - **Backend:** `GET /api/docker/connections/{id}/compose` locates the Compose
    file in the connection's `ComposeProjectPath` (spec precedence:
    `compose.yaml` → `compose.yml` → `docker-compose.yaml` →
    `docker-compose.yml`), parses it with **YamlDotNet**, and returns a typed
    `ComposeProjectResponse` mirroring the viewer subset of the Compose v3.x
    spec: services (image, container name, restart policy, ports, mounts,
    environment, env files, depends_on, networks, `deploy.resources`
    limits/reservations) plus the top-level **networks / volumes / secrets /
    configs** name lists. Long-form ports/volumes are normalised to the short
    syntax. Owner-scoped; `400` when no project path is configured, `404` when
    the directory/file is missing, `422` for unparseable YAML.
  - **Two read transports.** For **Local socket** connections the path is the
    V5.2 in-container bind mount. For **SSH** connections the **Compose project
    path** field is now available too — there it is a directory **on the remote
    Docker host**, and the viewer fetches the file over the connection's
    existing SSH credentials in one probe-and-`cat` round trip (read-only —
    nothing is executed beyond locating and printing the file; SSH failures
    surface as `502`). The compose-aware **"Update now"** recreate stays
    LocalSocket-only, exactly as in V5.2 — an SSH host keeps the raw recreate.
    TCP+TLS connections expose no file access and keep no path.
  - **Frontend:** one collapsible card per service reusing the **same**
    `EntityCard` / state-pill family as the Docker page — each card wears the
    **live runtime state** of the container matched by its compose-service
    label (or a neutral *not deployed* pill). Cards expand into the
    `container-modal-summary` detail list (mounts, environment, limits, …);
    **Edit** affordances are rendered but disabled until V7.1.
  - **Hard fail-safe:** files using constructs the future editor can't
    round-trip yet — `x-*` extension fields, `extends`, YAML merge keys
    (`<<:`) — surface a **"Read-only — file uses X"** banner naming each
    construct instead of silently dropping data; plain anchor/alias pairs are
    resolved normally. Purely additive on the read side: no `docker compose`
    invocation, no write path, no entity-model changes.
    See [ROADMAP](./ROADMAP.md) Phase V7.0.

## [6.15.1] — 2026-06-10

### Fixed
- **Backup import no longer duplicates services (V6.15.1).** Importing a backup
  into an instance that already held the same services (e.g. restoring a staging
  export onto prod) created a fresh copy of **every** service, because services —
  unlike categories, tags and connections — were never merged. Services are now
  **merged by name + main URL**: an existing match is left untouched and reused
  for Docker-watch links, so a re-import is idempotent. A service with the same
  name but a different URL is still imported as new. The import's return value
  counts only newly created services.
- **Deleting a used Docker connection now names the blocking services.** The
  delete refusal used to say only "N service(s) use this connection. Reassign
  them first." — with a service whose Docker connection was assigned in the
  service modal (not via container links), the user was left guessing which one.
  The 409 from `DELETE /api/docker/connections/{id}` now lists the service names
  (`…use this connection: OMV, Jellyfin. Reassign them first.` + a `services`
  array), and the UI surfaces the server message instead of composing a
  count-only one client-side.

## [6.15.0] — 2026-06-09

### Added
- **Proxmox connections in backup / restore (V6.15).** The config backup
  ([`BackupService`](src/Stashboard.Api/Services/BackupService.cs), endpoints
  `GET /api/backup/export` + `POST /api/backup/import`) exported categories, tags,
  Docker connections, services, Docker watches and settings — but **omitted
  Proxmox entirely**, so a user who exported a backup and restored it (e.g.
  migrating hosts) silently lost every Proxmox host and its per-guest monitoring
  choices. This closes that data-integrity gap.
  - **`ProxmoxConnections`** are now exported / imported alongside
    `DockerConnections`, reusing the same **merge-by-name** strategy. Connection
    fields covered: node name, API base URL + token id, SSH host/user/port, the
    `AllowConsole` / `AllowUpdates` / `AllowDestroy` / `AllowCreate` toggles,
    `Enabled`, server type, telemetry poll interval, notification preferences,
    schedule, and webhook token. The encrypted **API token secret** and **SSH
    private key / passphrase** are decrypted on export and re-encrypted on import,
    exactly like Docker TLS/SSH material, so a backup stays portable across
    instances with different encryption keys.
  - **Per-guest monitoring intent** travels with the host: a guest the user turned
    monitoring **off** for (`MonitoringEnabled`), or **snoozed**
    (`MonitoringSnoozedUntil`), is exported keyed by VmId and re-seeded on import
    so the next scan re-attaches it. Default-monitored guests and all scan-derived
    state (status, pending counts, errors, IP/uptime/resources) are **not**
    exported — they repopulate on the next scan.
  - Import stays **additive**: an existing host (matched by name) is never
    duplicated, a webhook token colliding with an existing one is dropped
    (re-issue in the UI), and per-guest intent is only seeded for guests not
    already present on the host. A pre-V6.15 backup with no Proxmox section
    imports cleanly. The **Backup** page copy and the export's documented contents
    now mention Proxmox. See [ROADMAP](./ROADMAP.md) Phase V6.15.

## [6.14.0] — 2026-06-09

### Added
- **VM (QEMU) support (V6.14).** Stashboard's Proxmox integration covered LXC +
  nodes only; many homelabs also run QEMU VMs. This phase adds VMs as a
  first-class guest type so the Proxmox page reflects the whole host, not just its
  containers — discovery, lifecycle, and stats, **reusing the LXC surface** so the
  experience is one and the same.
  - **New `ProxmoxGuestType.Qemu`** (value `2`) threaded through the enum, the
    scan service, the API responses, and the TS `ProxmoxGuestType` union. Proxmox
    vmids are unique cluster-wide across LXC and QEMU, so `(connection, vmid)`
    stays a clean natural key and the scan's upsert needed no change.
  - **Discovery & status.** `IProxmoxApiClient.ListQemuAsync` lists VMs via
    `GET /nodes/{node}/qemu` (same resource fields as the LXC list — no extra
    round-trips), and the scan checker maps each VM to a card. The live-status
    sync endpoint (`POST …/lxc/sync`) now reads both guest lists so a VM
    started/stopped outside Stashboard is reflected within the poll interval.
  - **Lifecycle.** Start / stop / shutdown / reboot via
    `POST /api/proxmox/connections/{id}/qemu/{vmId}/status/{verb}` →
    `qemu/{vmid}/status/{action}`, reusing the LXC action UI (card buttons +
    modal **Lifecycle** section).
  - **Graceful shutdown no longer optimistically marks a guest "stopped."** A
    `shutdown` is *asynchronous* — the guest OS / agent may take a while or ignore
    it (a QEMU VM with no guest-agent in particular) — so the card now keeps
    showing the guest as **running** and lets the live-status sync flip it to
    stopped once the host actually stops it, instead of flipping immediately while
    it's still running. Only a hard **Stop** (and Start / Reboot) updates the card
    optimistically. This also fixes the same long-standing behaviour for LXC
    (V6.4). The Proxmox card's running-state buttons are relabelled to match the
    modal's Proxmox-accurate terminology — the graceful quick action is now
    **Shutdown** (not "Stop") and **Reboot** (not "Restart"); a hard **Stop** lives
    in the modal's Lifecycle section.
  - **Stop / Shutdown now ask for confirmation, with an explanation.** Both
    power-off verbs open a confirm dialog (reusing the destroy dialog's
    `remove-confirm-*` surface) that spells out the difference: **Shutdown** is a
    graceful ACPI / guest-agent request the guest OS handles cleanly (no data
    loss; may take a moment or be ignored by a guest with no agent), while
    **Stop** is a hard power-off — immediate, with possible data loss / a dirty
    filesystem — so its confirm button is danger-styled. Start / Reboot still run
    directly. Applies to both LXC and VM.
  - **Stats & Tasks.** Live (`qemu/{vmid}/status/current`) + history
    (`qemu/{vmid}/rrddata`) reuse the existing Stats tab + sparklines (identical
    sample shape), and the Tasks tab reuses the vmid-scoped task listing
    unchanged. The status / rrddata / lifecycle reads share a private `{kind}`
    path helper with their LXC twins — one path segment differs.
  - **Card + modal reuse the LXC surface.** VM cards render in the same guest grid
    with a **VM `<vmid>`** subtitle, and a new **All / LXC / VM** type filter
    appears on the page toolbar once a host has at least one VM. The modal exposes
    only the VM-applicable tabs — **Overview · Config (read-only) · Tasks ·
    Stats** — with the VM's disks / NICs surfaced on a read-only Config tab.
  - **Destroy works for VMs.** The modal's Lifecycle **Destroy** action is reused
    for a stopped VM under the **same triple gate** as LXC destroy (global
    **Destroy LXC** switch + per-host **Allow destroy** + a stopped guest), routed
    to `DELETE /nodes/{node}/qemu/{vmid}` (new `IProxmoxApiClient.DeleteQemuAsync`)
    and recorded in the same destroy audit trail. The confirm dialog and Lifecycle
    copy are VM-worded (**Destroy VM? · VM `<vmid>` · disks**).
  - **Out of scope for the first cut (clearly marked LXC-only):** APT update
    monitoring / "Update now" (a VM isn't necessarily Debian and may have no
    SSH/guest-agent — the **Watch** tab is hidden for VMs), the **Console**
    (SPICE/VNC, a different protocol from the LXC SSH shell), the **Logs** tab
    (`pct`-backed), config **editing**, and **create** (LXC only). See the
    [ROADMAP](./ROADMAP.md) Phase V6.14.

## [6.13.1] — 2026-06-08

### Added
- **Create LXC (V6.13.1).** The Proxmox page's per-host block header gains a
  **New LXC** button — provision a container from a template without dropping to
  the Proxmox web UI, closing the last leg of full LXC lifecycle from Stashboard
  (edit shipped in V6.5/V6.9, destroy in V6.13).
  - **Wired to the Proxmox API.** A new `IProxmoxApiClient.CreateLxcAsync` calls
    `POST /nodes/{node}/lxc`, then polls the returned task UPID
    (`GET …/tasks/{upid}/status`) to a terminal state so the UI reports real
    success/failure rather than "request accepted" (the client surfaces the
    host's error / non-`OK` task exit verbatim). New endpoint
    `POST /api/proxmox/connections/{id}/lxc`, plus reads for the form:
    `GET …/lxc/nextid` (`/cluster/nextid`) and `GET …/lxc/templates`
    (aggregated `vztmpl` content across template-capable storages).
  - **A guided create form (`LxcCreateModal`)** that reuses the Docker
    `container-modal-*` / `service-modal-*` styling (not a parallel form system),
    with near-parity to the Proxmox "Create CT" wizard: identity (vmid defaulted
    from next-free id, hostname, description, tags), an **editable template
    combobox** (pick a discovered `vztmpl` or type any volid), root password /
    SSH key, resources (cores / memory / swap / rootfs storage + size), a full
    `net0` row (name / bridge / MAC / VLAN / rate / IPv4 + gw / IPv6 + gw /
    firewall), **DNS** (nameserver + search domain), and options — **Unprivileged**
    (default on), **Nesting**, onboot, **start after create**, and **Add to HA**
    (best-effort `POST /cluster/ha/resources` after the container is created).
  - **Double-gated, off by default — minus the running-guest check** (there is no
    guest yet): a DB-backed server-wide master switch at **Settings → Create LXC**
    (seeded from `Stashboard:AllowProxmoxCreate`) and a per-host **Allow create**
    opt-in. Gate failures are deterministic and returned *before* any Proxmox
    call (global off ⇒ 403, host opt-in off ⇒ 403); a vmid already on the host ⇒
    409, a malformed spec ⇒ 400 (`ProxmoxLxcCreateValidator`, reusing the V6.9
    network rules). Proxmox stays authoritative and relays its rejection as a 502.
  - **Discovery.** On success the host's **Check now** scan is triggered so the
    brand-new container appears as a card without waiting for the schedule.
  - **Audited.** Every attempt that reaches the host records who / when / host /
    node / vmid / hostname / template / result on **Settings → Audit → LXC
    create** (`ProxmoxCreateAuditEntity`).
  - **Out of scope:** cloning from an existing container/snapshot (planned for
    V8.0), restoring from a backup (vzdump) (planned for V8.1), advanced
    multi-mount rootfs at create time (edit afterwards via the Config tab), and
    VM (QEMU) creation.

### Fixed
- **LXC cards now reflect state changed *outside* Stashboard.** Previously a card's
  running state came only from the scheduled scan, so a container stopped/started
  from the Proxmox UI, the CLI, or a crash showed stale until the next scan (up to
  the host's schedule, e.g. 24h). The Proxmox page now polls a cheap live-status
  endpoint (`POST …/lxc/sync` → one `GET /nodes/{node}/lxc`, no SSH) every ~20s
  while open and updates each guest's running state / uptime / resources; it never
  touches pending-update counts (still the scan's job) and never adds/removes
  cards (discovery stays with the scan).
- **LXC card stayed "running" after Stop (and stale after start/reboot).** A
  lifecycle action only sent the command to Proxmox; the card's running state came
  from the last *scan*, so it didn't change until the next scheduled scan (which
  can be hours away). The action now optimistically updates the guest's persisted
  running state from the verb (start/reboot ⇒ running, stop/shutdown ⇒ stopped) and
  returns the refreshed host, so the card flips immediately; the next scan
  reconciles edge cases. Added controller tests for the lifecycle action (it had
  none — which is also why the 405 below went unnoticed).
- **LXC lifecycle actions returned 405 (start / stop / shutdown / reboot).** The
  route `POST …/lxc/{vmId}/status/{action}` used `{action}` — a **reserved** MVC
  routing token that binds to the action *method name* — so the route never
  matched a real URL value (`shutdown`, `start`, …) and fell through to the SPA
  fallback as `405 Method Not Allowed`. Renamed the token to `{verb}` (the URL is
  unchanged, `…/status/shutdown`). Added a reflection guard
  (`RouteTemplateConventionsTests`) that fails the build if any controller route
  template uses a reserved token, since controller unit tests bypass routing and
  can't catch this class of bug.
- **Proxmox node telemetry on non-US server locales (V6.13.1).** The Proxmox JSON
  reader parsed dot-decimal *string* fields (e.g. `cpuinfo.mhz` `"3100.00"`)
  culture-sensitively, so a host running under a comma-decimal locale read them as
  `null` (a blank CPU MHz on the node card). `ReadDouble` now parses invariant,
  matching the array-value reader.

## [6.13.0] — 2026-06-08

### Added
- **Destroy / remove LXC (V6.13).** The LXC modal's **Lifecycle** section gains a
  **Destroy** action — the container analogue of Docker's "Remove container",
  closing the last LXC lifecycle gap (previously `start | stop | shutdown | reboot`
  only).
  - **Wired to the Proxmox API.** A new `IProxmoxApiClient.DeleteLxcAsync` calls
    `DELETE /nodes/{node}/lxc/{vmid}` (the client surfaces the host's error body
    verbatim, e.g. a permission error). New endpoint
    `DELETE /api/proxmox/connections/{id}/lxc/{vmId}`.
  - **Triple-gated, off by default — the same pattern as the console / "Update
    now".** A DB-backed server-wide master switch at **Settings → Destroy LXC**
    (seeded from `Stashboard:AllowProxmoxDestroy`), a per-host **Allow destroy**
    opt-in, and a **stopped** guest. Gate failures are deterministic and returned
    *before* any Proxmox call: global off ⇒ 403, host opt-in off ⇒ 403, running
    guest ⇒ 409 (stop it first).
  - **Double confirmation that names the exact guest.** The **Destroy** button
    appears only for a stopped, gated container and opens a confirm dialog
    (`LxcDestroyDialog`, a verbatim reuse of the Docker `remove-confirm-*`
    markup/CSS) naming `CT <vmid> · <name>`. On success the card disappears
    immediately and the modal closes.
  - **Audited.** Every attempt that reaches the host records who triggered it,
    when, against which host / node / guest, and the result — on the Audit page's
    new **LXC destroy** tab.
  - **Out of scope, as planned.** Purging associated backups / external storage
    volumes (only the container and its root disk are removed) and bulk destroy.
  - Migration `AddProxmoxDestroy` adds the `AllowDestroy` column, the
    `ProxmoxDestroySettings` singleton table, and the `ProxmoxDestroyAudits` table.

## [6.12.0] — 2026-06-08

### Added
- **LXC live logs (Logs tab) (V6.12).** The LXC modal gains a **Logs** tab (after
  **Tasks**) that streams a guest's system journal in real time — the observability
  surface the Docker modal already had, now for Proxmox containers.
  - **Read-only live tail.** A new `ProxmoxLogsController` reuses the V6.6 console
    transport *verbatim* — the same single-use ticket service, per-user/per-host
    concurrency registry, SSH PTY connector, byte pump, and WebSocket adapter. It
    SSHes to the Proxmox host and runs `pct exec <vmid> -- sh -c 'journalctl -f …'`,
    falling back to `tail -F /var/log/syslog`/`messages` when the guest has no
    journald. The remote command is built server-side and the stream carries no
    input, so the surface is strictly read-only.
  - **Same gate as the console.** Logs require the global **AllowProxmoxConsole**
    switch, the per-host **Allow LXC console** opt-in, SSH credentials, and a running
    guest — each blocked state showing the same calm hint as the rest of the modal.
  - **Docker-parity UI.** The
    [`LxcLogsPanel`](frontend/src/components/proxmox/LxcLogsPanel.tsx) renders into
    the Docker logs toolbar/viewport (`docker-logs-*`) with **Pause / Resume / Stop /
    Stream / Clear / Copy / Download** and autoscroll. **Download** pulls a one-shot
    non-follow snapshot (`journalctl -n 5000`).
  - **No reaping, no audit, no migration.** Unlike the interactive console the tail
    runs with **no idle timeout** (a quiet guest's stream isn't closed after ten
    silent minutes) and writes **no audit row** (nothing executes beyond a read-only
    read). No new tables.

### Changed
- **LXC modal SSH sessions now survive tab switches.** The **Console** and **Logs**
  tabs are kept mounted once opened, so switching to another tab (e.g. Overview) no
  longer drops the live SSH session and reconnecting it on return — the terminal
  refits/refocuses and the log view re-snaps to the bottom when you come back. The
  session is torn down only when the **modal closes**. (Previously each tab switch
  unmounted the panel and closed its SSH session.)

### Fixed
- **LXC Logs no longer leaks SSH sessions (showed "No log lines" after a few
  opens).** The Logs tab auto-starts its stream on mount, and React StrictMode's
  mount→unmount→remount could store the first (in-flight) socket's handle without
  ever closing it — so each open leaked a live session until the per-user cap
  (`MaxSessionsPerUser`, 3) was exhausted and every subsequent open was rejected
  and fell silently to idle. The stream lifecycle now tracks a monotonic run id and
  **closes any superseded in-flight handle** instead of storing it, and a
  server-initiated close (e.g. the session-cap rejection) is now **surfaced** in the
  panel instead of dropping silently to "idle".

## [6.11.0] — 2026-06-06

### Added
- **Bulk LXC monitoring & update operations + audit (V6.11).** Host-wide controls
  for operators with many guests, built on the existing per-LXC endpoints — no
  parallel system.
  - **Bulk monitoring toggle.** "Enable all" / "Disable all" on each host's section
    header flips update monitoring for every LXC on the host in one call
    (`PUT …/lxc/monitoring/bulk`) — one transaction, one audit row per actually-changed
    guest, behind a confirmation dialog. The node row is never touched.
  - **Bulk "Update now".** "Update all" reuses the V6.7.1 confirm → stream → result
    flow over a **checklist** of eligible targets — the **node** and its containers
    (running, monitored, not snoozed, with pending updates — pre-checked, uncheck any),
    streaming each one's `apt` log in turn via `POST …/lxc/update/bulk`. Same triple
    gate (global switch + per-host **Allow updates** + SSH), and one finalised audit
    session per target. The node runs first and carries the usual reboot caveat.
  - **Maintenance snooze.** A nullable `MonitoringSnoozedUntil` on the guest row lets
    you skip a container for a window (1h / 6h / 24h / 7d, or clear) from the LXC
    **Watch** tab. The scan service excludes snoozed guests from scheduled **and**
    manual checks, then **auto-re-includes** them once the window passes (clearing the
    field on the first scan at/after it). Monitoring stays on; the card shows a
    **Snoozed** badge and mutes meanwhile.
  - **Monitoring audit trail.** Every monitoring change (single toggle, bulk, snooze,
    unsnooze) writes a `ProxmoxMonitoringAuditEntity` row — who / when / guest / new
    state — surfaced read-only on **Settings → Audit → LXC monitoring**.
  - **Update-check webhook.** An opt-in, off-by-default public endpoint
    (`POST /api/proxmox/webhooks/{token}`) — the Proxmox analogue of the Docker watch
    webhook — kicks off an immediate host scan, drained by the background service's
    new scan queue. Rotate / remove the token from the host's edit modal; rotating
    invalidates the old URL immediately.
  - Migration `AddProxmoxBulkMonitoringAndWebhook` adds the snooze column, the host
    webhook token (+ unique index) and last-received timestamp, and the
    `ProxmoxMonitoringAudits` table.
  - Out of scope (unchanged): scheduled / unattended bulk upgrades stay manual.

## [6.10.0] — 2026-06-05

### Added
- **Proxmox page Docker-parity redesign (V6.10).** The Proxmox page now wears the
  Docker instances page's `dock` shell, **reusing the `searchbox`, `segmented`,
  `dock-summary`, and connection-`switcher` markup + CSS verbatim** — no parallel
  system. A user with multiple hosts and many LXCs gets the same command-centre
  affordances the Docker page already offers:
  - **Search box** filtering LXC cards by name.
  - **State filter** segmented control (`All / Running / Stopped`).
  - **Monitoring filter** segmented control (`All / Enabled / Disabled / Updates`),
    driven by the existing `monitoringEnabled` / `pendingUpdates` fields. `Updates`
    requires monitoring **on** and a positive pending count.
  - **Summary strip** aggregating cross-host totals — objects, running, stopped,
    pending updates (`objects === running + stopped` always holds).
  - **Connection switcher** (`All connections` + a chip per host with running/total
    and update counts); hidden for a single host, like the Docker switcher.
  - **Deep-link** into the LXC modal via `?connection=…&vmid=…` (the `vmid` param is
    consumed and stripped once the modal opens), reusing the Docker page's deep-link
    `useEffect` pattern.
- **Grouping by PVE node** is satisfied by the existing per-connection sections:
  each Proxmox connection already maps to exactly one node, so "grouping by node" is
  the node card (host summary) with its LXC cards in the grid below. The phase stayed
  UI-only — no `GET /pools` backend or database migration was needed.
- The filter/aggregation predicates live in a pure
  [`proxmox-page.ts`](frontend/src/lib/proxmox-page.ts) module with unit tests.

## [6.9.0] — 2026-06-04

### Added
- **Edit LXC network interfaces & mount points (V6.9.0).** The LXC **Config**
  tab finishes what V6.5 started: the read-only `net<n>` / `mp<n>` / `rootfs`
  lines become guided **row editors** with explicit **Edit / Add / Remove**
  affordances, reusing the Docker container modal surface (`container-modal-*`
  styling, the same review/confirm pattern).
  - **Network rows** expose structured fields — name, bridge, IPv4/IPv6
    (`dhcp` / `manual` / static CIDR), gateway, VLAN tag, firewall, MTU, rate
    limit, MAC, and link-down (disable) — with an advanced **raw** mode for
    options Stashboard doesn't model.
  - **Mount rows** expose storage/source, mount path, size, read-only, backup,
    quota, ACL, shared, replicate and mount options, and support both
    storage-backed volumes and bind mounts.
  - **rootfs** gets a dedicated **edit-only** section (size + safe flags); it
    cannot be removed, and storage migration stays out of scope.
  - Unknown-but-valid options are **preserved verbatim** (never a lossy write);
    every row has a raw expander showing the **exact** generated config line.
  - New interfaces/mounts take the **next free key**; removals flow through
    Proxmox **`delete=`**. The owner-scoped write path builds the exact Proxmox
    payload server-side (numbered keys + `delete=` list) rather than trusting the
    client to craft request strings.
  - A **per-change review** classifies each edit conservatively (*applies live*
    / *restart likely* / *destructive — naming the exact `net1` / `mp2` key*)
    before a single write. Client + server both validate IP/CIDR, gateways,
    MACs, sizes, duplicate names/paths and the rootfs-protect rule; Proxmox
    permission/validation rejections are surfaced verbatim.
- **Operator caveats.** Some changes may require a guest **restart** to fully
  apply — the success state says so inline when the guest is running. Removing a
  mount config entry **does not delete the underlying storage content**.

## [6.8.2] — 2026-06-04

### Fixed
- **Node-alert re-notification spam.** The notification throttle keyed on the
  metric *value*, so a steady deviation whose value wiggles every tick (CPU
  96↔97 %, or the NIC error delta) re-sent the alert on every evaluation. The
  signature now keys on **category + severity** only — a steady alert pings once;
  only a severity change (warn↔crit), a new category, or a clear re-notifies.
- **Noisy NIC alerts.** The Network category counted rx/tx *drops*, which climb
  for entirely benign reasons on a bridged Proxmox node (frames not addressed to
  the host) and fired a near-constant warning. It now counts true **errors**
  (rx_errs + tx_errs) only.

### Added
- **PVE node deep telemetry (V6.8.2).** The node modal gains the host-side
  metrics the Proxmox REST API doesn't expose, each read by an independent SSH
  collector behind its own capability check — a missing source degrades to "not
  available", never a hard failure (mirroring the V6.8 `sensors` path).
  - **CPU — per-core utilisation + steal.** Two `/proc/stat` samples drive
    per-core bars on the CPU/RAM tab and a steal indicator on Overview (the API
    gives only aggregate CPU + iowait).
  - **Memory — available.** `MemAvailable` from `/proc/meminfo` on Overview (the
    API reports only `free`).
  - **Storage — disk IO.** Two `/proc/diskstats` samples add a per-disk
    read/write throughput · IOPS · await table to the Storage/SMART tab.
  - **Storage — thin pools.** `lvs` data%/metadata% surfaces an LVM-thin pool
    fill warning as a pool nears full.
  - **Network — per-interface throughput + errors + link.** Two `/proc/net/dev`
    samples plus `/sys/class/net` replace the node-aggregate-only view with
    per-interface RX/TX rate, error/drop counters, and link speed/duplex/state.
  - **SMART — last self-test + critical counters.** `smartctl -l selftest -A`
    badges the last self-test result + age and the critical counters
    (reallocated / pending / uncorrectable / power-on hours) on each disk row.
  - **Sensors — voltage / power.** The `sensors -j` parser now also emits voltage
    (`in*`) and power (`power*`) inputs alongside temperatures and fans.
  - **Per-connection telemetry poll interval + failure backoff.** A configurable
    refresh interval per host (default 20s, clamped 5–300s; new
    `TelemetryPollSeconds` column) drives the node modal's live tabs, with
    exponential backoff while a host is unreachable. The real-time 2s "Live" view
    is unaffected.
- **Proxmox Backup Server (PBS) support.** A Proxmox host now carries a
  **server type** (PVE / PBS), selectable in the connection modal, so a PBS
  appliance can be added and monitored with the same node card, modal, and
  V6.8.1 alerting as a PVE node — just without LXC guests.
  - **Auth + endpoints.** The API client picks the right token scheme per type
    (`PVEAPIToken` joins id/secret with `=`; `PBSAPIToken` with `:` — the exact
    mismatch that 401s when a PBS host is added as PVE) and branches the few
    endpoints that differ: PBS has no LXC discovery (the node status doubles as
    the auth probe), reads the kernel/version from its own status shape
    (`root` filesystem + `info.kversion` + `/version`), and surfaces its
    **datastores** (`/status/datastore-usage`) in place of PVE storage pools —
    mapped onto the same storage shape so the Storage/SMART tab + the
    storage-fullness alert work unchanged.
  - **Parity.** Node status (CPU/RAM/swap/root), RRD history, disks + SMART,
    network, `apt` updates, sensors (over SSH), node-health **alerts**, one-click
    **Update now**, and the **node console** all work on PBS — it's Debian +
    `apt` + SSH like PVE. SSH on a PBS host powers sensors, NIC-error alerts,
    Update now, and the console (there are no per-LXC apt counts).
  - Additive migration (a `ServerType` column on the Proxmox connection,
    defaulting to PVE so every existing host is untouched).

## [6.8.1] — 2026-06-04

### Added
- **PVE node alerting (V6.8.1).** The V6.8 node card becomes a *watch*: a new
  **Alerts** tab on the node modal opts a node into critical-deviation
  notifications, with explicit persisted state — the node analogue of a Docker
  watch's `Enabled` flag — **off by default** (the additive migration leaves
  every node muted until you enable it).
  - **Thresholds + per-node overrides.** Reuses the V6.8 global defaults (CPU
    80/95, RAM 85/95, storage 85/95) as the baseline; a per-node override row
    lets a deliberately hot node be tuned without muting the fleet. Categories:
    **CPU** saturation, **memory** pressure, **storage** fullness (worst of root
    FS + active pools), **thermal** (vs the chip's own high/crit, falling back to
    defaults), **SMART** degradation (health ≠ PASSED, or SSD wearout ≤
    thresholds), and **NIC** error/drop spikes (rise in `/proc/net/dev` error +
    drop counters between evaluations). Optional granular per-category toggles
    (CPU / RAM / Storage / Thermal / SMART / Network) ship on by default.
  - **Evaluation loop.** Folded into the existing
    `ProxmoxUpdateBackgroundService` tick, but evaluated on **every tick**
    (~5 min) for opted-in nodes — independent of the (often 24 h) per-host update
    schedule — so saturation/thermal deviations are caught in minutes. Each tick
    reads the same REST API / SSH sources the card uses and classifies them with
    the same `ok / warn / crit` boundaries as the card (a shared backend port of
    the frontend classifier), so the colour and the alert never disagree.
  - **Debounce / hysteresis.** A deviation must persist across **N consecutive**
    evaluations before it fires, and must read normal for N before "recovered"
    is sent — suppressing flapping. A per-channel **state signature** throttle
    (the same discipline as the update notifier) means a steady deviation never
    re-pings; an escalation (warn→crit) or a clear re-sends once.
  - **Channel reuse.** Active alerts route through the **existing email +
    Telegram channels** — no new transport. An alert carries severity (warn /
    crit), the metric + value + threshold, and a first-seen timestamp; the
    Alerts tab lists current alerts live and the email/Telegram digest re-sends
    only on a real change (with an "all clear" on full recovery).
  - **Safety.** A source that's merely unavailable (no SSH / lm-sensors, API
    field absent) is treated as **n/a and never alerts**. Degraded metrics on the
    card keep the V6.8 tooltip with the root reason + suggested action.
  - **Data / migration.** New `ProxmoxNodeAlertSettings` (per-connection: enabled,
    category mask, threshold overrides, per-channel notification signatures) +
    `ProxmoxNodeAlertState` (per-category debounce/hysteresis state with
    first-seen). Additive migration; defaults keep every node opted out.

## [6.8.0] — 2026-06-04

### Added
- **PVE node card (V6.8).** The node row on the Proxmox page becomes a live
  hardware/health card and gains a detailed multi-tab modal — the node analogue
  of the LXC card, reusing the same Docker `container-modal-*` shell and stat
  tiles so the surfaces stay identical.
  - **Live health summary on the card.** The node card follows the Docker page's
    **`.host-card`** pattern (full-width host summary above the LXC grid),
    reusing the shared status dot + `StateBadge` (online/offline). It polls the
    node's status (~20s) and shows CPU % · RAM % · root-FS % as colour-coded
    chips (ok / warn / crit by sane default thresholds — CPU 80/95, RAM 85/95,
    storage 85/95) with a "Refreshed Xs ago" timestamp; **degraded chips expose
    a tooltip with the root reason + suggested action**. The per-node **Check
    now** rescan and **Update now** live on the node card (host **Edit** /
    **Delete** stay in the connection header). Long CT names truncate with an
    ellipsis instead of overlapping the badges. Click the card to open the modal.
  - **Node modal tabs:** **Overview** (identity, uptime, kernel, PVE version,
    subscription, CPU model/topology/frequency/virtualization, live CPU %, load
    avg, IO wait, memory + swap, root FS), **CPU/RAM** (**Live** real-time view —
    polls node status every 2s with Pause/Resume — plus a **History** toggle for
    the RRD sparklines with hour/day/week, the same UX as the LXC Stats tab),
    **Storage/SMART** (per-pool usage meters + physical disks with SMART
    health/wearout badges, each expandable to its full SMART attribute table for
    ATA or NVMe text, loaded on demand), **Network** (node throughput sparkline +
    configured interfaces — type, link state, address, gateway, bridge ports /
    bond slaves), **Sensors** (CPU/board temperatures + fan RPMs), and
    **Console** (an SSH shell **on the node itself** — host login shell, not
    `pct exec` — reusing the V6.6 console transport + audit, gated the same way).
  - **Transport.** Base metrics come from the Proxmox REST API
    (`/nodes/{node}/status`, `/rrddata`, `/storage`, `/disks/list`,
    `/disks/smart`, `/network`, `/subscription`); CPU/board temperatures and fan
    speeds — the one signal the API doesn't expose — are parsed from
    `sensors -j` over SSH, with a clear "install lm-sensors / add SSH" state when
    unavailable. Each source degrades independently: a missing source renders a
    "not available" marker, never a hard failure.
  - New read-only, owner-scoped endpoints:
    `GET /api/proxmox/connections/{id}/node/status | /node/rrddata |
    /node/storage | /node/disks | /node/disks/smart | /node/network |
    /node/sensors`. No new tables or migrations.
  - **Refactor.** The `StatTile` / `Sparkline` were extracted from the LXC modal
    into `components/shared/StatTile.tsx` so the LXC and node modals share one
    component instead of two copies.
  - **Deferred:** threshold-based alerting → **V6.8.1**; the metric bullets the
    Proxmox API doesn't expose (per-core CPU % + steal, memory `available`, disk
    IO/IOPS/latency, thin-pool warnings, per-interface RX/TX + errors + link
    speed/duplex, SMART last-self-test, PSU voltages) need host-side SSH
    collectors and move to **V6.8.2** (along with configurable per-connection
    polling). See ROADMAP.

### Changed
- **Deleting a Proxmox host now opens a confirmation modal** (host name, node,
  object count, and an explicit "this does not touch the Proxmox host itself"
  warning) instead of the easy-to-miss inline Confirm/Cancel buttons in the
  connection header — matching the Docker remove-confirm dialog. Surfaces the
  API error in-dialog on failure.

## [6.7.1] — 2026-06-03

### Added
- **Proxmox "Update now" (V6.7.1).** The Docker analogue of one-click
  **Update now**, now for Proxmox: a button on the node card and in each LXC's
  **Watch** tab *applies* pending package updates over SSH.
  - **What it runs.** `apt-get update && apt-get -y -o Dpkg::Options::=--force-confold
    dist-upgrade`, either directly on the node (`vmId 0`) or via
    `pct exec <vmid> -- …` inside an LXC. Non-interactive (keeps existing config
    files on conflict) so it never hangs on a prompt; non-Debian guests are
    detected and reported as "nothing to upgrade".
  - **Live streamed output.** The apt log streams to the browser line-by-line as
    NDJSON over fetch (the same transport the Docker log viewer uses), rendered
    in a confirm → run → result dialog. Because a check is a single SSH sweep of
    the node, a node-level run upgrades the **whole node** (a new kernel may need
    a reboot) — the confirm step spells this out. The confirm step also shows the
    **exact command** that will run (copyable), sourced from the backend so it
    can't drift — the Proxmox analogue of the Docker "Update command" panel
    (`GET …/update-command?vmId=`).
  - **Triple-gated like the LXC console**, all required and **off by default**:
    the `Stashboard:AllowProxmoxUpdates` master switch (DB-backed, managed at
    **Settings → Proxmox updates**), a per-host **Allow apply updates** opt-in
    (`ProxmoxConnection.AllowUpdates`), and SSH credentials on the host. Gate
    failures return a deterministic 403 / 409 before any command runs.
  - **Audited.** Every run writes a `ProxmoxUpdateSession` row (who, when, host /
    node / guest, exit status, bytes streamed, end reason), surfaced read-only on
    the Audit page's new **Proxmox updates** tab.
  - New endpoints: `POST /api/proxmox/connections/{id}/node/update` and
    `POST …/lxc/{vmId}/update` (NDJSON stream),
    `GET|PUT /api/settings/proxmox-updates`, and
    `GET /api/proxmox/updates/sessions`. Additive migration
    (`AddProxmoxUpdateApply`): the `AllowUpdates` column, the settings singleton,
    and the audit table. No new background worker — applying is on-demand only.

## [6.7.0] — 2026-06-03

### Added
- **Per-LXC update monitoring toggle (V6.7).** Each discovered LXC now carries
  its own `MonitoringEnabled` flag — the Proxmox analogue of enabling/disabling
  a Docker watch. A **Monitoring enabled** switch in the LXC modal's **Watch**
  tab turns update tracking off for a single container without disabling the
  whole host, so noisy or intentionally unmanaged guests can be excluded.
  - A disabled guest is **skipped by scheduled and manual checks**: the scan
    passes its vmid to the checker, which short-circuits the per-guest IP lookup
    and `pct exec` count entirely (the node row and other guests are still
    checked). It is also **excluded from the notification signature**, so
    turning monitoring off stops repeat email/Telegram alerts for it at once and
    clears its stale pending count.
  - **Discovery preserves the toggle**: a rediscovered LXC keeps the user's
    chosen state (matched on connection + vmid); newly discovered LXCs default
    to enabled, keeping the change backward-compatible.
  - **Docker-parity UI**: the disabled card is muted (dashed, dimmed) with a
    **Disabled** badge and no amber "updates pending" emphasis, the last-checked
    timestamp stays visible, and the Watch tab reuses the same checkbox +
    helper-text pattern as a Docker watch. The toggle is optimistic with
    rollback on error.
  - New owner-scoped endpoints under the existing route group:
    `PUT /api/proxmox/connections/{id}/lxc/{vmId}/monitoring` (toggle) and
    `POST …/lxc/{vmId}/check` (per-guest **Check now**). Because Proxmox has no
    per-container probe, an enabled guest's Check now re-scans the **whole node**
    (reusing the existing scan flow); a disabled guest returns a deterministic
    disabled outcome without scanning. The node row (`vmId 0`) stays
    host-controlled and is not toggleable.
  - Additive, backward-compatible migration (`MonitoringEnabled` backfilled to
    `true`); no new background worker — the existing
    `ProxmoxUpdateBackgroundService` + scan pipeline are reused.

## [6.6.0] — 2026-06-03

### Added
- **Browser LXC console (V6.6).** The Proxmox container modal gains a working
  **Console** tab: an interactive `xterm.js` shell *inside* an LXC, opened by
  SSHing to the Proxmox host and running `pct exec <vmid> -- /bin/bash`. It's the
  Proxmox analogue of the Docker **Exec** tab and the natural follow-up to the
  per-LXC update count ("LXC `pihole` has 7 updates pending" → `apt upgrade` it
  without leaving the browser). The console button on each LXC card opens it too.
  - **Reuses the shared shell transport** introduced in V5.3 / V5.7: the
    authenticated `POST …/lxc/{vmid}/console/ticket` mints a single-use,
    short-lived ticket (binding the chosen command server-side) and the socket
    opens at `…/lxc/{vmid}/console/ws?ticket=…`. The SSH PTY connector
    (`IHostShellConnector`), the byte pump, and the WebSocket adapter are the
    same components the host terminal and container exec use — only an optional
    initial command (`exec pct exec …`) was added to the connector.
  - The command defaults to `/bin/bash` and is editable per session (e.g.
    `/bin/sh` for an Alpine guest). As with the V5.3 SSH host terminal, live
    terminal resize is unavailable over SSH — the PTY is sized on connect.
  - **Off by default**, gated three ways (all required): the **Settings → LXC
    console** master switch (DB-backed `ProxmoxConsoleSettingsEntity`, seeded
    from the optional `Stashboard:AllowProxmoxConsole` config flag on first run),
    a per-host **Allow LXC console** opt-in (`ProxmoxConnection.AllowConsole`),
    and SSH credentials configured on the host. If any condition is missing the
    ticket request is refused server-side — the gate isn't just a hidden button.
  - **Audited start-to-finish.** Every session writes a row to the new
    `ProxmoxConsoleSessions` table (who, when, host / node / guest, command,
    duration, bytes in / out, end reason) and streams to the application log,
    surfaced on the **Settings → Audit** page's new **LXC console** tab. Per-user
    and per-host concurrency caps + a server-side idle timeout
    (`Stashboard:ProxmoxConsole:*`) close idle / over-cap sessions regardless of
    client state.
  - Pure-additive migration `AddProxmoxConsole` (the per-host `AllowConsole`
    column, the `ProxmoxConsoleSessions` audit table, the DB-backed master-switch
    row). Tests: ticket service (single-use / expiry / vmid+command binding),
    session-registry caps, the settings service (seed / persist), the mapper
    `AllowConsole` round-trip, and the controller's two-way gate + command
    binding.

## [6.5.0] — 2026-06-03

### Added
- **Edit LXC parameters (V6.5).** The Proxmox container modal's **Config** tab is
  no longer read-only for the scalar fields. An **Edit** button turns **Cores**,
  **Memory (MiB)**, **Swap (MiB)**, **Hostname** and **Start at boot** into a
  form; **Review changes** shows a per-field confirmation that classifies each
  change as *applies live* (cores / memory / swap), *needs restart* (hostname)
  or *next boot* (onboot) before anything is written. Saving calls a new
  owner-scoped endpoint `PUT /api/proxmox/connections/{id}/lxc/{vmid}/config`,
  which writes through to the Proxmox `PUT …/lxc/{vmid}/config` API. Requires the
  API token to hold `VM.Config.*`; a permission / validation rejection from
  Proxmox is surfaced verbatim. Only the fields the user actually changed are
  sent (Proxmox merges them), and memory / swap are sent in MiB (Proxmox's native
  config unit). Network interfaces (`net<n>`) and mount points (`mp<n>` /
  `rootfs`) stay read-only this phase.

## [6.4.0] — 2026-06-03

### Added
- **LXC lifecycle actions (V6.4).** Start / Stop / Shutdown / Reboot an LXC from
  Stashboard. A new **Lifecycle** section on the modal's Overview tab (and the
  card's Start / Stop / Restart buttons) call a new endpoint
  `POST /api/proxmox/connections/{id}/lxc/{vmid}/status/{action}` over the
  Proxmox `…/status/{start|stop|shutdown|reboot}` API. Requires the API token to
  hold `VM.PowerMgmt`. The card's Stop = graceful shutdown, Restart = reboot.
- **Real-time Stats (V6.4).** The Stats tab now defaults to a **Live** view that
  polls `…/lxc/{vmid}/status/current` every 2 s (via a new
  `GET /api/proxmox/connections/{id}/lxc/{vmid}/status` endpoint) and renders a
  rolling window of CPU / memory / network / disk-I/O sparklines, with Pause /
  Resume —
  mirroring the Docker live-stats panel. Proxmox has no stats stream for LXC, so
  this is polling, not a push stream. A **History** toggle keeps the V6.3 RRD
  view (Hour / Day / Week).

## [6.3.0] — 2026-06-02

### Added
- **LXC Stats + Tasks tabs (V6.3).** Two more Proxmox container-modal tabs go
  live (read-only), bringing it closer to the Docker modal's Stats/Logs.
  - **Stats** — RRD sparklines for CPU, memory, network (in/out), and disk I/O
    (read/write), with an **Hour / Day / Week** timeframe switch. Backed by a new
    endpoint `GET /api/proxmox/connections/{id}/lxc/{vmid}/rrddata` over the
    Proxmox `…/lxc/{vmid}/rrddata` series; auto-refreshes every 30 s.
  - **Tasks** — the recent node tasks scoped to this guest (type, OK / running /
    error status, start time, duration), each expandable to a **per-task log
    viewer**. Backed by `…/lxc/{vmid}/tasks` and a `…/tasks/log?upid=` endpoint
    over the Proxmox tasks API.
  - The card's **Stats** and **Tasks** action buttons are enabled accordingly;
    Console and lifecycle stay disabled until V6.4 / V6.6. No schema changes —
    both reads go straight to the Proxmox API.

### Changed
- **Unified the Proxmox LXC surfaces with the Docker container ones.** The LXC
  **card** and **modal** now reuse the **exact** Docker components/styles instead
  of parallel `lxc-*` / `proxmox-cc-*` systems:
  - New shared **`EntityCard`** and **`StateBadge`** components — the Docker
    `ContainerCard` and the Proxmox LXC card both compose `EntityCard`, so they
    render literally the same DOM (`cc-*` / `docker-instances-card-*` classes,
    same state pill). `ContainerStateBadge` is now a thin re-export of the shared
    `StateBadge`, which also understands the `stopped` state (red, like a Docker
    `exited` card).
  - The LXC **modal** reuses the Docker modal shell (`container-modal-*`), the
    `docker-stats-*` stat tiles / sparklines, and the shared `Button`.
  - Tabs and card action buttons match the Docker order — Overview · Config
    (≈Inspect) · Tasks (≈Logs) · Stats · **Watch** · Console (≈Exec) — with a new
    **Watch** tab carrying the per-LXC update-tracking summary. Console + the
    lifecycle buttons stay disabled until V6.6 / V6.4.
  - The bespoke `lxc-*` and `proxmox-cc-*` styles were removed.

## [6.2.0] — 2026-06-02

### Added
- **LXC Config tab (V6.2).** The Proxmox container modal's **Config** tab is now
  live (read-only). It reads an LXC's full configuration straight from the
  Proxmox REST API — `GET /nodes/{node}/lxc/{vmid}/config` merged with
  `/status/current` — via a new owner-scoped endpoint
  `GET /api/proxmox/connections/{id}/lxc/{vmid}/config`. The tab shows:
  - **Resources** — configured cores / memory / swap plus live CPU %, memory
    used / max, disk used / max, and uptime.
  - **System** — hostname, OS type, arch, start-at-boot, unprivileged, features.
  - **Mount points** (`rootfs` / `mp<n>`) and **Network** (`net<n>`) as their
    raw Proxmox option lines.

  The card's **Config** action button is enabled accordingly; Stats / Tasks /
  Console / lifecycle stay disabled until V6.3–V6.6. Byte units are normalised
  server-side (config reports memory/swap in MiB, status in bytes). No schema
  changes — the read goes directly to the Proxmox API.

## [6.1.0] — 2026-06-02

### Added
- **Proxmox LXC detail modal + Docker-style cards (V6.1).** First step of
  bringing the Proxmox page to parity with the Docker instances page.
  - **LXC cards restyled** to mirror the Docker container card — name + runtime
    state badge, an amber **Update** badge when updates are pending, a monospace
    `CT <vmid>` line, an `Up <uptime>` / `Stopped` status line, and the existing
    **resources / IP / uptime** shown as chips. The card also carries the Docker
    card's **action row** — tab-shortcut icons (Config / Stats / Tasks / Console)
    on the left and **Start / Stop / Restart** on the right. These are laid out
    and wired now but **disabled** until their backing phases land (Config V6.2,
    Stats/Tasks V6.3, lifecycle V6.4, Console V6.6), so the card markup is final.
    The Docker container card is unchanged; the LXC card uses its own
    `proxmox-cc` styles (copied values, no cross-page coupling). The Proxmox
    **node** card keeps its V6.0 layout.
  - **Click-to-open LXC modal** (`LxcModal`) shaped like the Docker container
    modal: header + tab navigation + body. The **Overview** tab is functional
    (VMID, node, host, IP, status, uptime, resources, tags, pending-update
    count, last-checked, errors), built from data already on the page. The
    **Config / Stats / Tasks / Console** tabs are scaffolded (disabled, with a
    "coming in a later version" hint) to show the target shape — they light up
    in V6.2–V6.6 as the backing Proxmox endpoints land.
  - No backend changes: V6.1 is presentation only.

## [6.0.0] — 2026-06-02

### Added
- **Proxmox LXC update monitoring (V6.0).** A new top-level **Proxmox** page
  (`/proxmox`) tracks pending package updates one layer below Docker — on the
  LXC containers and the Proxmox node itself.
  - **Hybrid transport.** The Proxmox **REST API** (authenticated with a
    `PVEAPIToken`) lists LXC containers and reports the node's own pending
    updates via `GET /nodes/{node}/apt/update`. **SSH** to the Proxmox host
    runs `pct exec <vmid> -- apt list --upgradable` for the per-LXC count —
    the Proxmox API has no command-exec endpoint for LXC, so SSH is the only
    way to read it. (The roadmap's suggested `status/exec` REST path does not
    exist for LXC.)
  - **Auto-discovery.** Configure a host once; every scan discovers the node
    + its LXCs and renders one card per object showing the pending-update
    count, running state, and last-checked time. Cards for guests that
    disappear from the host are pruned automatically.
  - **Rich LXC cards.** Each container card also shows its **IPv4** (read from
    the Proxmox `interfaces` API — no guest agent needed), **vCPU count**,
    **memory limit**, **disk size**, **uptime**, and **Proxmox tags** — all
    sourced from the API, so the cards are informative even when SSH isn't
    configured. A host without SSH shows a calm "Updates n/a" hint on its LXC
    cards rather than a red error (reading the per-container count is the only
    thing SSH gates).
  - **Reused scheduling + notifications.** The same V2.2 Hourly / Daily /
    Weekly cadence model as Docker watches drives a dedicated background scan,
    and new updates fire email + Telegram notifications through the existing
    channels (throttled by a signature of the pending state so the same
    un-applied updates aren't re-sent every tick).
  - **Per-host management.** Create / edit / delete hosts, a **Test
    connection** button that probes both API and SSH independently, and a
    **Check now** button that runs an immediate scan bypassing the schedule.
  - Self-signed Proxmox certificates are supported via a per-host
    **Skip TLS verification** toggle (on by default).
  - New tables `ProxmoxConnections` + `ProxmoxGuests` (migration
    `AddProxmox`). Secrets (API token secret, SSH private key, passphrase)
    are encrypted at rest via the existing `IEncryptionService`.

### Notes
- **Out of scope (follow-ups).** Triggering `apt upgrade` inside an LXC from
  Stashboard (lands with the V6.1 browser-SSH story) and non-Debian LXC
  templates (Alpine `apk`, Rocky `dnf`).

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
