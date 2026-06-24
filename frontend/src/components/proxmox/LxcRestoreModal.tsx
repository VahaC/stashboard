import { useMemo, useState } from 'react'
import { AlertCircle, AlertTriangle, ArchiveRestore, Loader2, RefreshCw } from 'lucide-react'
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import {
  useProxmoxBackups,
  useProxmoxNextVmId,
  useProxmoxNodeStorage,
  useRestoreProxmoxLxc,
} from '@/lib/proxmox-queries'
import type { ProxmoxBackup, ProxmoxConnection, ProxmoxLxcRestore } from '@/lib/types'
import { getApiErrorMessage } from '@/lib/utils'
// Reuse the Docker container modal's styles verbatim so the restore modal is the
// same surface as the create / clone modals, not a parallel one.
import '@/styles/docker-instances.css'  // .container-modal-* shell + .remove-confirm-* dialog
import '@/styles/service-modal.css'     // .service-modal-* form fields

/**
 * V8.1 — Restore LXC. Opened from the Proxmox page's per-host block header, this
 * re-creates a container from an existing `vzdump` backup archive via `POST …/lxc`
 * with `restore=1`. It reuses the **exact** Docker container modal shell
 * (`container-modal-*` classes) and the `service-modal-*` form fields so restoring is
 * the same surface as creating one (V6.13.1), not a parallel form system.
 *
 * Gated server-side (global switch + per-host opt-in); the caller only renders the
 * entry point when both are on. Restoring **over** an existing container (force) is
 * destructive, so it requires that container stopped and a double-confirm naming the
 * target — the server re-checks both. Proxmox stays authoritative and any host
 * rejection is surfaced verbatim.
 */
export function LxcRestoreModal({ connection, onClose }: { connection: ProxmoxConnection; onClose: () => void }) {
  const restore = useRestoreProxmoxLxc(connection.id)
  const nextId = useProxmoxNextVmId(connection.id)
  const backups = useProxmoxBackups(connection.id)
  const storage = useProxmoxNodeStorage(connection.id)

  // ── backup archive ──
  // backupChoice holds the picked volid OR the typed value; customBackup switches
  // the control between the styled <select> and a free-text <Input>.
  const [backupChoice, setBackupChoice] = useState<string | null>(null)
  const [customBackup, setCustomBackup] = useState(false)

  // ── target ──
  const [vmidChoice, setVmidChoice] = useState<string | null>(null)
  const [rootfsStorageChoice, setRootfsStorageChoice] = useState<string | null>(null)  // '' ⇒ keep backup's
  const [hostname, setHostname] = useState('')

  // ── options ──
  const [unprivileged, setUnprivileged] = useState(true)
  const [onboot, setOnboot] = useState(false)
  const [start, setStart] = useState(false)
  const [force, setForce] = useState(false)

  const [confirmOpen, setConfirmOpen] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const rootfsStorages = useMemo(
    () => (storage.data ?? []).filter((s) => s.content?.split(',').some((c) => c.trim() === 'rootdir')),
    [storage.data],
  )

  // Effective values: the user's explicit choice, else the live default from the
  // queries (volid ← first archive, vmid ← /cluster/nextid).
  const backupVolid = customBackup ? (backupChoice ?? '') : (backupChoice ?? backups.data?.[0]?.volid ?? '')
  const vmid = vmidChoice ?? (nextId.data ? String(nextId.data.vmId) : '')
  const rootfsStorage = rootfsStorageChoice ?? ''

  const selectedBackup = useMemo(
    () => (backups.data ?? []).find((b) => b.volid === backupVolid) ?? null,
    [backups.data, backupVolid],
  )

  const guestsByVmId = useMemo(() => new Map(connection.guests.map((g) => [g.vmId, g])), [connection.guests])
  const targetGuest = vmid.trim() === '' ? undefined : guestsByVmId.get(Number(vmid))

  // Client-side guards mirroring the server validator. The server stays
  // authoritative; this is just for a clean message before the call.
  const errors = useMemo(() => {
    const errs: string[] = []
    const id = vmid.trim() === '' ? null : Number(vmid)
    if (id == null || !Number.isInteger(id) || id < 100 || id > 999_999_999)
      errs.push('VMID must be a whole number between 100 and 999999999.')

    if (!backupVolid) errs.push('Pick a backup archive.')
    else if (!backupVolid.includes('vzdump-lxc-')) errs.push('The selected volume is not an LXC backup archive.')

    if (id != null) {
      const guest = guestsByVmId.get(id)
      if (force) {
        if (!guest || guest.guestType === 'Node')
          errs.push(`VMID ${id} is not an existing container — clear "overwrite" to restore to a new id.`)
        else if (guest.isRunning) errs.push(`Stop CT ${id} before restoring over it.`)
      } else if (guest) {
        errs.push(`VMID ${id} is already in use. Tick "overwrite" to replace it.`)
      }
    }
    return errs
  }, [vmid, backupVolid, force, guestsByVmId])

  const buildSpec = (): ProxmoxLxcRestore => ({
    vmId: Number(vmid),
    backupVolid,
    storage: rootfsStorage.trim() || null,
    hostname: hostname.trim() || null,
    unprivileged,
    onboot,
    start,
    force,
  })

  const doRestore = () => {
    setError(null)
    restore.mutate(buildSpec(), {
      onError: (e) => { setConfirmOpen(false); setError(getApiErrorMessage(e) ?? 'Failed to restore the container') },
      onSuccess: () => onClose(),
    })
  }

  const submit = () => {
    setError(null)
    // Overwriting an existing container is destructive — double-confirm naming the
    // target, the same pattern as destroy.
    if (force) { setConfirmOpen(true); return }
    doRestore()
  }

  const busy = restore.isPending

  return (
    <Dialog open onOpenChange={(open) => { if (!open && !busy) onClose() }}>
      <DialogContent className="container-modal-content">
        <DialogHeader>
          <DialogTitle className="container-modal-title">
            <ArchiveRestore className="h-4 w-4" />
            <span>Restore LXC</span>
          </DialogTitle>
          <DialogDescription className="container-modal-image">
            Restore a container from a backup on {connection.nodeName} · {connection.name}
          </DialogDescription>
        </DialogHeader>

        <div className="container-modal-body">
          <div className="container-modal-overview">
            <section className="container-modal-section">
              <h3 className="container-modal-section-title">Backup archive</h3>
              <label className="service-modal-label">
                Archive <span className="service-modal-label-help">(pick one or enter a volid)</span>
                {customBackup ? (
                  <>
                    <Input
                      value={backupVolid}
                      onChange={(e) => setBackupChoice(e.target.value)}
                      placeholder="local:backup/vzdump-lxc-…​.tar.zst"
                    />
                    <button
                      type="button"
                      className="lxc-create-link"
                      onClick={() => { setCustomBackup(false); setBackupChoice(null) }}
                    >
                      ← Choose from list
                    </button>
                  </>
                ) : (
                  <select
                    className="proxmox-select"
                    value={backupVolid}
                    onChange={(e) => {
                      if (e.target.value === '__custom__') { setCustomBackup(true); setBackupChoice('') }
                      else setBackupChoice(e.target.value)
                    }}
                  >
                    {backups.isLoading && <option value="">Loading…</option>}
                    {!backups.isLoading && (backups.data?.length ?? 0) === 0 && <option value="">No backups found</option>}
                    {backups.data?.map((b) => (
                      <option key={b.volid} value={b.volid}>{describeBackup(b)}</option>
                    ))}
                    <option value="__custom__">Custom volid…</option>
                  </select>
                )}
              </label>
              <p className="container-modal-empty">
                Lists <strong>vzdump backups</strong> on the node's storages — <em>not snapshots</em>. To roll a
                container back to a snapshot, use its <strong>Snapshots</strong> tab instead.
              </p>
              {!backups.isLoading && !backups.error && (backups.data?.length ?? 0) === 0 && (
                <p className="container-modal-empty">
                  No <code>vzdump-lxc-*</code> backups found on this node. Make a backup (vzdump) of the container
                  first — a snapshot won't show up here. (Proxmox Backup Server datastores aren't listed.)
                </p>
              )}
              {backups.error && (
                <p className="container-modal-empty">Couldn't load backups — check the host is reachable.</p>
              )}
            </section>

            <section className="container-modal-section">
              <h3 className="container-modal-section-title">Target</h3>
              <div className="container-modal-edit-grid lxc-create-rootfs-grid">
                <label className="service-modal-label lxc-create-field-size">
                  VMID
                  <Input type="number" min={100} value={vmid} onChange={(e) => setVmidChoice(e.target.value)}
                    placeholder={nextId.isLoading ? 'loading…' : '100'} />
                  {selectedBackup?.vmId != null && Number(vmid) !== selectedBackup.vmId && (
                    <button type="button" className="lxc-create-link"
                      onClick={() => setVmidChoice(String(selectedBackup.vmId))}>
                      Use original ({selectedBackup.vmId})
                    </button>
                  )}
                </label>
                <label className="service-modal-label lxc-create-field-storage">
                  Root FS storage 
                  <select className="proxmox-select" value={rootfsStorage}
                    onChange={(e) => setRootfsStorageChoice(e.target.value)}>
                    <option value="">Keep backup's storage</option>
                    {rootfsStorages.map((s) => (
                      <option key={s.storage} value={s.storage}>{s.storage}</option>
                    ))}
                  </select>
                </label>
                <label className="service-modal-label lxc-create-field-template">
                  Hostname <span className="service-modal-label-help">(blank = keep backup's)</span>
                  <Input value={hostname} onChange={(e) => setHostname(e.target.value)} placeholder="e.g. wireguard" />
                </label>
              </div>
            </section>

            <section className="container-modal-section">
              <h3 className="container-modal-section-title">Options</h3>
              <label className="service-modal-checkbox-label service-modal-label">
                <input type="checkbox" checked={unprivileged} onChange={(e) => setUnprivileged(e.target.checked)} /> Unprivileged container
              </label>
              <label className="service-modal-checkbox-label service-modal-label mt-2">
                <input type="checkbox" checked={onboot} onChange={(e) => setOnboot(e.target.checked)} /> Start at boot
              </label>
              <label className="service-modal-checkbox-label service-modal-label mt-2">
                <input type="checkbox" checked={start} onChange={(e) => setStart(e.target.checked)} /> Start after restore
              </label>
              <label className="service-modal-checkbox-label service-modal-label mt-2">
                <input type="checkbox" checked={force} onChange={(e) => setForce(e.target.checked)} /> Overwrite existing container <span className="service-modal-label-help">(force — replaces a stopped container with this VMID)</span>
              </label>
              {force && (
                <p className="container-modal-error">
                  <AlertTriangle className="h-3.5 w-3.5 inline" /> This replaces CT {vmid || '—'}
                  {targetGuest ? ` (${targetGuest.name})` : ''} on the host. This cannot be undone.
                </p>
              )}
            </section>

            {(error || errors.length > 0) && (
              <p className="container-modal-error">
                <AlertCircle className="h-3.5 w-3.5 inline" /> {error ?? errors[0]}
              </p>
            )}

            <div className="container-modal-actions">
              <Button type="button" size="sm" disabled={busy || errors.length > 0} onClick={submit}>
                {busy ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <ArchiveRestore className="h-3.5 w-3.5" />}
                {busy ? 'Restoring…' : 'Restore container'}
              </Button>
              <Button type="button" size="sm" variant="outline" disabled={busy} onClick={onClose}>Cancel</Button>
              {busy && (
                <span className="container-modal-empty">
                  <RefreshCw className="h-3.5 w-3.5 inline animate-spin" /> Proxmox is restoring — this can take a moment.
                </span>
              )}
            </div>
          </div>
        </div>
      </DialogContent>

      {/* Double-confirm for an overwrite restore — the second safety gate, mirroring
          the destroy dialog (the global switch + per-host opt-in are the first). */}
      <Dialog open={confirmOpen} onOpenChange={(v) => { if (!v && !busy) setConfirmOpen(false) }}>
        <DialogContent className="remove-confirm-dialog">
          <DialogHeader>
            <DialogTitle>Overwrite container?</DialogTitle>
            <DialogDescription className="sr-only">
              Confirm restoring over container CT {vmid}
            </DialogDescription>
          </DialogHeader>

          <dl className="remove-confirm-summary">
            <dt>Container</dt>
            <dd>CT {vmid}{targetGuest ? ` · ${targetGuest.name}` : ''}</dd>
            <dt>Node</dt>
            <dd>{connection.nodeName}</dd>
            <dt>Backup</dt>
            <dd>{backupVolid}</dd>
          </dl>

          <p className="remove-confirm-warning">
            This replaces the existing container CT {vmid} with the contents of the backup. The current container and its
            disk are destroyed first. This cannot be undone.
          </p>

          {error && (
            <p className="remove-confirm-error">
              <AlertCircle className="h-3.5 w-3.5 inline" /> {error}
            </p>
          )}

          <DialogFooter>
            <Button type="button" variant="outline" disabled={busy} onClick={() => setConfirmOpen(false)}>Cancel</Button>
            <Button type="button" variant="destructive" disabled={busy} onClick={doRestore}>
              <ArchiveRestore className="h-3.5 w-3.5" />
              {busy ? 'Restoring…' : `Overwrite CT ${vmid}`}
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </Dialog>
  )
}

/** A human label for a backup option: the guest id, its creation time, and size. */
function describeBackup(b: ProxmoxBackup): string {
  const parts: string[] = [b.vmId != null ? `CT ${b.vmId}` : 'CT ?']
  if (b.cTime != null) parts.push(new Date(b.cTime * 1000).toLocaleString())
  if (b.size != null) parts.push(formatSize(b.size))
  return parts.join(' · ')
}

function formatSize(bytes: number): string {
  if (!Number.isFinite(bytes) || bytes < 0) return '—'
  const units = ['B', 'KiB', 'MiB', 'GiB', 'TiB']
  let value = bytes
  let i = 0
  while (value >= 1024 && i < units.length - 1) { value /= 1024; i += 1 }
  return `${value.toFixed(value >= 10 || i === 0 ? 0 : 1)} ${units[i]}`
}
