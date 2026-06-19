export type ServiceStatus = 'Unknown' | 'Up' | 'Down' | 'NeedsAttention' | 0 | 1 | 2 | 3
export type HealthCheckMethod = 'Get' | 'Head' | 0 | 1
export type LogoSource = 'AutoFavicon' | 'Custom' | 0 | 1

export interface Credential {
  id: string
  key: string
  value: string
  isSecret: boolean
}

export interface Service {
  id: string
  name: string
  mainUrl: string
  mainUrlHealthCheckEnabled: boolean
  additionalUrl: string | null
  additionalUrlHealthCheckEnabled: boolean
  offlineNotificationsEnabled: boolean
  healthCheckUrl: string | null
  healthCheckMethod: HealthCheckMethod
  expectedStatusRange: string | null
  notes: string | null
  categoryId: string | null
  categoryName: string | null
  categoryColor: string | null
  logoSource: LogoSource
  customLogoPath: string | null
  faviconUrl: string | null
  currentStatus: ServiceStatus
  lastCheckedUtc: string | null
  lastResponseTimeMs: number | null
  lastError: string | null
  additionalUrlStatus: ServiceStatus
  additionalUrlLastResponseTimeMs: number | null
  additionalUrlLastError: string | null
  tags: string[]
  credentials: Credential[]
  createdUtc: string
  updatedUtc: string
  /** Docker update tracking status if this service has a watch configured. */
  dockerUpdateStatus: DockerUpdateStatus | null
  /** Id of the user-level Docker connection this service is assigned to, or null. */
  dockerConnectionId: string | null
  /** V3.6 — read-only summary of the containers linked to this service. The
   *  service modal renders these as deep links into the Docker page where the
   *  tracking is managed. */
  linkedDockerWatches: LinkedDockerWatchSummary[] | null
}

/** V3.6 — compact, read-only projection of a watch linked to a service. */
export interface LinkedDockerWatchSummary {
  id: string
  dockerConnectionId: string
  label: string
  containerName: string
  imageReference: string
  enabled: boolean
  updateStatus: DockerUpdateStatus
  lastCheckedUtc: string | null
}

export interface ServiceUpsert {
  name: string
  mainUrl: string
  mainUrlHealthCheckEnabled: boolean
  additionalUrl: string | null
  additionalUrlHealthCheckEnabled: boolean
  offlineNotificationsEnabled: boolean
  healthCheckUrl: string | null
  healthCheckMethod: HealthCheckMethod
  expectedStatusRange: string | null
  notes: string | null
  categoryId: string | null
  logoSource: LogoSource
  customLogoPath: string | null
  tags: string[]
  credentials: Array<{ key: string; value: string; isSecret: boolean }>
  /** Id of an existing Docker connection to assign to the service, or null
   *  to unassign. */
  dockerConnectionId: string | null
}

export interface Category {
  id: string
  name: string
  color: string
  serviceCount: number
}

export interface Tag {
  id: string
  name: string
  serviceCount: number
}

export interface User {
  id: string
  email: string
}

export interface AuthResponse {
  accessToken: string
  accessTokenExpiresUtc: string
  refreshToken: string
  refreshTokenExpiresUtc: string
  sessionExpiresUtc: string
  user: User
}

export interface Profile {
  id: string
  email: string
  displayName: string | null
  emailConfirmed: boolean
  pendingEmail: string | null
  /** "system" | "light" | "dark" — kept loose-typed here to avoid a circular import with theme-store. */
  theme: string
  createdUtc: string
  lastLoginUtc: string | null
}

export interface DashboardPreferences {
  sortMode: 'name' | 'category'
  groupByCategory: boolean
}

export interface TelegramSettings {
  botToken: string | null
  chatId: string | null
  notificationsEnabled: boolean
}

/** App-wide SMTP / email-sender settings. The password is never returned —
 *  `hasPassword` indicates whether one is stored. */
export interface EmailSettings {
  provider: string
  host: string
  port: number
  useStartTls: boolean
  username: string
  hasPassword: boolean
  fromAddress: string
  fromName: string
  appBaseUrl: string
}

export interface EmailSettingsUpdate {
  provider: string
  host: string
  port: number
  useStartTls: boolean
  username: string
  /** Tri-state secret — `null` keeps the stored password. */
  password: SecretValueUpsert | null
  fromAddress: string
  fromName: string
  appBaseUrl: string
}

// ── Docker update checker ──────────────────────────────────────────────────

/**
 * V2.5 adds `'Ssh'` (enum value `2`) so a connection can target a remote
 * Docker daemon over an SSH tunnel without exposing `2376/tcp`.
 */
export type DockerHostType = 'LocalSocket' | 'TcpTls' | 'Ssh' | 0 | 1 | 2

export type DockerUpdateStatus =
  | 'Unknown' | 'UpToDate' | 'UpdateAvailable' | 'Error' | 'Disabled'
  | 0 | 1 | 2 | 3 | 4

/**
 * Tri-state action for sensitive fields. `Keep` lets the form preserve an
 * already-encrypted value on the server without sending the secret round-trip;
 * `Set` replaces it; `Clear` drops it.
 */
export type SecretValueAction = 'Keep' | 'Set' | 'Clear'

export interface SecretValueUpsert {
  action: SecretValueAction
  value: string | null
}

/** V2.2 schedule mode. */
export type CheckScheduleType = 'Hourly' | 'Daily' | 'Weekly' | 0 | 1 | 2

/** UTC day-of-week, matching .NET `DayOfWeek`. */
export type DayOfWeek = 0 | 1 | 2 | 3 | 4 | 5 | 6

/** V2.4 — registry authentication strategy. */
export type RegistryAuthType = 'Auto' | 'Basic' | 'AwsEcr' | 0 | 1 | 2

export const resolveRegistryAuthType = (value: RegistryAuthType): 'Auto' | 'Basic' | 'AwsEcr' =>
  typeof value === 'number'
    ? ((['Auto', 'Basic', 'AwsEcr'][value] as 'Auto') ?? 'Auto')
    : value

export interface DockerWatch {
  id: string
  /** V3.6 — the Docker host connection this container runs on. Always set. */
  dockerConnectionId: string
  /** V3.6 — optional link to a service. `null` for a standalone tracked
   *  container. */
  webResourceId: string | null
  /** Human-readable label for the tracked container (e.g. "app" vs. "db").
   *  Defaults to the container name. */
  label: string
  enabled: boolean
  imageReference: string
  registryHost: string
  repository: string
  tag: string
  containerName: string
  /** Whether both username AND password are configured server-side. */
  hasRegistryCredentials: boolean
  /** V2.3 — whether a GitHub PAT is stored for release-notes enrichment of
   *  private GHCR images. The PAT itself is never returned by the API. */
  hasGitHubPat: boolean
  /** V2.4 — chosen registry authentication strategy. */
  registryAuthType: RegistryAuthType
  /** V2.4 — true when both AWS access key id + secret are configured
   *  server-side. The values themselves are never returned. */
  hasAwsCredentials: boolean
  /** V2.4 — AWS region for ECR (plaintext, e.g. "eu-central-1"). */
  awsRegion: string | null
  updateNotificationsEnabled: boolean
  telegramNotificationsEnabled: boolean
  /** V2.2 — schedule mode. */
  scheduleType: CheckScheduleType
  /** V2.2 — only meaningful when `scheduleType === 'Hourly'`. Allowed:
   *  1, 2, 4, 6, 12, 24. */
  checkEveryHours: number
  /** V2.2 — UTC time-of-day for Daily / Weekly schedules. Wire format is
   *  ISO-8601 `HH:mm[:ss]`. */
  checkAtTime: string | null
  /** V2.2 — UTC day-of-week for Weekly schedules. */
  checkOnDayOfWeek: DayOfWeek | null
  /** Optional .NET regex applied to the registry's tag list when resolving
   *  "latest". `null` means "compare the pinned tag's digest directly"
   *  (V1 behaviour). When set, the backend lists matching tags and picks
   *  the highest semver / lexicographic candidate. (V2.1) */
  tagPatternFilter: string | null
  updateStatus: DockerUpdateStatus
  currentDigest: string | null
  latestDigest: string | null
  currentVersionTag: string | null
  latestVersionTag: string | null
  /** V2.3 — GitHub release web URL for `latestVersionTag` when the registry
   *  is `ghcr.io` and a matching release exists. */
  latestReleaseUrl: string | null
  /** V2.3 — release notes markdown body, truncated to 2 000 chars server-side. */
  latestReleaseBody: string | null
  lastCheckedUtc: string | null
  lastUpdateDetectedUtc: string | null
  /** V2.2 — projected next-check timestamp, computed server-side from the
   *  schedule + `lastCheckedUtc`. `null` when the schedule is incomplete. */
  nextCheckUtc: string | null
  lastError: string | null
  /** V2.6 — per-watch webhook URL token (64-char hex). `null` means the
   *  user hasn't opted in to webhook delivery; the public POST endpoint
   *  is disabled for this watch. */
  webhookToken: string | null
  /** V2.6 — UTC timestamp of the most recent accepted webhook delivery. */
  lastWebhookReceivedUtc: string | null
  createdUtc: string
  updatedUtc: string
}

export interface DockerWatchUpsert {
  label: string
  enabled: boolean
  imageReference: string
  containerName: string
  /** V3.6 — optional service to link this tracked container to. `null` leaves
   *  the container standalone. Ignored by the legacy service-scoped endpoint
   *  (the route already pins the service). */
  webResourceId?: string | null
  registryUsername: SecretValueUpsert | null
  registryPassword: SecretValueUpsert | null
  /** V2.3 — optional GitHub PAT for release-notes enrichment on private GHCR
   *  images. Same tri-state convention as the registry secrets. */
  gitHubPat: SecretValueUpsert | null
  /** V2.4 — registry authentication strategy. */
  registryAuthType: RegistryAuthType
  /** V2.4 — AWS access key id (tri-state secret) for ECR. */
  awsAccessKeyId: SecretValueUpsert | null
  /** V2.4 — AWS secret access key (tri-state secret) for ECR. */
  awsSecretAccessKey: SecretValueUpsert | null
  /** V2.4 — AWS region for ECR (plaintext). Empty / null when not ECR. */
  awsRegion: string | null
  updateNotificationsEnabled: boolean
  telegramNotificationsEnabled: boolean
  /** V2.2 — schedule mode. Default `'Hourly'`. */
  scheduleType: CheckScheduleType
  /** V2.2 — only honoured when `scheduleType === 'Hourly'`. Allowed:
   *  1, 2, 4, 6, 12, 24. Default 24. */
  checkEveryHours: number
  /** V2.2 — UTC `HH:mm[:ss]`; required for Daily / Weekly. */
  checkAtTime: string | null
  /** V2.2 — required for Weekly. */
  checkOnDayOfWeek: DayOfWeek | null
  /** Optional .NET regex; `null` preserves V1 behaviour. (V2.1) */
  tagPatternFilter: string | null
}

/** Per-watch reachability probe — verifies the container exists on the
 *  service's existing Docker connection and the registry is reachable for
 *  the image. */
export interface DockerWatchTestRequest {
  imageReference: string
  containerName: string
  registryUsername: SecretValueUpsert | null
  registryPassword: SecretValueUpsert | null
  /** Optional .NET regex; sent through so the registry leg of the test
   *  exercises the same tag-resolution path the background loop uses. */
  tagPatternFilter: string | null
  /** V2.3 — optional GitHub PAT mirroring the upsert payload. */
  gitHubPat: SecretValueUpsert | null
  /** V2.4 — registry authentication strategy mirroring the upsert payload. */
  registryAuthType: RegistryAuthType
  /** V2.4 — AWS access key id (tri-state secret) for ECR. */
  awsAccessKeyId: SecretValueUpsert | null
  /** V2.4 — AWS secret access key (tri-state secret) for ECR. */
  awsSecretAccessKey: SecretValueUpsert | null
  /** V2.4 — AWS region for ECR. */
  awsRegion: string | null
}

export interface DockerWatchTestResponse {
  dockerHostReachable: boolean
  containerFound: boolean
  registryReachable: boolean
  error: string | null
}

// ── V2.7 — Auto-update ("Update now") ──────────────────────────────────────

/** V2.7 — outcome of a one-click "Update now" attempt. Mirrors the
 *  backend `DockerUpdateAttemptStatus` enum. */
export type DockerUpdateAttemptStatus =
  | 'Success'
  | 'PullFailed'
  | 'RecreateFailed'
  | 'HostUnreachable'
  | 'ContainerNotFound'
  | 0 | 1 | 2 | 3 | 4

export interface DockerUpdateAttempt {
  id: string
  /** V2.7: always populated. V3.5: nullable — instance-page actions
   *  aren't tied to a watch. */
  dockerWatchId: string | null
  /** V2.7: always populated. V3.5: nullable — instance-page actions
   *  don't have a parent service. */
  webResourceId: string | null
  /** V3.5 — Docker connection the action ran against. Nullable for
   *  pre-V3.5 rows. */
  dockerConnectionId: string | null
  /** V3.5 — Docker container id at action time. */
  containerId: string | null
  /** V3.5 — discriminator. V2.7 rows default to `Update`. */
  actionType: DockerContainerActionType
  status: DockerUpdateAttemptStatus
  imageReference: string
  containerName: string
  previousDigest: string | null
  newDigest: string | null
  error: string | null
  completedUtc: string
  createdUtc: string
  /** V3.2 — true when the recreated container reported a `healthy`
   * Docker healthcheck state (or had no healthcheck and stayed running)
   * within the post-start verification window. Always false on
   * non-success statuses. */
  healthVerified: boolean
  /** V3.2 — UTC timestamp the recreated container was observed healthy.
   * `null` on every non-verified attempt. */
  healthVerifiedUtc: string | null
}

export interface DockerWatchUpdateResponse {
  attempt: DockerUpdateAttempt
  watch: DockerWatch
}

export const resolveAttemptStatus = (s: DockerUpdateAttemptStatus): string =>
  typeof s === 'number'
    ? (['Success', 'PullFailed', 'RecreateFailed', 'HostUnreachable', 'ContainerNotFound'][s] ?? 'Unknown')
    : s

// ── Docker connection (per-service daemon transport) ───────────────────────

export interface DockerConnection {
  id: string
  name: string
  hostType: DockerHostType
  hostUrl: string | null
  hasTlsConfigured: boolean
  /** V2.5 — SSH host (DNS or IP). Non-null only for `hostType === 'Ssh'`. */
  sshHost: string | null
  /** V2.5 — SSH port (default 22). */
  sshPort: number | null
  /** V2.5 — SSH login username. */
  sshUsername: string | null
  /** V2.5 — whether an SSH private key is configured server-side. PEM is never returned. */
  hasSshPrivateKey: boolean
  /** V2.5 — whether the private key has a passphrase configured server-side. */
  hasSshPrivateKeyPassphrase: boolean
  /** V2.5 — remote socket path (default `/var/run/docker.sock`). */
  sshRemoteSocketPath: string | null
  /** V7.1 — host-side prefix of the optional Compose path mapping
   *  (LocalSocket only). Project directories are discovered per project from
   *  the containers' working_dir labels. */
  composePathHostPrefix: string | null
  /** V7.1 — container-side prefix of the optional Compose path mapping. */
  composePathContainerPrefix: string | null
  /** V5.3 — whether this connection has opted in to the browser host terminal.
   *  Only meaningful for SSH hosts; the server also requires the global
   *  AllowHostShell flag before honouring it. */
  allowHostShell: boolean
  /** V5.7 — whether this connection has opted in to the browser container-exec
   *  terminal. Works for every host type; the server also requires the global
   *  AllowContainerExec switch before honouring it. */
  allowExec: boolean
  /** V5.5 — whether this connection participates in the background
   *  image-prune sweep. Default `true` on new + upgraded connections. */
  allowImagePrune: boolean
  /** V5.5 — opt this connection in to pruning non-dangling "unused"
   *  images on top of dangling ones. Default `false`. */
  pruneUnusedImages: boolean
  /** Number of services currently assigned to this connection — drives
   *  the delete-blocked warning. */
  usageCount: number
  createdUtc: string
  updatedUtc: string
}

export interface DockerConnectionUpsert {
  name: string
  hostType: DockerHostType
  hostUrl: string | null
  tlsCaCert: SecretValueUpsert | null
  tlsClientCert: SecretValueUpsert | null
  tlsClientKey: SecretValueUpsert | null
  /** V2.5 — SSH host (required for SSH hosts). */
  sshHost: string | null
  /** V2.5 — SSH port (default 22). */
  sshPort: number | null
  /** V2.5 — SSH login username (required for SSH hosts). */
  sshUsername: string | null
  /** V2.5 — PEM private key (tri-state secret). */
  sshPrivateKey: SecretValueUpsert | null
  /** V2.5 — optional passphrase for the private key (tri-state secret). */
  sshPrivateKeyPassphrase: SecretValueUpsert | null
  /** V2.5 — remote socket path override (default `/var/run/docker.sock`). */
  sshRemoteSocketPath: string | null
  /** V7.1 — host-side prefix of the optional Compose path mapping
   *  (LocalSocket only; both prefixes must be set together to take effect). */
  composePathHostPrefix: string | null
  /** V7.1 — container-side prefix of the optional Compose path mapping. */
  composePathContainerPrefix: string | null
  /** V5.3 — opt this connection in to the browser host terminal (SSH only). */
  allowHostShell: boolean
  /** V5.7 — opt this connection in to the browser container-exec terminal (any host type). */
  allowExec: boolean
  /** V5.5 — whether this connection participates in the background image-prune sweep. */
  allowImagePrune: boolean
  /** V5.5 — opt this connection in to pruning non-dangling unused images. */
  pruneUnusedImages: boolean
}

export interface DockerConnectionPingRequest {
  hostType: DockerHostType
  hostUrl: string | null
  tlsCaCert: SecretValueUpsert | null
  tlsClientCert: SecretValueUpsert | null
  tlsClientKey: SecretValueUpsert | null
  /** V2.5 — SSH host (required for SSH hosts). */
  sshHost: string | null
  /** V2.5 — SSH port (default 22). */
  sshPort: number | null
  /** V2.5 — SSH login username. */
  sshUsername: string | null
  /** V2.5 — PEM private key (tri-state secret). */
  sshPrivateKey: SecretValueUpsert | null
  /** V2.5 — optional passphrase for the private key (tri-state secret). */
  sshPrivateKeyPassphrase: SecretValueUpsert | null
  /** V2.5 — remote socket path override. */
  sshRemoteSocketPath: string | null
}

export interface DockerConnectionPingResponse {
  dockerHostReachable: boolean
  error: string | null
}

export interface DockerContainerInfo {
  name: string
  image: string
  imageId: string
  state: string
  status: string
  composeProject: string | null
  composeService: string | null
  composeConfigFiles: string | null
}

export interface DockerUpdateCommandResponse {
  command: string
  /** "compose" | "fallback" — tells the UI whether the command is safe to run as-is. */
  source: string
  warning: string | null
}

export const resolveDockerUpdateStatus = (status: DockerUpdateStatus): string =>
  typeof status === 'number'
    ? (['Unknown', 'UpToDate', 'UpdateAvailable', 'Error', 'Disabled'][status] ?? 'Unknown')
    : status

export const resolveDockerHostType = (hostType: DockerHostType): 'LocalSocket' | 'TcpTls' | 'Ssh' =>
  typeof hostType === 'number'
    ? (hostType === 2 ? 'Ssh' : hostType === 1 ? 'TcpTls' : 'LocalSocket')
    : hostType

// ── V3.1 — Container Inspect viewer ────────────────────────────────────────

/**
 * V3.1 — slimmed snapshot of a Docker container's `inspect` payload, surfaced
 * by the "Inspect" tab on a Docker watch. Mirrors the shape returned by
 * `GET /api/services/{id}/docker/watches/{id}/inspect`. Env values for keys
 * matching the secret heuristic arrive with `value: null, masked: true`.
 */
export interface DockerContainerInspect {
  id: string
  name: string
  /** Image reference the container was launched with (e.g. `nginx:1.27`). */
  image: string
  /** Resolved image id — `sha256:…`. */
  imageId: string
  /** RepoDigests for the resolved image. Empty for locally-built images. */
  imageRepoDigests: string[]
  createdUtc: string | null
  restartCount: number
  platform: string | null
  driver: string | null
  state: DockerInspectState
  config: DockerInspectConfig
  hostConfig: DockerInspectHostConfig
  networkSettings: DockerInspectNetworkSettings
  mounts: DockerInspectMount[]
}

export interface DockerInspectState {
  status: string
  running: boolean
  restarting: boolean
  paused: boolean
  oomKilled: boolean
  dead: boolean
  exitCode: number
  error: string | null
  startedUtc: string | null
  finishedUtc: string | null
  health: DockerInspectHealth | null
}

export interface DockerInspectHealth {
  /** `none` | `starting` | `healthy` | `unhealthy`. */
  status: string
  failingStreak: number
  log: DockerInspectHealthLog[]
}

export interface DockerInspectHealthLog {
  startUtc: string | null
  endUtc: string | null
  exitCode: number
  output: string | null
}

export interface DockerInspectConfig {
  hostname: string | null
  user: string | null
  workingDir: string | null
  image: string | null
  entrypoint: string[]
  cmd: string[]
  /** Env vars. Values for secret-looking keys arrive with `masked: true` and
   *  `value: null`. */
  env: DockerInspectEnvVar[]
  labels: Record<string, string>
  exposedPorts: string[]
}

export interface DockerInspectEnvVar {
  key: string
  value: string | null
  masked: boolean
}

export interface DockerInspectHostConfig {
  networkMode: string | null
  restartPolicy: DockerInspectRestartPolicy | null
  memoryBytes: number | null
  cpuShares: number | null
  privileged: boolean
  readonlyRootfs: boolean
  autoRemove: boolean
  portBindings: DockerInspectPortBinding[]
}

export interface DockerInspectRestartPolicy {
  name: string
  maximumRetryCount: number
}

export interface DockerInspectPortBinding {
  /** e.g. `80/tcp`. */
  containerPort: string
  hostIp: string | null
  hostPort: string | null
}

export interface DockerInspectNetworkSettings {
  networks: Record<string, DockerInspectNetwork>
}

export interface DockerInspectNetwork {
  networkID: string | null
  endpointID: string | null
  ipAddress: string | null
  gateway: string | null
  ipPrefixLen: number | null
  macAddress: string | null
  aliases: string[]
}

export interface DockerInspectMount {
  type: string
  name: string | null
  source: string | null
  destination: string
  driver: string | null
  mode: string
  readWrite: boolean
  propagation: string
}

// ── V3.3 — Real-time container logs ──────────────────────────────────────

/** Which Docker channel a log line came from. `error` is a synthetic
 *  channel the API uses to surface a terminal stream failure to the UI
 *  (host unreachable, container vanished mid-stream, etc). */
export type DockerLogStreamChannel = 'stdout' | 'stderr' | 'error'

export interface DockerLogLine {
  stream: DockerLogStreamChannel
  /** Per-line UTC timestamp the daemon prepends when `timestamps=true`.
   *  Null when timestamps were disabled or the prefix failed to parse. */
  timestamp: string | null
  message: string
}

export interface DockerLogStreamOptions {
  follow?: boolean
  tail?: number
  /** Unix seconds. Logs emitted strictly before this are skipped. */
  since?: number
  timestamps?: boolean
  stdout?: boolean
  stderr?: boolean
}

// ── V3.5 — Docker instances page ─────────────────────────────────────────

/** Discriminator for a row in the Docker activity log. V2.7 rows arrive
 *  as `Update`; V3.5 instance-page actions tag themselves with the matching
 *  value. */
export type DockerContainerActionType =
  | 'Update'
  | 'Start'
  | 'Stop'
  | 'Restart'
  | 'Remove'
  /** V5.4 — aggregate parent row for a bulk "Update project" attempt. */
  | 'UpdateProject'

export interface DockerContainerPortMapping {
  privatePort: number
  publicPort: number | null
  type: string
  ip: string | null
}

export interface DockerContainerCard {
  id: string
  name: string
  image: string
  imageId: string
  state: string
  status: string
  createdUtc: string | null
  ports: DockerContainerPortMapping[]
  composeProject: string | null
  composeService: string | null
  /** The user's DockerWatch tracking this container, if one exists. */
  watchId: string | null
  /** The user's WebResource the watch belongs to, if one exists. */
  webResourceId: string | null
  /** V7.8 — resolved card avatar (custom upload → official dashboard-icon →
   *  null). Null falls back to the placeholder treatment. */
  iconDataUri: string | null
}

export interface StashboardFeatures {
  allowContainerRemoval: boolean
  /** V5.3 — global master switch for the browser host terminal. A connection's
   *  own `allowHostShell` opt-in is also required for the Terminal tab to go live. */
  allowHostShell: boolean
  /** V5.7 — global master switch for the browser container-exec terminal. A
   *  connection's own `allowExec` opt-in is also required for the Exec tab. */
  allowContainerExec: boolean
  /** V6.6 — global master switch for the browser Proxmox LXC console. A host's
   *  own `allowConsole` opt-in + SSH credentials are also required for the
   *  Console tab. */
  allowProxmoxConsole: boolean
  /** V6.7.1 — global master switch for one-click Proxmox "Update now". A host's
   *  own `allowUpdates` opt-in + SSH credentials are also required for the
   *  Update affordances. */
  allowProxmoxUpdates: boolean
  /** V6.13 — global master switch for destroying an LXC. A host's own
   *  `allowDestroy` opt-in is also required, and the guest must be stopped. */
  allowProxmoxDestroy: boolean
  /** V6.13.1 — global master switch for creating an LXC. A host's own
   *  `allowCreate` opt-in is also required. */
  allowProxmoxCreate: boolean
}

/** V5.3 — app-wide host-terminal master switch, managed from Settings → Host terminal. */
export interface HostShellSettings {
  enabled: boolean
}

/** V5.7 — app-wide container-exec master switch, managed from Settings → Container exec. */
export interface ContainerExecSettings {
  enabled: boolean
}

/** V6.6 — app-wide LXC-console master switch, managed from Settings → LXC console. */
export interface ProxmoxConsoleSettings {
  enabled: boolean
}

/** V6.7.1 — app-wide Proxmox "Update now" master switch, managed from Settings → Proxmox updates. */
export interface ProxmoxUpdateApplySettings {
  enabled: boolean
}

/** V6.13 — app-wide destroy-LXC master switch, managed from Settings → Destroy LXC. */
export interface ProxmoxDestroySettings {
  enabled: boolean
}

/** V6.13.1 — app-wide create-LXC master switch, managed from Settings → Create LXC. */
export interface ProxmoxCreateSettings {
  enabled: boolean
}

/** V5.5 — app-wide image-prune settings, managed from Settings → Image cleanup. */
export interface ImagePruneSettings {
  enabled: boolean
  intervalHours: number
}

/** V5.6 — app-wide health-check tuning (offline-alert reliability), managed from Settings → Health checks. */
export interface HealthCheckSettings {
  intervalSeconds: number
  failureThreshold: number
  retryCount: number
  retryDelayMs: number
}

/** V5.5 — trigger that kicked off a prune run. Mirrors the backend enum. */
export type DockerPruneTrigger = 'Scheduled' | 'Manual'

/** V5.5 — terminal outcome of a single prune run. Mirrors the backend enum. */
export type DockerPruneStatus =
  | 'Success'
  | 'NothingToPrune'
  | 'HostUnreachable'
  | 'Failed'
  | 'Skipped'

/** V5.5 — wire shape of a single `DockerPruneRunEntity` row. */
export interface DockerPruneRun {
  id: string
  trigger: DockerPruneTrigger
  status: DockerPruneStatus
  includedUnused: boolean
  imagesDeleted: number
  spaceReclaimedBytes: number
  startedUtc: string
  completedUtc: string | null
  error: string | null
}

/** V5.5 — one image on the host, for the Storage drill-down. */
export interface DockerImageInfo {
  id: string
  /** Repo tags (e.g. `nginx:1.27`); empty for a dangling image. */
  repoTags: string[]
  sizeBytes: number
  createdUtc: string | null
  isDangling: boolean
  isUnused: boolean
  /** Containers (running or stopped) using this image. Non-empty means a
   *  prune can't remove it until those containers are gone. */
  usedByContainers: string[]
}

/** V5.5 — storage summary for the V3.5 Storage widget. */
export interface DockerImageStorage {
  totalImageCount: number
  danglingImageCount: number
  danglingImageBytes: number
  unusedImageCount: number
  unusedImageBytes: number
  allowImagePrune: boolean
  pruneUnusedImages: boolean
  lastPruneUtc: string | null
  recentRuns: DockerPruneRun[]
  /** Every image on the host, largest first, each flagged dangling / unused. */
  images: DockerImageInfo[]
}

export interface DockerContainerActionResponse {
  attempt: DockerUpdateAttempt
}

/** V5.4 — response from the bulk "Update project" endpoint. */
export interface DockerProjectUpdateResponse {
  parent: DockerUpdateAttempt
  services: DockerUpdateAttempt[]
  /** Dispatch path the backend took: `Compose` for the one-shot
   *  `docker compose pull && up -d`, `Recreate` for the per-service
   *  fallback. Surfaced so the UI can hint at why the update was slow or
   *  why a particular service is the one that failed. */
  mode: 'Compose' | 'Recreate'
}

// ── V7.0 — Read-only Compose viewer ──────────────────────────────────────

/** V7.0 — one environment variable; `value` is null for pass-through
 *  entries (`- KEY` with no value). */
export interface ComposeEnvVar {
  name: string
  value: string | null
}

/** V7.2 — one `ulimits:` entry; the scalar form (`nproc: 65535`) yields equal
 *  soft/hard. */
export interface ComposeUlimit {
  name: string
  soft: number | null
  hard: number | null
}

/** V7.2 — a service's resource constraints, flattened across the two Compose
 *  conventions. `convention` (`"deploy"` | `"legacy"`) tells the UI where
 *  cpu/mem/pids live so it edits and writes back in the same home; legacy mode
 *  has no CPU-reservation key (`cpuReservation` stays null and the input is
 *  disabled). `cpuShares` / `oom*` / `shmSize` / `ulimits` are always top-level. */
export interface ComposeResourceConstraints {
  convention: 'deploy' | 'legacy'
  cpuLimit: string | null
  cpuReservation: string | null
  memLimit: string | null
  memReservation: string | null
  pidsLimit: string | null
  cpuShares: number | null
  oomKillDisable: boolean | null
  oomScoreAdj: number | null
  shmSize: string | null
  ulimits: ComposeUlimit[]
}

/** V7.2 — host capacity already reserved by other running containers (the
 *  edited project's own containers excluded), from
 *  `GET /compose/{project}/allocation`. */
export interface ComposeAllocation {
  containerCount: number
  reservedCpus: number
  reservedMemoryBytes: number
}

/** V7.0 — one Compose service card. Ports / volumes are normalised to the
 *  short syntax by the backend regardless of which form the file used. */
export interface ComposeService {
  name: string
  image: string | null
  containerName: string | null
  restart: string | null
  ports: string[]
  volumes: string[]
  environment: ComposeEnvVar[]
  envFiles: string[]
  dependsOn: string[]
  networks: string[]
  /** V7.2 — CPU / memory / pids / ulimits / OOM constraints, normalised across
   *  the deploy and legacy conventions. */
  resources: ComposeResourceConstraints
  /** V7.1 — `labels:` as name/value pairs in file order. */
  labels: ComposeEnvVar[]
  /** V7.1 — `command:`; exec (list) form folded into a `["json", "style"]` flow string. */
  command: string | null
  /** V7.1 — `entrypoint:`, same folding as `command`. */
  entrypoint: string | null
  /** V7.1 — `user:`. */
  user: string | null
  /** V7.1 — `working_dir:`. */
  workingDir: string | null
}

/** V7.0 — parsed Compose project from
 *  `GET /api/docker/connections/{id}/compose/{project}`.
 *  `unsupportedFeatures` powers the "Read-only — file uses X" banner. */
export interface ComposeProject {
  projectName: string | null
  fileName: string
  projectPath: string
  services: ComposeService[]
  /** V7.3 — top-level networks with their editable options (was a name list). */
  networks: ComposeNetwork[]
  volumes: ComposeVolume[]
  secrets: ComposeFileResource[]
  configs: ComposeFileResource[]
  unsupportedFeatures: string[]
  /** V7.7 — linter findings for this file, computed on every load and after
   *  every save. Each finding renders on its `service` card (or project-level
   *  when `service` is null) and aggregates into the Health badge. */
  lint: ComposeLintFinding[]
}

/** V7.7 — severity of one {@link ComposeLintFinding}. `Error` blocks / breaks
 *  the project; `Warning` is a smell worth surfacing but not blocking. */
export type ComposeLintSeverity = 'Warning' | 'Error'

/** V7.7 — one linter finding from `GET …/compose/{project}`. `rule` is a stable
 *  kebab-case id; `service` is the service the finding renders on (null for a
 *  project-level finding, e.g. a top-level `version:` key). */
export interface ComposeLintFinding {
  rule: string
  severity: ComposeLintSeverity
  message: string
  service: string | null
}

/** V7.3 — one top-level `networks:` entry. External entries bind to a
 *  pre-existing network; the driver / ipam / driver_opts fields are then unused. */
export interface ComposeNetwork {
  name: string
  external: boolean
  nameOverride: string | null
  driver: string | null
  subnet: string | null
  gateway: string | null
  driverOpts: ComposeEnvVar[]
}

/** V7.3 — one top-level `volumes:` entry. */
export interface ComposeVolume {
  name: string
  external: boolean
  nameOverride: string | null
  driver: string | null
  driverOpts: ComposeEnvVar[]
}

/** V7.3 — one top-level `secrets:` / `configs:` entry: a host `file:` path or an
 *  external reference. */
export interface ComposeFileResource {
  name: string
  external: boolean
  nameOverride: string | null
  file: string | null
}

/** V7.3 — which top-level section a resource edit targets (the route segment). */
export type ComposeResourceKind = 'networks' | 'volumes' | 'secrets' | 'configs'

/** V7.3 — desired final state of one top-level resource entry. The entry name
 *  (key) and the kind travel in the URL; the backend writes only the fields that
 *  apply to the kind. */
export interface ComposeResourceEditRequest {
  external: boolean
  nameOverride: string | null
  driver: string | null
  subnet: string | null
  gateway: string | null
  file: string | null
  driverOpts: ComposeEnvVar[]
}

/** V7.3 — result of a top-level resource edit / delete. */
export interface ComposeResourceEditResponse {
  changed: boolean
  project: ComposeProject
}

/** V7.3 — one network already defined on the host, for the subnet-overlap warning. */
export interface ComposeHostNetwork {
  name: string
  driver: string | null
  subnets: string[]
}

/** V7.3 — on-disk usage of one named volume; `sizeBytes` null when unknown. */
export interface ComposeVolumeUsage {
  name: string
  sizeBytes: number | null
  refCount: number | null
}

/** V7.1 — one Compose project discovered on a connection from its containers'
 *  `com.docker.compose.project` / `…working_dir` labels. */
export interface ComposeDiscoveredProject {
  name: string
  /** The project directory as the host knows it (the working_dir label). */
  workingDir: string | null
  /** The directory Stashboard would actually read; `null` when the project
   *  advertises no working_dir (Compose file access unavailable). */
  resolvedPath: string | null
  containerCount: number
  runningCount: number
}

/** V7.1 — desired final state of one service's editable fields. Send back
 *  exactly what the GET returned with the fields you want changed; the
 *  backend diffs per key, so untouched fields are a guaranteed zero-diff. */
export interface ComposeServiceEditRequest {
  image: string | null
  restart: string | null
  ports: string[]
  volumes: string[]
  environment: ComposeEnvVar[]
  labels: ComposeEnvVar[]
  command: string | null
  entrypoint: string | null
  user: string | null
  workingDir: string | null
  /** V7.2 — desired resource constraints. */
  resources: ComposeResourceConstraints
}

/** V7.1 — result of a service edit; `project` is the freshly re-parsed file. */
export interface ComposeServiceEditResponse {
  changed: boolean
  project: ComposeProject
}

/** V7.4 — desired state of a brand-new service appended to the project's
 *  `services:` map. Mirrors {@link ComposeServiceEditRequest} plus the
 *  service `name` (validated `^[a-zA-Z0-9._-]+$`, unique). */
export interface ComposeServiceCreateRequest extends ComposeServiceEditRequest {
  name: string
}

/** V7.4.1 — bootstrap a brand-new Compose project: write a new file holding a
 *  top-level `name:` + first service into `directory` (created when
 *  `createDirectory` is set), optionally running `docker compose up -d`. */
export interface ComposeProjectCreateRequest {
  projectName: string
  directory: string
  fileName: string | null
  createDirectory: boolean
  run: boolean
  /** V7.5 — the services to write, in order. The first seeds the file; the rest
   *  are appended. The from-scratch wizard sends one; templates may send several. */
  services: ComposeServiceCreateRequest[]
}

/** V7.4.1 — result of a project bootstrap. `started` reflects the optional
 *  `up -d`; `startError` carries the run failure (the file is still written). */
export interface ComposeProjectCreateResponse {
  projectName: string
  fileName: string
  directory: string
  started: boolean
  startError: string | null
}

/** V7.5 — one per-deployment input a template asks the user to fill. The
 *  placeholder `${key}` in the template's service fields is replaced with the
 *  entered value (or `default`). */
export interface ServiceTemplateVariable {
  key: string
  label: string
  /** `text` (default) | `password` | `path` | `port`. */
  type: string | null
  hint: string | null
  default: string | null
  required: boolean
  generate: boolean
}

/** V7.5 — one starter recipe behind the "From template" wizard tab. Services
 *  mirror {@link ComposeServiceCreateRequest} with `${key}` placeholders the
 *  client resolves from {@link variables} before posting to `create-project`. */
export interface ServiceTemplate {
  id: string
  name: string
  description: string
  category: string
  /** dashboard-icons slug; the UI builds the CDN URL, falling back to a badge. */
  icon: string | null
  projectName: string
  variables: ServiceTemplateVariable[]
  services: ComposeServiceCreateRequest[]
  notes: string | null
  documentation: string | null
}

/** V7.4 — the project's Compose file as raw text, for the "Raw YAML" tab. */
export interface ComposeFile {
  fileName: string
  projectPath: string
  content: string
}

/** V7.4 — result of a raw-file save; `changed` is false when the submitted
 *  text already matched the file on disk. */
export interface ComposeFileSaveResponse {
  changed: boolean
}

/** V7.4 — result of the "Save and run" `docker compose up -d`. */
export interface ComposeUpResponse {
  success: boolean
  output: string | null
  error: string | null
}

/** V7.1 — registry tag list for the image dropdown; `error` set when the
 *  registry refused (private image) — the UI falls back to free text. */
export interface ComposeImageTags {
  repository: string
  tags: string[]
  error: string | null
}

// ── V7.6 — diff, dry-run, apply, history ──────────────────────────────────

/** Serialised `ComposeDiffLineType` enum. */
export type ComposeDiffLineType = 'Context' | 'Added' | 'Removed'

/** V7.6 — one line of the pre-save diff. `oldLine` / `newLine` are 1-based line
 *  numbers in the on-disk / proposed file, or null when absent on that side. */
export interface ComposeDiffLine {
  type: ComposeDiffLineType
  text: string
  oldLine: number | null
  newLine: number | null
}

/** V7.6 — result of the pre-save diff + `docker compose config -q` dry-run
 *  (nothing is written). `changedServices` is what "Apply now" would recreate
 *  (new + modified); `removedServices` are dropped from the file. */
export interface ComposeFileDiff {
  fileName: string
  projectPath: string
  unchanged: boolean
  diff: ComposeDiffLine[]
  valid: boolean
  validationError: string | null
  cliAvailable: boolean
  changedServices: string[]
  removedServices: string[]
}

/** V7.6 — result of an "Apply now" `docker compose up -d <services>`. */
export interface ComposeApplyResponse {
  success: boolean
  appliedServices: string[]
  output: string | null
  error: string | null
}

/** V7.6 — one kept revision of the project's Compose file. */
export interface ComposeHistoryEntry {
  id: string
  savedUtc: string
  sizeBytes: number
}

/** V7.6 — the raw text of one kept revision. */
export interface ComposeHistoryFile {
  id: string
  content: string
}

/** V7.6 — result of restoring a kept revision. */
export interface ComposeRestoreResponse {
  changed: boolean
  project: ComposeProject
}

// ── V3.4 — Live container stats ──────────────────────────────────────────

export interface DockerContainerStatsSample {
  timestampUtc: string
  /** `null` on the very first sample of a fresh stream — no PreCPUStats
   *  baseline to compute the delta against. */
  cpuPercent: number | null
  memoryUsageBytes: number
  memoryLimitBytes: number
  memoryPercent: number | null
  networkRxBytes: number
  networkTxBytes: number
  blockReadBytes: number
  blockWriteBytes: number
  onlineCpus: number
}

// ── V6.0 — Proxmox LXC update monitoring ────────────────────────────────────

/** What a discovered Proxmox card represents. Mirrors the backend enum.
 *  V6.14 added `'Qemu'` (value 2) for QEMU/KVM virtual machines. */
export type ProxmoxGuestType = 'Node' | 'Lxc' | 'Qemu' | 0 | 1 | 2

/** V6.11 — the kind of LXC monitoring change recorded in the audit trail.
 *  Mirrors the backend `ProxmoxMonitoringChangeType` enum (serialised by name). */
export type ProxmoxMonitoringChangeType = 'Enabled' | 'Disabled' | 'Snoozed' | 'SnoozeCleared'

/** One auto-discovered node/LXC card on a Proxmox host. */
export interface ProxmoxGuest {
  vmId: number
  name: string
  guestType: ProxmoxGuestType
  isRunning: boolean
  /** Pending package updates, or `null` when it couldn't be determined
   *  (guest stopped, not Debian-based, SSH/exec failed). */
  pendingUpdates: number | null
  lastError: string | null
  /** Primary IPv4 of a running LXC (from the Proxmox interfaces API), or null. */
  ipAddress: string | null
  /** Seconds since the guest started (running guests), else null. */
  uptimeSeconds: number | null
  cpuCores: number | null
  /** Memory limit in bytes. */
  memoryBytes: number | null
  /** Root disk size in bytes. */
  diskBytes: number | null
  tags: string[]
  lastCheckedUtc: string | null
  /** V6.7 — whether update monitoring is on for this LXC. Always `true` for the
   *  node row. When `false` the guest is skipped by checks and excluded from
   *  notifications, and the card drops its "updates pending" emphasis. */
  monitoringEnabled: boolean
  /** V6.11 — active maintenance-window snooze until this UTC instant, or `null`
   *  when not snoozed. While set the guest is skipped by checks exactly like
   *  monitoring-off, then auto-re-included once it passes. */
  monitoringSnoozedUntil: string | null
}

/** V6.2 — one `net<n>` / `mp<n>` / `rootfs` line from an LXC config, kept as the
 *  raw Proxmox option string plus its key. */
export interface ProxmoxLxcConfigLine {
  key: string
  value: string
}

/** V6.2 — an LXC's static config merged with its live status, for the Config
 *  tab. Byte fields are normalised to bytes server-side. */
export interface ProxmoxLxcDetail {
  vmId: number
  status: string
  cores: number | null
  memoryBytes: number | null
  swapBytes: number | null
  hostname: string | null
  osType: string | null
  arch: string | null
  onboot: boolean | null
  unprivileged: boolean | null
  features: string | null
  networks: ProxmoxLxcConfigLine[]
  mounts: ProxmoxLxcConfigLine[]
  cpuFraction: number | null
  memUsedBytes: number | null
  memMaxBytes: number | null
  diskUsedBytes: number | null
  diskMaxBytes: number | null
  uptimeSeconds: number | null
}

/** V6.3 — one RRD sample for an LXC. Rate fields are bytes/s averaged over the
 *  bucket; `cpu` is a 0..1 fraction; any field may be null for an empty bucket. */
export interface ProxmoxLxcRrdPoint {
  time: number
  cpu: number | null
  memUsed: number | null
  memMax: number | null
  netIn: number | null
  netOut: number | null
  diskRead: number | null
  diskWrite: number | null
}

/** V6.3 — one node task scoped to a guest. `status` is the exit status
 *  (`OK` / error string) for finished tasks, null while running. */
export interface ProxmoxTask {
  upid: string
  type: string
  status: string | null
  user: string | null
  startTime: number | null
  endTime: number | null
}

/** V6.4 — the live runtime snapshot polled for the real-time Stats view.
 *  `netIn`/`netOut` are cumulative byte counters (the client derives rates). */
export interface ProxmoxLxcStatus {
  status: string
  cpu: number | null
  memUsed: number | null
  memMax: number | null
  diskUsed: number | null
  diskMax: number | null
  netIn: number | null
  netOut: number | null
  diskRead: number | null
  diskWrite: number | null
  uptimeSeconds: number | null
}

/** V6.4 — LXC lifecycle actions. */
export type ProxmoxLxcAction = 'start' | 'stop' | 'shutdown' | 'reboot'

// ── V6.8 — PVE node card ─────────────────────────────────────────────────────

/** V6.8 — the node's own health snapshot (`GET …/node/status`), bytes-normalised.
 *  Every field is nullable so a partial response still renders a useful card. */
export interface ProxmoxNodeStatus {
  cpuModel: string | null
  sockets: number | null
  cpus: number | null
  cores: number | null
  cpuMhz: number | null
  hvm: boolean | null
  cpuFraction: number | null
  ioWaitFraction: number | null
  load1: number | null
  load5: number | null
  load15: number | null
  memTotal: number | null
  memUsed: number | null
  memFree: number | null
  swapTotal: number | null
  swapUsed: number | null
  rootTotal: number | null
  rootUsed: number | null
  uptimeSeconds: number | null
  kernelVersion: string | null
  pveVersion: string | null
  subscriptionStatus: string | null
  subscriptionLevel: string | null
}

/** V6.8 — one storage pool (`GET …/node/storage`). */
export interface ProxmoxNodeStorage {
  storage: string
  type: string | null
  content: string | null
  enabled: boolean
  active: boolean
  total: number | null
  used: number | null
  avail: number | null
}

/** V6.8 — one physical disk + its SMART health summary (`GET …/node/disks`). */
export interface ProxmoxNodeDisk {
  devPath: string
  model: string | null
  serial: string | null
  vendor: string | null
  type: string | null
  size: number | null
  health: string | null
  wearoutPercent: number | null
  rpm: number | null
  used: string | null
}

/** V6.8 — one critical SMART attribute (ATA disks). */
export interface ProxmoxSmartAttribute {
  id: number
  name: string
  value: number | null
  worst: number | null
  threshold: number | null
  raw: string | null
}

/** V6.8 — detailed SMART for one disk, loaded on demand (`GET …/node/disks/smart`). */
export interface ProxmoxNodeDiskSmart {
  health: string | null
  type: string | null
  attributes: ProxmoxSmartAttribute[]
  text: string | null
}

/** V6.8 — one configured network interface (`GET …/node/network`). */
export interface ProxmoxNodeNetworkInterface {
  iface: string
  type: string | null
  active: boolean | null
  autostart: boolean | null
  method: string | null
  address: string | null
  cidr: string | null
  gateway: string | null
  bridgePorts: string | null
  bondSlaves: string | null
}

/** V6.8 — one RRD sample for the node. Rate fields are bytes/s; `cpu`/`ioWait`
 *  are 0..1; any field may be null for an empty bucket. */
export interface ProxmoxNodeRrdPoint {
  time: number
  cpu: number | null
  ioWait: number | null
  loadAvg: number | null
  memTotal: number | null
  memUsed: number | null
  netIn: number | null
  netOut: number | null
  rootTotal: number | null
  rootUsed: number | null
  swapUsed: number | null
}

/** V6.8 — one reading from `sensors -j` over SSH. V6.8.2 added voltage (`volts`)
 *  and power (`watts`) inputs alongside the temperature / fan ones. */
export interface ProxmoxSensorReading {
  chip: string
  label: string
  tempC: number | null
  highC: number | null
  critC: number | null
  rpm: number | null
  volts: number | null
  watts: number | null
}

/** V6.8 — the node's thermal/fan snapshot (`GET …/node/sensors`). `available`
 *  is false (with a reason) when SSH or lm-sensors is missing. */
export interface ProxmoxNodeSensors {
  available: boolean
  error: string | null
  readings: ProxmoxSensorReading[]
}

// ── V6.8.2 — PVE node deep telemetry (SSH collectors) ─────────────────────────

/** V6.8.2 — one logical CPU core's utilisation + steal between two /proc/stat
 *  samples. */
export interface ProxmoxCpuCoreStat {
  core: number
  utilPercent: number
  stealPercent: number
}

/** V6.8.2 — per-core CPU utilisation + steal and MemAvailable over SSH
 *  (`GET …/node/cpu`). `available` is false (with a reason) when SSH is missing. */
export interface ProxmoxNodeCpuStats {
  available: boolean
  error: string | null
  cores: ProxmoxCpuCoreStat[]
  stealPercent: number | null
  memAvailableBytes: number | null
}

/** V6.8.2 — one physical disk's IO between two /proc/diskstats samples. */
export interface ProxmoxDiskIoStat {
  device: string
  readBytesPerSec: number
  writeBytesPerSec: number
  readIops: number
  writeIops: number
  readAwaitMs: number | null
  writeAwaitMs: number | null
}

/** V6.8.2 — per-disk IO throughput / IOPS / latency over SSH (`GET …/node/diskio`). */
export interface ProxmoxNodeDiskIo {
  available: boolean
  error: string | null
  disks: ProxmoxDiskIoStat[]
}

/** V6.8.2 — one LVM-thin pool's fill level from `lvs`. */
export interface ProxmoxThinPool {
  name: string
  volumeGroup: string
  sizeBytes: number | null
  dataPercent: number | null
  metadataPercent: number | null
}

/** V6.8.2 — LVM-thin pool fill levels over SSH (`GET …/node/thinpools`).
 *  `available` is false when SSH/lvs is missing or the host has no thin pools. */
export interface ProxmoxNodeThinPools {
  available: boolean
  error: string | null
  pools: ProxmoxThinPool[]
}

/** V6.8.2 — one interface's throughput + counters + link between two
 *  /proc/net/dev samples and /sys/class/net. */
export interface ProxmoxInterfaceStat {
  iface: string
  rxBytesPerSec: number
  txBytesPerSec: number
  rxErrors: number
  txErrors: number
  rxDropped: number
  txDropped: number
  speedMbps: number | null
  duplex: string | null
  operState: string | null
}

/** V6.8.2 — per-interface throughput / errors / link over SSH
 *  (`GET …/node/interfaces`). */
export interface ProxmoxNodeInterfaceStats {
  available: boolean
  error: string | null
  interfaces: ProxmoxInterfaceStat[]
}

/** V6.8.2 — one disk's last SMART self-test + critical counters over SSH
 *  (`GET …/node/disks/selftest`). Every field is nullable. */
export interface ProxmoxDiskSelfTest {
  available: boolean
  error: string | null
  lastTestType: string | null
  lastTestStatus: string | null
  lastTestPowerOnHours: number | null
  powerOnHours: number | null
  reallocatedSectors: number | null
  pendingSectors: number | null
  uncorrectableSectors: number | null
}

// ── V6.8.1 — PVE node alerting ────────────────────────────────────────────────

/** V6.8.1 — which deviation categories are armed for a node. */
export interface ProxmoxAlertCategoryToggles {
  cpu: boolean
  memory: boolean
  storage: boolean
  thermal: boolean
  smart: boolean
  network: boolean
}

/** V6.8.1 — a warn/crit threshold set. On `thresholds` a `null` means "use the
 *  default"; on `defaults` every field is populated for placeholders. */
export interface ProxmoxNodeAlertThresholdValues {
  cpuWarn: number | null
  cpuCrit: number | null
  memWarn: number | null
  memCrit: number | null
  storageWarn: number | null
  storageCrit: number | null
  tempWarn: number | null
  tempCrit: number | null
}

/** V6.8.1 — a node's alert configuration (`GET …/node/alerts/settings`). */
export interface ProxmoxNodeAlertSettings {
  enabled: boolean
  categories: ProxmoxAlertCategoryToggles
  thresholds: ProxmoxNodeAlertThresholdValues
  defaults: ProxmoxNodeAlertThresholdValues
  lastNotificationSentUtc: string | null
}

/** V6.8.1 — upsert payload for a node's alert configuration. */
export interface ProxmoxNodeAlertSettingsUpdate {
  enabled: boolean
  categories: ProxmoxAlertCategoryToggles
  thresholds: ProxmoxNodeAlertThresholdValues
}

/** V6.8.1 — one currently-active alert (`GET …/node/alerts`). */
export interface ProxmoxNodeAlert {
  category: string
  severity: 'warn' | 'crit'
  metric: string | null
  value: number | null
  threshold: number | null
  firstSeenUtc: string | null
}

/** V6.9 — one intentful change to a `net<n>` interface. `key` empty ⇒ add (the
 *  server assigns the next free `net<n>`); `remove` ⇒ the key goes into Proxmox's
 *  `delete=` list. For an upsert either set the structured fields (the server
 *  formats the exact option line) or `raw` to supply the whole option string
 *  verbatim (advanced mode). `extra` carries unknown-but-valid options parsed off
 *  the original line so a structured edit never drops them. */
export interface ProxmoxLxcNetChange {
  key: string
  remove?: boolean
  raw?: string | null
  name?: string | null
  bridge?: string | null
  ip?: string | null      // 'dhcp' | 'manual' | 'x.x.x.x/nn'
  gw?: string | null
  ip6?: string | null     // 'dhcp' | 'auto' | 'manual' | 'xxxx::/nn'
  gw6?: string | null
  tag?: number | null
  firewall?: boolean | null
  rate?: number | null
  mtu?: number | null
  hwaddr?: string | null
  linkDown?: boolean | null
  extra?: string | null
}

/** V6.9 — one intentful change to a secondary mount point (`mp<n>`). `key` empty
 *  ⇒ add; `remove` ⇒ `delete=mp<n>` (config entry only — storage content is not
 *  touched). `volume` is the positional storage/source token. */
export interface ProxmoxLxcMountChange {
  key: string
  remove?: boolean
  raw?: string | null
  volume?: string | null
  mountPoint?: string | null
  size?: string | null
  readOnly?: boolean | null
  backup?: boolean | null
  quota?: boolean | null
  acl?: boolean | null
  shared?: boolean | null
  replicate?: boolean | null
  mountOptions?: string | null
  extra?: string | null
}

/** V6.9 — an edit to the container's `rootfs`. No key, cannot be removed — only
 *  the safe subset (size + selected flags) is editable. */
export interface ProxmoxLxcRootfsChange {
  raw?: string | null
  volume?: string | null
  size?: string | null
  readOnly?: boolean | null
  quota?: boolean | null
  acl?: boolean | null
  replicate?: boolean | null
  mountOptions?: string | null
  extra?: string | null
}

/** V6.5 / V6.9 — editable subset of an LXC's config. The scalar fields (V6.5)
 *  are each optional — only the changed ones are sent, and a `null`/omitted field
 *  leaves that Proxmox key untouched. Memory / swap are in MiB. V6.9 adds the
 *  structured `networks` / `mounts` / `rootfs` add/update/remove operations (a
 *  `null`/omitted list means "no changes here"). */
export interface ProxmoxLxcConfigUpdate {
  cores?: number | null
  memoryMib?: number | null
  swapMib?: number | null
  hostname?: string | null
  onboot?: boolean | null
  networks?: ProxmoxLxcNetChange[] | null
  mounts?: ProxmoxLxcMountChange[] | null
  rootfs?: ProxmoxLxcRootfsChange | null
}

/** V6.13.1 — one container template (a `vztmpl` volume) for the create form's
 *  template dropdown. `volid` is passed back as `ProxmoxLxcCreate.osTemplate`. */
export interface ProxmoxTemplate {
  volid: string
  storage: string
  size: number | null
}

/** V6.13.1 — the next free guest id from `GET /cluster/nextid`. */
export interface ProxmoxNextId {
  vmId: number
}

/** V6.13.1 — create-one-LXC-from-a-template payload (`POST …/lxc`). `net0` reuses
 *  the V6.9 structured network model so the create and edit forms format an
 *  interface identically. `password` / `sshPublicKeys` are write-only. */
export interface ProxmoxLxcCreate {
  vmId: number
  osTemplate: string
  rootfsStorage: string
  rootfsSizeGib: number
  hostname?: string | null
  description?: string | null
  tags?: string | null
  password?: string | null
  sshPublicKeys?: string | null
  cores?: number | null
  memoryMib?: number | null
  swapMib?: number | null
  net0?: ProxmoxLxcNetChange | null
  unprivileged: boolean
  onboot: boolean
  start: boolean
  /** Enable the `nesting=1` feature (Docker / nested LXC inside the container). */
  nesting: boolean
  /** DNS server(s) (`nameserver`); null ⇒ inherit the host's setting. */
  nameserver?: string | null
  /** DNS search domain (`searchdomain`); null ⇒ inherit. */
  searchDomain?: string | null
  /** Register the new container with the cluster HA manager after create. */
  addToHa: boolean
}

/** A configured Proxmox host with its discovered guests inline. Never carries
 *  decrypted secrets — only presence flags. */
/** V6.8.3 — which Proxmox product a host points at (PVE vs Proxmox Backup
 *  Server). Serialized as a string by the API's global enum converter. */
export type ProxmoxServerType = 'Pve' | 'Pbs'

export interface ProxmoxConnection {
  id: string
  name: string
  apiBaseUrl: string
  nodeName: string
  /** V6.8.3 — PVE or PBS. */
  serverType: ProxmoxServerType
  apiTokenId: string
  hasApiTokenSecret: boolean
  skipTlsVerify: boolean
  sshHost: string | null
  sshPort: number | null
  sshUsername: string | null
  hasSshPrivateKey: boolean
  hasSshPrivateKeyPassphrase: boolean
  /** V6.6 — per-host opt-in to the browser LXC console (SSH + `pct exec`). The
   *  server also requires the global `allowProxmoxConsole` switch and SSH
   *  credentials before the Console tab goes live. */
  allowConsole: boolean
  /** V6.7.1 — per-host opt-in to one-click "Update now". The server also
   *  requires the global `allowProxmoxUpdates` switch and SSH credentials. */
  allowUpdates: boolean
  /** V6.13 — per-host opt-in to destroying an LXC. The server also requires the
   *  global `allowProxmoxDestroy` switch and a stopped guest. */
  allowDestroy: boolean
  /** V6.13.1 — per-host opt-in to creating an LXC. The server also requires the
   *  global `allowProxmoxCreate` switch. */
  allowCreate: boolean
  enabled: boolean
  updateNotificationsEnabled: boolean
  telegramNotificationsEnabled: boolean
  scheduleType: CheckScheduleType
  checkEveryHours: number
  checkAtTime: string | null
  checkOnDayOfWeek: DayOfWeek | null
  lastCheckedUtc: string | null
  lastError: string | null
  guests: ProxmoxGuest[]
  /** V6.8.2 — node-modal telemetry poll interval (seconds); null = app default (20s). */
  telemetryPollSeconds: number | null
  /** V6.11 — the host's public update-check webhook token, or `null` when webhook
   *  delivery isn't enabled. Full URL: `POST /api/proxmox/webhooks/{token}`. */
  webhookToken: string | null
  /** V6.11 — when the last webhook delivery was accepted, or `null`. */
  lastWebhookReceivedUtc: string | null
  createdUtc: string
  updatedUtc: string
}

/** Create-or-update payload for a Proxmox host. Secret fields are tri-state. */
export interface ProxmoxConnectionUpsert {
  name: string
  apiBaseUrl: string
  nodeName: string
  serverType: ProxmoxServerType
  apiTokenId: string
  apiTokenSecret: SecretValueUpsert | null
  skipTlsVerify: boolean
  sshHost: string | null
  sshPort: number | null
  sshUsername: string | null
  sshPrivateKey: SecretValueUpsert | null
  sshPrivateKeyPassphrase: SecretValueUpsert | null
  /** V6.6 — opt this host in to the browser LXC console. */
  allowConsole: boolean
  /** V6.7.1 — opt this host in to one-click "Update now". */
  allowUpdates: boolean
  /** V6.13 — opt this host in to destroying an LXC. */
  allowDestroy: boolean
  /** V6.13.1 — opt this host in to creating an LXC. */
  allowCreate: boolean
  enabled: boolean
  updateNotificationsEnabled: boolean
  telegramNotificationsEnabled: boolean
  scheduleType: CheckScheduleType
  checkEveryHours: number
  checkAtTime: string | null
  checkOnDayOfWeek: DayOfWeek | null
  /** V6.8.2 — node-modal telemetry poll interval (seconds), clamped 5..300 on the
   *  server; null resets to the app default (20s). */
  telemetryPollSeconds: number | null
}

export interface ProxmoxConnectionPingRequest {
  apiBaseUrl: string
  nodeName: string
  serverType: ProxmoxServerType
  apiTokenId: string
  apiTokenSecret: SecretValueUpsert | null
  skipTlsVerify: boolean
  sshHost: string | null
  sshPort: number | null
  sshUsername: string | null
  sshPrivateKey: SecretValueUpsert | null
  sshPrivateKeyPassphrase: SecretValueUpsert | null
}

export interface ProxmoxConnectionPingResponse {
  apiReachable: boolean
  /** `null` when SSH wasn't configured (and therefore not tested). */
  sshReachable: boolean | null
  error: string | null
}
