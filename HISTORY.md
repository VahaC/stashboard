# Stashboard — Shipped History

> This document holds the shipped-phase detail extracted from
> [`ROADMAP.md`](./ROADMAP.md) when the roadmap grew past 2 400 lines.
> Nothing here is forward-looking — every section below was shipped and
> the PRs that landed it are linked inline.
>
> - §1–§13 cover the original Docker Update Checker plan (V1 → V3, all
>   delivered) and the V4 migration to SQLite (shipped).
> - §14 holds the shipped V5.x Docker feature phases (V5.0–V5.9), archived
>   from `ROADMAP.md` once they all shipped.
> - §15 is the original V1 Phase 1 implementation checklist, kept for
>   provenance only.
>
> For the active roadmap (V6.0+ and the Proxmox/V7 tracks) see [`ROADMAP.md`](./ROADMAP.md).

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

## 14. Post-V4 backlog (V5+) — shipped Docker feature phases (V5.0–V5.9)

> **Archived from [`ROADMAP.md`](./ROADMAP.md) §14** once every V5.x phase shipped.
> These are the deferred Docker features delivered across the V5 line. The active
> roadmap now keeps only V6.0+ and the Proxmox parity track (§15). The original
> sequencing rationale follows, then each shipped phase in full.

> The Docker items here were previously catalogued as **V3.6 – V3.11**. They are
> sequenced **after the V4 SQLite migration** and renumbered **V5.2 – V5.8**.
> The interactive-shell phases — **V5.3** host terminal, **V5.7** container-exec
> and **V6.6** Proxmox-LXC SSH — share one xterm.js + WebSocket + ticket
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
  WebSocket bridge later shell phases (V5.7 / V6.6) will reuse. `Program.cs` now
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


## 16. Proxmox parity & LXC/VM management — shipped phases (V6.0 – V6.15.1)

> Archived from [`ROADMAP.md`](./ROADMAP.md) once all V6.x phases shipped. Covers
> the first Proxmox phase (V6.0) and the full Proxmox-page Docker-parity track:
> Docker-style LXC cards + detail modal, Config/Stats/Tasks/Logs/Console tabs,
> LXC lifecycle + parameter/network/mount editing, PVE node health + alerting +
> deep telemetry, bulk operations + audit, destroy/create LXC, VM (QEMU) support,
> and Proxmox connections in backup/restore. Forward-looking Proxmox work
> continues in [`ROADMAP.md`](./ROADMAP.md) (V8).

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

### Track — Proxmox page: Docker parity & LXC management (V6.1+)

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

### ✅ Phase V6.15.1 — Idempotent service import + connection-delete diagnostics (image 6.15.1)

**Complexity:** Low
**Value:** Fixes the V6.15 backup flow's sharpest edge in practice: restoring a
backup onto an instance that already held the same services (staging → prod)
**duplicated every service**, because services — unlike categories, tags, Docker
and Proxmox connections — were created fresh on every import. And the cleanup
afterwards was needlessly painful: deleting a connection still referenced by a
service refused with a **count-only** 409 ("1 service(s) use this connection"),
leaving the user guessing which service held it — the assignment lives on the
service (modal → Docker tab), not on the container links the Docker page shows.

**Shipped (6.15.1):**

- `BackupService.ImportAsync` now **merges services by name + main URL** — an
  existing match is left untouched and only mapped so imported Docker watches
  re-attach to it; re-importing the same backup is idempotent. A service with
  the same name but a different URL still imports as new. The returned
  `imported` count covers only newly created services.
- `DELETE /api/docker/connections/{id}` refusal now **names the blocking
  services** in the 409 error (plus a `services` array), and the connection
  form surfaces the server message instead of composing a count-only one
  client-side.
- Tests: backup re-import does not duplicate services (and same-name /
  different-URL still imports); connection delete 409 names the services;
  unused connection deletes cleanly.

---

## 17. Visual Compose editor (Docker) — shipped phases (V7.0 – V7.9)

> Archived from [`ROADMAP.md`](./ROADMAP.md) once all V7.x phases shipped. The
> visual Compose editor track for the Docker page: a read-only Compose viewer that
> grew into a comment-preserving YAML editor (basic fields, resources, top-level
> networks/volumes/secrets/configs), a from-scratch service/project bootstrapper
> with a ~126-recipe template catalogue, diff/dry-run/apply with history + audit,
> a dependency graph + linter, container/guest card icons, and — past the editor
> itself — linking Proxmox guests to services and cross-linking Docker containers
> to their Proxmox host (V7.9). Two PBS bug-fix point releases (V7.2.1) also rode
> this line. Forward-looking work continues in [`ROADMAP.md`](./ROADMAP.md) (V8+).

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

### ✅ Phase V7.7 — Dependency graph + linter (image 7.7.0)

**Complexity:** Medium–High
**Value:** Once a project has a dozen services, the
`depends_on` / network / volume relationships become hard to reason
about. A small DAG view plus an inline linter catches the issues that
"works on my machine" hides — and is the kind of thing Stashboard can
do that a raw text editor structurally cannot.

**What shipped:**

- A **Graph** tab with a **hand-rolled SVG** DAG (no `react-flow` /
  graph-library dependency — it keeps the bundle lean and matches the
  rest of the hand-built UI). Nodes = services (with their live state
  pill), edges = `depends_on` (arrow points to the dependency,
  dependencies layered below their dependents), shared networks drawn as
  translucent **group boxes** behind their members, named volumes in a
  side legend. A simple layered layout, deterministic and cycle-safe;
  clicking a node opens that service's tab.
- A pure backend **linter** whose findings ride on every project
  response (run on every load + every save). Rules:
  - Port collisions across services on the same host (error).
  - `depends_on` cycles (error).
  - Missing healthcheck on a service other services depend on with
    `condition: service_healthy` (error).
  - Bind mounts pointing outside the project root (warning).
  - Deprecated Compose keys (`links`, `volumes_from`, top-level
    `version:`) (warning).
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

### ✅ Phase V7.8 — Container card icons (image 7.8.0)

**Complexity:** Low–Medium
**Value:** Most homelab images map cleanly to a well-known service logo; surfacing
it makes the Docker page scannable at a glance instead of a wall of identical cards.

> Container cards on `/docker` (and inside `ServiceModal → Docker`) get a leading
> avatar resolved per card, highest priority first: a **custom image** the user
> uploaded → the service's **official icon** (auto, from the
> `homarr-labs/dashboard-icons` set) → a **placeholder** (a `Box` glyph + name
> initials). Reuses the existing `WebResource` logo machinery and
> `IImageReferenceParser` for slug derivation, so it was mostly wiring.

**What shipped:**

- An `IContainerIconResolver` / `ContainerIconResolver` (in
  `Infrastructure/Services`, built on the exact `FaviconService` pattern —
  `IHttpClientFactory` + `IMemoryCache`, 24h cache, best-effort, `null` on
  failure). `SlugFor` parses the image via `IImageReferenceParser` and takes the
  repository's last segment (host / namespace / arch prefix / tag all drop away),
  plus a small alias table (`postgres → postgresql`, `pihole → pi-hole`,
  `homeassistant → home-assistant`). `ResolveIconDataUriAsync` fetches
  `{base}/webp/{slug}.webp` as a `data:image/webp;base64,…` URI and caches the
  result — **misses too**, so a refresh doesn't re-hit the CDN. Base URL is the
  single-line constant `https://cdn.jsdelivr.net/gh/homarr-labs/dashboard-icons`.
- A `ContainerIconEntity : AuditableEntity` mirroring the `WebResource` logo
  fields (`UserId`, `DockerConnectionId`, `ContainerName`, `IconSource`,
  `LogoBase64`, `CustomLogoPath`), unique on `(UserId, DockerConnectionId,
  ContainerName)` — the `(connection, name)` key is stable even though containers
  are ephemeral. `POST …/containers/{name}/icon` (a copy of
  `WebResourcesController.UploadLogo`) stores the upload as base64 + a file under
  `/uploads/container-icons/` and upserts the row to `Custom`; `DELETE …/icon`
  reverts to `Auto`. `GET containers` builds an `iconByContainer` dictionary and
  sets a final `IconDataUri` per card (custom → official → `null`); the resolver
  is only consulted when there's no custom image.
- Frontend: `EntityCard` gained an optional `icon?: ReactNode` slot (a leading
  avatar column under the header), a `ContainerIcon` atom (rounded 32px avatar,
  `object-contain`, `Box` + initials fallback), the `cc-card-icon` styling, and
  custom-icon management in the `ContainerModal` overview tab (preview + upload +
  **Reset to auto**, react-query mutations that invalidate the card list).
- **Proxmox parity.** The same icon slot is wired onto **Proxmox LXC / VM** cards:
  a new `ProxmoxGuestIconEntity` (custom uploads, keyed by `(user, connection,
  vmid)`) + a `GET …/guest-icons` map endpoint (custom → official OS icon → none),
  the official icon derived from the guest's `ostype` (lazily captured by the scan
  onto `ProxmoxGuestEntity.OsType` and mapped to a dashboard-icons OS slug). Also
  made same-row cards share one height.

**Deferred (not in 7.8.0):** a configurable / self-hosted icon base URL; per-arch
icon variants; reusing the resolver to give a **service** its container's icon on
the dashboard; a "refresh icons" action that drops the negative cache; a
type-aware placeholder (db / proxy).

---

### ✅ Phase V7.9 — Link Proxmox guests to services + cross-link Docker containers to their Proxmox host (image 7.9.0)

**Complexity:** Medium
**Value:** Today a service on the **Services page** can link to **Docker
containers** (a `DockerConnectionId` + one `DockerWatch` per container), and the
dashboard aggregates those watches into the card's **"Update available"** badge
(`WebResourceMapper.AggregateDockerStatus`). Proxmox LXCs / VMs already carry
everything the badge needs — `PendingUpdates`, `MonitoringEnabled`, `IsRunning`
on `ProxmoxGuestEntity` — but there is **no way to attach a guest to a service**,
so a service that actually *is* an LXC (or a VM) shows no update state, and a
service that is half-Docker-half-Proxmox can only express the Docker half. This
phase closes both gaps: a service can link **Docker and Proxmox simultaneously**,
and a Docker container can record the **Proxmox guest it physically runs inside**
(the common homelab case — Docker living in an LXC or VM).

**Scope:**

- **Service → Proxmox guest links.** A new join entity
  `WebResourceProxmoxGuestLinkEntity` keyed on
  `(WebResourceId, ProxmoxConnectionId, VmId)` — a **many-to-many** link, *not*
  ownership: guests stay auto-discovered and owned by their connection, and
  deleting a service drops its links while the guest lives on (mirrors the
  `DockerWatch.WebResourceId` nullable-FK rule). A service can link any number of
  guests across any of the user's Proxmox connections, independently of whether it
  also links Docker.
- **Proxmox update badge on the service card.** `WebResourceMapper` gains a
  sibling to `AggregateDockerStatus` that reduces the linked guests' state to one
  service-level `ProxmoxUpdateStatus` (per guest: **updates available** when
  `PendingUpdates > 0`, else up-to-date / disabled / unknown for
  monitoring-off / stopped / never-checked), with the same actionable-first
  precedence (`UpdateAvailable > Error/Unknown > UpToDate > Disabled`). The DTO
  exposes it alongside the existing `DockerUpdateStatus` so the card can show
  **both badges at once** — reuse the existing badge component family
  (`ContainerStateBadge` / the update-status pill), no new visual system.
- **Service modal — Proxmox section.** The `ServiceModal` Docker section gets a
  parallel **Proxmox** section: pick a connection, then add one or more guests from
  a picker fed by the already-scanned `ProxmoxGuestEntity` rows (LXCs **and** VMs),
  each shown with its live state pill and pending-update count. Linked guests list
  with an unlink action. No new guest discovery — it only links what a scan already
  found.
- **Docker container → Proxmox guest cross-link.** A new
  `ContainerProxmoxLinkEntity` keyed on `(UserId, DockerConnectionId,
  ContainerName)` — the exact shape `ContainerIconEntity` already uses, so it works
  for **any** container (watched or not) and survives container churn — storing the
  target `(ProxmoxConnectionId, VmId)`. On the Docker page / `ContainerModal`, a
  **"Runs on"** picker sets it; the container card shows a small **"on `<guest>`"**
  chip that deep-links to that Proxmox guest's modal. `GET containers` builds a
  `linkByContainer` map the same way the icon endpoint does.
- **Backup/restore:** both new link tables (service↔guest and container↔guest) are
  added to `BackupService` export/import and its round-trip test in the same change
  (Definition-of-Done §10.3). Links reference guests by their stable
  `(connection, vmid)` natural key, so they survive a restore even though guest
  rows are re-discovered rather than exported.

**Out of scope:** triggering a Proxmox **update run** from the service card (the
badge is read-only here; one-click "Update now" already lives on the Proxmox page
from V6.7.1 and stays there); auto-suggesting a guest↔container link by matching IPs
or hostnames (manual link only in V7.9); linking a service to a **Proxmox node**
(the `VmId == 0` node row) — service links are guests only.

**Tests:** a service link is created/removed and is owner-scoped (a foreign
connection/guest is rejected); deleting a service drops its guest links but leaves
the guest rows; `AggregateProxmoxStatus` returns `UpdateAvailable` when any linked
guest has `PendingUpdates > 0` and follows the documented precedence, and the DTO
carries Docker and Proxmox status independently so both badges can render together;
the container↔guest link upserts on `(user, connection, container)`, surfaces in the
`containers` payload, and a missing target guest yields no chip rather than an error;
the backup round-trip preserves both link sets keyed by `(connection, vmid)`.

**Acceptance bar:** a user can open a service, link it to one or more Proxmox
LXCs/VMs (alongside any Docker containers it already tracks), and the service card
shows a Proxmox update badge — together with the Docker badge when both are linked —
driven by the guests' existing scan data; and on the Docker page a container can be
marked as running on a specific Proxmox guest, shown as a chip that jumps to that
guest, with every link surviving a backup/restore.

**Shipped (7.9.0):** implemented exactly as scoped. `WebResourceProxmoxGuestLinkEntity`
(many-to-many, owner-scoped, node-row + foreign-connection/guest refused, idempotent
link/unlink, dropped on service delete with the guest left intact) and
`ContainerProxmoxLinkEntity` (upsert on `(user, connection, container)`, surfaced as a
`linkByContainer` map in `GET containers`, missing target → no chip) both land, with
`WebResourceMapper.AggregateProxmoxStatus` driving a service-level
`ProxmoxUpdateStatus` exposed independently of `DockerUpdateStatus` so both badges
render together. Frontend: a `ProxmoxLinkSection` in `ServiceModal` (connection + guest
picker, unlink) and a "Runs on" picker + deep-linking chip on the Docker container
card/modal. Both link tables round-trip through `BackupService` keyed by
`(connection, vmid)`. Covered by `ProxmoxLinkTests`, `WebResourceMapperTests`, the
`DockerInstancesControllerTests` cross-link cases, and the `BackupServiceTests`
round-trip.

---
