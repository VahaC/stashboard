import { useMemo, useState } from 'react'
import { AlertCircle, Loader2, MonitorPlay, Plus, RefreshCw } from 'lucide-react'
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
  useCreateProxmoxQemu,
  useProxmoxIsos,
  useProxmoxNextVmId,
  useProxmoxNodeStorage,
} from '@/lib/proxmox-queries'
import type { ProxmoxConnection, ProxmoxQemuCreate } from '@/lib/types'
import { getApiErrorMessage } from '@/lib/utils'
// Reuse the Docker container modal's styles verbatim so the create modal is the
// same surface as the LXC create / restore / clone modals, not a parallel one.
import '@/styles/docker-instances.css'  // .container-modal-* shell
import '@/styles/service-modal.css'     // .service-modal-* form fields

/** Curated QEMU OS-type hints (Proxmox `ostype`), friendly label → value. Mirrors
 *  the set the Proxmox web UI offers; the value is what `POST …/qemu` accepts. */
const OS_TYPES: { value: string; label: string }[] = [
  { value: 'l26', label: 'Linux (6.x / 5.x / 4.x / 3.x / 2.6 kernel)' },
  { value: 'l24', label: 'Linux 2.4 kernel' },
  { value: 'win11', label: 'Windows 11 / 2022 / 2025' },
  { value: 'win10', label: 'Windows 10 / 2016 / 2019' },
  { value: 'win8', label: 'Windows 8 / 2012 / 2012r2' },
  { value: 'win7', label: 'Windows 7 / 2008r2' },
  { value: 'wvista', label: 'Windows Vista / 2008' },
  { value: 'wxp', label: 'Windows XP / 2003' },
  { value: 'solaris', label: 'Solaris / OpenIndiana' },
  { value: 'other', label: 'Other' },
]

/**
 * V8.4 — New VM. Opened from the Proxmox page's per-host header, this modal
 * provisions a QEMU/KVM virtual machine from scratch via `POST …/qemu`. It is the
 * VM analogue of {@link LxcCreateModal}: it reuses the **exact** Docker container
 * modal shell (`container-modal-*`) and the `service-modal-*` form fields, but a VM
 * is hardware — a system disk on a chosen storage, a NIC bridge, an install ISO, and
 * the firmware / chipset / OS-type — rather than a single template.
 *
 * Gated server-side (the same global switch + per-host opt-in as LXC create); the
 * caller only renders the entry point when both are on. Client-side guards mirror the
 * server validator; Proxmox stays authoritative and any host rejection is surfaced
 * verbatim. Installing the guest OS is the user's job — this provisions the VM and
 * boots it into the installer.
 */
export function QemuCreateModal({ connection, onClose }: { connection: ProxmoxConnection; onClose: () => void }) {
  const create = useCreateProxmoxQemu(connection.id)
  const nextId = useProxmoxNextVmId(connection.id)
  const isos = useProxmoxIsos(connection.id)
  const storage = useProxmoxNodeStorage(connection.id)

  // ── identity ──
  const [vmidChoice, setVmidChoice] = useState<string | null>(null)
  const [name, setName] = useState('')
  const [description, setDescription] = useState('')
  const [tags, setTags] = useState('')

  // ── system disk ──
  const [diskStorageChoice, setDiskStorageChoice] = useState<string | null>(null)
  const [diskSize, setDiskSize] = useState('32')

  // ── installation media ──
  // isoChoice holds the picked volid OR the typed value; customIso switches the
  // control between the styled <select> and a free-text <Input>. '' ⇒ no media.
  const [isoChoice, setIsoChoice] = useState<string | null>(null)
  const [customIso, setCustomIso] = useState(false)

  // ── resources ──
  const [cores, setCores] = useState('2')
  const [sockets, setSockets] = useState('1')
  const [memory, setMemory] = useState('2048')

  // ── network (net0) ──
  const [bridge, setBridge] = useState('vmbr0')
  const [mac, setMac] = useState('')
  const [vlan, setVlan] = useState('')
  const [firewall, setFirewall] = useState(false)

  // ── system ──
  const [osType, setOsType] = useState('l26')
  const [bios, setBios] = useState('seabios')
  const [machine, setMachine] = useState('q35')

  // ── options ──
  const [agent, setAgent] = useState(true)
  const [onboot, setOnboot] = useState(false)
  const [start, setStart] = useState(false)

  const [error, setError] = useState<string | null>(null)

  // A VM's system disk needs a storage that can hold disk images.
  const diskStorages = useMemo(
    () => (storage.data ?? []).filter((s) => s.content?.split(',').some((c) => c.trim() === 'images')),
    [storage.data],
  )

  // Effective values: the user's explicit choice, else the live default from the
  // queries (vmid ← /cluster/nextid, storage ← first images-capable, iso ← first).
  const vmid = vmidChoice ?? (nextId.data ? String(nextId.data.vmId) : '')
  const diskStorage = diskStorageChoice ?? diskStorages[0]?.storage ?? ''
  // In custom mode the value is purely what the user typed; otherwise it defaults to
  // the first discovered ISO until they pick another (or "no media").
  const iso = customIso ? (isoChoice ?? '') : (isoChoice ?? isos.data?.[0]?.volid ?? '')

  const existingVmIds = useMemo(() => new Set(connection.guests.map((g) => g.vmId)), [connection.guests])

  // Client-side guards mirroring the server validator. The server stays
  // authoritative; this is just for a clean message before the call.
  const errors = useMemo(() => {
    const errs: string[] = []
    const id = vmid.trim() === '' ? null : Number(vmid)
    if (id == null || !Number.isInteger(id) || id < 100 || id > 999_999_999)
      errs.push('VMID must be a whole number between 100 and 999999999.')
    else if (existingVmIds.has(id)) errs.push(`VMID ${id} is already in use on this host.`)

    if (!diskStorage) errs.push('Pick a disk storage.')
    const size = Number(diskSize)
    if (!Number.isInteger(size) || size < 1) errs.push('Disk size must be at least 1 GiB.')

    const c = cores.trim() === '' ? null : Number(cores)
    if (c != null && (!Number.isInteger(c) || c < 1 || c > 8192)) errs.push('Cores must be between 1 and 8192.')
    const s = sockets.trim() === '' ? null : Number(sockets)
    if (s != null && (!Number.isInteger(s) || s < 1 || s > 4)) errs.push('Sockets must be between 1 and 4.')
    const m = memory.trim() === '' ? null : Number(memory)
    if (m != null && (!Number.isInteger(m) || m < 16)) errs.push('Memory must be at least 16 MiB.')

    if (!bridge.trim()) errs.push('A network bridge is required.')
    const tag = vlan.trim() === '' ? null : Number(vlan)
    if (tag != null && (!Number.isInteger(tag) || tag < 1 || tag > 4094)) errs.push('VLAN tag must be between 1 and 4094.')
    if (mac.trim() && !/^([0-9A-Fa-f]{2}:){5}[0-9A-Fa-f]{2}$/.test(mac.trim()))
      errs.push(`'${mac}' is not a valid MAC address (aa:bb:cc:dd:ee:ff).`)

    if (iso.trim() && !iso.toLowerCase().includes('iso'))
      errs.push('The selected install media is not an ISO image.')

    return errs
  }, [vmid, existingVmIds, diskStorage, diskSize, cores, sockets, memory, bridge, vlan, mac, iso])

  const submit = () => {
    setError(null)
    const spec: ProxmoxQemuCreate = {
      vmId: Number(vmid),
      diskStorage,
      diskSizeGib: Number(diskSize),
      name: name.trim() || null,
      cores: cores.trim() === '' ? null : Number(cores),
      sockets: sockets.trim() === '' ? null : Number(sockets),
      memoryMib: memory.trim() === '' ? null : Number(memory),
      bridge: bridge.trim(),
      vlanTag: vlan.trim() === '' ? null : Number(vlan),
      macAddr: mac.trim() || null,
      netFirewall: firewall,
      osType,
      bios,
      machine: machine.trim() || null,
      isoVolid: iso.trim() || null,
      agent,
      onboot,
      start,
      description: description.trim() || null,
      tags: tags.trim() || null,
    }
    create.mutate(spec, {
      onError: (e) => setError(getApiErrorMessage(e) ?? 'Failed to create the VM'),
      onSuccess: () => onClose(),
    })
  }

  const busy = create.isPending

  return (
    <Dialog open onOpenChange={(open) => { if (!open && !busy) onClose() }}>
      <DialogContent className="container-modal-content">
        <DialogHeader>
          <DialogTitle className="container-modal-title">
            <MonitorPlay className="h-4 w-4" />
            <span>New VM</span>
          </DialogTitle>
          <DialogDescription className="container-modal-image">
            Create a virtual machine on {connection.nodeName} · {connection.name}
          </DialogDescription>
        </DialogHeader>

        <div className="container-modal-body">
          <div className="container-modal-overview">
            <section className="container-modal-section">
              <h3 className="container-modal-section-title">Identity</h3>
              <div className="container-modal-edit-grid">
                <label className="service-modal-label">
                  VMID
                  <Input type="number" min={100} value={vmid} onChange={(e) => setVmidChoice(e.target.value)}
                    placeholder={nextId.isLoading ? 'loading…' : '100'} />
                </label>
                <label className="service-modal-label">
                  Name
                  <Input value={name} onChange={(e) => setName(e.target.value)} placeholder="e.g. debian-vm" />
                </label>
                <label className="service-modal-label">
                  Tags <span className="service-modal-label-help">(semicolon-separated)</span>
                  <Input value={tags} onChange={(e) => setTags(e.target.value)} placeholder="prod;net" />
                </label>
                <label className="service-modal-label">
                  Description
                  <Input value={description} onChange={(e) => setDescription(e.target.value)} placeholder="optional note" />
                </label>
              </div>
            </section>

            <section className="container-modal-section">
              <h3 className="container-modal-section-title">Installation media</h3>
              <label className="service-modal-label">
                ISO image <span className="service-modal-label-help">(pick one or enter a volid)</span>
                {customIso ? (
                  <>
                    <Input
                      value={iso}
                      onChange={(e) => setIsoChoice(e.target.value)}
                      placeholder="local:iso/debian-12.iso"
                    />
                    <button
                      type="button"
                      className="lxc-create-link"
                      onClick={() => { setCustomIso(false); setIsoChoice(null) }}
                    >
                      ← Choose from list
                    </button>
                  </>
                ) : (
                  <select
                    className="proxmox-select"
                    value={iso}
                    onChange={(e) => {
                      if (e.target.value === '__custom__') { setCustomIso(true); setIsoChoice('') }
                      else setIsoChoice(e.target.value)
                    }}
                  >
                    {isos.isLoading && <option value="">Loading…</option>}
                    {!isos.isLoading && (isos.data?.length ?? 0) === 0 && <option value="">No ISO images found</option>}
                    {isos.data?.map((i) => (
                      <option key={i.volid} value={i.volid}>{i.volid}</option>
                    ))}
                    <option value="">No install media</option>
                    <option value="__custom__">Custom volid…</option>
                  </select>
                )}
              </label>
              <p className="container-modal-empty">
                The ISO is attached as a CD-ROM and the VM boots into it. Installing the OS is up to you —
                use the host's <strong>Console</strong> once the VM is created.
              </p>
              {isos.error && (
                <p className="container-modal-empty">Couldn't load ISO images — check the host is reachable.</p>
              )}
            </section>

            <section className="container-modal-section">
              <h3 className="container-modal-section-title">System disk</h3>
              <div className="container-modal-edit-grid lxc-create-rootfs-grid">
                <label className="service-modal-label lxc-create-field-storage">
                  Disk storage
                  <select className="proxmox-select" value={diskStorage} onChange={(e) => setDiskStorageChoice(e.target.value)}>
                    {storage.isLoading && <option value="">Loading…</option>}
                    {!storage.isLoading && diskStorages.length === 0 && <option value="">No image storage</option>}
                    {diskStorages.map((s) => (
                      <option key={s.storage} value={s.storage}>{s.storage}</option>
                    ))}
                  </select>
                </label>
                <label className="service-modal-label lxc-create-field-size">
                  Disk size (GiB)
                  <Input type="number" min={1} value={diskSize} onChange={(e) => setDiskSize(e.target.value)} />
                </label>
              </div>
              <p className="container-modal-empty">
                A primary SCSI disk (<code>scsi0</code>) on a <code>virtio-scsi</code> controller. Extra disks can be
                added later from the VM's Config tab.
              </p>
            </section>

            <section className="container-modal-section">
              <h3 className="container-modal-section-title">Resources</h3>
              <div className="container-modal-edit-grid">
                <label className="service-modal-label">
                  Cores (per socket)
                  <Input type="number" min={1} max={8192} value={cores} onChange={(e) => setCores(e.target.value)} />
                </label>
                <label className="service-modal-label">
                  Sockets
                  <Input type="number" min={1} max={4} value={sockets} onChange={(e) => setSockets(e.target.value)} />
                </label>
                <label className="service-modal-label">
                  Memory (MiB)
                  <Input type="number" min={16} value={memory} onChange={(e) => setMemory(e.target.value)} />
                </label>
              </div>
            </section>

            <section className="container-modal-section">
              <h3 className="container-modal-section-title">Network (net0)</h3>
              <div className="container-modal-edit-grid">
                <label className="service-modal-label">
                  Bridge
                  <Input value={bridge} onChange={(e) => setBridge(e.target.value)} placeholder="vmbr0" />
                </label>
                <label className="service-modal-label">
                  MAC <span className="service-modal-label-help">(auto if blank)</span>
                  <Input value={mac} onChange={(e) => setMac(e.target.value)} placeholder="aa:bb:cc:dd:ee:ff" />
                </label>
                <label className="service-modal-label">
                  VLAN tag
                  <Input type="number" value={vlan} onChange={(e) => setVlan(e.target.value)} placeholder="—" />
                </label>
              </div>
              <label className="service-modal-checkbox-label service-modal-label mt-2">
                <input type="checkbox" checked={firewall} onChange={(e) => setFirewall(e.target.checked)} /> Enable firewall on this interface
              </label>
            </section>

            <section className="container-modal-section">
              <h3 className="container-modal-section-title">System</h3>
              <div className="container-modal-edit-grid">
                <label className="service-modal-label">
                  OS type
                  <select className="proxmox-select" value={osType} onChange={(e) => setOsType(e.target.value)}>
                    {OS_TYPES.map((o) => (
                      <option key={o.value} value={o.value}>{o.label}</option>
                    ))}
                  </select>
                </label>
                <label className="service-modal-label">
                  BIOS
                  <select className="proxmox-select" value={bios} onChange={(e) => setBios(e.target.value)}>
                    <option value="seabios">SeaBIOS (default)</option>
                    <option value="ovmf">OVMF (UEFI)</option>
                  </select>
                </label>
                <label className="service-modal-label">
                  Machine
                  <select className="proxmox-select" value={machine} onChange={(e) => setMachine(e.target.value)}>
                    <option value="q35">q35</option>
                    <option value="i440fx">i440fx (default)</option>
                  </select>
                </label>
              </div>
              {bios === 'ovmf' && (
                <p className="container-modal-empty">
                  OVMF needs an EFI vars disk — one is provisioned automatically on the chosen disk storage.
                </p>
              )}
            </section>

            <section className="container-modal-section">
              <h3 className="container-modal-section-title">Options</h3>
              <label className="service-modal-checkbox-label service-modal-label">
                <input type="checkbox" checked={agent} onChange={(e) => setAgent(e.target.checked)} /> QEMU guest agent <span className="service-modal-label-help">(install it inside the guest too)</span>
              </label>
              <label className="service-modal-checkbox-label service-modal-label mt-2">
                <input type="checkbox" checked={onboot} onChange={(e) => setOnboot(e.target.checked)} /> Start at boot
              </label>
              <label className="service-modal-checkbox-label service-modal-label mt-2">
                <input type="checkbox" checked={start} onChange={(e) => setStart(e.target.checked)} /> Start after create
              </label>
            </section>

            {(error || errors.length > 0) && (
              <p className="container-modal-error">
                <AlertCircle className="h-3.5 w-3.5 inline" /> {error ?? errors[0]}
              </p>
            )}

            <div className="container-modal-actions">
              <Button type="button" size="sm" disabled={busy || errors.length > 0} onClick={submit}>
                {busy ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <Plus className="h-3.5 w-3.5" />}
                {busy ? 'Creating…' : 'Create VM'}
              </Button>
              <Button type="button" size="sm" variant="outline" disabled={busy} onClick={onClose}>Cancel</Button>
              {busy && (
                <span className="container-modal-empty">
                  <RefreshCw className="h-3.5 w-3.5 inline animate-spin" /> Proxmox is provisioning — this can take a moment.
                </span>
              )}
            </div>
          </div>
        </div>
      </DialogContent>
    </Dialog>
  )
}
