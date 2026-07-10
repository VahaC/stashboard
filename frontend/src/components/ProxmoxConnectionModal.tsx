import { useState } from 'react'
import { Link } from 'react-router-dom'
import { AlertCircle, Info } from 'lucide-react'
import { Dialog, DialogContent, DialogHeader, DialogTitle, DialogDescription, DialogFooter } from '@/components/ui/dialog'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import {
  SecretFieldRow,
  existingSecret,
  pickFieldError,
  toUpsert,
  type SecretField,
} from '@/components/connections/secret-field'
import { SshCredentialFields } from '@/components/connections/SshCredentialFields'
import {
  useCreateProxmoxConnection,
  useUpdateProxmoxConnection,
  useTestProxmoxConnection,
  useRotateProxmoxWebhook,
  useDeleteProxmoxWebhook,
} from '@/lib/proxmox-queries'
import { cn, getApiErrorMessage, parseApiErrors } from '@/lib/utils'
import type {
  CheckScheduleType,
  ProxmoxConnection,
  ProxmoxConnectionUpsert,
  ProxmoxServerType,
} from '@/lib/types'
import '@/styles/service-modal.css'
import '@/styles/proxmox.css'

const HOURLY_VALUES = [1, 2, 4, 6, 12, 24]
const DAYS = ['Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday']

/** V8.1 — one per-host permission toggle inside the collapsible Permissions group:
 *  a short checkbox label plus an info icon whose tooltip carries the full detail,
 *  so the Options block no longer needs a paragraph under every checkbox. The icon
 *  sits outside the <label> so hovering / clicking it doesn't toggle the box. */
function PermRow({
  checked, onChange, label, hint,
}: { checked: boolean; onChange: (v: boolean) => void; label: string; hint: string }) {
  return (
    <div className="proxmox-perm-row">
      <label className="service-modal-checkbox-label service-modal-label">
        <input type="checkbox" checked={checked} onChange={(e) => onChange(e.target.checked)} /> {label}
      </label>
      <span className="proxmox-perm-info" title={hint} tabIndex={0} role="note" aria-label={hint}>
        <Info className="h-3.5 w-3.5" />
      </span>
    </div>
  )
}

export function ProxmoxConnectionModal({
  open,
  onOpenChange,
  connection,
}: {
  open: boolean
  onOpenChange: (open: boolean) => void
  connection: ProxmoxConnection | null
}) {
  const isEdit = connection !== null
  const create = useCreateProxmoxConnection()
  const update = useUpdateProxmoxConnection()
  const test = useTestProxmoxConnection()

  const [name, setName] = useState(connection?.name ?? '')
  const [serverType, setServerType] = useState<ProxmoxServerType>(connection?.serverType ?? 'Pve')
  const [apiBaseUrl, setApiBaseUrl] = useState(connection?.apiBaseUrl ?? 'https://')
  const [nodeName, setNodeName] = useState(connection?.nodeName ?? '')
  const [apiTokenId, setApiTokenId] = useState(connection?.apiTokenId ?? '')
  const [apiTokenSecret, setApiTokenSecret] = useState<SecretField>(() => existingSecret(connection?.hasApiTokenSecret ?? false))
  const [skipTlsVerify, setSkipTlsVerify] = useState(connection?.skipTlsVerify ?? true)

  const [sshHost, setSshHost] = useState(connection?.sshHost ?? '')
  const [sshPort, setSshPort] = useState(connection?.sshPort ? String(connection.sshPort) : '22')
  const [sshUsername, setSshUsername] = useState(connection?.sshUsername ?? 'root')
  const [sshPrivateKey, setSshPrivateKey] = useState<SecretField>(() => existingSecret(connection?.hasSshPrivateKey ?? false))
  const [sshPassphrase, setSshPassphrase] = useState<SecretField>(() => existingSecret(connection?.hasSshPrivateKeyPassphrase ?? false))

  const [scheduleType, setScheduleType] = useState<CheckScheduleType>(connection?.scheduleType ?? 'Hourly')
  const [checkEveryHours, setCheckEveryHours] = useState(connection?.checkEveryHours ?? 24)
  const [checkAtTime, setCheckAtTime] = useState(connection?.checkAtTime?.slice(0, 5) ?? '03:00')
  const [checkOnDayOfWeek, setCheckOnDayOfWeek] = useState<number>(connection?.checkOnDayOfWeek ?? 1)
  // V6.8.2 — node-modal live telemetry poll interval (seconds); blank = default 20s.
  const [telemetryPoll, setTelemetryPoll] = useState(
    connection?.telemetryPollSeconds ? String(connection.telemetryPollSeconds) : '')

  const [allowConsole, setAllowConsole] = useState(connection?.allowConsole ?? false)
  const [allowUpdates, setAllowUpdates] = useState(connection?.allowUpdates ?? false)
  const [allowDestroy, setAllowDestroy] = useState(connection?.allowDestroy ?? false)
  const [allowCreate, setAllowCreate] = useState(connection?.allowCreate ?? false)
  const [allowClone, setAllowClone] = useState(connection?.allowClone ?? false)
  const [allowRestore, setAllowRestore] = useState(connection?.allowRestore ?? false)
  const [enabled, setEnabled] = useState(connection?.enabled ?? true)
  const [emailNotify, setEmailNotify] = useState(connection?.updateNotificationsEnabled ?? true)
  const [telegramNotify, setTelegramNotify] = useState(connection?.telegramNotificationsEnabled ?? false)

  const [error, setError] = useState<string | null>(null)
  const [fieldErrors, setFieldErrors] = useState<Record<string, string>>({})
  const [pingResult, setPingResult] = useState<{ apiReachable: boolean; sshReachable: boolean | null; error: string | null } | null>(null)

  const parsedSshPort = (() => {
    const n = parseInt(sshPort, 10)
    return Number.isFinite(n) && n > 0 && n <= 65535 ? n : null
  })()

  const parsedTelemetryPoll = (() => {
    if (telemetryPoll.trim() === '') return null
    const n = parseInt(telemetryPoll, 10)
    return Number.isFinite(n) ? Math.min(300, Math.max(5, n)) : null
  })()

  const isPbs = serverType === 'Pbs'

  // V8.1 — the per-host permission toggles are collapsed by default (they're set
  // once at setup, not every edit); open the group when this host already has any
  // permission on so an existing config isn't hidden.
  const perms = [allowConsole, allowUpdates, ...(isPbs ? [] : [allowDestroy, allowCreate, allowClone, allowRestore])]
  const permCount = perms.filter(Boolean).length
  const permTotal = perms.length
  const [permsOpen, setPermsOpen] = useState(permCount > 0)

  const buildUpsert = (): ProxmoxConnectionUpsert => ({
    name: name.trim(),
    apiBaseUrl: apiBaseUrl.trim(),
    nodeName: nodeName.trim(),
    serverType,
    apiTokenId: apiTokenId.trim(),
    apiTokenSecret: toUpsert(apiTokenSecret),
    skipTlsVerify,
    sshHost: sshHost.trim() || null,
    sshPort: parsedSshPort,
    sshUsername: sshUsername.trim() || null,
    sshPrivateKey: toUpsert(sshPrivateKey),
    sshPrivateKeyPassphrase: toUpsert(sshPassphrase),
    allowConsole,
    allowUpdates,
    allowDestroy,
    allowCreate,
    allowClone,
    allowRestore,
    enabled,
    updateNotificationsEnabled: emailNotify,
    telegramNotificationsEnabled: telegramNotify,
    scheduleType,
    checkEveryHours,
    // The backend stores time in UTC; for this single-user homelab tool we keep
    // it simple and treat the entered HH:mm as UTC.
    checkAtTime: scheduleType === 'Hourly' ? null : `${checkAtTime}:00`,
    checkOnDayOfWeek: scheduleType === 'Weekly' ? (checkOnDayOfWeek as ProxmoxConnectionUpsert['checkOnDayOfWeek']) : null,
    telemetryPollSeconds: parsedTelemetryPoll,
  })

  const submit = async () => {
    setError(null)
    setFieldErrors({})
    setPingResult(null)
    const payload = buildUpsert()
    if (!payload.name) {
      setError('Name is required — use something memorable like "home-pve".')
      return
    }
    try {
      if (isEdit) await update.mutateAsync({ id: connection!.id, data: payload })
      else await create.mutateAsync(payload)
      onOpenChange(false)
    } catch (e) {
      const { fieldErrors: fe, globalError } = parseApiErrors(e)
      setFieldErrors(fe)
      setError(globalError ?? (Object.keys(fe).length === 0 ? 'Failed to save the Proxmox host.' : null))
    }
  }

  const runTest = async () => {
    setError(null)
    setFieldErrors({})
    setPingResult(null)
    try {
      const result = await test.mutateAsync({
        connectionId: connection?.id,
        data: {
          apiBaseUrl: apiBaseUrl.trim(),
          nodeName: nodeName.trim(),
          serverType,
          apiTokenId: apiTokenId.trim(),
          apiTokenSecret: toUpsert(apiTokenSecret),
          skipTlsVerify,
          sshHost: sshHost.trim() || null,
          sshPort: parsedSshPort,
          sshUsername: sshUsername.trim() || null,
          sshPrivateKey: toUpsert(sshPrivateKey),
          sshPrivateKeyPassphrase: toUpsert(sshPassphrase),
        },
      })
      setPingResult({ apiReachable: result.apiReachable, sshReachable: result.sshReachable, error: result.error })
    } catch (e) {
      const { fieldErrors: fe, globalError } = parseApiErrors(e)
      setFieldErrors(fe)
      setError(globalError ?? (Object.keys(fe).length === 0 ? 'Test failed — could not reach the server.' : null))
    }
  }

  const saving = create.isPending || update.isPending
  const nameError = pickFieldError(fieldErrors, 'name')
  const apiBaseUrlError = pickFieldError(fieldErrors, 'apiBaseUrl')
  const nodeNameError = pickFieldError(fieldErrors, 'nodeName')
  const apiTokenIdError = pickFieldError(fieldErrors, 'apiTokenId')
  const apiTokenSecretError = pickFieldError(fieldErrors, 'apiTokenSecret')

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="proxmox-modal">
        <DialogHeader>
          <DialogTitle>{isEdit ? `Edit Proxmox host: ${connection!.name}` : 'New Proxmox host'}</DialogTitle>
          <DialogDescription>
            {isPbs
              ? 'Stashboard polls the Proxmox Backup Server REST API for node health + datastores, and SSHes in for sensors, updates, and the node console.'
              : 'Stashboard polls the Proxmox VE REST API for LXC discovery + node updates, and SSHes in to read per-container apt counts.'}
          </DialogDescription>
        </DialogHeader>

        {error && <p className="service-modal-error">{error}</p>}

        <div className="docker-section-grid proxmox-modal-grid">
          <div className="service-modal-field">
            <Label className="service-modal-label">Name</Label>
            <Input
              placeholder="home-pve"
              value={name}
              onChange={(e) => setName(e.target.value)}
              className={nameError ? 'border-destructive' : ''}
            />
            {nameError && <p className="service-modal-field-error">{nameError}</p>}
          </div>

          <div className="service-modal-field">
            <Label className="service-modal-label">Server type</Label>
            <select
              className="proxmox-select"
              value={serverType}
              onChange={(e) => setServerType(e.target.value as ProxmoxServerType)}
            >
              <option value="Pve">Proxmox VE</option>
              <option value="Pbs">Proxmox Backup Server</option>
            </select>
            <p className="text-xs text-[var(--muted-foreground)] mt-1">
              {isPbs
                ? 'PBS (port 8007). Monitored as a node + its datastores — no LXC guests.'
                : 'PVE (port 8006). Discovers LXC guests + node health.'}
            </p>
          </div>

          <h4 className="proxmox-form-section docker-section-field-full">API (token)</h4>

          <div className="service-modal-field">
            <Label className="service-modal-label">API base URL</Label>
            <Input
              placeholder={isPbs ? 'https://pbs.lan:8007' : 'https://pve.lan:8006'}
              value={apiBaseUrl}
              onChange={(e) => setApiBaseUrl(e.target.value)}
              className={cn('font-mono text-[12px]', apiBaseUrlError && 'border-destructive')}
            />
            {apiBaseUrlError && <p className="service-modal-field-error">{apiBaseUrlError}</p>}
          </div>

          <div className="service-modal-field">
            <Label className="service-modal-label">Node name</Label>
            <Input
              placeholder={isPbs ? 'pbs-main' : 'pve'}
              value={nodeName}
              onChange={(e) => setNodeName(e.target.value)}
              className={nodeNameError ? 'border-destructive' : ''}
            />
            {nodeNameError && <p className="service-modal-field-error">{nodeNameError}</p>}
          </div>

          <div className="service-modal-field">
            <Label className="service-modal-label">API token ID</Label>
            <Input
              placeholder="root@pam!stashboard"
              value={apiTokenId}
              onChange={(e) => setApiTokenId(e.target.value)}
              className={cn('font-mono text-[12px]', apiTokenIdError && 'border-destructive')}
            />
            {apiTokenIdError && <p className="service-modal-field-error">{apiTokenIdError}</p>}
          </div>

          <SecretFieldRow
            label="API token secret"
            field={apiTokenSecret}
            hasExisting={connection?.hasApiTokenSecret ?? false}
            secret
            error={apiTokenSecretError}
            onChange={setApiTokenSecret}
          />

          <div className="service-modal-field docker-section-field-full">
            <label className="service-modal-checkbox-label service-modal-label">
              <input type="checkbox" checked={skipTlsVerify} onChange={(e) => setSkipTlsVerify(e.target.checked)} />
              Skip TLS certificate validation
            </label>
            <p className="text-xs text-[var(--muted-foreground)] mt-1">
              Leave on for the self-signed certificate most homelab Proxmox installs ship with.{' '}
              <Link to="/help/proxmox-api" target="_blank" rel="noreferrer" className="text-[var(--primary)] underline">
                How to create an API token →
              </Link>
            </p>
          </div>

          <h4 className="proxmox-form-section docker-section-field-full">
            {isPbs ? 'SSH (sensors, updates, node console)' : 'SSH (per-LXC apt counts)'}
          </h4>

          <SshCredentialFields
            host={sshHost}
            onHostChange={setSshHost}
            port={sshPort}
            onPortChange={setSshPort}
            username={sshUsername}
            onUsernameChange={setSshUsername}
            usernamePlaceholder="root"
            privateKey={sshPrivateKey}
            onPrivateKeyChange={setSshPrivateKey}
            hasPrivateKey={connection?.hasSshPrivateKey ?? false}
            passphrase={sshPassphrase}
            onPassphraseChange={setSshPassphrase}
            hasPassphrase={connection?.hasSshPrivateKeyPassphrase ?? false}
            errors={fieldErrors}
          />
          <div className="service-modal-field docker-section-field-full">
            <p className="text-xs text-[var(--muted-foreground)]">
              {isPbs ? (
                <>
                  SSH is optional and powers the node's <strong>temperature/fan sensors</strong>, <strong>NIC error
                  alerts</strong>, one-click <strong>Update now</strong>, and the <strong>node console</strong> — none of
                  which the REST API exposes. Leave SSH blank to track only the node's metrics + datastores over the API.{' '}
                </>
              ) : (
                <>
                  SSH is optional and only powers <strong>per-LXC apt counts</strong>: Stashboard runs{' '}
                  <code>pct exec &lt;vmid&gt; -- apt list --upgradable</code> over SSH for each container. The SSH user must
                  be able to run <code>pct</code> (root, or a sudo-capable user). Leave SSH blank to track only the node's
                  own updates.{' '}
                </>
              )}
              <Link to="/help/proxmox-ssh" target="_blank" rel="noreferrer" className="text-[var(--primary)] underline">
                How to set up the SSH key →
              </Link>
            </p>
          </div>

          <h4 className="proxmox-form-section docker-section-field-full">Schedule</h4>
          <div className="service-modal-field docker-section-field-full">
            <div className="proxmox-form-row">
              <select className="proxmox-select" value={scheduleType} onChange={(e) => setScheduleType(e.target.value as CheckScheduleType)}>
                <option value="Hourly">Every N hours</option>
                <option value="Daily">Daily at</option>
                <option value="Weekly">Weekly on</option>
              </select>
              {scheduleType === 'Hourly' && (
                <select className="proxmox-select" value={checkEveryHours} onChange={(e) => setCheckEveryHours(Number(e.target.value))}>
                  {HOURLY_VALUES.map((h) => <option key={h} value={h}>{h} h</option>)}
                </select>
              )}
              {scheduleType === 'Weekly' && (
                <select className="proxmox-select" value={checkOnDayOfWeek} onChange={(e) => setCheckOnDayOfWeek(Number(e.target.value))}>
                  {DAYS.map((d, i) => <option key={d} value={i}>{d}</option>)}
                </select>
              )}
              {scheduleType !== 'Hourly' && (
                <Input type="time" className="proxmox-time" value={checkAtTime} onChange={(e) => setCheckAtTime(e.target.value)} />
              )}
            </div>
            <p className="text-xs text-[var(--muted-foreground)] mt-1">
              How often Stashboard scans this host for pending updates.
            </p>
          </div>

          <div className="service-modal-field docker-section-field-full">
            <Label className="service-modal-label">Live telemetry refresh (seconds)</Label>
            <Input
              type="number"
              min={5}
              max={300}
              placeholder="20 (default)"
              value={telemetryPoll}
              onChange={(e) => setTelemetryPoll(e.target.value)}
              className="proxmox-time"
            />
            <p className="text-xs text-[var(--muted-foreground)] mt-1">
              How often the node modal's live tabs (status, CPU/RAM, sensors, disk IO, network) poll this host.
              5–300s; leave blank for the 20s default. The real-time 2s “Live” view is unaffected. Unreachable hosts
              back off automatically.
            </p>
          </div>

          <h4 className="proxmox-form-section docker-section-field-full">Options</h4>
          <div className="service-modal-field docker-section-field-full">
            {/* V8.1 — the per-host permissions collapse into one disclosure so the
                Options block stays compact: short labels + an info tooltip carry the
                detail, and the shared "off by default / Settings switch / audited"
                rule is stated once instead of repeated per row. */}
            <details
              className="proxmox-perms"
              open={permsOpen}
              onToggle={(e) => setPermsOpen((e.currentTarget as HTMLDetailsElement).open)}
            >
              <summary className="proxmox-perms-summary">
                Permissions <span className="proxmox-perms-count">· {permCount} of {permTotal} on</span>
              </summary>
              <p className="proxmox-perms-note">
                Each is <strong>off by default</strong> and also needs its matching server-wide switch in <strong>Settings</strong>;
                every action it enables is audited. The SSH-based ones (console, updates) also need the SSH credentials above.
              </p>

              <PermRow
                checked={allowConsole}
                onChange={setAllowConsole}
                label={isPbs ? 'Allow node console' : 'Allow LXC console'}
                hint={isPbs
                  ? 'Opens a browser shell on the node itself (Console tab) over SSH. Needs the SSH credentials above + Settings → LXC console.'
                  : 'Opens a browser shell inside a container (Console tab) by SSHing to the host and running pct exec. Needs the SSH credentials above + Settings → LXC console.'}
              />
              <PermRow
                checked={allowUpdates}
                onChange={setAllowUpdates}
                label="Allow apply updates"
                hint={`Adds one-click "Update now" that runs apt-get dist-upgrade ${isPbs ? 'on the node' : 'on the node and inside its LXCs'} over SSH. Needs the SSH credentials above + Settings → Proxmox updates.`}
              />
              {!isPbs && (
                <>
                  <PermRow checked={allowDestroy} onChange={setAllowDestroy} label="Allow destroy"
                    hint="Adds a Destroy button to a stopped guest's Lifecycle section (LXC or VM — removes the guest and its disk(s)). Needs Settings → Destroy guest + an explicit double confirmation." />
                  <PermRow checked={allowCreate} onChange={setAllowCreate} label="Allow create"
                    hint="Adds New LXC + New VM buttons to this host's header that provision a container from a template or a QEMU VM from hardware + an install ISO. Needs Settings → Create guest." />
                  <PermRow checked={allowClone} onChange={setAllowClone} label="Allow clone/snapshot"
                    hint="Adds a Clone button + a Snapshots tab to a guest (LXC or VM) — duplicate it and take / roll back / delete snapshots. Needs Settings → Clone/snapshot; rollback / delete double-confirm." />
                  <PermRow checked={allowRestore} onChange={setAllowRestore} label="Allow restore"
                    hint="Adds Restore LXC + Restore VM buttons that re-create a guest from a vzdump backup archive. Needs Settings → Restore guest; overwriting an existing guest needs it stopped + double-confirm." />
                </>
              )}
            </details>
            <label className="service-modal-checkbox-label service-modal-label mt-2">
              <input type="checkbox" checked={enabled} onChange={(e) => setEnabled(e.target.checked)} /> Enabled (scan on schedule)
            </label>
            <label className="service-modal-checkbox-label service-modal-label mt-2">
              <input type="checkbox" checked={emailNotify} onChange={(e) => setEmailNotify(e.target.checked)} /> Email me when updates appear
            </label>
            <label className="service-modal-checkbox-label service-modal-label mt-2">
              <input type="checkbox" checked={telegramNotify} onChange={(e) => setTelegramNotify(e.target.checked)} /> Also notify via Telegram
            </label>
          </div>

          {isEdit && (
            <>
              <h4 className="proxmox-form-section docker-section-field-full">Update-check webhook</h4>
              <div className="service-modal-field docker-section-field-full">
                <ProxmoxWebhookPanel connection={connection!} />
              </div>
            </>
          )}

          {pingResult && (
            <div className="service-modal-field docker-section-field-full">
              <div className="docker-test-result">
                <span className={pingResult.apiReachable ? 'docker-test-result-ok' : 'docker-test-result-fail'}>
                  {pingResult.apiReachable ? '✓' : '✗'}
                </span>
                <span>{pingResult.apiReachable ? 'API reachable' : 'API unreachable'}</span>
              </div>
              <div className="docker-test-result mt-1">
                <span className={pingResult.sshReachable == null ? '' : pingResult.sshReachable ? 'docker-test-result-ok' : 'docker-test-result-fail'}>
                  {pingResult.sshReachable == null ? '—' : pingResult.sshReachable ? '✓' : '✗'}
                </span>
                <span>{pingResult.sshReachable == null ? 'SSH not configured (node-only)' : pingResult.sshReachable ? 'SSH reachable' : 'SSH unreachable'}</span>
              </div>
              {pingResult.error && <div className="docker-test-result-error">{pingResult.error}</div>}
            </div>
          )}
        </div>

        <DialogFooter>
          <Button type="button" variant="outline" onClick={runTest} disabled={test.isPending}>
            {test.isPending ? 'Testing…' : 'Test connection'}
          </Button>
          <Button type="button" onClick={submit} disabled={saving || !name.trim() || !apiBaseUrl.trim() || !nodeName.trim()}>
            {saving ? 'Saving…' : isEdit ? 'Save changes' : 'Save connection'}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}

/**
 * V6.11 — the host's update-check webhook controls. An external trigger (CI,
 * cron, mirror hook) POSTs to the shown URL to kick off an immediate scan,
 * bypassing the schedule — the Proxmox analogue of the Docker watch webhook. Off
 * by default; the token exists only after the owner creates one, and rotating it
 * invalidates the old URL. Mirrors the Docker watch webhook panel.
 */
function ProxmoxWebhookPanel({ connection }: { connection: ProxmoxConnection }) {
  const rotate = useRotateProxmoxWebhook(connection.id)
  const del = useDeleteProxmoxWebhook(connection.id)
  // Seed from the snapshot; the mutations return the refreshed host so the panel
  // reflects rotate / delete without waiting for the parent to re-render.
  const [token, setToken] = useState<string | null>(connection.webhookToken)
  const [lastReceived, setLastReceived] = useState<string | null>(connection.lastWebhookReceivedUtc)
  const [copied, setCopied] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const url = token ? `${window.location.origin}/api/proxmox/webhooks/${token}` : null
  const busy = rotate.isPending || del.isPending

  const doRotate = () => {
    setError(null)
    rotate.mutate(undefined, {
      onSuccess: (c) => { setToken(c.webhookToken); setLastReceived(c.lastWebhookReceivedUtc) },
      onError: (e) => setError(getApiErrorMessage(e) ?? 'Failed to generate the webhook token'),
    })
  }
  const doDelete = () => {
    setError(null)
    del.mutate(undefined, {
      onSuccess: (c) => { setToken(c.webhookToken); setLastReceived(c.lastWebhookReceivedUtc) },
      onError: (e) => setError(getApiErrorMessage(e) ?? 'Failed to remove the webhook token'),
    })
  }
  const copy = () => {
    if (!url) return
    void navigator.clipboard?.writeText(url).then(() => {
      setCopied(true)
      setTimeout(() => setCopied(false), 1500)
    })
  }

  return (
    <>
      <p className="text-xs text-[var(--muted-foreground)]">
        A unique URL an external system can POST to in order to trigger an immediate update scan of this host,
        bypassing the schedule. The token in the URL is the only authentication — keep it secret; rotating it
        invalidates the old URL. Off by default.
      </p>
      {url ? (
        <>
          <div className="proxmox-webhook-url mt-2">
            <Input readOnly value={url} onFocus={(e) => e.currentTarget.select()} className="proxmox-webhook-input" />
            <Button type="button" variant="outline" size="sm" onClick={copy}>{copied ? 'Copied' : 'Copy'}</Button>
          </div>
          <p className="text-xs text-[var(--muted-foreground)] mt-1">
            Last delivery: {lastReceived ? new Date(lastReceived).toLocaleString() : 'never received'}.
          </p>
          <div className="container-modal-actions mt-2">
            <Button type="button" variant="outline" size="sm" disabled={busy} onClick={doRotate}>Rotate token</Button>
            <Button type="button" variant="outline" size="sm" disabled={busy} onClick={doDelete}>Remove webhook</Button>
          </div>
        </>
      ) : (
        <div className="container-modal-actions mt-2">
          <Button type="button" variant="outline" size="sm" disabled={busy} onClick={doRotate}>
            {rotate.isPending ? 'Generating…' : 'Generate webhook URL'}
          </Button>
        </div>
      )}
      {error && <p className="container-modal-error mt-1"><AlertCircle className="h-3.5 w-3.5 inline" /> {error}</p>}
    </>
  )
}
