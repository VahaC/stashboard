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
  /** V5.2 — in-container path to the Compose project directory used by the
   *  Compose-aware "Update now" recreate. Non-null only for `LocalSocket`. */
  composeProjectPath: string | null
  /** V5.3 — whether this connection has opted in to the browser host terminal.
   *  Only meaningful for SSH hosts; the server also requires the global
   *  AllowHostShell flag before honouring it. */
  allowHostShell: boolean
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
  /** V5.2 — absolute in-container path to the Compose project directory.
   *  Only meaningful for `LocalSocket` hosts. */
  composeProjectPath: string | null
  /** V5.3 — opt this connection in to the browser host terminal (SSH only). */
  allowHostShell: boolean
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
export type DockerContainerActionType = 'Update' | 'Start' | 'Stop' | 'Restart' | 'Remove'

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
}

export interface StashboardFeatures {
  allowContainerRemoval: boolean
  /** V5.3 — global master switch for the browser host terminal. A connection's
   *  own `allowHostShell` opt-in is also required for the Terminal tab to go live. */
  allowHostShell: boolean
}

/** V5.3 — app-wide host-terminal master switch, managed from Settings → Host terminal. */
export interface HostShellSettings {
  enabled: boolean
}

export interface DockerContainerActionResponse {
  attempt: DockerUpdateAttempt
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
