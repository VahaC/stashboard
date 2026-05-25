# Stashboard — Product Roadmap

> This document began life as the **Docker Update Checker** feature plan and has
> grown into the product-wide roadmap. §1–§12 cover the Docker update-monitoring
> feature (V1 → V3, all delivered); §13 is the active **V4 — migration to SQLite**
> release; §14 is the post-V4 feature backlog (V5+).
>
> **Status:** ✅ **V1, V2 and the delivered V3 phases are shipped.** V1 (planning + 7 phases) shipped; V2.1 → V2.7 shipped; V3.1 → V3.5 + the container/connection decoupling shipped (container inspect, async health verification, live logs/stats, instances page, first-class containers). The remaining Docker ideas (grouping, prune, Proxmox, exec/SSH shells) are **deferred to the post-V4 backlog (V5+) — see §14.** ✅ **V4 (migration to SQLite, single-container self-hosted) shipped — see §13.** ✅ **V5.0 (disabled card style + one-click removal) — see §14.** ✅ **V5.0.1 (unlink container from service) — see §14.** ✅ **V5.0.2 (editable SMTP / email settings) shipped — see §14.** ✅ **V5.0.3 (dedicated notifications settings page) — see §14.** ✅ **V5.1 (secure key auto-provisioning) shipped in image 5.1.0 — see §14.** ✅ **V5.2 (true Compose-aware recreate) shipped in image 5.2.0 — see §14.** ✅ **Phase V5.3 — Host terminal (browser SSH shell to the Docker host) shipped in image v5.3.0** End-user documentation: [`DOCKER_UPDATE_MONITORING_GUIDE.md`](./DOCKER_UPDATE_MONITORING_GUIDE.md). For the feature's source-level surface see §6 — every phase links to the PR that landed it.

## 1. Goal

Allow a user to mark a `WebResource` as a **Docker-backed service**, point Stashboard at the Docker host where the container runs, and let the system periodically compare the running container's image digest with the latest digest published in the registry. When a newer image is available, the dashboard card shows an **"Update available"** badge and (if enabled) the user is emailed.

**Non-goals (V1):**
- No automatic `docker pull` / container recreate. Notifications only. *(Shipped in V2.7 as a per-click opt-in — see §11.)*
- No "Update now" button against the live Docker daemon. *(Shipped in V2.7 — see §11.)*
- No support for Quay, ECR, Harbor, self-hosted registries — V1 is **Docker Hub + GHCR** (plus a generic OCI fallback that *may* work for others but is not officially supported).
- No Kubernetes / Podman / containerd. Plain Docker host only.
- No SSH-based Docker host. Local socket + remote TCP (with TLS) only in V1. (SSH deferred to V2 — see §11.)

---

## 2. User experience

### 2.1 Adding Docker tracking to a service
On the service modal a new collapsed section **"Docker container"** appears. Toggling it on reveals:

| Field | Required | Notes |
|---|---|---|
| Image reference | ✅ | e.g. `ghcr.io/linuxserver/sonarr:latest`, `nginx:1.27`, `library/postgres:16-alpine`. Parsed into `registry / namespace / repository / tag`. |
| Docker host type | ✅ | `LocalSocket` (default) \| `TcpTls` |
| Docker host URL | conditional | Required when `TcpTls`. Format: `tcp://host:2376`. Hidden for `LocalSocket`. |
| Container name | ✅ | Used to look up the running container on the host. Container ID also accepted. |
| TLS CA cert | conditional | For `TcpTls`. Stored encrypted. |
| TLS client cert | conditional | For `TcpTls`. Stored encrypted. |
| TLS client key | conditional | For `TcpTls`. Stored encrypted. |
| Registry username | — | Optional. For private images. Stored encrypted. |
| Registry password / PAT | — | Optional. For private images. Stored encrypted. Marked as secret in UI. |
| Update notifications | — | Bool, default `true`. Reuses the email channel. |
| Check interval (minutes) | — | Per-watch override. Default 360 (6h). Range 30–1440. |

A **"Test connection"** button validates: (a) Docker daemon reachable, (b) named container exists, (c) registry tag resolvable.

### 2.2 Dashboard card
- A new pill badge **"Update available"** sits next to the status dot (accent color, e.g. amber).
- Tooltip on the badge: `Current: sha256:abc1234 • Latest: sha256:def5678 • Checked 2h ago`.
- If the check is in `Error` state, a small warning icon appears with the last error in the tooltip.

### 2.3 Service modal — details view
When Docker tracking is enabled, the modal also shows a read-only status row:
```
Current digest: sha256:abc1234…   Tag: 1.27.3
Latest digest:  sha256:def5678…   Tag: 1.27.4   ← Update available
Last checked: 2026-05-15 14:22 UTC   [Check now]
```
A small "Copy update command" button copies a templated snippet, e.g.:
```bash
docker pull nginx:1.27 && docker compose up -d nginx
```
(template uses container name; user can edit before pasting).

### 2.4 Email
Reuses existing SMTP infrastructure (`AccountNotificationService` pattern). New template **"docker-update-available"** with subject `[Stashboard] Update available for {ServiceName}` and body listing image, old/new digest, and tag.

---

## 3. Architecture

### 3.1 New domain entity
`DockerWatchEntity : AuditableEntity` — 1:1 with `WebResourceEntity`. Lives in `src/Stashboard.Core/Entities/`.

```csharp
public class DockerWatchEntity : AuditableEntity
{
    public Guid WebResourceId { get; set; }            // FK, unique
    public WebResourceEntity WebResource { get; set; } = default!;

    public Guid UserId { get; set; }                   // denormalized for query convenience

    public bool Enabled { get; set; } = true;

    [Required, MaxLength(500)]
    public string ImageReference { get; set; } = default!;   // raw user input, e.g. "ghcr.io/owner/repo:tag"

    // Parsed components (computed at save time, indexed for queries)
    [Required, MaxLength(200)] public string RegistryHost { get; set; } = default!;   // "ghcr.io"
    [Required, MaxLength(300)] public string Repository { get; set; } = default!;     // "owner/repo"
    [Required, MaxLength(100)] public string Tag { get; set; } = default!;            // "latest" if omitted

    public DockerHostType HostType { get; set; } = DockerHostType.LocalSocket;
    [MaxLength(500)] public string? HostUrl { get; set; }

    [Required, MaxLength(200)]
    public string ContainerName { get; set; } = default!;

    // Encrypted blobs (AES-256-GCM via existing IEncryptionService)
    public string? TlsCaCertEncrypted { get; set; }
    public string? TlsClientCertEncrypted { get; set; }
    public string? TlsClientKeyEncrypted { get; set; }
    public string? RegistryUsernameEncrypted { get; set; }
    public string? RegistryPasswordEncrypted { get; set; }

    public bool UpdateNotificationsEnabled { get; set; } = true;
    public int CheckIntervalMinutes { get; set; } = 360;

    // Status fields (mutated by the background loop / manual check)
    public DockerUpdateStatus UpdateStatus { get; set; } = DockerUpdateStatus.Unknown;
    [MaxLength(100)] public string? CurrentDigest { get; set; }
    [MaxLength(100)] public string? LatestDigest { get; set; }
    [MaxLength(100)] public string? CurrentVersionTag { get; set; }
    [MaxLength(100)] public string? LatestVersionTag { get; set; }
    public DateTime? LastCheckedUtc { get; set; }
    public DateTime? LastUpdateDetectedUtc { get; set; }    // when LatestDigest != CurrentDigest first observed
    public DateTime? LastNotificationSentUtc { get; set; }  // throttle re-notifications for same LatestDigest
    public string? LastError { get; set; }

    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}
```

### 3.2 New enums (`src/Stashboard.Core/Enums/`)
- `DockerHostType { LocalSocket = 0, TcpTls = 1 }` *(SSH = 2 reserved for V2)*
- `DockerUpdateStatus { Unknown = 0, UpToDate = 1, UpdateAvailable = 2, Error = 3, Disabled = 4 }`

### 3.3 Service layer

**`src/Stashboard.Core/Abstractions/IDockerUpdateChecker.cs`**
```csharp
public interface IDockerUpdateChecker
{
    Task<DockerCheckResult> CheckAsync(DockerWatchEntity watch, CancellationToken cancellationToken);
    Task<DockerConnectionTestResult> TestConnectionAsync(DockerWatchEntity watch, CancellationToken cancellationToken);
}

public sealed record DockerCheckResult(
    DockerUpdateStatus Status,
    string? CurrentDigest,
    string? LatestDigest,
    string? CurrentVersionTag,
    string? LatestVersionTag,
    string? Error);

public sealed record DockerConnectionTestResult(
    bool DockerHostReachable,
    bool ContainerFound,
    bool RegistryReachable,
    string? Error);
```

Implementations in `src/Stashboard.Infrastructure/`:

- **`DockerUpdateChecker`** — orchestrator that wires together a `IDockerHostClient` (resolves the container's current image digest) and an `IRegistryClient` (resolves the latest digest in the registry).
- **`DockerHostClient`** — uses [`Docker.DotNet`](https://www.nuget.org/packages/Docker.DotNet) NuGet package. Supports local socket and TCP+TLS via `DockerClientConfiguration` + `CertificateCredentials`.
- **`OciRegistryClient`** — implements the OCI Distribution API v2:
  - Anonymous: `HEAD https://{registry}/v2/{repository}/manifests/{tag}` → `Docker-Content-Digest` header. If `401`, follow `Www-Authenticate: Bearer realm=…,service=…,scope=…` to fetch a token, retry.
  - **Docker Hub specifics:** `registry-1.docker.io` is the manifest host; auth via `auth.docker.io/token?service=registry.docker.io&scope=repository:{repo}:pull`. Implicit `library/` prefix for official images.
  - **GHCR specifics:** auth via `ghcr.io/token?service=ghcr.io&scope=repository:{repo}:pull`.
  - Request header: `Accept: application/vnd.oci.image.index.v1+json, application/vnd.docker.distribution.manifest.list.v2+json, application/vnd.docker.distribution.manifest.v2+json` — covers single + multi-arch manifest lists. Digest comes back the same way.
  - Multi-arch: digest of the *list* is what the daemon stores for the container, so comparing list-digest to list-digest is the correct comparison. No need to descend into platform manifests.

**`src/Stashboard.Core/Abstractions/IImageReferenceParser.cs`** — parses `[registry/]repository[:tag][@digest]` into structured parts. Defaults: registry = `docker.io`, namespace = `library` for single-segment repos, tag = `latest`. Pure function; trivial unit tests.

### 3.4 Background loop
**`src/Stashboard.Api/Services/DockerUpdateBackgroundService.cs`** — new `BackgroundService` (separate from `HealthCheckBackgroundService` because its cadence is very different).

- Ticks every 5 minutes (configurable: `DockerUpdate__TickIntervalSeconds`, default 300).
- Each tick: load all `DockerWatchEntity` rows where `Enabled = true` AND `LastCheckedUtc IS NULL OR LastCheckedUtc < UtcNow - CheckIntervalMinutes`.
- For each watch, call `IDockerUpdateChecker.CheckAsync(…)`. Persist the result. If `UpdateStatus` changed to `UpdateAvailable` (or the `LatestDigest` differs from the previously notified one), call `IDockerUpdateNotificationService.NotifyAsync(...)`.
- Concurrency: process watches **sequentially** (registry rate limits + Docker daemon back-pressure). Optional small `SemaphoreSlim` with `MaxDegreeOfParallelism = 2` later.
- Throttle re-notifications: only re-notify when `LatestDigest` is *different* from the digest used for the previous notification (i.e., a *new* update — not the same one again).

### 3.5 Notification
**`src/Stashboard.Api/Notifications/IDockerUpdateNotificationService.cs`** — mirrors `IServiceStatusNotificationService` pattern:

```csharp
public interface IDockerUpdateNotificationService
{
    Task NotifyAsync(
        UserEntity user,
        WebResourceEntity service,
        DockerWatchEntity watch,
        CancellationToken cancellationToken = default);
}
```

Reuses `IEmailSender`. Template lives alongside existing email templates (or as raw `StringBuilder` if templates are currently inline — to be verified during implementation).

### 3.6 API surface
Endpoints added to `WebResourcesController` (kept colocated for nested resource semantics):

```
GET    /api/services/{id}/docker                       → DockerWatchResponse (404 if not configured)
PUT    /api/services/{id}/docker  DockerWatchUpsertRequest  → DockerWatchResponse
DELETE /api/services/{id}/docker                       → 204
POST   /api/services/{id}/docker/check                 → DockerWatchResponse (status refreshed)
POST   /api/services/{id}/docker/test-connection  TestConnectionRequest → DockerConnectionTestResponse
```

All require `[Authorize]`, owner-only access via `User.GetUserId()`.

**Contracts** (`src/Stashboard.Api/Contracts/DockerWatchContracts.cs`):
- `DockerWatchResponse` — all status fields + non-sensitive config; secrets returned as opaque "•••" placeholder so the UI can show "configured / not configured" without leaking values.
- `DockerWatchUpsertRequest` — all editable fields; secret fields use a tri-state convention `{ value: string | null, action: "keep" | "set" | "clear" }` so an edit can preserve the existing encrypted value without sending it round-trip.

### 3.7 EF Core migration
- New table `DockerWatches` with a unique index on `WebResourceId` (1:1) and a non-unique index on `(UserId, Enabled, LastCheckedUtc)` for the background scan.
- Cascade delete from `WebResources` (deleting a service deletes its watch).
- Migration name: `AddDockerWatch`.

### 3.8 Frontend
**Types** (`frontend/src/lib/types.ts`):
- `DockerWatch`, `DockerWatchUpsert`, `DockerHostType`, `DockerUpdateStatus`, `DockerConnectionTestResponse`.

**API client** (`frontend/src/lib/api.ts`): functions for the 5 new endpoints.

**Queries** (`frontend/src/lib/queries.ts`):
- `useDockerWatch(serviceId)`, `useUpsertDockerWatch()`, `useDeleteDockerWatch()`, `useCheckDockerNow()`, `useTestDockerConnection()`.
- `useServices()` polling stays 30s; Docker status hydrates through the existing service payload (extended).

**`ServiceModal.tsx`** — new collapsible "Docker container" section under the existing layout. Conditional fields based on `hostType`. Secret fields with reveal/hide and the tri-state action.

**`Dashboard.tsx`** — render the "Update available" badge when `service.dockerUpdateStatus === 'UpdateAvailable'`. New CSS variable `--status-update-available` (likely amber `#f59e0b` to differentiate from `--status-attention`).

---

## 4. Security model

| Concern | Mitigation |
|---|---|
| Docker socket access from inside the Stashboard container | Documented opt-in mount: `- /var/run/docker.sock:/var/run/docker.sock:ro` in `docker-compose.yml`. Read-only is sufficient for `docker inspect`. **Read-only mount is mandatory** in the documented example. |
| Remote Docker API exposure | Only `tcp://host:2376` with TLS is supported. Plain `2375` is rejected at config-parse time with a clear error. |
| TLS material at rest | Encrypted via existing `IEncryptionService` (AES-256-GCM) before persistence. Stored as Base64 strings. |
| Registry credentials at rest | Same — `IEncryptionService`. |
| Credential leak via API response | Secrets never returned — `DockerWatchResponse` exposes only `hasRegistryCredentials: bool`, `hasTlsConfigured: bool`. |
| Rate limits (Docker Hub 100/6h anon, 200/6h authed) | Default per-watch interval 6h. Background loop batches across all users sequentially. Per-watch interval has a minimum of 30 min. Authenticated Docker Hub requests count against the user's quota — acceptable, since user supplies their own creds. |
| SSRF via "registry host" field | Registry host is derived from the parsed image reference, not free-form. Only `https://` requests; `http://` only if explicitly allowed (V2). |
| Container escape via Docker socket | The container runs unprivileged inside Stashboard; even with socket access, no `--privileged` flag is needed. The socket itself is the trust boundary — same as Portainer / Watchtower / Diun. |

---

## 5. Test plan

Per `REQUIREMENTS.md §2`: every behavior must be covered, and persistence must be verified against the DB.

### 5.1 Unit tests
- `ImageReferenceParserTests` — Docker Hub library shorthand (`nginx` → `docker.io/library/nginx:latest`), namespaced (`linuxserver/sonarr`), full GHCR (`ghcr.io/owner/repo:tag`), digest references (`nginx@sha256:…`), edge cases (empty, malformed, port in registry host like `registry:5000/foo`).
- `OciRegistryClientTests` — anonymous flow, 401 → token → retry, multi-arch manifest header, 404 (image/tag missing), 429 rate limit handling, network errors.
- `DockerHostClientTests` — uses Docker.DotNet test fakes; covers container-by-name and container-by-id lookup, container not found, image digest extraction.
- `DockerUpdateCheckerTests` — orchestrator with mocked host + registry clients; covers all `DockerUpdateStatus` transitions.

### 5.2 Controller tests (in-memory DB pattern already used by `WebResourcesController` tests)
- GET / PUT / DELETE / POST check / POST test-connection — happy paths, owner-only access (404 for foreign user's service), validation errors, secret-preservation tri-state behavior.
- Verify entity is **persisted** correctly (Per requirement §2): re-read from DB after each mutation.

### 5.3 Background service tests
- `DockerUpdateBackgroundServiceTests` — watches whose interval hasn't elapsed are skipped; status transitions trigger notification; same `LatestDigest` does NOT re-notify.

### 5.4 Notification tests
- `DockerUpdateNotificationServiceTests` — pattern parallel to `ServiceStatusNotificationServiceTests`.

### 5.5 Frontend
- TanStack Query hooks: smoke tests for cache invalidation.
- ServiceModal: rendering of conditional Docker section, validation messages, tri-state secret control.

---

## 6. Phased delivery

> All 7 phases shipped. Each phase below carries the PR that landed it.

### ✅ Phase 0 — Planning artifacts ([PR #1](https://github.com/VahaC/stashboard/pull/1))
This document + initial `BUSINESS_REQUIREMENTS.md §4.6` stub.

### ✅ Phase 1 — Foundations ([PR #2](https://github.com/VahaC/stashboard/pull/2))
1. ~~Add NuGet `Docker.DotNet`~~ deferred to Phase 3 (no point shipping an unused dependency).
2. ✓ New enums (`DockerHostType`, `DockerUpdateStatus`) in `Stashboard.Core/Enums`.
3. ✓ `DockerWatchEntity` + EF config in `ApplicationDbContext`.
4. ✓ EF migration `20260515210915_AddDockerWatch`.
5. ✓ `IImageReferenceParser` + `ImageReferenceParser` (24 unit tests) + persistence smoke test (4 tests).

**DoD met:** migration ran cleanly against `stashboard_dev`; entity persists and round-trips via DbContext.

### ✅ Phase 2 — Registry client ([PR #3](https://github.com/VahaC/stashboard/pull/3))
1. ✓ `IRegistryClient` + `OciRegistryClient` (Docker Hub + GHCR via OCI Distribution v2).
2. ✓ Named `HttpClient` `"registry"` registered in `Stashboard.Infrastructure.DependencyInjection`.
3. ✓ 19 unit tests (anonymous, Bearer flow with token caching via `IMemoryCache`, multi-arch Accept headers, 404 / 429 / 500 / network errors).
4. ✓ 2 opt-in integration tests (`STASHBOARD_RUN_NETWORK_TESTS=1`) against real `library/nginx:latest` and `ghcr.io/home-assistant/home-assistant:stable`.

**DoD met:** real `library/nginx:latest` digest resolved in 754 ms.

### ✅ Phase 3 — Docker host client ([PR #4](https://github.com/VahaC/stashboard/pull/4))
1. ✓ `IDockerHostClient` + `DockerHostClient`, `IDockerClientFactory` + `DockerClientFactory` using `Docker.DotNet 3.125.15` + `Docker.DotNet.X509`.
2. ✓ Local socket (platform default URI) + remote TCP+TLS with a custom `DockerTlsCredentials` (custom CA `CustomRootTrust` without polluting the OS trust store, Windows-side PKCS12 re-import workaround for `dotnet/runtime#23749`).
3. ✓ ContainerInspect → ImageInspect → RepoDigest match using the existing `IImageReferenceParser` for normalisation. Translates `DockerContainerNotFoundException` / `DockerImageNotFoundException` / `HttpRequestException` / timeout into typed `DockerHostStatus`.
4. ✓ 15 unit tests + 3 opt-in integration tests against the local Docker socket on the dev machine (gated by `STASHBOARD_RUN_LOCAL_DOCKER_TESTS=1`).

**DoD met:** real digest of `stashboard-db-dev` container read in 18 ms.

### ✅ Phase 4 — Checker orchestrator + API ([PR #5](https://github.com/VahaC/stashboard/pull/5))
1. ✓ `IDockerUpdateChecker` + `DockerUpdateChecker` orchestrator (entity-free, takes decrypted `DockerWatchProfile`).
2. ✓ `DockerWatchesController` — separate controller, 5 endpoints under `/api/services/{webResourceId:guid}/docker`.
3. ✓ Contracts (`DockerWatchUpsertRequest` / `DockerWatchResponse` / `DockerConnectionTestRequest` / `DockerConnectionTestResponse`) with tri-state `SecretValueUpsert(Action, Value)` enum `Keep`/`Set`/`Clear`. **Implementation note:** response uses `HasTlsConfigured` / `HasRegistryCredentials` boolean flags rather than the originally-proposed `"•••"` placeholder — strictly typed and easier for the UI to switch on.
4. ✓ `IDockerWatchMapper` + `DockerWatchMapper` (entity ↔ response, tri-state secret application, profile builders for both orchestrator and test-connection paths).
5. ✓ 12 + 19 + 31 = 62 tests (orchestrator, mapper, all 5 endpoints against real test PostgreSQL).

**DoD met:** all 5 endpoints return correct responses; tests pass.

### ✅ Phase 5 — Background loop + notifications ([PR #6](https://github.com/VahaC/stashboard/pull/6))
1. ✓ `DockerUpdateBackgroundService` — ticks every `DockerUpdateOptions.TickIntervalSeconds` (default 300, floor 30), 10 s startup delay, in-memory due-ness filter, per-watch try/catch, public `ScanOnceAsync` for testability.
2. ✓ `IDockerUpdateNotificationService` + `DockerUpdateNotificationService` — reuses `IEmailSender`, throttle key on `LastNotifiedDigest` stamped only after successful send.
3. ✓ `EmailTemplates.DockerUpdateAvailable` — HTML table + text body with shortened digests.
4. ✓ 9 + 9 = 18 tests including same-digest-only-emails-once integration test against real test DB.

**DoD met:** seeded watch flips status without manual intervention.

### ✅ Phase 6 — Frontend ([PR #7](https://github.com/VahaC/stashboard/pull/7))
1. ✓ TypeScript types + 5 TanStack Query hooks (`useDockerWatch` / `useUpsertDockerWatch` / `useDeleteDockerWatch` / `useCheckDockerNow` / `useTestDockerConnection`).
2. ✓ `DockerWatchSection` component (own file, ~400 lines) with split-on-load + `key`-remount pattern (cleanly passes the `react-hooks/set-state-in-effect` rule).
3. ✓ Dashboard "Update" pill + new `--status-update-available` CSS variable.
4. ✓ Conditional fields based on `hostType`, tri-state secret controls, Test connection 3-stage rundown, Check now, status panel, Copy update command snippet.
5. ✓ Backend extension to surface `DockerUpdateStatus?` on the `Service` payload (nav property + mapper field) so the dashboard reads it without a per-card fetch.

**DoD met:** end-to-end flow works in the dev stack.

### ✅ Phase 6.5 — UX refresh ([PR #8](https://github.com/VahaC/stashboard/pull/8))
Not in the original plan but worth shipping: the modal had grown to 14+ fields. Split into 4 tabs (**General · Healthcheck · Credentials · Docker**) with status indicators on the tab strip. Also added the user guide.

### ✅ Phase 7 — Documentation (this commit)
- ✓ `README.md` — Docker socket mount example for `docker-compose.yml` in a dedicated section, `DockerUpdate__TickIntervalSeconds` in the configuration table, 5 new endpoints in the API surface listing.
- ✓ `docker-compose.yml` — commented-out `:ro` socket mount with security note in place, opt-in by uncomment.
- ✓ `BUSINESS_REQUIREMENTS.md` — `§4.6` un-stubbed (planned marker removed), REST surface + tri-state semantics + security notes documented as shipped.
- ✓ `ROADMAP.md` (this document; formerly `DOCKER_UPDATE_CHECKER_ROADMAP.md`) — this section. Every phase carries its delivery PR.
- ✓ `REQUIREMENTS.md` — **not modified**. No new repo-wide code convention emerged; the tri-state secret pattern is feature-specific.
- ✓ End-user `DOCKER_UPDATE_MONITORING_GUIDE.md` already in place from PR #8.

---

## 7. Open questions — resolution log

> These questions were raised during V1 planning. All are now closed.

| # | Question | Resolution |
|---|---|---|
| 1 | **SSH host support** — is there demand for monitoring containers on a remote host? | ✅ **Yes — planned as V2.5.** SSH reserved as `DockerHostType = 2` in the enum since V1. |
| 2 | **Per-user Docker Hub rate-limit pool** — coalesce queries for the same image across users? | ✅ **No coalescing in V1.** Each user pays their own quota. Documented in `DOCKER_UPDATE_MONITORING_GUIDE.md §10`. |
| 3 | **"Copy update command" template** — plain `docker run` variant needed? | ✅ **No.** Compose is the de-facto standard; the template ships Compose-only. V2.7 ("Update now") now handles both transparently — the raw recreate works regardless of whether the container is Compose-managed or plain `docker run`. |
| 4 | **Pre-release / semver filtering** — ignore `-rc`, `-beta` tags? | ✅ **Deferred — planned as V2.1.** |
| 5 | **GHCR private images** — surface explicit auth-required error? | ✅ **Shipped in V1.** `OciRegistryClient` returns a typed `RegistryAuthRequired` error; UI shows "Authentication required — add registry credentials". |

---

## 8. V1 extras shipped (beyond the original scope)

> All five additions proposed at planning time were shipped in V1.

| Addition | Shipped in | Notes |
|---|---|---|
| **Tri-state secret control** (`Keep` / `Set` / `Clear`) in `DockerWatchUpsertRequest` | PR #5 | Backport to the core `Credentials` flow is tracked as a separate improvement. |
| **Throttled re-notification** keyed on `LastNotifiedDigest` / `LastTelegramNotifiedDigest` | PR #6 | Independent per-channel throttle keys; stamped only after a successful send. |
| **`Disabled` as a first-class `DockerUpdateStatus`** | PR #2 | Enum value `4`; background loop skips disabled watches entirely. |
| **Per-watch check schedule** (replaces a single global interval) | PR #5 (V1: minutes field); V2.2 ✅ (full Hourly/Daily/Weekly redesign) | V1 default was 6 h; V2.2 changes the default to 24 h and adds Daily/Weekly modes. |
| **"Test connection" preflight** | PR #5 | Three-stage rundown: Docker host reachable → container found → registry reachable. |

---

## 9. Risks

> Risks that were fully mitigated by V1 delivery are marked ✅ **Closed**.
> Remaining risks carry forward to V2.

| Risk | Likelihood | Impact | Status | Mitigation |
|---|---|---|---|---|
| Docker Hub rate-limiting under multi-user load | Low *(was Medium)* | Medium | ✅ **Closed** | Default interval raised to **24 h** in V2.2 (was 6 h in V1). Full guidance in `DOCKER_UPDATE_MONITORING_GUIDE.md §10`. Encourage authenticated requests for > 4 Docker Hub watches. |
| Docker.DotNet version drift (`3.x` targets an older Docker API) | Low | Low | 🟡 **Ongoing** | Version pinned to `3.125.15`; integration-tested against local Docker socket on each PR. Reassess when Docker Engine ≥ 27 ships breaking API changes. |
| Container-name lookup fails when Compose prefixes the project name (`myproject_sonarr_1`) | Low *(was Medium)* | Low | ✅ **Closed** | Both name and container ID accepted; UI tooltip hints at `docker ps --format '{{.Names}}'`. No user reports of this issue post-launch. |
| Registry returns multi-arch manifest *list* digest; running container stores a *platform* digest → false-positive "update available" | Low *(was Medium)* | Medium | ✅ **Closed** | `Accept` header includes `manifest.list.v2+json`; list-digest compared to list-digest. Verified against `nginx:latest` (multi-arch) and `homeassistant:stable` in integration tests. |
| TLS material accidentally logged | Low | High | ✅ **Closed** | Decrypted values never passed to logger; redaction unit-tested. |
| V2.2 migration (`CheckIntervalMinutes` → `ScheduleType`) breaks existing watches | Low | Medium | ✅ **Closed** | Migration `20260517100151_UpdateDockerWatchCheckSchedule` adds the new columns, runs the bucketing SQL in-place, then drops the legacy column — reversible. |
| Auto-update (V2.7) silently restarts a production container on a false-positive digest match | Low | High | ✅ **Closed** | Per-click `confirm()` modal naming the exact image + container; writable-socket required only when the user opts in to clicking the button (`:ro` mount keeps the rest of the feature surface usable); every attempt — success or failure — written to `DockerUpdateAttempts` for audit. Verified by `UpdateNowEndpointTests`. |

---

## 10. Effort estimate (rough)

| Phase | Effort |
|---|---|
| 1 — Foundations | 0.5 day |
| 2 — Registry client | 1.5 days |
| 3 — Docker host client | 1 day |
| 4 — Checker orchestrator + API | 1.5 days |
| 5 — Background loop + notifications | 1 day |
| 6 — Frontend | 1.5 days |
| 7 — Docs + polish | 0.5 day |
| **Total** | **~7.5 days** |

---

## 11. V2 Roadmap (ordered simple → complex)

> V1 was delivered on 2026-05-16 (all 7 phases, PRs #1–#8, plus follow-up
> improvements through PR #13). The items below are the next layer of
> capability, sequenced by implementation cost so we can ship value
> incrementally.

---

### ✅ Phase V2.1 — Tag-pattern filtering

**Complexity:** Low  
**Value:** Eliminates false-positive "Update available" noise from pre-release builds.

Many upstream projects continuously publish `-rc`, `-beta`, `-alpha`, or
date-stamped (`20240401`) tags alongside the stable track. Users who pin
to `latest` or a major-version tag like `v2` will see spurious update
badges every time a pre-release tag is pushed on the same image.

**Shipped:**

- ✓ New optional field `TagPatternFilter` (`varchar(200)`) on `DockerWatchEntity`.
  Stores a .NET regex (e.g. `^v\d+\.\d+\.\d+$`, `^stable$`,
  `^(?!.*-(rc|beta|alpha)).*$`). Validated at mapper level — malformed
  regexes are rejected with a 400 instead of being persisted.
- ✓ `IRegistryClient.ListTagsAsync` + `OciRegistryClient` implementation of
  the OCI Distribution `GET /v2/{name}/tags/list` endpoint (anonymous +
  Bearer flows, `?n=` page size, single-page only — pagination via the
  `Link` header is deferred).
- ✓ `DockerUpdateChecker` orchestrator switches to a list-and-pick path
  when `TagPatternFilter` is set: list tags → regex filter → sort with
  `TagVersionComparer` (semver-aware, plain-release > pre-release per
  semver §11, lexicographic fallback) → resolve digest of the top
  candidate → compare against the running container's digest. No
  matching tag yields `UpToDate` with an explanatory `Error` string;
  registry failures during listing surface as `Error`.
- ✓ UI: collapsible "Tag pattern filter" row in the Docker watch form
  with a preset dropdown (`SemVer only`, `Stable only (no -rc/-beta/-alpha)`,
  `Tag equals "stable"`) and a free-text override.
- ✓ EF migration `20260517091959_AddDockerWatchTagPatternFilter`.
- ✓ Tests: 11 new OCI tag-listing tests (anonymous + Bearer + edge
  cases), 6 orchestrator filter tests including the DoD scenario, 5
  mapper round-trip + validation tests, 8 `TagVersionComparer` sort
  tests, 4 controller round-trip tests (Create + Update + clear + invalid
  regex). All 508 existing + new tests pass.

**DoD:** a watch with `TagPatternFilter = ^v\d+\.\d+\.\d+$` does not flip
to *Update available* when the registry publishes `v2.1.0-rc1`. ✅
(covered by
`DockerUpdateCheckerTests.CheckAsync_FilterSet_PreReleaseOnly_StaysUpToDate_DoD`.)

---

### ✅ Phase V2.2 — Scheduled check intervals (Hourly / Daily / Weekly)

**Complexity:** Low–Medium  
**Value:** Gives users full control over *when* checks happen, not just *how often*.
The default shifts from 6 h to **24 h**, dramatically reducing anonymous Docker Hub
quota consumption out of the box. A "Daily at 08:00" schedule is far more intuitive
than "every 1440 minutes".

The current model (`CheckIntervalMinutes`, range 30–1440) works but is blunt: it
only expresses "repeat every N minutes" with no concept of a preferred time-of-day.
Users who want checks in the early morning (when they're at the keyboard) currently
have to guess the right offset and hope the background loop coincides.

**New domain model:**

```csharp
public enum CheckScheduleType
{
    Hourly = 0,   // every N hours — rolling from LastCheckedUtc
    Daily  = 1,   // once per day at a fixed UTC time
    Weekly = 2,   // once per week on a fixed day + UTC time
}
```

New / changed fields on `DockerWatchEntity` (and the shared
`DockerConnectionEntity` where applicable):

| Field | Type | Notes |
|---|---|---|
| `ScheduleType` | `CheckScheduleType` | Default `Hourly` |
| `CheckEveryHours` | `int` | Only for `Hourly`. Allowed: **1, 2, 4, 6, 12, 24**. Default **24**. |
| `CheckAtTime` | `TimeOnly?` | Only for `Daily` / `Weekly`. Stored in UTC. |
| `CheckOnDayOfWeek` | `DayOfWeek?` | Only for `Weekly`. |
| ~~`CheckIntervalMinutes`~~ | ~~`int`~~ | **Removed.** Migration converts existing rows. |

**Migration: `UpdateDockerWatchCheckSchedule`**

Existing `CheckIntervalMinutes` values are converted to the nearest
`CheckEveryHours` bucket:

| Old interval | New schedule |
|---|---|
| ≤ 60 min | `Hourly`, every 1 h |
| ≤ 120 min | `Hourly`, every 2 h |
| ≤ 240 min | `Hourly`, every 4 h |
| ≤ 480 min | `Hourly`, every 6 h |
| ≤ 720 min | `Hourly`, every 12 h |
| > 720 min | `Hourly`, every 24 h |

After data migration the `CheckIntervalMinutes` column is dropped.

**Background service due-ness logic:**

```csharp
private static bool IsDue(DockerWatchEntity watch, DateTime now) =>
    watch.LastCheckedUtc is null ||
    watch.ScheduleType switch
    {
        CheckScheduleType.Hourly => now >= watch.LastCheckedUtc.Value
                                              .AddHours(watch.CheckEveryHours),

        CheckScheduleType.Daily  => IsDailyDue(
                                        watch.CheckAtTime!.Value,
                                        watch.LastCheckedUtc.Value, now),

        CheckScheduleType.Weekly => IsWeeklyDue(
                                        watch.CheckOnDayOfWeek!.Value,
                                        watch.CheckAtTime!.Value,
                                        watch.LastCheckedUtc.Value, now),
        _ => false,
    };

// IsDailyDue: the most recent past occurrence of CheckAtTime (today or
// yesterday in UTC) is strictly later than LastCheckedUtc.
//
// IsWeeklyDue: the most recent past occurrence of (CheckOnDayOfWeek,
// CheckAtTime) within the last 7 days is strictly later than LastCheckedUtc.
```

Both helpers handle the "server was down during the scheduled window" case
gracefully: as soon as the server comes back and the tick fires, the watch
is treated as due if its window was missed.

**UI — check schedule picker:**

Replace the "Check interval (minutes)" number input with a compact
segmented control:

```
Check schedule
  ● Every    [▼ 24 h    ]
  ○ Daily at [08 : 00]
  ○ Weekly   [▼ Monday  ]  at  [08 : 00]
```

- **Every** dropdown lists: `1 h / 2 h / 4 h / 6 h / 12 h / 24 h`.
- **Daily** and **Weekly** use `<input type="time">` in the user's local time;
  the frontend converts to UTC on save and back on load.
- A helper line beneath the picker shows:
  `"Next check ~08:00 UTC · in about 14 h"` (computed client-side from
  `lastCheckedUtc` + schedule).

**Impact on rate-limit guidance:**  
`DOCKER_UPDATE_MONITORING_GUIDE.md` §3.6 and §10 are updated: the new
default is **24 h**, which means a setup with 24 anonymous Docker Hub
watches consumes exactly 24 of the 100 anonymous pulls per window — well
within limits without any credentials.

**Tests:**
- Unit: `IsDailyDue` / `IsWeeklyDue` covering the midnight boundary,
  the "missed window" (server down) case, and the "already checked today
  after the target time" case.
- Migration test: every `CheckIntervalMinutes` bucket converts to the
  correct `CheckEveryHours` value.
- Controller: all three schedule types round-trip correctly through
  `POST` and `PUT /watches/{id}`; validation rejects `CheckAtTime = null`
  for `Daily` / `Weekly`.
- Background service: a `Daily`-scheduled watch is skipped when the
  target time hasn't arrived yet, and fires in the next tick after it does.

**Shipped:**

- ✓ `CheckScheduleType` enum in `Stashboard.Core/Enums/`, new schedule fields
  on `DockerWatchEntity` (`ScheduleType`, `CheckEveryHours`, `CheckAtTime`,
  `CheckOnDayOfWeek`), legacy `CheckIntervalMinutes` removed.
- ✓ EF migration `20260517100151_UpdateDockerWatchCheckSchedule` with the
  bucketing data conversion from the roadmap table (≤60→1h, ≤120→2h,
  ≤240→4h, ≤480→6h, ≤720→12h, >720→24h). Reversible — the `Down` path
  rebuilds `CheckIntervalMinutes` from the new fields.
- ✓ `Stashboard.Core/Scheduling/CheckScheduleEvaluator` — pure functions for
  due-ness (`IsDue`) and the projected next-check timestamp (`NextCheckUtc`).
  Handles the "missed window" case (server was down) by treating the missed
  occurrence as immediately due as soon as the tick fires.
- ✓ `DockerUpdateBackgroundService` switches to the evaluator for filtering
  the loaded watch list — same shape as before, new semantics.
- ✓ `DockerWatchMapper.NormalizeSchedule` validates the per-mode invariants
  (Hourly hours in `{1, 2, 4, 6, 12, 24}`, Daily needs `CheckAtTime`, Weekly
  needs both `CheckAtTime` and `CheckOnDayOfWeek`) and throws
  `FormatException` so the controller returns `400`.
- ✓ `DockerWatchResponse` exposes `ScheduleType` / `CheckEveryHours` /
  `CheckAtTime` / `CheckOnDayOfWeek` plus a derived `NextCheckUtc` so the
  UI helper line is computed server-side, consistent with the loop.
- ✓ Frontend: new `SchedulePicker` component (segmented radio control with
  Hourly preset dropdown, Daily and Weekly time pickers, day-of-week select)
  in `DockerWatchSection.tsx`. Time inputs run in the user's local timezone;
  the form converts to / from UTC at the API boundary. Helper line shows
  the next-check projection.
- ✓ Tests: 17 `CheckScheduleEvaluatorTests`, 7 new `DockerWatchMapperTests`
  for the validation paths, 4 new `DockerUpdateBackgroundServiceTests`
  covering Daily / Weekly due-ness, 4 new
  `CreateWatchEndpointTests` for the controller round-trip. All 542 tests pass.

**DoD met:** a watch set to `Daily at 08:00 UTC` is checked exactly once per
day (within the ±5-minute tick granularity of the background service) and
never again until the next 08:00 window — verified by
`DockerUpdateBackgroundServiceTests.ScanOnce_DailySchedule_BeforeTargetTime_IsSkipped`
and `…_AfterTargetTime_IsDue`. ✅

---

### ✅ Phase V2.3 — GitHub Releases enrichment

**Complexity:** Low  
**Value:** Surfaces the changelog inline — no more tab-switching to GitHub.

When a GHCR-hosted image (`ghcr.io/{owner}/{repo}:{tag}`) receives an
update, the owner's GitHub repository very likely has a matching release
with full release notes.

**Scope:**

- New service `IGitHubReleaseClient` / `GitHubReleaseClient` in
  `Stashboard.Infrastructure`. Calls
  `GET https://api.github.com/repos/{owner}/{repo}/releases/tags/{tag}`
  (unauthenticated for public repos; accepts optional PAT for private).
- `DockerUpdateChecker` enriches the `DockerCheckResult` with
  `ReleaseNotesUrl?` and `ReleaseBody?` (truncated to 2 000 chars) when
  the registry host is `ghcr.io` and a matching GitHub release exists.
- New nullable columns on `DockerWatchEntity`: `LatestReleaseUrl`, `LatestReleaseBody`.
- `DockerWatchResponse` exposes them.
- UI: collapsible "What's new" section in the watch status panel when
  `LatestReleaseBody` is non-null. A "View on GitHub" link opens
  `LatestReleaseUrl` in a new tab.
- Optional per-watch `GitHubPatEncrypted` field (same tri-state pattern
  as other secrets) — needed only for private repos.
- Email / Telegram notification template gets a one-liner
  `"Release notes: {url}"` appended when available.
- Tests: mock `IGitHubReleaseClient`; verify enrichment path; verify
  graceful degradation when GitHub API returns 404 (no release for that tag).

**Shipped:**

- ✓ `IGitHubReleaseClient` + `GitHubReleaseClient` in
  `Stashboard.Infrastructure/GitHub/`. One `GET /repos/{owner}/{repo}/releases/tags/{tag}`
  call with optional Bearer-PAT, sends `Accept: application/vnd.github+json`
  + `X-GitHub-Api-Version: 2022-11-28`. Categorises 404 / 401 / 403 (+
  rate-limit header) / 429 / network errors / malformed JSON into a typed
  `GitHubReleaseStatus`. Body truncated to **2 000 chars** at the source
  with a trailing `…`.
- ✓ Named HTTP client `"github-releases"` registered alongside the
  existing `"registry"` client.
- ✓ Three new columns on `DockerWatchEntity` —
  `GitHubPatEncrypted`, `LatestReleaseUrl`, `LatestReleaseBody` —
  plus the migration `20260517143416_AddDockerWatchGitHubReleaseEnrichment`
  (additive only, no data conversion).
- ✓ `DockerUpdateChecker.TryFetchReleaseAsync` fires the enrichment only
  when the registry host is `ghcr.io` and `Repository` is a flat
  `owner/repo` (so a nested `org/team/project` doesn't 404 every check).
  Decrypted PAT is propagated through the new `DockerWatchProfile.GitHubPat`.
  Enrichment never affects the parent `Status` — a 404 / rate-limit /
  network blip leaves the release fields null.
- ✓ Persistence rules in `ApplyCheckResult` (controller + background loop):
  refresh the cached release pair on every digest change; otherwise only
  fill in additional info, never blank out a previously-known one.
- ✓ Mapper handles the tri-state `GitHubPat` upsert/test fields,
  surfaces `HasGitHubPat` flag in the response, and surfaces
  `LatestReleaseUrl` / `LatestReleaseBody`. The PAT itself is never
  returned over the wire (asserted in tests).
- ✓ Notification templates pick up the release URL — email body /
  Telegram message both append a `Release notes: {url}` line when
  present.
- ✓ Frontend: `DockerWatch` / `DockerWatchUpsert` types extended;
  conditional GitHub PAT secret field (only rendered when image
  reference starts with `ghcr.io`); collapsible "What's new" panel in
  the watch status with a `View on GitHub →` link.
- ✓ Tests: 16 `GitHubReleaseClientTests` (happy path, headers, all
  failure modes, body truncation, blank-input guard), 5 new
  `DockerUpdateCheckerTests` (GHCR enrichment, graceful 404, non-GHCR
  skip, PAT propagation, nested-repo skip), 7 new
  `DockerWatchMapperTests` (PAT tri-state, response release fields,
  profile PAT propagation), 3 new
  `DockerUpdateNotificationServiceTests` (release line in email html +
  text + Telegram), persistence test updated to assert the new
  columns round-trip. All 575 tests pass.

**DoD met:** a GHCR image whose tag has a matching GitHub release surfaces
release notes in the modal's "What's new" panel and the notification
email/Telegram message carries the `Release notes: {url}` line. ✅
(`DockerUpdateCheckerTests.CheckAsync_GhcrUpdateAvailable_FetchesGitHubReleaseAndPopulatesEnrichmentFields`
and the matching notification tests cover the end-to-end path.)

---

### ✅ Phase V2.4 — Self-hosted registry support

**Complexity:** Medium  
**Value:** Unlocks Harbor, Nexus, Gitea Packages, and AWS ECR — the registries
most common in private / enterprise setups.

The existing `OciRegistryClient` already speaks OCI Distribution v2 and
handles arbitrary registry hosts. The gap is **authentication**, which
differs per registry type:

| Registry | Auth method |
|---|---|
| Harbor | Basic auth or robot-account token (same OCI Bearer flow, different realm) |
| Nexus Repository | HTTP Basic (no Bearer) |
| Gitea Packages | HTTP Basic or API token header |
| AWS ECR | `aws ecr get-login-password` → temporary Basic token (12-hour TTL) |

**Scope:**

- New enum `RegistryAuthType { Auto = 0, Basic = 1, AwsEcr = 2 }` on
  `DockerWatchEntity` (nullable; `Auto` = current behaviour).
- For `AwsEcr`: new encrypted fields `AwsAccessKeyIdEncrypted`,
  `AwsSecretAccessKeyEncrypted`, `AwsRegion`. A new
  `IAwsEcrTokenProvider` calls the ECR `GetAuthorizationToken` API and
  caches the temporary token (valid 12 h) in `IMemoryCache`.
- `OciRegistryClient` dispatches to the correct auth strategy based on
  `RegistryAuthType`.
- Image reference parser: recognise `{account}.dkr.ecr.{region}.amazonaws.com`
  as `AwsEcr` automatically; user can override.
- UI: new "Registry type" dropdown in the Docker watch form;
  AWS-specific fields (`Access key ID`, `Secret access key`, `Region`)
  appear conditionally.
- Tests: unit tests per auth strategy; integration-test flag
  `STASHBOARD_RUN_REGISTRY_TESTS=1` for opt-in tests against a real
  ECR / Nexus instance.

**Shipped:**

- ✓ New enum `RegistryAuthType { Auto = 0, Basic = 1, AwsEcr = 2 }` on
  `DockerWatchEntity`, three additional columns (`AwsAccessKeyIdEncrypted`,
  `AwsSecretAccessKeyEncrypted`, `AwsRegion`). Pure additive migration
  `20260517145654_AddDockerWatchRegistryAuthFields` — the auth-type
  column defaults to `0` (Auto) so every existing row keeps the V2.x
  behaviour.
- ✓ `IAwsEcrTokenProvider` + `AwsEcrTokenProvider` in
  `Stashboard.Infrastructure/Aws/`. Hand-rolled SigV4 signer hits
  `ecr.{region}.amazonaws.com` with the
  `AmazonEC2ContainerRegistry_V20150921.GetAuthorizationToken` target,
  decodes the Base64 `AWS:<token>` payload into a `RegistryCredentials`
  pair, and caches the result in `IMemoryCache` keyed on access-key-id +
  region until 30 minutes before the ECR-reported expiry. Deliberately
  carries **no** AWS SDK dependency.
- ✓ New `RegistryAuthContext` record threaded through `IRegistryClient`.
  `OciRegistryClient` dispatches on strategy: `Auto` keeps the existing
  anonymous → Bearer flow, `Basic` skips the Bearer round-trip
  (required for Nexus / Gitea Packages), `AwsEcr` resolves credentials
  via the provider then issues a Basic-authed call. Backwards-compatible
  `RegistryClientExtensions` overloads keep the V2.x call sites working.
- ✓ `ImageReferenceParser.LooksLikeEcrHost` / `TryExtractEcrRegion` —
  recognises private ECR hostnames (`{12 digits}.dkr.ecr.{region}.amazonaws.com`).
  The mapper auto-promotes `Auto` → `AwsEcr` and auto-fills `AwsRegion`
  from the hostname, but the user can override.
- ✓ Mapper handles the tri-state AWS secrets, clears AWS columns when
  the strategy switches away from `AwsEcr` (no stale credentials), and
  surfaces `RegistryAuthType` / `HasAwsCredentials` / `AwsRegion` in the
  response. AWS access key id and secret are never returned over the
  wire.
- ✓ Frontend: new "Registry type" dropdown (Auto / Basic / AWS ECR) +
  conditional AWS-specific fields (access key id, secret, region). The
  AWS fields auto-appear when the user types an ECR hostname even with
  `Auto` selected; a one-line hint suggests promoting to ECR.
- ✓ Tests: 10 `AwsEcrTokenProviderTests` (SigV4 header shape, cache
  reuse, all failure modes, blank-input guard, Unix-timestamp parsing),
  6 new `OciRegistryClientTests` (Basic-only flow, ECR token resolution
  + Basic dispatch, ECR error propagation, missing-region short-circuit),
  4 new `ImageReferenceParserTests` (ECR host detection + region
  extraction, public ECR / look-alikes rejected), 8 new
  `DockerWatchMapperTests` (auto-promotion, explicit override, switching
  away clears AWS columns, region inference, response presence flags,
  profile propagation), persistence test updated to assert the new
  columns round-trip. All 612 tests pass.

**DoD met:** a watch pointing at a private ECR image with valid IAM
credentials resolves "Update available" without manual Bearer token
plumbing — verified by
`OciRegistryClientTests.GetManifestDigest_AwsEcr_FetchesTokenFromProviderAndUsesItAsBasic`
and the surrounding mapper + parser tests. ✅
Harbor / Nexus / Gitea Packages reach the same outcome through the
`Basic` strategy
(`OciRegistryClientTests.GetManifestDigest_BasicAuth_SendsBasicHeaderAndNeverFollowsBearerChallenge`).

---

### ✅ Phase V2.5 — SSH Docker host

**Complexity:** Medium  
**Value:** Removes the requirement to expose the Docker daemon over TCP;
supports the majority of VPS setups where only SSH is available.

The `DockerHostType` enum already reserved `Ssh = 2` for this phase since V1.

**Shipped:**

- ✓ New enum value `DockerHostType.Ssh = 2` activated.
- ✓ Six additional columns on `DockerConnections`: `SshHost`,
  `SshPort`, `SshUsername`, `SshPrivateKeyEncrypted`,
  `SshPrivateKeyPassphraseEncrypted`, `SshRemoteSocketPath`. EF migration
  `20260517191340_AddDockerConnectionSshFields` is pure additive — every
  column is nullable and existing rows keep their `LocalSocket`/`TcpTls`
  host type unchanged. Reversible via the standard `Down` path.
- ✓ `SshDockerTunnel` (in `Stashboard.Infrastructure/Docker/Ssh/`)
  opens an `SshClient` via `SSH.NET 2024.2.0`, binds a fresh TCP listener
  on `127.0.0.1:<random port>`, and bridges each accepted TCP connection
  through a remote exec channel running `docker system dial-stdio`
  (with a `socat - UNIX-CONNECT:<path>` fallback for legacy hosts).
  Tunnel lifetime is bound to the `IDockerClient` returned from
  `DockerClientFactory` via the `SshTunnelledDockerClient` wrapper, so
  `using (client) { ... }` cleans up both the docker handle and the SSH
  session. The "lower-level: `docker context create`" alternative was
  rejected because it requires the Docker CLI to be installed inside the
  Stashboard container.
- ✓ `DockerClientFactory.Create(...)` gained an optional
  `DockerSshCredentials` parameter (default `null`) so existing call
  sites keep compiling; SSH hosts validate required fields and surface
  ArgumentException for bad inputs.
- ✓ `IDockerHostClient` consolidated host transport into a single
  `DockerHostTransport(HostType, HostUrl, Tls, Ssh)` bundle so every
  call site (host client, checker, controllers, mapper) sees the same
  shape. SSH handshake errors (`SshException`, `SocketException`) are
  caught centrally and surfaced as
  `DockerHostStatus.HostUnreachable` with a `"SSH connection failed: …"`
  message — same UX path as a TLS handshake failure.
- ✓ `DockerConnectionMapper.ApplyUpsert` clears every SSH column when
  the host type switches away from `Ssh` so an old SSH config can't
  shadow a switch back to `LocalSocket` / `TcpTls`. Same tri-state
  semantics for the private key and passphrase as for TLS.
- ✓ `DockerConnectionResponse` exposes `SshHost`, `SshPort`,
  `SshUsername`, `HasSshPrivateKey`, `HasSshPrivateKeyPassphrase`, and
  `SshRemoteSocketPath`. PEM and passphrase are never returned over
  the wire (covered by `ToResponse_NeverIncludesDecryptedSshSecrets`).
- ✓ Frontend: `'Ssh'` added to the host-type selector. Conditional SSH
  fields render when selected (host, port, username, remote socket
  path) + tri-state secret rows for the PEM private key and optional
  passphrase. Connection picker labels SSH hosts as
  `ssh://<user>@<host>`.
- ✓ Tests: 12 new `DockerConnectionMapperTests` (entity round-trip,
  tri-state Keep/Set/Clear, port-range guard, host-type switching
  clears SSH columns), 3 new `DockerHostClientTests` (SSH handshake
  failures map to HostUnreachable, factory sees credentials intact),
  2 new `SshDockerTunnelTests` (malformed PEM throws cleanly, default
  socket constant). End-to-end SSH bridging is verified by an opt-in
  integration test gated behind `STASHBOARD_RUN_LOCAL_DOCKER_TESTS=1`
  + a real OpenSSH-accessible Docker host.

**DoD met:** a watch with `HostType = Ssh` opens an SSH connection to the
remote host, bridges `docker system dial-stdio` over the resulting
session, and reads the container digest through that tunnel. The tunnel
is torn down on `IDockerClient.Dispose()` — no long-lived sessions are
held by the background loop. ✅

---

### ✅ Phase V2.6 — Webhook receiver

**Complexity:** High  
**Value:** Eliminates polling latency entirely for images the user publishes
themselves. An update is detected within seconds of the push.

**Shipped:**

- ✓ New entity columns `WebhookToken` (`varchar(64)`, nullable, unique) and
  `LastWebhookReceivedUtc` on `DockerWatchEntity`; pure-additive migration
  `20260518102015_AddDockerWatchWebhookToken`. The unique index allows
  multiple NULLs (the majority of watches won't opt in).
- ✓ `IDockerWebhookTokenGenerator` + `DockerWebhookTokenGenerator` —
  32-byte CSPRNG hex string (256 bits of entropy, lowercase, length 64).
- ✓ `IDockerWebhookPayloadParser` + `DockerWebhookPayloadParser` —
  recognises Docker Hub `push` events, GitHub `registry_package` events,
  and generic OCI distribution pushes. Never throws on malformed input
  (the URL token already authenticated the call); unknown shapes degrade
  to `DockerWebhookPayload.Unknown` and the check still fires.
- ✓ `IDockerWebhookCheckQueue` + `DockerWebhookCheckQueue` — process-local,
  lock-free `ConcurrentDictionary`-backed queue with duplicate collapsing
  (50 webhooks for the same watch produce at most one check per drain)
  and bounded capacity (1024).
- ✓ Public endpoint `POST /api/docker/webhooks/{watchToken}` in
  `DockerWebhooksController` ([`AllowAnonymous`], 64-char hex shape
  validated before DB lookup, 64 KB body cap, returns `202 Accepted` even
  on disabled watches / queue overflow / malformed bodies so registries
  don't retry-storm; `404` only for unknown / malformed tokens).
- ✓ Owner endpoints on `DockerWatchesController`: `POST
  /api/services/{id}/docker/watches/{watchId}/webhook/rotate` (generate or
  rotate, with collision-retry against the unique index) and `DELETE
  /api/services/{id}/docker/watches/{watchId}/webhook` (clear token +
  reset `LastWebhookReceivedUtc`).
- ✓ Background loop integration: `DockerUpdateBackgroundService` now ticks
  every 5 s, performs the schedule-driven sweep on the configured
  `TickIntervalSeconds` cadence and drains the webhook queue on every
  intermediate tick via the new public `DrainWebhookQueueOnceAsync`. Hybrid
  fallback — webhook delivery is best-effort, the schedule is the safety net.
- ✓ Frontend: `webhookToken` / `lastWebhookReceivedUtc` on the
  `DockerWatch` payload, `useRotateDockerWebhook` /
  `useDeleteDockerWebhook` mutations, and a new collapsible
  `WebhookPanel` on the existing-watch view. Token is hidden by default
  behind a reveal toggle, with copy-to-clipboard and rotate / disable
  controls. URL is composed client-side from `window.location.origin` +
  the token so the server doesn't need to know its public base URL.
- ✓ Tests: 6 webhook controller integration tests (token shapes, valid /
  unknown / malformed tokens, disabled watches, malformed bodies), 5
  rotate / delete controller tests (initial generation, rotation
  collision-retry, foreign-watch isolation, clear when set / not set),
  3 background-service drain tests (queued watch bypasses schedule,
  disabled skipped, empty queue is a no-op), 5 parser tests (Docker Hub /
  GHCR / generic OCI / unknown shapes / partial fields), 5 queue tests
  (accept / reject / duplicate collapse / drain idempotency / capacity),
  2 token generator tests (shape, distinctness), 2 persistence tests
  (round-trip, unique-token + multiple-NULL behaviour). All 668 tests pass.

**DoD met:** seeded watch with a known `WebhookToken` flips to
`UpdateAvailable` on the next 5-second drain tick after a `POST /api/docker/webhooks/{token}`
returns `202` — verified by
`DockerUpdateBackgroundServiceTests.DrainWebhookQueue_QueuedWatch_RunsCheckImmediatelyBypassingSchedule`
end-to-end against the real test database. ✅

---

### ✅ Phase V2.7 — Auto-update ("Update now")

**Complexity:** High  
**Value:** Closes the loop — from "there's an update" to "container is running
the new image" without leaving the UI.

The most operationally risky V2 feature: a bug or misconfiguration could
restart a production container unexpectedly. Therefore it is **opt-in
per-click** (each press of "Update now" pops a confirmation modal) and
every attempt is written to an immutable audit log so the user can always
reconstruct what happened.

**Shipped:**

- ✓ Docker socket must be **writable** for V2.7 to do its work. The
  user guide gains §5.1 with a clear warning, the `:ro` line stays as
  the safer default in `docker-compose.yml`, and the comment explicitly
  calls out which feature needs which mode.
- ✓ New `IDockerImageUpdater` + `DockerImageUpdater` in
  `Stashboard.Infrastructure/Docker/`. Watchtower-style raw recreate
  via `Docker.DotNet`: pull the new image (`ImagesCreateParameters`,
  with the user's registry credentials / ECR token / anonymous as
  appropriate), inspect the running container, stop + force-remove it,
  `CreateContainerAsync` with the same config — env, mounts, ports,
  network mode, **labels** (so Compose-managed containers stay
  Compose-managed) — and `StartContainerAsync`. Single
  `NetworkingConfig.EndpointsConfig` entry at create time + post-start
  `Networks.ConnectNetworkAsync` for the multi-network case. Never
  throws: every failure mode is mapped to a typed
  `DockerUpdateAttemptStatus` (`Success` / `PullFailed` /
  `RecreateFailed` / `HostUnreachable` / `ContainerNotFound`).
- ✓ Two new endpoints on `DockerWatchesController`:
  `POST /api/services/{id}/docker/watches/{watchId}/update` (synchronous
  pull + recreate; on success the controller immediately re-runs the
  orchestrator so the watch's status flips back to `UpToDate` in the same
  round-trip) and `GET /api/services/{id}/docker/watches/{watchId}/updates`
  (per-watch history, newest first, capped at 50 rows).
- ✓ `DockerUpdateAttemptStatus` enum + `DockerUpdateAttemptEntity` audit
  row + EF migration `20260518162140_AddDockerUpdateAttempts`. Cascades
  through both the parent watch and the parent service — when the
  audited thing goes away, so does the audit.
- ✓ Contracts: `DockerWatchUpdateResponse` (attempt + refreshed watch in
  one payload) and `DockerUpdateAttemptResponse` for the history.
- ✓ `IDockerWatchMapper.BuildUpdateProfile` decrypts the watch's
  credentials (registry + ECR + TLS + SSH) into the entity-free
  `DockerUpdateProfile` the updater takes.
- ✓ Frontend: `useUpdateDockerNow` mutation + `useDockerWatchUpdates`
  query (lazy — only fetches when the user opens the accordion), an
  **Update now** button next to **Check now** in the watch status row
  (highlighted on `UpdateAvailable`, hidden for disabled watches, wraps
  every click in a `confirm()` that names the exact image reference and
  container), and a collapsible **Update history** accordion that lists
  every attempt with its outcome badge, digest transition, and error
  string.
- ✓ Tests: 11 `DockerImageUpdaterTests` (happy recreate end-to-end,
  Compose label preservation, registry-auth-config translation,
  `library/` short-name normalisation, GHCR pull reference, container
  missing / daemon unreachable / pull failure / recreate failure after a
  successful pull), 9 `UpdateNowEndpointTests` (success + audit-row
  persistence + re-check flips status to UpToDate, pull failure leaves
  watch untouched, host unreachable, disabled watch refused with 400,
  missing / foreign watch returns 404, history listing newest-first +
  cross-service path-tampering 404), 3 new `DockerWatchMapperTests`
  (update profile builder, ECR auth fields decrypt path, attempt-entity
  round-trip). All **691** existing + new tests pass.
- ✓ Documentation: user guide gains §5.1 ("Auto-update — Update now")
  covering the writable-socket requirement, the per-click confirmation,
  what gets preserved across the recreate, the audit history, and the
  rollback story when something fails halfway through. README + business
  requirements updated.

**DoD met:** clicking **Update now** on an `UpdateAvailable` watch pulls
the new image, recreates the container with the same name + every
preserved config bit, the controller's inline re-check flips the watch
back to `UpToDate` in the same response, and a row in
`DockerUpdateAttempts` records the pre/new digest pair. Verified by
`UpdateNowEndpointTests.Update_SuccessfulPullRecreate_PersistsAttemptAndFlipsWatchToUpToDate`. ✅

---

### V2 effort estimate (rough)

| Phase | Feature | Complexity | Effort |
|---|---|---|---|
| V2.1 | Tag-pattern filtering ✅ | Low | 0.5 day (shipped) |
| V2.2 | Scheduled check intervals (Hourly / Daily / Weekly) ✅ | Low–Medium | 1 day (shipped) |
| V2.3 | GitHub Releases enrichment ✅ | Low–Medium | 1 day (shipped) |
| V2.4 | Self-hosted registries ✅ | Medium | 2 days (shipped) |
| V2.5 | SSH Docker host ✅ | Medium | 2 days (shipped) |
| V2.6 | Webhook receiver ✅ | High | 3 days (shipped) |
| V2.7 | Auto-update ("Update now") ✅ | High | 4 days (shipped) |
| | **Total** | | **~13.5 days** (all shipped) |

---

## 12. V3 Roadmap (future ideas)

> These are improvements worth implementing one day but not scheduled yet.
> None block any current functionality; they are quality-of-life or
> operational-robustness items that require non-trivial effort or external
> dependencies.
>
> **Items are ordered from simplest to most complex.** Earlier phases reuse
> infrastructure (Docker client, audit log, permission gate) that is already
> in place; later phases require new transports (WebSocket / SignalR), new
> external integrations (Proxmox API, SSH), or new security surfaces
> (interactive shells) and so are deliberately back-loaded.

---

### ✅ Phase V3.1 — Container Inspect viewer (shipped 2026-05-19)

**Complexity:** Low
**Value:** Surfaces the same `ContainerInspectAsync` payload that V2.7
already uses internally — full container config (image digest, command, env,
mounts, networks, labels, restart policy, health state, ports) — directly in
the service modal, so the user does not need shell access on the host to
debug a misconfigured container.

**What landed:**

- New API endpoint `GET /api/services/{webResourceId}/docker/watches/{watchId}/inspect`.
  Routed under the existing per-watch namespace (not the standalone
  `/api/docker-watches/{id}` originally sketched) so it inherits the
  service-ownership check the other watch endpoints already enforce.
- `IDockerHostClient.InspectContainerAsync` returns a slimmed
  `DockerContainerInspect` DTO covering id / name / image / image id /
  repo digests / state (+ health log) / command / env / labels / exposed +
  published ports / mounts / networks / restart policy / created /
  restart count / platform / driver. Env values whose key matches a
  conservative secret heuristic (`PASSWORD` / `PASSWD` / `SECRET` /
  `TOKEN` / `API_KEY` / `AUTH` / `CREDENTIAL` / `PRIVATE_KEY` /
  `ACCESS_KEY`, case-insensitive substring) arrive on the wire as
  `value: null, masked: true` so the UI cannot accidentally render
  plaintext credentials baked into the container.
- Frontend: a collapsible "Inspect container" panel in the watch
  details modal alongside the V2.6 Webhook + V2.7 Update history
  accordions. Lazy-fetches on first expand (`useDockerWatchInspect`,
  10 s stale-time), renders structured sub-sections per category, plus
  a "Raw JSON" view with copy-to-clipboard. Same admin-only permission
  gate as the rest of the Docker actions.
- Read-only — no daemon mutations, no new audit rows needed.

**Tests:** controller envelope tests (happy path, container-not-found
→ 404, host-unreachable / thrown HttpRequestException → 502,
owner-scoping → 404) plus unit coverage of the env-secret heuristic in
`DockerInspectMapperTests`.

---

### ✅ Phase V3.2 — Async post-update health verification (shipped 2026-05-19)

**Complexity:** Low–Medium
**Value:** Prevents a "success" audit row from being written when the new
container starts, crashes five seconds later, and Docker's restart policy is
still cycling. V2.7's inline re-check fired the moment `StartContainerAsync`
returned — at that instant the container is technically running, but it may not
be healthy yet.

**What landed:**

- After `StartContainerAsync` succeeds, `DockerImageUpdater.VerifyHealthAsync`
  polls `InspectContainerAsync` (default 10 attempts × 3 s = a 30-second
  window, both configurable via `DockerUpdateOptions.HealthVerificationMaxAttempts`
  / `HealthVerificationIntervalSeconds`) until the container reports `healthy`,
  has no `HEALTHCHECK` at all (`none` → treated as healthy once `Running`),
  reports `unhealthy`, or the window expires.
- `DockerUpdateAttemptEntity` gained `HealthVerified` (bool) + `HealthVerifiedUtc`
  (migration `20260519090814_AddDockerUpdateAttemptHealthVerified`). `HealthVerified`
  is true only when the container converged to `healthy` / `none` inside the
  window; false on `unhealthy`, timeout, or any pre-start failure.
- `unhealthy` within the window and "still `starting` when the window expired"
  both downgrade the attempt to `DockerUpdateAttemptStatus.RecreateFailed` with a
  descriptive error (`"Container started but reported 'unhealthy'…"` /
  `"…did not become healthy within N s"`).
- Setting `HealthVerificationMaxAttempts = 0` disables verification and restores
  the V2.7 behaviour (success the moment `StartContainerAsync` returns;
  `HealthVerified` stays false so consumers can tell "we didn't wait" from
  "we waited and saw healthy").

**Tests:** `DockerImageUpdaterTests` covers the healthy / no-healthcheck /
unhealthy / timeout / disabled paths with a stubbed `DelayAsync` so no real
wall-clock time is burned.

---

### ✅ Phase V3.3 — Real-time container logs (shipped 2026-05-19)

**Complexity:** Low–Medium
**Value:** Lets the user diagnose a misbehaving container (or one that just
failed the V3.2 health check) without SSH-ing into the host. Closes the most
common "why did my update fail?" support loop.

**What landed:**

- New API endpoint `GET /api/services/{webResourceId}/docker/watches/{watchId}/logs`.
  Routed under the existing per-watch namespace so it inherits the
  service-ownership check the other watch endpoints already enforce.
  Query params (`follow`, `tail`, `since`, `timestamps`, `stdout`, `stderr`)
  map directly onto the Engine's `/containers/{id}/logs` parameters.
- `IDockerLogStreamer` (Stashboard.Core) + `DockerLogStreamer`
  (Stashboard.Infrastructure) wrap `Docker.DotNet`'s
  `GetContainerLogsAsync`. The streamer inspects the container's `tty`
  flag up-front, then demuxes stdcopy frames into stdout / stderr
  channels, splits the byte stream into lines, and parses out the
  RFC3339Nano per-line timestamp prefix into `DockerLogLine.TimestampUtc`.
  Trailing-CR handling for CRLF logs from Windows containers; partial
  lines that span TCP chunks are buffered per channel so an interleaved
  stderr frame can't split a still-pending stdout line.
- Wire format: newline-delimited JSON (NDJSON) — one
  `{stream,timestamp,message}` object per HTTP chunk. Friendlier to
  `fetch` + `ReadableStream` on the browser side than SSE (which can't
  carry an `Authorization` header through `EventSource`) and avoids the
  query-token leak that a token-in-URL workaround would cause.
- Frontend: a collapsible "Container logs" panel in the watch details
  modal, between Inspect and Webhook. Opens an NDJSON stream on
  expand, supports stdout / stderr / timestamp toggles, Pause /
  Resume / Stop / Stream controls, a Clear button, and a Download
  button that re-fetches with `follow=false` and saves the snapshot
  as `<container>-<timestamp>.log`. Buffer bounded at 5 000 lines —
  oldest dropped first — so long-running tails don't blow up the tab.
  Auto-scroll while streaming; pause keeps the connection open but
  stops scroll.
- For SSH-based Docker connections (V2.5), the stream rides through
  the same `SshDockerClient` channel that already proxies the daemon
  socket — no extra wiring needed.
- Read-only — never writes to stdin. Same admin-only permission gate
  as the rest of the Docker actions.

**Tests:** controller envelope tests (NDJSON serialisation,
query-param forwarding, both-streams-disabled → 400, owner-scoping →
404, host-unreachable error frame) plus unit coverage of the
`BuildLine` decoder for trailing-CR strip, nanosecond-precision
RFC3339 parsing, timezone-offset prefixes, and the
no-timestamps-when-disabled path in `DockerLogStreamerTests`.

---

### ✅ Phase V3.4 — Live container stats (shipped 2026-05-19)

**Complexity:** Low–Medium
**Value:** Cheap visual signal of "is this container alive, hot, leaking?"
without the user having to stand up a full Prometheus + cAdvisor stack.

**What landed:**

- New API endpoint `GET /api/services/{webResourceId}/docker/watches/{watchId}/stats`
  routed under the existing per-watch namespace for the service-ownership
  check. `oneShot=true` returns a single snapshot (CPU% = null — no
  PreCPUStats baseline); the default streams ~one frame per second from
  the daemon's `/containers/{id}/stats?stream=true` endpoint as NDJSON
  until the client cancels. Same admin-only permission gate as the rest
  of the Docker actions.
- `IDockerStatsStreamer` (Stashboard.Core) + `DockerStatsStreamer`
  (Stashboard.Infrastructure) wrap Docker.DotNet's `GetContainerStatsAsync`.
  The daemon's push-based `IProgress<ContainerStatsResponse>` is decoupled
  from the async HTTP consumer via a bounded
  `Channel<ContainerStatsResponse>` (`DropOldest`, capacity 4) so a slow
  browser can't back-pressure the Docker socket — the user sees the
  latest snapshot instead of stale ones piling up.
- Per-snapshot transform (`ComputeSample`) flattens the raw response:
  CPU% from the canonical `(cpu_delta / system_delta) * online_cpus *
  100` formula, OnlineCPUs falling back on `len(PercpuUsage)` for
  pre-19.03 daemons, unsigned-subtraction guards for clock drift /
  daemon restarts → CPU% reported as `null` rather than garbage;
  memory usage with the kernel page cache subtracted (cgroups v1
  `cache`, v2 `inactive_file`) to match `docker stats`; network bytes
  summed across every interface; block I/O bytes summed from
  `IoServiceBytesRecursive`, ignoring `sync` / `async` / `total`
  rollups to avoid double-counting.
- Wire format: NDJSON over chunked HTTP, same shape as V3.3 — one
  `{timestampUtc,cpuPercent,memoryUsageBytes,memoryLimitBytes,
  memoryPercent,networkRxBytes,networkTxBytes,blockReadBytes,
  blockWriteBytes,onlineCpus}` object per HTTP chunk. Terminal failures
  surface as a synthetic `{stream:"error",...}` frame so the UI can
  show why the stream ended.
- Frontend: a collapsible "Live stats" panel in the watch details
  modal between Inspect and Logs. Opens an NDJSON stats stream on
  expand, keeps a bounded rolling window (last 120 samples ≈ 2 min),
  and renders a 4-tile grid (CPU / Memory / Network / Block I/O) with
  inline-SVG sparklines for CPU% and memory% (no chart library
  dependency). Network / block-I/O are absolute counters in the wire
  payload; the UI derives the per-second rate by subtracting the
  previous sample. Stream / Stop controls; status dot reflects
  streaming / idle / error.
- For SSH-based Docker connections (V2.5), the stream rides through
  the same `SshDockerClient` channel as V3.3.

**Reference-counting deferred:** The roadmap originally called for
reference-counting per container so multiple browser tabs share one
daemon connection. The watch modal is mounted per service so concurrent
views of the *same* container from one user are rare; the daemon
overhead of a second connection is negligible. We'll revisit when V3.5
ships the Docker instances page and grid views surface the same
container multiple times.

**Tests:** 11 `ComputeSample` unit tests in
`DockerStatsStreamerTests` (first-sample null CPU, classic CPU%
formula, counters-going-backwards guard, PercpuUsage fallback,
cgroup v1 / v2 page-cache subtraction, missing Stats dict,
zero-limit → null percent, multi-interface network sum, blkio
rollup exclusion, empty-response default). 7 controller tests in
`StatsEndpointTests` (NDJSON serialisation, oneShot flag
forwarding, default-streaming, owner-scoping → 404, watch-not-found
→ 404, container-not-found error frame, host-throws error frame).

---

### ✅ Phase V3.5 — Docker instances page with container management cards (shipped 2026-05-19)

**Complexity:** Medium
**Value:** Today every Docker watch is attached to a *service* — there is no
top-down "show me everything running on this host" view. A dedicated page
turns Stashboard into a lightweight Portainer for cases where Portainer would
be overkill.

**What landed:**

- New page `/docker` (route + nav link). Lists every Docker connection
  the current user owns as its own section; inside each section, a
  responsive card grid — one card per container returned by
  `ContainerListAsync(all: true)` on that host (richer than the V1
  list — adds container id, created timestamp, port mappings, and the
  standard `com.docker.compose.*` labels).
- Each card shows: name, image, state badge (running / exited /
  restarting / created / dead — colour-coded), `docker ps` status
  string, created-relative timestamp, exposed + published port chips,
  compose project / service when present, and (when the user has a
  watch tracking the container) an **Open in service** deep link that
  routes back to the watch modal — so V3.1 Inspect / V3.3 Logs / V3.4
  Stats panels are one click away.
- Action buttons per card: **Start** / **Stop** / **Restart** are
  inline; **Remove** only renders when the server-side
  `Stashboard:AllowContainerRemoval` feature flag is on, behind a
  second `window.confirm` that names the container. **Recreate** /
  **Logs** / **Inspect** / sparkline were *deferred* — see "What was
  cut" below.
- All actions write to the existing `DockerUpdateAttempts` audit
  table, extended with a new `ActionType` discriminator (`Update` /
  `Start` / `Stop` / `Restart` / `Remove`), a `DockerConnectionId` FK,
  and a `ContainerId` snapshot. `DockerWatchId` / `WebResourceId` are
  now nullable so actions on containers that aren't tracked by any
  watch fit the same row shape. Existing V2.7 rows back-fill cleanly
  to `ActionType=Update` / `DockerConnectionId=null`.
- New endpoints under `/api/docker/connections/{id}/instance`:
  `GET /containers` (joins on the user's own watches so each card
  carries its `watchId` / `webResourceId` back-link), `POST
  /containers/{name}/start` / `/stop` / `/restart`, `DELETE
  /containers/{name}` (gated by the feature flag — returns `403` even
  for the connection owner when off). Owner-scoped via the parent
  `DockerConnection`.
- New `/api/features` endpoint exposes the small set of feature
  flags the frontend needs to gate UI affordances (Remove button
  visibility, etc.).
- Page-level filters: free-text on name + image, running / stopped /
  all radio, and click-to-filter on a card's compose project badge
  (click again to clear).
- Auto-refresh: the per-connection container list refetches every
  10 s so the page reflects state changes (uptime, new containers,
  manual `docker stop` from a shell) without the user hitting
  Refresh.

**What was cut from the original roadmap (will revisit):**

- **Embedded live sparkline per card.** One stats stream per card
  means N daemon connections per page load — for a host with 50
  containers that's untenable. The V3.4 stats panel inside the watch
  modal stays the way to see live numbers; we'll revisit when V5.3+
  introduces a per-container reference-counted multiplexer.
- **Recreate from the page.** Recreate needs the watch's registry
  credentials / tag tracking, which the instances page doesn't have
  for containers that aren't tracked. The "Open in service" link
  routes the user to the watch modal where the V2.7 button already
  does the right thing.
- **Inline Logs / Inspect drawers.** The same "Open in service" link
  surfaces both via the V3.1 / V3.3 panels; we held off on
  duplicating those endpoints under the connection path to keep the
  scope contained.
- **Read-only fallback for read-only daemon sockets.** Deferred — the
  daemon returns `403`/`500` on write ops against a `:ro` mount and
  the UI surfaces the error inline. We'll add a proactive disable
  + tooltip when we wire the rest of the V3.5 cuts back in.

**Tests:** 9 endpoint tests in `DockerInstancesControllerTests` —
list-with-watch-link, lifecycle ops (success + failure paths) writing
the right `ActionType` audit row, `AllowContainerRemoval` gate
(403 when off / success when on), owner-scoping → 404, container-not-
found → 404. Schema migration tested through the standard
EnsureCreated path; the existing 755-test suite still passes.

---

## 13. V4 — Full migration to SQLite (single-container, self-hosted)

> **Status:** ✅ **Delivered (2026-05-21).** The app runs in a **single
> container** on SQLite, applies migrations on startup, and ships with a
> one-shot PostgreSQL→SQLite copy tool for existing deployments. `docker run -v stashboard-data:/app/Data …`
> and the app is up — no database container, no `POSTGRES_PASSWORD`, no migrator
> sidecar. This was a platform/infrastructure release: it changed *how
> Stashboard is deployed and stores data*, with no new end-user surface.

### 13.1 Why SQLite, why now

Stashboard is positioned as a **self-hosted homelab dashboard**. The audience
runs one instance for themselves / a household / a small team, not a horizontally
scaled multi-tenant deployment. For that profile PostgreSQL is overkill and adds
operational friction (a second container, a secret to manage, a healthcheck
dependency, a migrator job). SQLite is the established choice for this class of
product (Uptime Kuma, Vaultwarden) and it unlocks two self-hosting wins:

- **One container.** The whole product is the app image + a volume for the
  `.db` file.
- **Trivial backups.** A backup is a copy of one file — and `IBackupService`
  already exists to build on.

The schema is genuinely portable: every PK is a client-generated `Guid` (no DB
sequences), there is no `jsonb` / array / full-text usage, and the `Cascade` /
`SetNull` FK behaviours SQLite supports (EF Core sends `PRAGMA foreign_keys=ON`
per connection). The app already defaults to `Database:Provider=Sqlite` and the
`ApplicationDbContext` model is provider-neutral — so most of the work is
*removing* the Postgres half, not adding SQLite.

### 13.2 Current half-state to clean up

- Migrations are **Postgres-typed** (`uuid`, `timestamp with time zone`,
  `character varying`, `boolean`) — they will not run on SQLite.
- `Stashboard.Migrations/Program.cs` and `DesignTimeDbContextFactory` are
  **hardcoded to `UseNpgsql`**.
- The API **does not migrate on startup** — schema is applied only by the
  separate migrator container.
- `docker-compose.yml` is a **3-container** setup (db + migrator + app);
  `docker-compose.dev.yml` spins up a Postgres dev DB; CI runs against Postgres
  on `localhost:5433`.

### 13.3 Phases

#### ✅ V4.1 — Provider consolidation & migration regeneration

- Make SQLite the sole runtime provider. The runtime app (`Stashboard.Api`)
  drops the `Npgsql.EntityFrameworkCore.PostgreSQL` package and the `Postgres`
  branch of the `Database:Provider` switch; `Stashboard.Infrastructure` drops it
  too. **Npgsql survives only inside the one-shot migration utility (V4.2)** so
  the shipped image carries SQLite only.
- Point `Stashboard.Migrations/Program.cs` + `DesignTimeDbContextFactory` at
  `UseSqlite`.
- **Regenerate migrations for SQLite.** The project is pre-1.0 and every
  existing migration dates from this month, so squash the Postgres-typed history
  into a single fresh `InitialSchema` generated against SQLite. This avoids
  dragging the verbose table-rebuild migrations SQLite needs for the historical
  `Split`/`Decouple` column drops.

#### ✅ V4.2 — One-shot full-fidelity PG→SQLite copy tool (prod data migration)

The mechanism for moving the **existing production data** without loss. A
curated JSON backup is deliberately *not* used here — it merges by name,
regenerates GUIDs, and drops users / tokens / audit. Instead, a one-time copy
command preserves everything byte-for-byte:

- A command in `Stashboard.Migrations` (e.g. `migrate-pg-to-sqlite --source "<pg conn>" --target "Data/app.db"`)
  that references **both** providers (it is a dev/ops tool, not shipped on the
  app hot path).
- Opens a **source** `ApplicationDbContext` on Npgsql (prod, read-only) and a
  **target** `ApplicationDbContext` on SQLite (freshly migrated/empty; refuse to
  run against a non-empty target).
- Copies every table in FK-safe order, **preserving primary keys, foreign keys,
  timestamps, users, refresh tokens and audit rows verbatim**. Encrypted columns
  (TLS certs, SSH keys, registry/AWS/GitHub credentials, encrypted credential
  values) are copied **as-is** — no decrypt/re-encrypt — so the **same
  `STASHBOARD_Encryption__Key` must be carried over** to the new deployment.
  This is a hard prerequisite and must be called out in the runbook.
- Wraps the load in a transaction, then **verifies per-table row counts** source
  vs. target before declaring success. Postgres prod stays untouched until the
  operator has verified the SQLite copy.

#### ✅ V4.3 — Backup/restore overhaul + "update backup on every schema change" requirement

`BackupService` was written when only Categories / Tags / WebResources existed
and **was never updated as the Docker features landed**. Today its export/import
silently omits, and would therefore lose on a restore:

- **`DockerConnectionEntity` entirely** — TLS certs, SSH host/port/key/passphrase,
  remote socket path.
- **`DockerWatchEntity` entirely** — tracked containers, schedules, registry /
  GitHub / AWS credentials, tag filters, notification prefs, webhook tokens,
  digests.
- **`DockerUpdateAttemptEntity`** — the audit history.
- **`WebResource` fields** — `MainUrlHealthCheckEnabled`,
  `AdditionalUrlHealthCheckEnabled`, `OfflineNotificationsEnabled`,
  `DockerConnectionId`.
- **`UserEntity` settings** — `Theme`, `DashboardSortMode`,
  `DashboardGroupByCategory`, `TelegramBotToken` / `TelegramChatId` /
  `TelegramNotificationsEnabled`, `DisplayName`.

Work:

- Extend the export/import DTOs to cover the full per-user data set (Docker
  connections + watches + their encrypted secrets, the missing WebResource
  flags, user settings). Decide audit history (`DockerUpdateAttempts`) is
  **out of scope for the user export** — it is operational history, not config.
- Keep the existing **merge-by-name / new-GUID** import semantics for the
  user-facing "import into another instance" use case (the prod migration uses
  the V4.2 copy tool, not this), but document the behaviour explicitly.
- **Definition-of-Done requirement (process):** add to `BUSINESS_REQUIREMENTS.md`
  and the PR checklist that **any change adding / removing / renaming a persisted
  field or entity MUST update `BackupService` (export + import) and its tests in
  the same PR.** This is the root cause of the current drift — the fix is a
  standing requirement, not a one-off catch-up.

#### ✅ V4.4 — Single-container packaging & SQLite operational hardening

- **Collapse `docker-compose.yml`** to a single `app` service + a named volume
  on `/app/Data`. Remove the `db` and `migrator` services and all `POSTGRES_*` /
  Postgres connection-string env. Replace `docker-compose.dev.yml` (a Postgres
  dev DB) — local dev needs no DB container with SQLite.
- **Migrate on startup.** The API calls `db.Database.MigrateAsync()` at boot
  (referencing the migrations assembly) and the migrator container/entrypoint is
  deleted. This is the standard self-hosted pattern.
- **WAL + `busy_timeout`.** Two background services write periodically
  (health-check every 60 s, docker-update scan) alongside user requests; without
  WAL this produces `database is locked` errors. Enable `journal_mode=WAL` (once;
  persisted in the file header) and a per-connection `busy_timeout` via an EF
  `IDbConnectionInterceptor`. Drop `Cache=Shared` from the connection string
  (it does not play well with WAL); connection string becomes
  `Data Source=Data/app.db`.
- **WAL-aware backups.** Any file-level backup must `VACUUM INTO` or checkpoint
  first (or copy `app.db` + `-wal` + `-shm` together) so the copy is consistent.
- **DateTime audit.** SQLite stores dates as ISO-8601 TEXT and round-trips
  `DateTimeKind` as `Unspecified`; audit any code that assumes `Kind == Utc`.

#### ✅ V4.5 — Tests & CI

- Switch the test suite and `.github/workflows/ci.yml` off Postgres-on-5433 to
  SQLite. Tests already build the schema via `EnsureCreatedAsync` (provider-
  agnostic), so they only need their connection pointed at SQLite (file or
  in-memory). Removing the Postgres service simplifies CI.

### 13.4 Deliverables checklist

- [x] Npgsql removed from `Stashboard.Api` + `Stashboard.Infrastructure`; the provider switch is gone (`UseSqlite` only).
- [x] Migrations moved into `Stashboard.Api` and regenerated as a squashed SQLite `InitialSchema`; design-time factory on `UseSqlite`. *(Migrations live in Api rather than `Stashboard.Migrations` because Api cannot reference that project — it would be a circular dependency. This is what lets Api self-migrate and keeps Npgsql out of the runtime.)*
- [x] One-shot `pg-to-sqlite` tool (`Stashboard.Migrations`) backed by a provider-agnostic `DatabaseCopier`; reports per-table row counts and refuses a non-empty target. Covered by SQLite→SQLite tests.
- [x] `BackupService` export/import covers the full schema (Docker connections + watches + secrets, service flags + link, user settings) with a round-trip test; DoD requirement documented in `BUSINESS_REQUIREMENTS.md §10.3`.
- [x] `docker-compose.yml` collapsed to a single container + volume; `db` + `migrator` services removed; migrate-on-startup wired.
- [x] WAL + `busy_timeout` interceptor; `Cache=Shared` removed. *(The app's backup is the JSON export — unaffected by WAL. File-level copy guidance, `app.db` + `-wal` + `-shm`, is in the README.)*
- [x] CI + tests on SQLite; Postgres service removed from `ci.yml`.
- [x] Runbook: prod cutover steps (carry over `STASHBOARD_Encryption__Key`, run copy tool, mount the file) documented in the README.

### 13.5 Risks

| Risk | Likelihood | Impact | Mitigation |
| --- | --- | --- | --- |
| Prod data lost / corrupted during PG→SQLite copy | Low | High | One-shot tool preserves PKs/FKs; per-table row-count verification; Postgres prod left intact until verified; dry-run against a prod dump first. |
| Encryption key not carried over → all encrypted secrets undecryptable | Medium | High | Hard prerequisite in the runbook; tool reads/writes ciphertext verbatim and does not touch the key; fail fast with a clear message if the key env is unset on first decrypt. |
| `database is locked` under concurrent background writes | Medium | Medium | WAL + `busy_timeout`; the two writers are low-frequency. |
| Backup/restore drifts again as schema evolves | Medium | Medium | Standing DoD requirement (V4.3) enforced via PR checklist + tests. |

### 13.6 Effort estimate (rough)

| Phase | Scope | Complexity | Estimate |
| --- | --- | --- | --- |
| V4.1 | Provider consolidation + migration regen | Low–Medium | 1 day |
| V4.2 | PG→SQLite copy tool + verification | Medium | 1.5 days |
| V4.3 | Backup/restore overhaul + DoD requirement | Medium | 1.5 days |
| V4.4 | Single-container packaging + WAL hardening | Medium | 1 day |
| V4.5 | Tests + CI to SQLite | Low | 0.5 day |

---

## 14. Post-V4 backlog (V5+) — deferred Docker features

> The Docker items here were previously catalogued as **V3.6 – V3.11**. They are
> sequenced **after the V4 SQLite migration** and renumbered **V5.2 – V5.8**.
> The interactive-shell phases — **V5.3** host terminal, **V5.7** container-exec
> and **V5.8** Proxmox-LXC SSH — share one xterm.js + WebSocket + ticket
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

**Complexity:** High (pulled ahead of the medium-complexity V5.4–V5.6 phases by
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
  WebSocket bridge later shell phases (V5.7 / V5.8) will reuse. `Program.cs` now
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

### Phase V5.4 — Compose project grouping & bulk update

**Complexity:** Medium
**Value:** Real-world Docker hosts run *stacks*, not isolated containers.
Updating Postgres without also restarting the API that depends on it is the
class of mistake V5.2 is designed to prevent — but even with V5.2, the user
still has to click "Update now" once per service. Grouping makes this one
operation.

**Proposed approach:**

- Group containers on the V3.5 page by the `com.docker.compose.project`
  label. A project header card aggregates a counter such as "3 of 7 services
  have updates available".
- **Update project** button:
  - With V5.2 available + `ComposeProjectPath` set → shells out
    `docker compose pull && docker compose up -d` on the project root.
  - Without V5.2 → falls back to recreating each stale container in
    `depends_on` order, inferred from the labels Compose writes.
- One aggregate `DockerUpdateAttemptEntity` row with child rows per service,
  so the audit log treats the bulk operation as a single auditable unit.

---

### Phase V5.5 — Image cleanup / prune

**Complexity:** Medium
**Value:** Auto-update without cleanup is the fastest way to fill a disk.
The V2.7 recreate leaves the previous image tagged `<none>:<none>` once the
new one is in use; over months these dangling images can grow to many GB.

**Proposed approach:**

- Background task (configurable schedule, defaults to weekly) that calls
  `ImagesPruneAsync(filters: { "dangling": ["true"] })` per host and records
  freed bytes into a `DockerPruneRunEntity`.
- "Storage" widget on the V3.5 page showing total image count, dangling
  count, and a manual **Prune now** button (admin only, dry-run preview
  before commit).
- Opt-in setting: also prune **unused** images (anything not referenced by a
  running or stopped container). Off by default because it is more aggressive
  and can break "rollback to previous tag" workflows.
- Never touches volumes — volume cleanup is too easy to get wrong and is
  explicitly out of scope.

---

### Phase V5.6 — Container exec (browser terminal into a Docker container)

**Complexity:** High
**Value:** The "I just need to run one command in this container" use case
that today forces the user to SSH to the Docker host first. Pairs naturally
with V3.3 (logs) and V3.5 (instances page).

**Proposed approach:**

- Docker API: `POST /containers/{id}/exec` creates an exec instance; `POST
  /exec/{id}/start` upgrades the connection to a hijacked bidirectional
  stream. `Docker.DotNet` exposes this via `ExecCreateContainerAsync` +
  `StartAndAttachContainerExecAsync`.
- Backend: reuse the WebSocket bridge + short-lived-ticket auth introduced by
  V5.3 (host terminal), pumping bytes between the browser and the hijacked
  Docker stream. Per-session params: command (defaults to `/bin/sh`), TTY
  size, env.
- Frontend: `xterm.js` terminal in a full-page tab or side drawer; window
  resize calls `ResizeContainerExecTtyAsync` on the daemon.
- **Security model — the most sensitive feature on the list:**
  - Off by default; opt-in per `DockerConnection` (`AllowExec = false`).
  - Admin role required; UI hidden for non-admins.
  - Every exec session writes a start/stop row to a new
    `DockerExecSessionEntity` (who, when, container, command, duration, byte
    counts). Sessions also stream to the application log.
  - Hard cap on concurrent sessions per user and per host.
  - Server-side inactivity timeout (default 10 min) closes the connection
    regardless of client state.

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
  is V5.8-adjacent and should land separately, after the shell story exists.
- Non-Debian LXC templates (Alpine `apk`, Rocky `dnf`) — add as follow-ups
  once the Debian path is stable.

---

### Phase V6.1 — Browser-based SSH client for Proxmox LXC

**Complexity:** High
**Value:** Closes the loop on V5.6: once the user sees "LXC `pihole` has 7
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

## 15. Implementation checklist (Phase 1 entry point — historical)

> Historical artifact from the very first Docker-update-checker PR (V1 Phase 1).
> Kept for provenance; all items below shipped long ago.

When implementation starts, the first PR should land:
- [ ] `DockerHostType.cs`, `DockerUpdateStatus.cs` in `src/Stashboard.Core/Enums/`
- [ ] `DockerWatchEntity.cs` in `src/Stashboard.Core/Entities/`
- [ ] `IImageReferenceParser.cs` in `src/Stashboard.Core/Abstractions/`
- [ ] `ImageReferenceParser.cs` in `src/Stashboard.Infrastructure/Docker/` (new folder)
- [ ] DbSet + fluent config in `src/Stashboard.Api/Data/ApplicationDbContext.cs`
- [ ] EF migration `AddDockerWatch` in `src/Stashboard.Migrations/Migrations/`
- [ ] DI registration scaffold in `Program.cs` (parser only at this stage)
- [ ] Unit tests for `ImageReferenceParser` covering all the cases in §5.1
