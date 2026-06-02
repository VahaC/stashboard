import { useState } from 'react'
import { Activity, AlertCircle, Check, ChevronDown, ChevronRight, Copy, Download, HelpCircle, Unlink, Plus, RefreshCw, Settings, Trash2 } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Textarea } from '@/components/ui/textarea'
import {
  useConnectionWatchUpdates,
  useCreateConnectionWatch,
  useCreateDockerConnection,
  useDeleteConnectionWebhook,
  useDeleteDockerConnection,
  useDockerConnection,
  useDockerConnections,
  useDockerInstanceInspect,
  useDockerUpdateCommand,
  useRotateConnectionWebhook,
  useServices,
  useTelegramSettings,
  useTestConnectionWatch,
  useTestDockerConnectionPing,
  useUnlinkConnectionWatch,
  useUpdateConnectionWatch,
  useUpdateConnectionWatchNow,
  useUpdateDockerConnection,
  useUpsertService,
} from '@/lib/queries'
import type {
  CheckScheduleType,
  DayOfWeek,
  DockerConnection,
  DockerConnectionUpsert,
  DockerWatch,
  DockerWatchUpsert,
  LinkedDockerWatchSummary,
  SecretValueAction,
  SecretValueUpsert,
  Service,
} from '@/lib/types'
import {
  resolveAttemptStatus,
  resolveDockerHostType,
  resolveDockerUpdateStatus,
  resolveRegistryAuthType,
} from '@/lib/types'
import { cn, getApiErrorMessage, parseApiErrors } from '@/lib/utils'
import { Link } from 'react-router-dom'
import { ContainerInspectBody } from '@/components/docker/ContainerInspectBody'
import { ContainerLogsPanel } from '@/components/docker/ContainerLogsPanel'
import { ContainerStatsPanel } from '@/components/docker/ContainerStatsPanel'
import { UpdateConfirmDialog } from '@/components/docker/atoms/UpdateConfirmDialog'
import '@/styles/service-modal.css'

interface Props {
  service: Service
}

type ConnectionEditMode =
  | { kind: 'closed' }
  | { kind: 'create' }
  | { kind: 'edit'; connectionId: string }

// ── Top-level ──────────────────────────────────────────────────────────────

export function DockerWatchSection({ service }: Props) {
  const connectionsQuery = useDockerConnections()
  const [editMode, setEditMode] = useState<ConnectionEditMode>({ kind: 'closed' })

  const assignedConnectionId = service.dockerConnectionId
  const connections = connectionsQuery.data ?? []
  const assignedConnection = connections.find((c) => c.id === assignedConnectionId) ?? null
  const linkedWatches = service.linkedDockerWatches ?? []

  return (
    <div className="docker-section">
      <ConnectionPicker
        service={service}
        connections={connections}
        assignedConnection={assignedConnection}
        loading={connectionsQuery.isLoading}
        editMode={editMode}
        onEditMode={setEditMode}
      />

      {editMode.kind === 'closed' && (
        <LinkedContainersSummary
          linkedWatches={linkedWatches}
          assignedConnectionId={assignedConnectionId}
        />
      )}
    </div>
  )
}

// ── Read-only linked-container summary (V3.6) ──────────────────────────────
//
// Container update-tracking is now managed on the Docker page; the service
// modal only surfaces a read-only summary of the containers linked to this
// service plus a deep link into /docker where the watch can be edited.

function LinkedContainersSummary({
  linkedWatches,
  assignedConnectionId,
}: {
  linkedWatches: LinkedDockerWatchSummary[]
  assignedConnectionId: string | null
}) {
  const dockerHref = assignedConnectionId ? `/docker?connection=${assignedConnectionId}` : '/docker'
  const unlinkMutation = useUnlinkConnectionWatch()

  return (
    <div className="docker-watch-list">
      <div className="docker-section-header">
        <span className="docker-section-title">
          Tracked containers
          {linkedWatches.length > 0 && (
            <span className="service-modal-label-help">({linkedWatches.length})</span>
          )}
        </span>
        <Link to={dockerHref} className="docker-instances-card-watch-link">
          <Settings className="h-3.5 w-3.5" />
          Manage on Docker page
        </Link>
      </div>

      {linkedWatches.length === 0 ? (
        <p className="text-sm text-[var(--muted-foreground)]">
          No containers linked to this service yet. Open the{' '}
          <Link to={dockerHref} className="docker-instances-card-watch-link">Docker page</Link>{' '}
          to track a container for image updates and link it here.
        </p>
      ) : (
        <ul className="docker-linked-watches">
          {linkedWatches.map((w) => (
            <li key={w.id} className="docker-linked-watch-row">
              <div className="docker-linked-watch-main">
                <span className="docker-linked-watch-name">{w.label || w.containerName}</span>
                <span
                  className="docker-section-badge"
                  data-status={resolveDockerUpdateStatus(w.updateStatus)}
                  title={`Update status: ${resolveDockerUpdateStatus(w.updateStatus)}`}
                >
                  {resolveDockerUpdateStatus(w.updateStatus)}
                </span>
                {!w.enabled && <span className="service-modal-label-help">(paused)</span>}
              </div>
              <code className="docker-linked-watch-image">{w.imageReference}</code>
              <div className="docker-linked-watch-meta">
                <span className="service-modal-label-help">
                  container: {w.containerName}
                  {w.lastCheckedUtc ? ` · checked ${formatRelative(w.lastCheckedUtc)}` : ''}
                </span>
                <button
                  type="button"
                  className="docker-instances-card-watch-link"
                  disabled={unlinkMutation.isPending}
                  onClick={() => unlinkMutation.mutate({
                    connectionId: w.dockerConnectionId,
                    watchId: w.id,
                  })}
                  title="Unlink this container from the service (container tracking continues on the Docker page)"
                >
                  <Unlink className="h-3 w-3" />
                  Unlink
                </button>
                <Link
                  to={`/docker?connection=${w.dockerConnectionId}&container=${encodeURIComponent(w.containerName)}`}
                  className="docker-instances-card-watch-link"
                >
                  Open container
                </Link>
              </div>
            </li>
          ))}
        </ul>
      )}
    </div>
  )
}

// ── Connection picker + inline editor ──────────────────────────────────────

function ConnectionPicker({
  service,
  connections,
  assignedConnection,
  loading,
  editMode,
  onEditMode,
}: {
  service: Service
  connections: DockerConnection[]
  assignedConnection: DockerConnection | null
  loading: boolean
  editMode: ConnectionEditMode
  onEditMode: (mode: ConnectionEditMode) => void
}) {
  const upsertService = useUpsertService()
  const [error, setError] = useState<string | null>(null)
  const [fieldErrors, setFieldErrors] = useState<Record<string, string>>({})

  const assign = async (connectionId: string | null) => {
    setError(null)
    setFieldErrors({})
    try {
      await upsertService.mutateAsync({
        id: service.id,
        data: serviceToUpsert(service, connectionId),
      })
    } catch (e: unknown) {
      const { fieldErrors: fe, globalError } = parseApiErrors(e)
      setFieldErrors(fe)
      setError(globalError ?? (Object.keys(fe).length === 0 ? 'Failed to assign Docker connection' : null))
    }
  }

  const connectionError = pickFieldError(fieldErrors, 'dockerconnectionid', 'dockerConnectionId')

  if (editMode.kind === 'create') {
    return (
      <DockerConnectionForm
        existing={null}
        onSaved={async (newId) => {
          await assign(newId)
          onEditMode({ kind: 'closed' })
        }}
        onCancel={() => onEditMode({ kind: 'closed' })}
      />
    )
  }

  if (editMode.kind === 'edit') {
    return (
      <DockerConnectionForm
        existing={connections.find((c) => c.id === editMode.connectionId) ?? null}
        onSaved={() => onEditMode({ kind: 'closed' })}
        onCancel={() => onEditMode({ kind: 'closed' })}
      />
    )
  }

  return (
    <div className="docker-connection-picker">
      <div className="docker-section-header">
        <span className="docker-section-title">Docker host</span>
      </div>
      {error && <p className="service-modal-error">{error}</p>}
      <div className="docker-connection-picker-row">
        <select
          className={cn('service-modal-select', connectionError && 'border-destructive')}
          value={assignedConnection?.id ?? ''}
          disabled={loading || upsertService.isPending}
          onChange={(e) => void assign(e.target.value || null)}
        >
          <option value="">
            {loading ? 'Loading connections…' : '— no Docker host —'}
          </option>
          {connections.map((c) => {
            const hostType = resolveDockerHostType(c.hostType)
            const detail = hostType === 'TcpTls'
              ? (c.hostUrl || 'remote')
              : hostType === 'Ssh'
                ? `ssh://${c.sshUsername ?? '?'}@${c.sshHost ?? '?'}`
                : 'local socket'
            return (
              <option key={c.id} value={c.id}>
                {c.name} · {detail}
              </option>
            )
          })}
        </select>
        <Button
          type="button"
          variant="outline" 
          size="sm"
          onClick={() => onEditMode({ kind: 'create' })}
          aria-label="Add connection"
        >
          <Plus className="h-3.5 w-3.5" />
          <span className="docker-connection-button-label">Add connection</span>
        </Button>
        {assignedConnection && (
          <Button type="button" variant="outline" size="sm"
            onClick={() => onEditMode({ kind: 'edit', connectionId: assignedConnection.id })}
            aria-label="Edit this connection"
          >
            <Settings className="h-3.5 w-3.5" />
            <span className="docker-connection-button-label">Edit connection</span>
          </Button>
        )}
      </div>
      {connectionError && <p className="service-modal-field-error">{connectionError}</p>}

      {assignedConnection && (
        <p className="text-sm text-[var(--muted-foreground)]">
          {assignedConnection.hasTlsConfigured && 'TLS configured · '}
          {assignedConnection.hasSshPrivateKey && 'SSH key configured · '}
          Used by {assignedConnection.usageCount} service{assignedConnection.usageCount === 1 ? '' : 's'}
        </p>
      )}

      {!assignedConnection && connections.length === 0 && !loading && (
        <p className="text-sm text-[var(--muted-foreground)]">
          You don't have any Docker hosts yet. Add one to start tracking container updates —
          a single host can back many services, so you only need to set it up once.
        </p>
      )}
    </div>
  )
}

/** Project a Service back into a ServiceUpsert payload so we can call PUT
 *  to assign a connection without dropping the rest of the fields. The
 *  credentials list is intentionally empty — sending `[]` means "keep what's
 *  saved" because the modal only sends real credentials on its own save. */
function serviceToUpsert(s: Service, dockerConnectionId: string | null) {
  return {
    name: s.name,
    mainUrl: s.mainUrl,
    mainUrlHealthCheckEnabled: s.mainUrlHealthCheckEnabled,
    additionalUrl: s.additionalUrl,
    additionalUrlHealthCheckEnabled: s.additionalUrlHealthCheckEnabled,
    offlineNotificationsEnabled: s.offlineNotificationsEnabled,
    healthCheckUrl: s.healthCheckUrl,
    healthCheckMethod: s.healthCheckMethod,
    expectedStatusRange: s.expectedStatusRange,
    notes: s.notes,
    categoryId: s.categoryId,
    logoSource: s.logoSource,
    customLogoPath: s.customLogoPath,
    tags: s.tags,
    credentials: s.credentials.map((c) => ({ key: c.key, value: c.value, isSecret: c.isSecret })),
    dockerConnectionId,
  }
}

// ── Connection setup / edit form ──────────────────────────────────────────

interface ConnectionFormState {
  name: string
  hostType: 'LocalSocket' | 'TcpTls' | 'Ssh'
  hostUrl: string
  /** V2.5 — SSH-specific fields. Always present in state so we don't lose
   *  user input when the host type briefly toggles to TcpTls and back. */
  sshHost: string
  sshPort: string
  sshUsername: string
  sshRemoteSocketPath: string
  /** V5.3 — opt this SSH connection in to the browser host terminal. */
  allowHostShell: boolean
  /** V5.7 — opt this connection in to the browser container-exec terminal (any host type). */
  allowExec: boolean
  /** V5.2 — in-container Compose project directory (LocalSocket only). */
  composeProjectPath: string
  /** V5.5 — participate in the background image-prune sweep. */
  allowImagePrune: boolean
  /** V5.5 — also remove non-dangling "unused" images. */
  pruneUnusedImages: boolean
}

export function DockerConnectionForm({
  showTitle = true,
  existing,
  onSaved,
  onCancel,
}: {
  showTitle?: boolean
  existing: DockerConnection | null
  /** Called after a successful save. For `create`, receives the new id. */
  onSaved: (id: string) => void | Promise<void>
  onCancel: () => void
}) {
  // Re-fetch to get the latest TLS presence flag in case it changed since the
  // dropdown was loaded.
  useDockerConnection(existing?.id ?? null)
  const create = useCreateDockerConnection()
  const update = useUpdateDockerConnection()
  const remove = useDeleteDockerConnection()
  const ping = useTestDockerConnectionPing()

  const [form, setForm] = useState<ConnectionFormState>(() => ({
    name: existing?.name ?? '',
    hostType: existing ? resolveDockerHostType(existing.hostType) : 'LocalSocket',
    hostUrl: existing?.hostUrl ?? '',
    sshHost: existing?.sshHost ?? '',
    sshPort: existing?.sshPort ? String(existing.sshPort) : '22',
    sshUsername: existing?.sshUsername ?? '',
    sshRemoteSocketPath: existing?.sshRemoteSocketPath ?? '/var/run/docker.sock',
    allowHostShell: existing?.allowHostShell ?? false,
    allowExec: existing?.allowExec ?? false,
    composeProjectPath: existing?.composeProjectPath ?? '',
    allowImagePrune: existing?.allowImagePrune ?? true,
    pruneUnusedImages: existing?.pruneUnusedImages ?? false,
  }))
  const [tlsCa, setTlsCa] = useState<SecretField>(() => existingSecret(existing?.hasTlsConfigured ?? false))
  const [tlsCert, setTlsCert] = useState<SecretField>(() => existingSecret(existing?.hasTlsConfigured ?? false))
  const [tlsKey, setTlsKey] = useState<SecretField>(() => existingSecret(existing?.hasTlsConfigured ?? false))
  // V2.5 — SSH secret fields. The PEM key is multi-line, the passphrase is a
  // single-line secret. Both follow the same tri-state Keep/Set/Clear convention
  // as the TLS material so an edit can preserve the server-side encrypted blob.
  const [sshPrivateKey, setSshPrivateKey] = useState<SecretField>(() => existingSecret(existing?.hasSshPrivateKey ?? false))
  const [sshPassphrase, setSshPassphrase] = useState<SecretField>(() => existingSecret(existing?.hasSshPrivateKeyPassphrase ?? false))
  const [error, setError] = useState<string | null>(null)
  const [fieldErrors, setFieldErrors] = useState<Record<string, string>>({})
  const [pingResult, setPingResult] = useState<{ ok: boolean; error: string | null } | null>(null)

  const isTcpTls = form.hostType === 'TcpTls'
  const isSsh = form.hostType === 'Ssh'
  const isLocalSocket = form.hostType === 'LocalSocket'

  const parsedSshPort = (() => {
    const n = parseInt(form.sshPort, 10)
    return Number.isFinite(n) && n > 0 && n <= 65535 ? n : null
  })()

  const buildPayload = (): DockerConnectionUpsert => ({
    name: form.name.trim(),
    hostType: form.hostType,
    hostUrl: isTcpTls ? (form.hostUrl.trim() || null) : null,
    tlsCaCert: isTcpTls ? toUpsert(tlsCa) : null,
    tlsClientCert: isTcpTls ? toUpsert(tlsCert) : null,
    tlsClientKey: isTcpTls ? toUpsert(tlsKey) : null,
    sshHost: isSsh ? (form.sshHost.trim() || null) : null,
    sshPort: isSsh ? parsedSshPort : null,
    sshUsername: isSsh ? (form.sshUsername.trim() || null) : null,
    sshPrivateKey: isSsh ? toUpsert(sshPrivateKey) : null,
    sshPrivateKeyPassphrase: isSsh ? toUpsert(sshPassphrase) : null,
    sshRemoteSocketPath: isSsh ? (form.sshRemoteSocketPath.trim() || null) : null,
    allowHostShell: isSsh ? form.allowHostShell : false,
    allowExec: form.allowExec,
    composeProjectPath: isLocalSocket ? (form.composeProjectPath.trim() || null) : null,
    allowImagePrune: form.allowImagePrune,
    pruneUnusedImages: form.pruneUnusedImages,
  })

  const save = async () => {
    setError(null)
    setFieldErrors({})
    setPingResult(null)
    const payload = buildPayload()
    if (!payload.name) {
      setError('Name is required — use something memorable like "home-server" or "vps-prod".')
      return
    }
    try {
      if (existing) {
        const updated = await update.mutateAsync({ id: existing.id, data: payload })
        await onSaved(updated.id)
      } else {
        const created = await create.mutateAsync(payload)
        await onSaved(created.id)
      }
    } catch (e: unknown) {
      const { fieldErrors: fe, globalError } = parseApiErrors(e)
      setFieldErrors(fe)
      setError(globalError ?? (Object.keys(fe).length === 0 ? 'Failed to save Docker connection' : null))
    }
  }

  const testNow = async () => {
    setError(null)
    setFieldErrors({})
    setPingResult(null)
    try {
      const result = await ping.mutateAsync({
        connectionId: existing?.id,
        data: {
          hostType: form.hostType,
          hostUrl: isTcpTls ? (form.hostUrl.trim() || null) : null,
          tlsCaCert: isTcpTls ? toUpsert(tlsCa) : null,
          tlsClientCert: isTcpTls ? toUpsert(tlsCert) : null,
          tlsClientKey: isTcpTls ? toUpsert(tlsKey) : null,
          sshHost: isSsh ? (form.sshHost.trim() || null) : null,
          sshPort: isSsh ? parsedSshPort : null,
          sshUsername: isSsh ? (form.sshUsername.trim() || null) : null,
          sshPrivateKey: isSsh ? toUpsert(sshPrivateKey) : null,
          sshPrivateKeyPassphrase: isSsh ? toUpsert(sshPassphrase) : null,
          sshRemoteSocketPath: isSsh ? (form.sshRemoteSocketPath.trim() || null) : null,
        },
      })
      setPingResult({ ok: result.dockerHostReachable, error: result.error })
    } catch (e: unknown) {
      const { fieldErrors: fe, globalError } = parseApiErrors(e)
      setFieldErrors(fe)
      setError(globalError ?? (Object.keys(fe).length === 0 ? 'Test failed' : null))
    }
  }

  const nameError = pickFieldError(fieldErrors, 'name')
  const hostUrlError = pickFieldError(fieldErrors, 'hosturl', 'hostUrl')
  const tlsCaError = pickFieldError(fieldErrors, 'tlscacert', 'tlsCaCert')
  const tlsCertError = pickFieldError(fieldErrors, 'tlsclientcert', 'tlsClientCert')
  const tlsKeyError = pickFieldError(fieldErrors, 'tlsclientkey', 'tlsClientKey')
  const sshHostError = pickFieldError(fieldErrors, 'sshhost', 'sshHost')
  const sshPortError = pickFieldError(fieldErrors, 'sshport', 'sshPort')
  const sshUsernameError = pickFieldError(fieldErrors, 'sshusername', 'sshUsername')
  const sshPrivateKeyError = pickFieldError(fieldErrors, 'sshprivatekey', 'sshPrivateKey')
  const sshPassphraseError = pickFieldError(fieldErrors, 'sshprivatekeypassphrase', 'sshPrivateKeyPassphrase')
  const sshRemoteSocketError = pickFieldError(fieldErrors, 'sshremotesocketpath', 'sshRemoteSocketPath')
  const composeProjectPathError = pickFieldError(fieldErrors, 'composeprojectpath', 'composeProjectPath')

  const onDelete = async () => {
    if (!existing) return
    if (existing.usageCount > 0) {
      setError(`${existing.usageCount} service(s) use this connection. Reassign them first.`)
      return
    }
    if (!confirm(`Delete connection "${existing.name}"?`)) return
    try {
      await remove.mutateAsync(existing.id)
      onCancel()
    } catch (e: unknown) {
      setError(extractError(e) ?? 'Failed to delete connection')
    }
  }

  return (
    <div className={`docker-watch-form ${showTitle ? '' : 'docker-watch-form-no-title'}`}>
      {showTitle && (
        <div className="docker-section-header">
          <span className="docker-section-title">
            {existing ? `Edit connection: ${existing.name}` : 'New Docker host'}
          </span>
        </div>
      )}
      {error && <p className="service-modal-error">{error}</p>}

      <div className="docker-section-grid">
        <div className="service-modal-field">
          <Label className="service-modal-label">Name</Label>
          <Input
            placeholder="home-server"
            value={form.name}
            onChange={(e) => setForm({ ...form, name: e.target.value })}
            className={nameError ? 'border-destructive' : ''}
          />
          {nameError && <p className="service-modal-field-error">{nameError}</p>}
        </div>

        <div className="service-modal-field">
          <Label className="service-modal-label">Docker host</Label>
          <select
            className="service-modal-select"
            value={form.hostType}
            onChange={(e) => setForm({ ...form, hostType: e.target.value as 'LocalSocket' | 'TcpTls' | 'Ssh' })}
          >
            <option value="LocalSocket">Local socket</option>
            <option value="TcpTls">Remote TCP+TLS</option>
            <option value="Ssh">SSH tunnel</option>
          </select>
        </div>

        {isLocalSocket && (
          <div className="service-modal-field docker-section-field-full">
            <Label className="service-modal-label">Compose project path (optional)</Label>
            <Input
              placeholder="/compose-projects/home-server"
              value={form.composeProjectPath}
              onChange={(e) => setForm({ ...form, composeProjectPath: e.target.value })}
              className={cn('font-mono text-[12px]', composeProjectPathError && 'border-destructive')}
            />
            {composeProjectPathError && <p className="service-modal-field-error">{composeProjectPathError}</p>}
            <p className="text-xs text-[var(--muted-foreground)] mt-1">
              In-container path to this host's <code>docker-compose.yml</code> directory. Bind-mount the host
              project dir into Stashboard (e.g. <code>/srv/stack:/compose-projects/home-server:ro</code>) and set
              this to the in-container path. When set, <strong>Update now</strong> runs <code>docker compose pull</code>
              {' + '}<code>up&nbsp;-d</code> so <code>env_file</code>, <code>depends_on</code> ordering and profiles
              are honoured. Leave blank for the raw recreate.
            </p>
          </div>
        )}

        {isTcpTls && (
          <div className="service-modal-field docker-section-field-full">
            <Label className="service-modal-label">Host URL</Label>
            <Input
              placeholder="tcp://host.example.com:2376"
              value={form.hostUrl}
              onChange={(e) => setForm({ ...form, hostUrl: e.target.value })}
              className={cn('font-mono text-[12px]', hostUrlError && 'border-destructive')}
            />
            {hostUrlError && <p className="service-modal-field-error">{hostUrlError}</p>}
          </div>
        )}

        {isTcpTls && (
          <>
            <SecretFieldRow label="TLS CA cert (PEM)" field={tlsCa} hasExisting={existing?.hasTlsConfigured ?? false} multiline error={tlsCaError} onChange={setTlsCa} />
            <SecretFieldRow label="TLS client cert (PEM)" field={tlsCert} hasExisting={existing?.hasTlsConfigured ?? false} multiline error={tlsCertError} onChange={setTlsCert} />
            <SecretFieldRow label="TLS client key (PEM)" field={tlsKey} hasExisting={existing?.hasTlsConfigured ?? false} multiline secret error={tlsKeyError} onChange={setTlsKey} />
          </>
        )}

        {isSsh && (
          <>
            <div className="service-modal-field">
              <Label className="service-modal-label">SSH host</Label>
              <Input
                placeholder="vps.example.com"
                value={form.sshHost}
                onChange={(e) => setForm({ ...form, sshHost: e.target.value })}
                className={cn('font-mono text-[12px]', sshHostError && 'border-destructive')}
              />
              {sshHostError && <p className="service-modal-field-error">{sshHostError}</p>}
            </div>

            <div className="service-modal-field">
              <Label className="service-modal-label">SSH port</Label>
              <Input
                type="number"
                min={1}
                max={65535}
                placeholder="22"
                value={form.sshPort}
                onChange={(e) => setForm({ ...form, sshPort: e.target.value })}
                className={cn('font-mono text-[12px]', sshPortError && 'border-destructive')}
              />
              {sshPortError && <p className="service-modal-field-error">{sshPortError}</p>}
            </div>

            <div className="service-modal-field">
              <Label className="service-modal-label">SSH username</Label>
              <Input
                placeholder="docker"
                value={form.sshUsername}
                onChange={(e) => setForm({ ...form, sshUsername: e.target.value })}
                className={cn('font-mono text-[12px]', sshUsernameError && 'border-destructive')}
              />
              {sshUsernameError && <p className="service-modal-field-error">{sshUsernameError}</p>}
            </div>

            <div className="service-modal-field docker-section-field-full">
              <Label className="service-modal-label">Remote socket path</Label>
              <Input
                placeholder="/var/run/docker.sock"
                value={form.sshRemoteSocketPath}
                onChange={(e) => setForm({ ...form, sshRemoteSocketPath: e.target.value })}
                className={cn('font-mono text-[12px]', sshRemoteSocketError && 'border-destructive')}
              />
              {sshRemoteSocketError && <p className="service-modal-field-error">{sshRemoteSocketError}</p>}
              <p className="text-xs text-[var(--muted-foreground)] mt-1">
                Defaults to <code>/var/run/docker.sock</code>. Use <code>/run/user/&lt;uid&gt;/docker.sock</code> for rootless Docker.
              </p>
            </div>

            <SecretFieldRow
              label="SSH private key (PEM)"
              field={sshPrivateKey}
              hasExisting={existing?.hasSshPrivateKey ?? false}
              multiline
              secret
              error={sshPrivateKeyError}
              onChange={setSshPrivateKey}
            />
            <SecretFieldRow
              label="Private key passphrase (optional)"
              field={sshPassphrase}
              hasExisting={existing?.hasSshPrivateKeyPassphrase ?? false}
              secret
              error={sshPassphraseError}
              onChange={setSshPassphrase}
            />
            <div className="service-modal-field docker-section-field-full">
              <p className="text-xs text-[var(--muted-foreground)]">
                Stashboard opens an SSH connection per check and bridges <code>docker system dial-stdio</code> on
                the remote host. Make sure the SSH user is in the <code>docker</code> group (or has equivalent
                permission on the socket).
              </p>
            </div>

            <div className="service-modal-field docker-section-field-full">
              <label className="service-modal-checkbox-label service-modal-label">
                <input
                  type="checkbox"
                  checked={form.allowHostShell}
                  onChange={(e) => setForm({ ...form, allowHostShell: e.target.checked })}
                />
                Allow host terminal (interactive SSH shell on the host)
              </label>
              <p className="text-xs text-[var(--muted-foreground)] mt-1">
                Opens a full shell on the Docker host from the container modal's <strong>Terminal</strong> tab.
                This is host-level remote access — every session is audited. Off by default; the server operator
                must also enable it globally (<code>Stashboard:AllowHostShell</code>). Only applies to SSH connections.
              </p>
            </div>
          </>
        )}

        {/* V5.7 — container-exec opt-in. Applies to all host types (it goes
            through the daemon, not SSH). */}
        <div className="service-modal-field docker-section-field-full">
          <label className="service-modal-checkbox-label service-modal-label">
            <input
              type="checkbox"
              checked={form.allowExec}
              onChange={(e) => setForm({ ...form, allowExec: e.target.checked })}
            />
            Allow container exec (interactive shell inside a container)
          </label>
          <p className="text-xs text-[var(--muted-foreground)] mt-1">
            Opens a shell <strong>inside</strong> a running container from the container modal's <strong>Exec</strong> tab,
            via the Docker daemon's <code>exec</code> API — works for any host type. Every session is audited. Off by
            default; the server operator must also enable it globally (Settings → Container exec).
          </p>
        </div>

        {/* V5.5 — image-prune participation. Applies to all host types. */}
        <div className="service-modal-field docker-section-field-full">
          <label className="service-modal-checkbox-label service-modal-label">
            <input
              type="checkbox"
              checked={form.allowImagePrune}
              onChange={(e) => setForm({ ...form, allowImagePrune: e.target.checked })}
            />
            Participate in scheduled image prune
          </label>
          <p className="text-xs text-[var(--muted-foreground)] mt-1">
            On the configured interval, Stashboard runs <code>docker image prune</code> on this host to remove
            dangling <code>&lt;none&gt;:&lt;none&gt;</code> images left behind by container updates. The manual
            "Prune now" button on the Docker page always works regardless of this toggle.
          </p>
          <label className="service-modal-checkbox-label service-modal-label mt-2">
            <input
              type="checkbox"
              checked={form.pruneUnusedImages}
              onChange={(e) => setForm({ ...form, pruneUnusedImages: e.target.checked })}
              disabled={!form.allowImagePrune}
            />
            Also prune unused images (aggressive)
          </label>
          <p className="text-xs text-[var(--muted-foreground)] mt-1">
            Also removes any image not referenced by a running or stopped container. Off by default — can break
            "rollback to previous tag" workflows.
          </p>
        </div>

        {pingResult && (
          <div className="service-modal-field docker-section-field-full">
            <div className="docker-test-result">
              <span className={pingResult.ok ? 'docker-test-result-ok' : 'docker-test-result-fail'}>
                {pingResult.ok ? '✓' : '✗'}
              </span>
              <span>{pingResult.ok ? 'Docker host reachable' : 'Docker host unreachable'}</span>
              {pingResult.error && <div className="docker-test-result-error">{pingResult.error}</div>}
            </div>
          </div>
        )}
      </div>

      <div className="docker-status-row">
        <Button type="button" variant="outline" size="sm" onClick={testNow} disabled={ping.isPending}>
          Test connection
        </Button>
        <div className="docker-status-row-actions">
          {existing && (
            <Button type="button" variant="ghost" size="sm" onClick={onDelete} disabled={remove.isPending}>
              <Trash2 className="h-3.5 w-3.5" />
              Delete
            </Button>
          )}
          <Button type="button" variant="ghost" size="sm" onClick={onCancel}>Cancel</Button>
          <Button type="button" size="sm" onClick={save} disabled={create.isPending || update.isPending}>
            {create.isPending || update.isPending ? 'Saving…' : existing ? 'Save changes' : 'Save connection'}
          </Button>
        </div>
      </div>
    </div>
  )
}

// ── Per-watch form ─────────────────────────────────────────────────────────

interface FormState {
  label: string
  enabled: boolean
  imageReference: string
  containerName: string
  updateNotificationsEnabled: boolean
  telegramNotificationsEnabled: boolean
  /** V2.2 schedule mode. */
  scheduleType: 'Hourly' | 'Daily' | 'Weekly'
  /** Hours between checks for Hourly mode. */
  checkEveryHours: number
  /** Local-time "HH:mm" for Daily / Weekly. Converted to UTC on save. */
  checkAtTimeLocal: string
  /** Day-of-week (0=Sunday … 6=Saturday) for Weekly mode. */
  checkOnDayOfWeek: DayOfWeek
  /** Empty string = "no filter" (sent to the API as null). */
  tagPatternFilter: string
  /** V2.4 — chosen registry authentication strategy. */
  registryAuthType: 'Auto' | 'Basic' | 'AwsEcr'
  /** V2.4 — AWS region for ECR (plaintext). */
  awsRegion: string
  /** V3.6 — optional service to link this container to. '' = standalone. */
  webResourceId: string
}

/** Allowed values for the Hourly dropdown — must match the backend's
 *  `CheckScheduleEvaluator.AllowedHourlyValues`. */
const HOURLY_PRESETS = [1, 2, 4, 6, 12, 24] as const

const DAY_LABELS: readonly { value: DayOfWeek; label: string }[] = [
  { value: 1, label: 'Monday' },
  { value: 2, label: 'Tuesday' },
  { value: 3, label: 'Wednesday' },
  { value: 4, label: 'Thursday' },
  { value: 5, label: 'Friday' },
  { value: 6, label: 'Saturday' },
  { value: 0, label: 'Sunday' },
]

/** Preset .NET regexes for the V2.1 tag-pattern filter. Keep these in sync
 *  with the user guide examples. */
const TAG_FILTER_PRESETS: { label: string; pattern: string }[] = [
  { label: 'SemVer only (vMAJOR.MINOR.PATCH)', pattern: '^v?\\d+\\.\\d+\\.\\d+$' },
  { label: 'Stable only (no pre-release/rolling tags)', pattern: '(?i)^(?!.*-(rc|beta|alpha|dev|snapshot|pre|preview|canary|next|nightly|edge)).*$' },
  { label: 'Tag equals "stable"', pattern: '^stable$' },
]

interface SecretField {
  action: SecretValueAction
  value: string
  reveal: boolean
}

const emptyForm: FormState = {
  label: '',
  enabled: true,
  imageReference: '',
  containerName: '',
  updateNotificationsEnabled: true,
  telegramNotificationsEnabled: false,
  scheduleType: 'Hourly',
  checkEveryHours: 24,
  checkAtTimeLocal: '08:00',
  checkOnDayOfWeek: 1,
  tagPatternFilter: '',
  registryAuthType: 'Auto',
  awsRegion: '',
  webResourceId: '',
}

export interface DockerWatchFormProps {
  /** The Docker host connection this container runs on. */
  connectionId: string
  existing: DockerWatch | null
  /** V3.6 — default service to link a NEW watch to (e.g. when opened from a
   *  service context). Ignored when editing an existing watch (its own link
   *  wins). */
  defaultServiceId?: string | null
  /** V3.6 — prefill the container name for a NEW watch (e.g. when "Track this
   *  container" is clicked from a card). */
  defaultContainerName?: string
  /** V3.6 — prefill the image reference for a NEW watch. */
  defaultImageReference?: string
  onCheckNow?: () => void | Promise<void>
  onDelete?: () => void | Promise<void>
  onCancelNew?: () => void
  onCreated?: () => void
  checkInFlight: boolean
  /** When true, the embedded Inspect/Logs/Stats panels are not rendered.
   *  Used by the container modal Watch tab where those panels live in their
   *  own dedicated tabs. */
  hideContainerPanels?: boolean
}

export function DockerWatchForm({
  connectionId, existing, defaultServiceId = null,
  defaultContainerName = '', defaultImageReference = '',
  onCheckNow, onDelete, onCancelNew, onCreated, checkInFlight,
  hideContainerPanels = false,
}: DockerWatchFormProps) {
  const create = useCreateConnectionWatch(connectionId)
  const update = useUpdateConnectionWatch(connectionId)
  const testWatch = useTestConnectionWatch(connectionId)
  const servicesQuery = useServices()
  const telegramSettings = useTelegramSettings()
  const telegramConfigured = Boolean(
    telegramSettings.data
    && telegramSettings.data.botToken
    && telegramSettings.data.chatId
    && telegramSettings.data.notificationsEnabled
  )

  const [form, setForm] = useState<FormState>(() => existing
    ? {
        label: existing.label,
        enabled: existing.enabled,
        imageReference: existing.imageReference,
        containerName: existing.containerName,
        updateNotificationsEnabled: existing.updateNotificationsEnabled,
        telegramNotificationsEnabled: existing.telegramNotificationsEnabled,
        scheduleType: resolveScheduleType(existing.scheduleType),
        checkEveryHours: existing.checkEveryHours,
        checkAtTimeLocal: utcTimeToLocalHHmm(existing.checkAtTime) ?? '08:00',
        checkOnDayOfWeek: (existing.checkOnDayOfWeek ?? 1) as DayOfWeek,
        tagPatternFilter: existing.tagPatternFilter ?? '',
        registryAuthType: resolveRegistryAuthType(existing.registryAuthType),
        awsRegion: existing.awsRegion ?? '',
        webResourceId: existing.webResourceId ?? '',
      }
    : {
        ...emptyForm,
        webResourceId: defaultServiceId ?? '',
        containerName: defaultContainerName,
        imageReference: defaultImageReference,
        label: defaultContainerName,
      })
  const [user, setUser] = useState<SecretField>(() => existingSecret(existing?.hasRegistryCredentials ?? false))
  const [password, setPassword] = useState<SecretField>(() => existingSecret(existing?.hasRegistryCredentials ?? false))
  const [gitHubPat, setGitHubPat] = useState<SecretField>(() => existingSecret(existing?.hasGitHubPat ?? false))
  const [awsAccessKeyId, setAwsAccessKeyId] = useState<SecretField>(() => existingSecret(existing?.hasAwsCredentials ?? false))
  const [awsSecretAccessKey, setAwsSecretAccessKey] = useState<SecretField>(() => existingSecret(existing?.hasAwsCredentials ?? false))
  const [error, setError] = useState<string | null>(null)
  const [fieldErrors, setFieldErrors] = useState<Record<string, string>>({})
  const [testResult, setTestResult] = useState<{ host: boolean; container: boolean; registry: boolean; error: string | null } | null>(null)

  const buildUpsertPayload = (): DockerWatchUpsert => {
    const utcTime = form.scheduleType === 'Hourly' ? null : localHHmmToUtcTime(form.checkAtTimeLocal)
    const isAwsEcr = form.registryAuthType === 'AwsEcr'
    return {
      label: form.label.trim(),
      enabled: form.enabled,
      imageReference: form.imageReference.trim(),
      containerName: form.containerName.trim(),
      registryUsername: toUpsert(user),
      registryPassword: toUpsert(password),
      gitHubPat: toUpsert(gitHubPat),
      registryAuthType: form.registryAuthType,
      awsAccessKeyId: isAwsEcr ? toUpsert(awsAccessKeyId) : null,
      awsSecretAccessKey: isAwsEcr ? toUpsert(awsSecretAccessKey) : null,
      awsRegion: isAwsEcr ? (form.awsRegion.trim() || null) : null,
      updateNotificationsEnabled: form.updateNotificationsEnabled,
      telegramNotificationsEnabled: telegramConfigured && form.telegramNotificationsEnabled,
      scheduleType: form.scheduleType,
      checkEveryHours: form.checkEveryHours,
      checkAtTime: utcTime,
      checkOnDayOfWeek: form.scheduleType === 'Weekly' ? form.checkOnDayOfWeek : null,
      tagPatternFilter: form.tagPatternFilter.trim() || null,
      webResourceId: form.webResourceId || null,
    }
  }

  const save = async () => {
    setError(null)
    setFieldErrors({})
    setTestResult(null)
    const payload = buildUpsertPayload()
    if (!payload.label) {
      setError('Label is required — use something short like "app" or "db".')
      return
    }
    try {
      if (existing) {
        await update.mutateAsync({ watchId: existing.id, data: payload })
      } else {
        await create.mutateAsync(payload)
        onCreated?.()
      }
    } catch (e: unknown) {
      const { fieldErrors: fe, globalError } = parseApiErrors(e)
      setFieldErrors(fe)
      setError(globalError ?? (Object.keys(fe).length === 0 ? 'Failed to save Docker watch' : null))
    }
  }

  const onTestConnection = async () => {
    setError(null)
    setFieldErrors({})
    setTestResult(null)
    try {
      const isAwsEcr = form.registryAuthType === 'AwsEcr'
      const result = await testWatch.mutateAsync({
        watchId: existing?.id,
        data: {
          imageReference: form.imageReference.trim(),
          containerName: form.containerName.trim(),
          registryUsername: toUpsert(user),
          registryPassword: toUpsert(password),
          gitHubPat: toUpsert(gitHubPat),
          registryAuthType: form.registryAuthType,
          awsAccessKeyId: isAwsEcr ? toUpsert(awsAccessKeyId) : null,
          awsSecretAccessKey: isAwsEcr ? toUpsert(awsSecretAccessKey) : null,
          awsRegion: isAwsEcr ? (form.awsRegion.trim() || null) : null,
          tagPatternFilter: form.tagPatternFilter.trim() || null,
        },
      })
      setTestResult({
        host: result.dockerHostReachable,
        container: result.containerFound,
        registry: result.registryReachable,
        error: result.error,
      })
    } catch (e: unknown) {
      const { fieldErrors: fe, globalError } = parseApiErrors(e)
      setFieldErrors(fe)
      setError(globalError ?? (Object.keys(fe).length === 0 ? 'Test failed' : null))
    }
  }

  const labelError = pickFieldError(fieldErrors, 'label')
  const imageReferenceError = pickFieldError(fieldErrors, 'imagereference', 'imageReference')
  const scheduleError = pickFieldError(fieldErrors, 'scheduletype', 'scheduleType',
    'checkeveryhours', 'checkEveryHours',
    'checkattime', 'checkAtTime',
    'checkondayofweek', 'checkOnDayOfWeek')
  const registryUserError = pickFieldError(fieldErrors, 'registryusername', 'registryUsername')
  const registryPasswordError = pickFieldError(fieldErrors, 'registrypassword', 'registryPassword')
  const gitHubPatError = pickFieldError(fieldErrors, 'githubpat', 'gitHubPat')
  const awsAccessKeyError = pickFieldError(fieldErrors, 'awsaccesskeyid', 'awsAccessKeyId')
  const awsSecretAccessError = pickFieldError(fieldErrors, 'awssecretaccesskey', 'awsSecretAccessKey')
  const awsRegionError = pickFieldError(fieldErrors, 'awsregion', 'awsRegion')
  const tagFilterError = pickFieldError(fieldErrors, 'tagpatternfilter', 'tagPatternFilter')

  // V2.3 — GitHub PAT only makes sense for GHCR-hosted images. Hide the field
  // entirely for other registries so the form doesn't grow noise. Parse from
  // the live form input so the field appears as soon as the user types a
  // ghcr.io reference.
  const isGhcr = /^\s*ghcr\.io\b/i.test(form.imageReference) || form.imageReference.startsWith('ghcr.io/')
  // V2.4 — auto-promote to ECR when the user types a private ECR hostname
  // and hasn't explicitly picked another strategy. The backend does the same
  // promotion at save time; mirroring it here keeps the UI honest.
  const ecrHostMatch = form.imageReference.match(/^\s*(\d{12})\.dkr\.ecr\.([a-z]{2}-[a-z]+-\d+)\.amazonaws\.com/i)
  const isAwsEcr = form.registryAuthType === 'AwsEcr'
    || (form.registryAuthType === 'Auto' && ecrHostMatch !== null)

  return (
    <div className="docker-watch-form">
      {existing && (
        <>
          <StatusPanel watch={existing} />
          {!hideContainerPanels && <InspectPanel connectionId={connectionId} watch={existing} />}
          {!hideContainerPanels && <StatsPanel connectionId={connectionId} watch={existing} />}
          {!hideContainerPanels && <LogsPanel connectionId={connectionId} watch={existing} />}
          <WebhookPanel connectionId={connectionId} watch={existing} />
          <UpdateHistoryPanel connectionId={connectionId} watch={existing} />
          <div className="docker-status-row">
            <span>Last checked: {formatRelative(existing.lastCheckedUtc)}</span>
            <div className="docker-status-row-actions">
              <UpdateNowButton connectionId={connectionId} watch={existing} />
              {onCheckNow && (
                <Button type="button" variant="outline" size="sm" onClick={() => void onCheckNow()} disabled={checkInFlight}>
                  <RefreshCw className={cn('h-3.5 w-3.5', checkInFlight && 'animate-spin')} />
                  Check now
                </Button>
              )}
              {onDelete && (
                <Button type="button" variant="ghost" size="icon" onClick={() => void onDelete()} aria-label="Remove this watch">
                  <Trash2 className="h-3.5 w-3.5" />
                </Button>
              )}
            </div>
          </div>
        </>
      )}

      {error && <p className="service-modal-error">{error}</p>}

      <div className="docker-section-grid">
        <div className="service-modal-field">
          <Label className="service-modal-label">
            Label <span className="service-modal-label-help">(e.g. app, db)</span>
          </Label>
          <Input
            placeholder="app"
            value={form.label}
            onChange={(e) => setForm({ ...form, label: e.target.value })}
            className={labelError ? 'border-destructive' : ''}
          />
          {labelError && <p className="service-modal-field-error">{labelError}</p>}
        </div>

        <div className="service-modal-field">
          <Label className="service-modal-label">
            Linked service <span className="service-modal-label-help">(optional)</span>
            <span
              title="Link this container to a service so its update status shows on the dashboard. Leave as standalone to track it on its own."
              className="docker-label-help"
            >
              <HelpCircle className="h-3 w-3 inline" />
            </span>
          </Label>
          <select
            className="service-modal-select"
            value={form.webResourceId}
            onChange={(e) => setForm({ ...form, webResourceId: e.target.value })}
          >
            <option value="">— standalone container —</option>
            {(servicesQuery.data ?? []).map((s) => (
              <option key={s.id} value={s.id}>{s.name}</option>
            ))}
          </select>
        </div>

        <div className="service-modal-field docker-section-field-full">
          <Label className="service-modal-label">Image reference</Label>
          <Input
            placeholder="ghcr.io/owner/repo:tag"
            value={form.imageReference}
            onChange={(e) => setForm({ ...form, imageReference: e.target.value })}
            className={cn('font-mono text-[12px]', imageReferenceError && 'border-destructive')}
          />
          {imageReferenceError && <p className="service-modal-field-error">{imageReferenceError}</p>}
        </div>

        <SecretFieldRow
          label="Registry username (optional)"
          tooltip="Credentials for private registries (private GHCR, Docker Hub private repos, AWS ECR, etc). Leave blank for public images."
          field={user}
          hasExisting={existing?.hasRegistryCredentials ?? false}
          error={registryUserError}
          onChange={setUser}
        />
        <SecretFieldRow
          label="Registry password / PAT (optional)"
          tooltip="Credentials for private registries (private GHCR, Docker Hub private repos, AWS ECR, etc). Leave blank for public images."
          field={password}
          hasExisting={existing?.hasRegistryCredentials ?? false}
          secret
          error={registryPasswordError}
          onChange={setPassword}
        />

        <div className="service-modal-field docker-section-field-full">
          <Label className="service-modal-label">
            Registry type
            <span
              title="V2.4 — pick how Stashboard should authenticate to the registry. Auto handles Docker Hub + GHCR (anonymous → Bearer). Basic is for Nexus / Gitea Packages and similar registries that only accept HTTP Basic. AWS ECR resolves a temporary Basic token from IAM at check time."
              className="docker-label-help"
            >
              <HelpCircle className="h-3 w-3 inline" />
            </span>
          </Label>
          <select
            className="service-modal-select"
            value={form.registryAuthType}
            onChange={(e) => setForm({ ...form, registryAuthType: e.target.value as 'Auto' | 'Basic' | 'AwsEcr' })}
          >
            <option value="Auto">Auto (Docker Hub / GHCR / generic OCI)</option>
            <option value="Basic">HTTP Basic (Nexus / Gitea Packages)</option>
            <option value="AwsEcr">AWS Elastic Container Registry</option>
          </select>
          {form.registryAuthType === 'Auto' && ecrHostMatch && (
            <p className="service-modal-help">
              Detected an AWS ECR hostname — switch to <strong>AWS Elastic Container Registry</strong> to authenticate via IAM.
            </p>
          )}
        </div>

        {isAwsEcr && (
          <>
            <SecretFieldRow
              label="AWS access key id"
              tooltip="V2.4 — IAM access key id with permission to call ECR GetAuthorizationToken (the AmazonEC2ContainerRegistryReadOnly managed policy is the minimum). Stored encrypted at rest."
              field={awsAccessKeyId}
              hasExisting={existing?.hasAwsCredentials ?? false}
              error={awsAccessKeyError}
              onChange={setAwsAccessKeyId}
            />
            <SecretFieldRow
              label="AWS secret access key"
              tooltip="V2.4 — IAM secret access key paired with the access key id above. Stored encrypted at rest."
              field={awsSecretAccessKey}
              hasExisting={existing?.hasAwsCredentials ?? false}
              secret
              error={awsSecretAccessError}
              onChange={setAwsSecretAccessKey}
            />
            <div className="service-modal-field">
              <Label className="service-modal-label">
                AWS region
                <span
                  title="V2.4 — AWS region the ECR registry lives in (e.g. eu-central-1). Auto-filled from the registry hostname when you leave this blank."
                  className="docker-label-help"
                >
                  <HelpCircle className="h-3 w-3 inline" />
                </span>
              </Label>
              <Input
                placeholder={ecrHostMatch?.[2] ?? 'eu-central-1'}
                value={form.awsRegion}
                onChange={(e) => setForm({ ...form, awsRegion: e.target.value })}
                className={cn('font-mono text-[12px]', awsRegionError && 'border-destructive')}
              />
              {awsRegionError && <p className="service-modal-field-error">{awsRegionError}</p>}
            </div>
          </>
        )}

        <div className="docker-release-schedule-grid docker-section-field-full">
          <div className="service-modal-field">
            <Label className="service-modal-label">Check schedule</Label>
            <SchedulePicker
              form={form}
              onChange={(patch) => setForm({ ...form, ...patch })}
              nextCheckUtc={existing?.nextCheckUtc ?? null}
            />
            {scheduleError && <p className="service-modal-field-error">{scheduleError}</p>}
          </div>

          <div className="docker-release-settings-stack">
            {isGhcr && (
              <SecretFieldRow
                label="GitHub PAT for release notes (optional)"
                tooltip="V2.3 — GitHub personal access token used to fetch release notes from the upstream GitHub repository. Public repos work without a PAT but anonymous calls are limited to 60 requests / hour. A PAT raises the ceiling to 5 000 / hour and is required for private repos. Only the public_repo / repo scope is needed."
                field={gitHubPat}
                hasExisting={existing?.hasGitHubPat ?? false}
                secret
                error={gitHubPatError}
                onChange={setGitHubPat}
              />
            )}

            <div className="service-modal-field">
              <Label className="service-modal-label">
                Tag pattern filter (optional)
                <span
                  title="Restrict which registry tags count as &quot;latest&quot;. Useful when the upstream publishes pre-release tags (-rc, -beta) alongside the stable track. Leave blank to compare the pinned tag's digest directly. Accepts a .NET regex."
                  className="docker-label-help"
                >
                  <HelpCircle className="h-3 w-3 inline" />
                </span>
              </Label>
              <div className="docker-secret-row">
                <select
                  value={form.tagPatternFilter}
                  onChange={(e) => setForm({ ...form, tagPatternFilter: e.target.value })}
                >
                  <option value="">— no filter —</option>
                  {TAG_FILTER_PRESETS.map((p) => (
                    <option key={p.pattern} value={p.pattern}>{p.label}</option>
                  ))}
                  {form.tagPatternFilter
                    && !TAG_FILTER_PRESETS.some((p) => p.pattern === form.tagPatternFilter)
                    && <option value={form.tagPatternFilter}>Custom…</option>}
                </select>
                <Input
                  placeholder="^v\\d+\\.\\d+\\.\\d+$"
                  value={form.tagPatternFilter}
                  onChange={(e) => setForm({ ...form, tagPatternFilter: e.target.value })}
                  className={cn('font-mono text-[12px]', tagFilterError && 'border-destructive')}
                />
              </div>
              {tagFilterError && <p className="service-modal-field-error">{tagFilterError}</p>}
            </div>
          </div>
        </div>

        <div className="service-modal-field service-modal-field-full">
          <label className="service-modal-checkbox-label service-modal-label">
            <input
              type="checkbox"
              checked={form.enabled}
              onChange={(e) => setForm({ ...form, enabled: e.target.checked })}
            />
            Enabled
          </label>
          <label className="service-modal-checkbox-label service-modal-label">
            <input
              type="checkbox"
              checked={form.updateNotificationsEnabled}
              onChange={(e) => setForm({ ...form, updateNotificationsEnabled: e.target.checked })}
            />
            Email me when an update is available
          </label>
          <label
            className="service-modal-checkbox-label service-modal-label"
            title={!telegramConfigured ? 'Configure Telegram in Account → Notifications first.' : undefined}
          >
            <input
              type="checkbox"
              checked={telegramConfigured && form.telegramNotificationsEnabled}
              disabled={!telegramConfigured}
              onChange={(e) => setForm({ ...form, telegramNotificationsEnabled: e.target.checked })}
            />
            Send a Telegram message when an update is available
          </label>
          {!telegramConfigured && (
            <p className="service-modal-help">
              Telegram is not configured yet. Set your bot token and chat ID on the
              Account page and enable notifications there to use this channel.
            </p>
          )}
        </div>

        {form.containerName && (
          <div className="service-modal-field service-modal-field-full">
            <UpdateCommandPanel connectionId={connectionId} containerName={form.containerName} />
          </div>
        )}

        {testResult && (
          <div className="service-modal-field service-modal-field-full">
            <div className="docker-test-result">
              <span className={testResult.host ? 'docker-test-result-ok' : 'docker-test-result-fail'}>
                {testResult.host ? '✓' : '✗'}
              </span>
              <span>Docker host reachable</span>
              <span className={testResult.container ? 'docker-test-result-ok' : 'docker-test-result-fail'}>
                {testResult.container ? '✓' : '✗'}
              </span>
              <span>Container found</span>
              <span className={testResult.registry ? 'docker-test-result-ok' : 'docker-test-result-fail'}>
                {testResult.registry ? '✓' : '✗'}
              </span>
              <span>Registry reachable</span>
              {testResult.error && <div className="docker-test-result-error">{testResult.error}</div>}
            </div>
          </div>
        )}
      </div>

      <div className="docker-status-row">
        <Button type="button" variant="outline" size="sm" onClick={onTestConnection} disabled={testWatch.isPending}>
          Test connection
        </Button>
        <div className="docker-status-row-actions">
          {onCancelNew && (
            <Button type="button" variant="ghost" size="sm" onClick={onCancelNew}>Cancel</Button>
          )}
          <Button type="button" size="sm" onClick={save} disabled={create.isPending || update.isPending}>
            {(create.isPending || update.isPending) ? 'Saving…' : existing ? 'Save changes' : 'Add container'}
          </Button>
        </div>
      </div>
    </div>
  )
}

// ── Update command panel ───────────────────────────────────────────────────

function UpdateCommandPanel({ connectionId, containerName }: { connectionId: string; containerName: string }) {
  const query = useDockerUpdateCommand(connectionId, containerName)
  const [copied, setCopied] = useState(false)

  const copy = () => {
    if (!query.data) return
    void navigator.clipboard.writeText(query.data.command).then(() => {
      setCopied(true)
      setTimeout(() => setCopied(false), 1500)
    })
  }

  if (query.isLoading) {
    return (
      <>
        <Label className="service-modal-label">Update command</Label>
        <p className="text-sm text-[var(--muted-foreground)]">Generating from daemon…</p>
      </>
    )
  }

  if (query.error) {
    return (
      <>
        <Label className="service-modal-label">Update command</Label>
        <p className="docker-test-result-error">{extractError(query.error) ?? 'Failed to generate command'}</p>
      </>
    )
  }

  if (!query.data) return null

  const sourceLabel = query.data.source === 'compose'
    ? 'Generated from docker compose labels'
    : 'Approximate — container not managed by docker compose'

  return (
    <>
      <Label className="service-modal-label">
        Update command <span className="service-modal-label-help">({sourceLabel})</span>
      </Label>
      <div className="docker-copy-command">
        <pre>{query.data.command}</pre>
        <button type="button" className="service-modal-copy-btn" onClick={copy} title="Copy command">
          {copied ? <Check className="h-3.5 w-3.5 service-modal-copy-success" /> : <Copy className="h-3.5 w-3.5" />}
        </button>
      </div>
      {query.data.warning && (
        <p className="service-modal-help docker-test-result-error">
          <AlertCircle className="h-3.5 w-3.5 inline" /> {query.data.warning}
        </p>
      )}
    </>
  )
}

// ── helpers ─────────────────────────────────────────────────────────────────

/**
 * Renders a digest row: the tag the digest belongs to (e.g. `v2.0.0`) as a
 * chip, followed by the `sha256:…` value. Falls back to an em dash when the
 * digest hasn't been resolved yet.
 */
function DigestValue({ tag, digest }: { tag: string | null; digest: string | null }) {
  if (!digest) return <>—</>
  return (
    <>
      {tag && <span className="docker-digest-tag">{tag}</span>}
      {digest}
    </>
  )
}

function StatusPanel({ watch }: { watch: DockerWatch }) {
  return (
    <>
      <dl className="docker-status-panel">
        <dt>Current digest</dt>
        <dd><DigestValue tag={watch.currentVersionTag} digest={watch.currentDigest} /></dd>
        <dt>Latest digest</dt>
        <dd><DigestValue tag={watch.latestVersionTag} digest={watch.latestDigest} /></dd>
        {watch.lastError && (
          <>
            <dt>Last error</dt>
            <dd className="docker-test-result-error">{watch.lastError}</dd>
          </>
        )}
      </dl>
      {(watch.latestReleaseUrl || watch.latestReleaseBody) && (
        <ReleaseNotesPanel
          url={watch.latestReleaseUrl}
          body={watch.latestReleaseBody}
          tag={watch.latestVersionTag}
        />
      )}
    </>
  )
}

/**
 * V2.6 — webhook configuration panel. Collapsed by default; expands to
 * show either "enable" (generate token) or the live URL + rotate / disable
 * controls. The URL is composed client-side from `window.location.origin`
 * + the token so the user can self-serve without needing the server to
 * know its public base URL.
 */
function WebhookPanel({ connectionId, watch }: { connectionId: string; watch: DockerWatch }) {
  const [open, setOpen] = useState(false)
  const [revealed, setRevealed] = useState(false)
  const [copied, setCopied] = useState(false)
  const rotate = useRotateConnectionWebhook(connectionId)
  const remove = useDeleteConnectionWebhook(connectionId)

  const hasToken = watch.webhookToken !== null
  const webhookUrl = hasToken
    ? `${window.location.origin}/api/docker/webhooks/${watch.webhookToken}`
    : null

  const generate = async () => {
    try {
      await rotate.mutateAsync(watch.id)
      setRevealed(true)
      setOpen(true)
    } catch { /* surfaced via mutation error elsewhere */ }
  }

  const onRotate = async () => {
    if (!confirm('Rotate the webhook URL? The current URL will stop working immediately.')) return
    try {
      await rotate.mutateAsync(watch.id)
      setRevealed(true)
    } catch { /* swallow */ }
  }

  const onDisable = async () => {
    if (!confirm('Disable webhook delivery? The current URL will stop working immediately.')) return
    try { await remove.mutateAsync(watch.id) }
    catch { /* swallow */ }
  }

  const copy = () => {
    if (!webhookUrl) return
    void navigator.clipboard.writeText(webhookUrl).then(() => {
      setCopied(true)
      setTimeout(() => setCopied(false), 1500)
    })
  }

  return (
    <div className="docker-release-panel">
      <button
        type="button"
        className="docker-release-panel-header"
        onClick={() => setOpen((v) => !v)}
        aria-expanded={open}
      >
        {open ? <ChevronDown className="h-3.5 w-3.5" /> : <ChevronRight className="h-3.5 w-3.5" />}
        <span>Webhook receiver{hasToken ? ' — enabled' : ''}</span>
        {hasToken && watch.lastWebhookReceivedUtc && (
          <span className="service-modal-label-help">
            last hit {formatRelative(watch.lastWebhookReceivedUtc)}
          </span>
        )}
      </button>
      {open && !hasToken && (
        <div className="docker-release-panel-body">
          <p className="service-modal-help">
            Generate a public URL your registry can POST to whenever this image
            is pushed. Stashboard re-checks the watch within seconds — no
            waiting for the schedule. The URL contains a 256-bit secret token;
            anyone who has it can trigger a re-check (no other action) until
            you rotate or disable it.
          </p>
          <div className="docker-status-row-actions">
            <Button type="button" size="sm" onClick={() => void generate()} disabled={rotate.isPending}>
              {rotate.isPending ? 'Generating…' : 'Generate webhook URL'}
            </Button>
          </div>
        </div>
      )}
      {open && hasToken && webhookUrl && (
        <div className="docker-release-panel-body">
          <Label className="service-modal-label">Webhook URL</Label>
          <div className="docker-copy-command">
            <pre>{revealed ? webhookUrl : webhookUrl.replace(/[0-9a-f]{16,}/, '••••••••••••••••••••••••••••••••')}</pre>
            <button
              type="button"
              className="service-modal-copy-btn"
              onClick={() => setRevealed((v) => !v)}
              title={revealed ? 'Hide token' : 'Reveal token'}
            >
              {revealed ? <ChevronDown className="h-3.5 w-3.5" /> : <ChevronRight className="h-3.5 w-3.5" />}
            </button>
            <button type="button" className="service-modal-copy-btn" onClick={copy} title="Copy URL">
              {copied ? <Check className="h-3.5 w-3.5 service-modal-copy-success" /> : <Copy className="h-3.5 w-3.5" />}
            </button>
          </div>
          <p className="service-modal-help">
            Paste this URL into your registry's webhook settings. For Docker Hub:
            Repository → Webhooks. For GitHub Container Registry: use a GitHub
            Actions <code>workflow_dispatch</code> step that POSTs to the URL on
            release. Self-hosted OCI registries (Harbor, Nexus) usually accept
            an arbitrary HTTP endpoint under "Webhook policies".
          </p>
          <div className="docker-status-row-actions">
            <Button type="button" variant="outline" size="sm" onClick={() => void onRotate()} disabled={rotate.isPending}>
              <RefreshCw className={cn('h-3.5 w-3.5', rotate.isPending && 'animate-spin')} />
              Rotate token
            </Button>
            <Button type="button" variant="ghost" size="sm" onClick={() => void onDisable()} disabled={remove.isPending}>
              Disable webhook
            </Button>
          </div>
        </div>
      )}
    </div>
  )
}

/**
 * V2.7 — "Update now" button. Visible whenever the watch is enabled and
 * we have a Docker connection (the back-end is the source of truth on
 * whether an update is actually available, but we keep the button live
 * for a re-pull-from-tag refresh even when status is `UpToDate`).
 * Highlighted when an update is pending. V5.2 — clicking opens a
 * confirmation dialog (warning + docs link); the recreate only runs once
 * the user confirms there, so a misclick can't recreate a production
 * container.
 */
function UpdateNowButton({ connectionId, watch }: { connectionId: string; watch: DockerWatch }) {
  const update = useUpdateConnectionWatchNow(connectionId)
  const status = resolveDockerUpdateStatus(watch.updateStatus)
  const highlight = status === 'UpdateAvailable'
  const [confirmOpen, setConfirmOpen] = useState(false)

  // Don't show the button for disabled watches — Update now has no
  // meaning when the schedule isn't running. The Enabled checkbox in
  // the form handles re-enabling.
  if (!watch.enabled) return null

  // V5.4 — runs after the user confirms in the unified UpdateProgressDialog.
  // We return the full DockerWatchUpdateResponse so the dialog can render
  // ✓ / ✗ on the per-watch row in its done phase. The backend returns 200
  // even on RecreateFailed (the attempt.status carries the outcome), so the
  // dialog's row picks up the failure from attempt.status without going
  // through the error phase.
  const confirmUpdate = () => update.mutateAsync(watch.id)

  return (
    <>
      <Button
        type="button"
        variant={highlight ? 'default' : 'outline'}
        size="sm"
        onClick={() => setConfirmOpen(true)}
        disabled={update.isPending}
        title={highlight
          ? 'Pull the new image and recreate the container'
          : 'Re-pull the configured tag and recreate the container'}
      >
        <Download className={cn('h-3.5 w-3.5', update.isPending && 'animate-pulse')} />
        {update.isPending ? 'Updating…' : 'Update now'}
      </Button>

      <UpdateConfirmDialog
        open={confirmOpen}
        imageReference={watch.imageReference}
        containerName={watch.containerName}
        updateAvailable={highlight}
        onConfirm={confirmUpdate}
        onClose={() => setConfirmOpen(false)}
      />
    </>
  )
}

/**
 * V2.7 — collapsible "Update history" accordion under the digest panel.
 * Lists the audit-log rows written by every click of "Update now",
 * newest first. Each row shows the outcome, the digest transition, the
 * timestamp, and the error string when the attempt didn't succeed.
 */
function UpdateHistoryPanel({ connectionId, watch }: { connectionId: string; watch: DockerWatch }) {
  const [open, setOpen] = useState(false)
  // Only fetch the history when the user opens the accordion — no point
  // hammering the endpoint for every watch on every modal open.
  const history = useConnectionWatchUpdates(connectionId, watch.id, open)
  const attempts = history.data ?? []
  return (
    <div className="docker-release-panel">
      <button
        type="button"
        className="docker-release-panel-header"
        onClick={() => setOpen((v) => !v)}
        aria-expanded={open}
      >
        {open ? <ChevronDown className="h-3.5 w-3.5" /> : <ChevronRight className="h-3.5 w-3.5" />}
        <span>Update history</span>
        {attempts.length > 0 && (
          <span className="service-modal-label-help">{attempts.length} {attempts.length === 1 ? 'attempt' : 'attempts'}</span>
        )}
      </button>
      {open && history.isLoading && (
        <p className="docker-release-panel-empty">Loading…</p>
      )}
      {open && !history.isLoading && attempts.length === 0 && (
        <p className="docker-release-panel-empty">No Update now attempts yet for this watch.</p>
      )}
      {open && attempts.length > 0 && (
        <ul className="docker-update-history">
          {attempts.map((a) => (
            <li key={a.id} className="docker-update-history-row">
              <div className="docker-update-history-head">
                <span
                  className="docker-section-badge"
                  data-status={resolveAttemptStatus(a.status) === 'Success' ? 'UpToDate' : 'Error'}
                >
                  {resolveAttemptStatus(a.status)}
                </span>
                {/* V3.2 — show the post-start health-verification badge
                    only for Success rows. On every other status the flag
                    is meaningless (the recreate didn't reach the
                    verification step). */}
                {resolveAttemptStatus(a.status) === 'Success' && (
                  <span
                    className="docker-section-badge"
                    data-status={a.healthVerified ? 'UpToDate' : 'Unknown'}
                    title={a.healthVerified
                      ? 'Container reported healthy within the post-start verification window.'
                      : 'Container started but health verification was skipped or did not converge.'}
                  >
                    {a.healthVerified ? 'Health verified' : 'Health unverified'}
                  </span>
                )}
                <span className="service-modal-label-help">{formatLocalDateTime(a.completedUtc)}</span>
              </div>
              {(a.previousDigest || a.newDigest) && (
                <div className="docker-update-history-digests">
                  <span title="Previous">{shortenDigest(a.previousDigest) ?? '—'}</span>
                  <span> → </span>
                  <span title="New">{shortenDigest(a.newDigest) ?? '—'}</span>
                </div>
              )}
              {a.error && <p className="docker-test-result-error">{a.error}</p>}
            </li>
          ))}
        </ul>
      )}
    </div>
  )
}

const shortenDigest = (d: string | null) => d ? d.slice(0, 18) + '…' : null

/**
 * V3.1 — collapsible "Inspect" panel. Lazy-fetches the slimmed
 * `docker inspect` payload on first expand and renders it as a set of
 * structured sub-sections (state, image, command, env, labels, ports,
 * mounts, networks, restart policy, health) plus a raw-JSON view with
 * copy-to-clipboard. Env values flagged `masked` are never rendered.
 */
function InspectPanel({ connectionId, watch }: { connectionId: string; watch: DockerWatch }) {
  const [open, setOpen] = useState(false)
  const query = useDockerInstanceInspect(connectionId, watch.containerName, open)
  return (
    <div className="docker-release-panel">
      <button
        type="button"
        className="docker-release-panel-header"
        onClick={() => setOpen((v) => !v)}
        aria-expanded={open}
      >
        {open ? <ChevronDown className="h-3.5 w-3.5" /> : <ChevronRight className="h-3.5 w-3.5" />}
        <span>Inspect container</span>
        {query.data && (
          <span className="service-modal-label-help">
            {query.data.state.status}
            {query.data.state.health && ` · ${query.data.state.health.status}`}
          </span>
        )}
      </button>
      {open && query.isLoading && (
        <p className="docker-release-panel-empty">Loading from daemon…</p>
      )}
      {open && query.error && (
        <p className="docker-release-panel-empty docker-test-result-error">
          <AlertCircle className="h-3.5 w-3.5 inline" /> {extractError(query.error) ?? 'Failed to inspect container'}
        </p>
      )}
      {open && query.data && (
        <ContainerInspectBody data={query.data} />
      )}
    </div>
  )
}


/** V2.3 — collapsible "What's new" panel rendered under the digest table
 *  when the orchestrator successfully fetched the matching GitHub release.
 *  Markdown body is rendered as monospace plain text rather than HTML to
 *  avoid pulling a markdown renderer / sanitiser dependency just for this. */
function ReleaseNotesPanel({ url, body, tag }: { url: string | null; body: string | null; tag: string | null }) {
  const [open, setOpen] = useState(false)
  return (
    <div className="docker-release-panel">
      <button
        type="button"
        className="docker-release-panel-header"
        onClick={() => setOpen((v) => !v)}
        aria-expanded={open}
      >
        {open ? <ChevronDown className="h-3.5 w-3.5" /> : <ChevronRight className="h-3.5 w-3.5" />}
        <span>What's new{tag ? ` in ${tag}` : ''}</span>
        {url && (
          <a
            href={url}
            target="_blank"
            rel="noopener noreferrer"
            className="docker-release-panel-link"
            onClick={(e) => e.stopPropagation()}
          >
            View on GitHub →
          </a>
        )}
      </button>
      {open && body && (
        <pre className="docker-release-panel-body">{body}</pre>
      )}
      {open && !body && (
        <p className="docker-release-panel-empty">No release notes published for this tag.</p>
      )}
    </div>
  )
}

interface SecretRowProps {
  label: string
  tooltip?: string
  field: SecretField
  hasExisting: boolean
  secret?: boolean
  multiline?: boolean
  error?: string | null
  onChange: (next: SecretField) => void
}

function SecretFieldRow({ label, tooltip, field, hasExisting, secret, multiline, error, onChange }: SecretRowProps) {
  return (
    <div className={cn('service-modal-field', multiline && 'docker-section-field-full')}>
      <Label className="service-modal-label">
        {label}
        {tooltip && (
          <span title={tooltip} className="docker-label-help">
            <HelpCircle className="h-3 w-3 inline" />
          </span>
        )}
      </Label>
      <div className="docker-secret-row">
        <select
          className={error ? 'border-destructive' : ''}
          value={field.action}
          onChange={(e) => onChange({ ...field, action: e.target.value as SecretValueAction, value: '' })}
        >
          {hasExisting && <option value="Keep">Keep</option>}
          <option value="Set">Set</option>
          {hasExisting && <option value="Clear">Clear</option>}
        </select>

        {field.action === 'Set' ? (
          multiline ? (
            <Textarea
              rows={3}
              value={field.value}
              onChange={(e) => onChange({ ...field, value: e.target.value })}
              placeholder={secret ? '-----BEGIN PRIVATE KEY-----' : ''}
              className={error ? 'border-destructive' : ''}
            />
          ) : (
            <Input
              type={secret && !field.reveal ? 'password' : 'text'}
              value={field.value}
              onChange={(e) => onChange({ ...field, value: e.target.value })}
              className={error ? 'border-destructive' : ''}
            />
          )
        ) : field.action === 'Keep' ? (
          <span className="docker-secret-placeholder">Using saved value</span>
        ) : (
          <span className="docker-secret-placeholder">Will be cleared on save</span>
        )}
      </div>
      {error && <p className="service-modal-field-error">{error}</p>}
    </div>
  )
}

const normalizeFieldKey = (key: string) => key.replace(/[^a-z0-9]/gi, '').toLowerCase()

const pickFieldError = (errors: Record<string, string>, ...keys: string[]) => {
  for (const key of keys) {
    if (errors[key]) return errors[key]
    if (errors[key.toLowerCase()]) return errors[key.toLowerCase()]

    const normalized = normalizeFieldKey(key)
    const found = Object.entries(errors).find(([k]) => normalizeFieldKey(k) === normalized)
    if (found?.[1]) return found[1]
  }
  return null
}

const existingSecret = (hasValue: boolean): SecretField =>
  hasValue
    ? { action: 'Keep', value: '', reveal: false }
    : { action: 'Set', value: '', reveal: false }

const toUpsert = (s: SecretField): SecretValueUpsert => ({
  action: s.action,
  value: s.action === 'Set' ? s.value : null,
})

/** Schedule picker — V2.2 segmented control. Edits the slice of FormState
 *  that maps to the backend's `scheduleType` / `checkEveryHours` /
 *  `checkAtTime` / `checkOnDayOfWeek` fields. Time inputs run in the user's
 *  local timezone for usability — conversion to/from UTC happens at the
 *  serialisation boundary. */
function SchedulePicker({
  form,
  onChange,
  nextCheckUtc,
}: {
  form: FormState
  onChange: (patch: Partial<FormState>) => void
  nextCheckUtc: string | null
}) {
  const radioName = `schedule-${useRadioId()}`
  return (
    <div className="docker-schedule-picker">
      <label className="docker-schedule-row">
        <input
          type="radio"
          name={radioName}
          checked={form.scheduleType === 'Hourly'}
          onChange={() => onChange({ scheduleType: 'Hourly' })}
        />
        <span className="docker-schedule-label">Every</span>
        <select
          className="docker-schedule-select"
          value={form.checkEveryHours}
          disabled={form.scheduleType !== 'Hourly'}
          onChange={(e) => onChange({ checkEveryHours: Number(e.target.value) })}
        >
          {HOURLY_PRESETS.map((h) => (
            <option key={h} value={h}>{h} h</option>
          ))}
        </select>
      </label>

      <label className="docker-schedule-row">
        <input
          type="radio"
          name={radioName}
          checked={form.scheduleType === 'Daily'}
          onChange={() => onChange({ scheduleType: 'Daily' })}
        />
        <span className="docker-schedule-label">Daily at</span>
        <input
          type="time"
          className="docker-schedule-time"
          value={form.checkAtTimeLocal}
          disabled={form.scheduleType === 'Hourly'}
          onChange={(e) => onChange({ checkAtTimeLocal: e.target.value })}
        />
      </label>

      <label className="docker-schedule-row">
        <input
          type="radio"
          name={radioName}
          checked={form.scheduleType === 'Weekly'}
          onChange={() => onChange({ scheduleType: 'Weekly' })}
        />
        <span className="docker-schedule-label">Weekly on</span>
        <select
          className="docker-schedule-select"
          value={form.checkOnDayOfWeek}
          disabled={form.scheduleType !== 'Weekly'}
          onChange={(e) => onChange({ checkOnDayOfWeek: Number(e.target.value) as DayOfWeek })}
        >
          {DAY_LABELS.map((d) => (
            <option key={d.value} value={d.value}>{d.label}</option>
          ))}
        </select>
        <span className="docker-schedule-label">at</span>
        <input
          type="time"
          className="docker-schedule-time"
          value={form.checkAtTimeLocal}
          disabled={form.scheduleType !== 'Weekly'}
          onChange={(e) => onChange({ checkAtTimeLocal: e.target.value })}
        />
      </label>

      <p className="docker-schedule-hint">
        {scheduleHint(form, nextCheckUtc)}
      </p>
    </div>
  )
}

let _radioCounter = 0
function useRadioId() {
  const [id] = useState(() => ++_radioCounter)
  return id
}

const resolveScheduleType = (value: CheckScheduleType): 'Hourly' | 'Daily' | 'Weekly' => {
  if (typeof value === 'number') return (['Hourly', 'Daily', 'Weekly'][value] as 'Hourly') ?? 'Hourly'
  return value
}

/** Backend returns `HH:mm:ss` (UTC). Convert to a `HH:mm` string in the
 *  user's local timezone for the time input. */
const utcTimeToLocalHHmm = (utc: string | null): string | null => {
  if (!utc) return null
  const [hh, mm] = utc.split(':')
  if (hh === undefined || mm === undefined) return null
  // Anchor on today so we get a Date that honours DST for the conversion.
  const now = new Date()
  const utcDate = new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), now.getUTCDate(),
    Number(hh), Number(mm), 0))
  return `${pad(utcDate.getHours())}:${pad(utcDate.getMinutes())}`
}

/** Local `HH:mm` → UTC `HH:mm:00` string for the API. */
const localHHmmToUtcTime = (local: string): string => {
  const [hhStr, mmStr] = local.split(':')
  const hh = Number(hhStr)
  const mm = Number(mmStr)
  if (Number.isNaN(hh) || Number.isNaN(mm)) return '00:00:00'
  const now = new Date()
  const localDate = new Date(now.getFullYear(), now.getMonth(), now.getDate(), hh, mm, 0)
  return `${pad(localDate.getUTCHours())}:${pad(localDate.getUTCMinutes())}:00`
}

const pad = (n: number) => String(n).padStart(2, '0')

const scheduleHint = (form: FormState, nextCheckUtc: string | null) => {
  if (form.scheduleType === 'Hourly') {
    return nextCheckUtc
      ? `Next check ~${formatLocalDateTime(nextCheckUtc)} (every ${form.checkEveryHours} h)`
      : `Will check every ${form.checkEveryHours} h after first save`
  }
  const utcTime = localHHmmToUtcTime(form.checkAtTimeLocal)
  if (form.scheduleType === 'Daily') {
    return nextCheckUtc
      ? `Next check ~${formatLocalDateTime(nextCheckUtc)} (daily at ${form.checkAtTimeLocal} local · ${utcTime.slice(0, 5)} UTC)`
      : `Will check daily at ${form.checkAtTimeLocal} local time (${utcTime.slice(0, 5)} UTC)`
  }
  const dayLabel = DAY_LABELS.find((d) => d.value === form.checkOnDayOfWeek)?.label ?? '?'
  return nextCheckUtc
    ? `Next check ~${formatLocalDateTime(nextCheckUtc)} (weekly on ${dayLabel} at ${form.checkAtTimeLocal} local · ${utcTime.slice(0, 5)} UTC)`
    : `Will check weekly on ${dayLabel} at ${form.checkAtTimeLocal} local time (${utcTime.slice(0, 5)} UTC)`
}

const formatLocalDateTime = (iso: string) => {
  const d = new Date(iso)
  return d.toLocaleString(undefined, {
    weekday: 'short', month: 'short', day: 'numeric',
    hour: '2-digit', minute: '2-digit',
  })
}

const formatRelative = (iso: string | null) => {
  if (!iso) return 'never'
  const diffMs = Date.now() - new Date(iso).getTime()
  if (diffMs < 60_000) return 'just now'
  if (diffMs < 3_600_000) return `${Math.floor(diffMs / 60_000)} min ago`
  if (diffMs < 86_400_000) return `${Math.floor(diffMs / 3_600_000)} h ago`
  return `${Math.floor(diffMs / 86_400_000)} d ago`
}

const extractError = (e: unknown): string | null => {
  return getApiErrorMessage(e)
}

// ── Logs / Stats — collapsible wrappers around the shared panels ──────────
//
// The bodies live in `components/docker/ContainerLogsPanel.tsx` and
// `components/docker/ContainerStatsPanel.tsx` — the only thing this file
// owns is the collapsible accordion shell so the watch card matches the
// existing "Release notes" / "Update history" panels.

function LogsPanel({ connectionId, watch }: { connectionId: string; watch: DockerWatch }) {
  const [open, setOpen] = useState(false)
  return (
    <div className="docker-release-panel">
      <button
        type="button"
        className="docker-release-panel-header"
        onClick={() => setOpen((v) => !v)}
        aria-expanded={open}
      >
        {open ? <ChevronDown className="h-3.5 w-3.5" /> : <ChevronRight className="h-3.5 w-3.5" />}
        <span>Container logs</span>
      </button>
      {open && (
        <ContainerLogsPanel
          connectionId={connectionId}
          containerName={watch.containerName}
        />
      )}
    </div>
  )
}

function StatsPanel({ connectionId, watch }: { connectionId: string; watch: DockerWatch }) {
  const [open, setOpen] = useState(false)
  return (
    <div className="docker-release-panel">
      <button
        type="button"
        className="docker-release-panel-header"
        onClick={() => setOpen((v) => !v)}
        aria-expanded={open}
      >
        {open ? <ChevronDown className="h-3.5 w-3.5" /> : <ChevronRight className="h-3.5 w-3.5" />}
        <Activity className="h-3.5 w-3.5" />
        <span>Live stats</span>
      </button>
      {open && (
        <ContainerStatsPanel
          connectionId={connectionId}
          containerName={watch.containerName}
        />
      )}
    </div>
  )
}

