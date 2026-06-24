import { useMemo, useState } from 'react'
import { AlertCircle, Copy, Loader2, RefreshCw } from 'lucide-react'
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import {
  useCloneProxmoxLxc,
  useProxmoxNextVmId,
  useProxmoxNodeStorage,
  useProxmoxSnapshots,
} from '@/lib/proxmox-queries'
import type { ProxmoxConnection, ProxmoxGuest, ProxmoxLxcClone } from '@/lib/types'
import { getApiErrorMessage } from '@/lib/utils'
// Reuse the Docker container modal's styles verbatim so the clone modal is the
// same surface as the create / LXC modal, not a parallel one.
import '@/styles/docker-instances.css'  // .container-modal-* shell
import '@/styles/service-modal.css'     // .service-modal-* form fields

/**
 * V8.0 — Clone LXC. Opened from a guest's Lifecycle action row, this duplicates an
 * existing container via `POST …/lxc/{vmid}/clone`. It reuses the **exact** Docker
 * container modal shell (`container-modal-*` classes) and the `service-modal-*`
 * form fields the {@link LxcCreateModal} uses, so cloning is the same surface as
 * creating — not a parallel form system.
 *
 * Gated server-side (global switch + per-host opt-in); the caller only renders the
 * entry point when both are on. Client-side guards mirror the create form's vmid
 * rules; Proxmox stays authoritative (whether a linked clone is possible, storage
 * existence) and any host rejection is surfaced verbatim.
 */
export function LxcCloneModal({
  guest, connection, onClose,
}: { guest: ProxmoxGuest; connection: ProxmoxConnection; onClose: () => void }) {
  const clone = useCloneProxmoxLxc(connection.id, guest.vmId)
  const nextId = useProxmoxNextVmId(connection.id)
  const storage = useProxmoxNodeStorage(connection.id)
  const snapshots = useProxmoxSnapshots(connection.id, guest.vmId)

  const [vmidChoice, setVmidChoice] = useState<string | null>(null)
  const [hostname, setHostname] = useState(`${guest.name}-clone`)
  const [description, setDescription] = useState('')
  // Full vs linked: a linked clone is CoW and needs the source to be a template or
  // to have a snapshot, so default to a full (independent) clone — always valid.
  const [full, setFull] = useState(true)
  const [targetStorageChoice, setTargetStorageChoice] = useState<string | null>(null)
  const [snapName, setSnapName] = useState('')
  const [error, setError] = useState<string | null>(null)

  const rootfsStorages = useMemo(
    () => (storage.data ?? []).filter((s) => s.content?.split(',').some((c) => c.trim() === 'rootdir')),
    [storage.data],
  )

  // Effective vmid: the user's explicit choice, else the live /cluster/nextid.
  const vmid = vmidChoice ?? (nextId.data ? String(nextId.data.vmId) : '')
  // A full clone can re-target storage; a linked clone references the source's, so
  // the picker is only meaningful (and shown) for a full clone. Empty ⇒ keep source.
  const targetStorage = targetStorageChoice ?? ''

  const existingVmIds = useMemo(() => new Set(connection.guests.map((g) => g.vmId)), [connection.guests])
  const hasSnapshots = (snapshots.data?.length ?? 0) > 0

  // Client-side guards mirroring the create form / server validator.
  const errors = useMemo(() => {
    const errs: string[] = []
    const id = vmid.trim() === '' ? null : Number(vmid)
    if (id == null || !Number.isInteger(id) || id < 100 || id > 999_999_999)
      errs.push('VMID must be a whole number between 100 and 999999999.')
    else if (existingVmIds.has(id)) errs.push(`VMID ${id} is already in use on this host.`)
    // Proxmox refuses to clone a RUNNING container from its live state (both full
    // and linked) — it must clone from a snapshot. Guide the user before the host
    // rejects it (which would also write a failed-audit row).
    if (guest.isRunning && !snapName.trim()) {
      errs.push(hasSnapshots
        ? 'Proxmox can’t clone a running container from its live state — pick a source snapshot below, or stop the container first.'
        : 'Proxmox can’t clone a running container from its live state — take a snapshot first (Snapshots tab) or stop the container.')
    }
    return errs
  }, [vmid, existingVmIds, guest.isRunning, snapName, hasSnapshots])

  const submit = () => {
    setError(null)
    const spec: ProxmoxLxcClone = {
      newVmId: Number(vmid),
      hostname: hostname.trim() || null,
      targetStorage: full && targetStorage.trim() ? targetStorage.trim() : null,
      full,
      snapName: snapName.trim() || null,
      description: description.trim() || null,
    }
    clone.mutate(spec, {
      onError: (e) => setError(getApiErrorMessage(e) ?? 'Failed to clone the container'),
      onSuccess: () => onClose(),
    })
  }

  const busy = clone.isPending

  return (
    <Dialog open onOpenChange={(open) => { if (!open && !busy) onClose() }}>
      <DialogContent className="container-modal-content">
        <DialogHeader>
          <DialogTitle className="container-modal-title">
            <Copy className="h-4 w-4" />
            <span>Clone LXC</span>
          </DialogTitle>
          <DialogDescription className="container-modal-image">
            Clone CT {guest.vmId} · {guest.name} on {connection.nodeName} · {connection.name}
          </DialogDescription>
        </DialogHeader>

        <div className="container-modal-body">
          <div className="container-modal-overview">
            <section className="container-modal-section">
              <h3 className="container-modal-section-title">Destination</h3>
              <div className="container-modal-edit-grid">
                <label className="service-modal-label">
                  New VMID
                  <Input type="number" min={100} value={vmid} onChange={(e) => setVmidChoice(e.target.value)}
                    placeholder={nextId.isLoading ? 'loading…' : '100'} />
                </label>
                <label className="service-modal-label">
                  Hostname
                  <Input value={hostname} onChange={(e) => setHostname(e.target.value)} placeholder="e.g. wireguard-clone" />
                </label>
                <label className="service-modal-label">
                  Description
                  <Input value={description} onChange={(e) => setDescription(e.target.value)} placeholder="optional note" />
                </label>
              </div>
            </section>

            <section className="container-modal-section">
              <h3 className="container-modal-section-title">Clone mode</h3>
              <label className="service-modal-checkbox-label service-modal-label">
                <input type="checkbox" checked={full} onChange={(e) => setFull(e.target.checked)} /> Full clone
                <span className="service-modal-label-help">(independent copy — uncheck for a linked CoW clone)</span>
              </label>
              {full && (
                <label className="service-modal-label mt-2">
                  Target storage <span className="service-modal-label-help">(blank = keep source's storage)</span>
                  <select className="proxmox-select" value={targetStorage} onChange={(e) => setTargetStorageChoice(e.target.value)}>
                    <option value="">Same as source</option>
                    {rootfsStorages.map((s) => (
                      <option key={s.storage} value={s.storage}>{s.storage}</option>
                    ))}
                  </select>
                </label>
              )}
              {!full && (
                <p className="container-modal-empty">
                  A linked clone references the source's disk (copy-on-write) and needs the source to be a template or to
                  have a snapshot — Proxmox will reject it otherwise.
                </p>
              )}
            </section>

            {(snapshots.data?.length ?? 0) > 0 && (
              <section className="container-modal-section">
                <h3 className="container-modal-section-title">Source snapshot</h3>
                <label className="service-modal-label">
                  Clone from <span className="service-modal-label-help">(blank = current state)</span>
                  <select className="proxmox-select" value={snapName} onChange={(e) => setSnapName(e.target.value)}>
                    <option value="">Current state</option>
                    {snapshots.data?.map((s) => (
                      <option key={s.name} value={s.name}>{s.name}</option>
                    ))}
                  </select>
                </label>
              </section>
            )}

            {(error || errors.length > 0) && (
              <p className="container-modal-error">
                <AlertCircle className="h-3.5 w-3.5 inline" /> {error ?? errors[0]}
              </p>
            )}

            <div className="container-modal-actions">
              <Button type="button" size="sm" disabled={busy || errors.length > 0} onClick={submit}>
                {busy ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <Copy className="h-3.5 w-3.5" />}
                {busy ? 'Cloning…' : 'Clone container'}
              </Button>
              <Button type="button" size="sm" variant="outline" disabled={busy} onClick={onClose}>Cancel</Button>
              {busy && (
                <span className="container-modal-empty">
                  <RefreshCw className="h-3.5 w-3.5 inline animate-spin" /> Proxmox is cloning — this can take a moment.
                </span>
              )}
            </div>
          </div>
        </div>
      </DialogContent>
    </Dialog>
  )
}
