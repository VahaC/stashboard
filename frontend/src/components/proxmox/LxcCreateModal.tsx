import { useMemo, useState } from 'react'
import { AlertCircle, Boxes, Eye, EyeOff, Loader2, Plus, RefreshCw } from 'lucide-react'
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Textarea } from '@/components/ui/textarea'
import {
  useCreateProxmoxLxc,
  useProxmoxNextVmId,
  useProxmoxTemplates,
  useProxmoxNodeStorage,
} from '@/lib/proxmox-queries'
import type { ProxmoxConnection, ProxmoxLxcCreate } from '@/lib/types'
import { getApiErrorMessage } from '@/lib/utils'
// Reuse the Docker container modal's styles verbatim so the create modal is the
// same surface as the LXC modal, not a parallel one.
import '@/styles/docker-instances.css'  // .container-modal-* shell
import '@/styles/service-modal.css'     // .service-modal-* form fields

/**
 * V6.13.1 — New LXC. Opened from the Proxmox page's per-host block header, this
 * modal provisions a container from a template via `POST …/lxc`. It reuses the
 * **exact** Docker container modal shell (`container-modal-*` classes) and the
 * `service-modal-*` form fields so creating an LXC is the same surface as editing
 * one (V6.5 / V6.9), not a parallel form system.
 *
 * Gated server-side (global switch + per-host opt-in); the caller only renders
 * the entry point when both are on. Client-side guards mirror the V6.9 editor;
 * Proxmox stays authoritative and any host rejection is surfaced verbatim.
 */
export function LxcCreateModal({ connection, onClose }: { connection: ProxmoxConnection; onClose: () => void }) {
  const create = useCreateProxmoxLxc(connection.id)
  const nextId = useProxmoxNextVmId(connection.id)
  const templates = useProxmoxTemplates(connection.id)
  const storage = useProxmoxNodeStorage(connection.id)

  // ── identity ──
  // vmid / template / rootfs storage default to live query data when the user
  // hasn't picked yet (a null choice ⇒ use the fallback), derived during render
  // so there's no setState-in-effect churn.
  const [vmidChoice, setVmidChoice] = useState<string | null>(null)
  const [hostname, setHostname] = useState('')
  const [description, setDescription] = useState('')
  const [tags, setTags] = useState('')

  // ── template / rootfs ──
  // templateChoice holds the picked volid OR the typed value; customTemplate
  // switches the control between the styled <select> and a free-text <Input>.
  const [templateChoice, setTemplateChoice] = useState<string | null>(null)
  const [customTemplate, setCustomTemplate] = useState(false)
  const [rootfsStorageChoice, setRootfsStorageChoice] = useState<string | null>(null)
  const [rootfsSize, setRootfsSize] = useState('8')

  // ── secrets (write-only) ──
  const [password, setPassword] = useState('')
  const [revealPassword, setRevealPassword] = useState(false)
  const [sshKeys, setSshKeys] = useState('')

  // ── resources ──
  const [cores, setCores] = useState('1')
  const [memory, setMemory] = useState('512')
  const [swap, setSwap] = useState('512')

  // ── DNS ──
  const [nameserver, setNameserver] = useState('')
  const [searchDomain, setSearchDomain] = useState('')

  // ── network (net0) ──
  const [netName, setNetName] = useState('eth0')
  const [bridge, setBridge] = useState('vmbr0')
  const [mac, setMac] = useState('')
  const [ip, setIp] = useState('dhcp')
  const [gw, setGw] = useState('')
  const [vlan, setVlan] = useState('')
  const [rate, setRate] = useState('')
  const [ip6, setIp6] = useState('')
  const [gw6, setGw6] = useState('')
  const [firewall, setFirewall] = useState(false)

  // ── options ──
  const [unprivileged, setUnprivileged] = useState(true)
  const [nesting, setNesting] = useState(false)
  const [onboot, setOnboot] = useState(false)
  const [start, setStart] = useState(true)
  const [addToHa, setAddToHa] = useState(false)

  const [error, setError] = useState<string | null>(null)

  const rootfsStorages = useMemo(
    () => (storage.data ?? []).filter((s) => s.content?.split(',').some((c) => c.trim() === 'rootdir')),
    [storage.data],
  )

  // Effective values: the user's explicit choice, else the live default from the
  // queries (vmid ← /cluster/nextid, template / storage ← first available).
  const vmid = vmidChoice ?? (nextId.data ? String(nextId.data.vmId) : '')
  // In custom mode the value is purely what the user typed; otherwise it defaults
  // to the first discovered template until they pick another.
  const template = customTemplate ? (templateChoice ?? '') : (templateChoice ?? templates.data?.[0]?.volid ?? '')
  const rootfsStorage = rootfsStorageChoice ?? rootfsStorages[0]?.storage ?? ''

  const existingVmIds = useMemo(() => new Set(connection.guests.map((g) => g.vmId)), [connection.guests])

  // Client-side guards mirroring the V6.9 editor / server validator. The server
  // stays authoritative; this is just for a clean message before the call.
  const errors = useMemo(() => {
    const errs: string[] = []
    const id = vmid.trim() === '' ? null : Number(vmid)
    if (id == null || !Number.isInteger(id) || id < 100 || id > 999_999_999)
      errs.push('VMID must be a whole number between 100 and 999999999.')
    else if (existingVmIds.has(id)) errs.push(`VMID ${id} is already in use on this host.`)

    if (!template) errs.push('Pick a template.')
    if (!rootfsStorage) errs.push('Pick a root filesystem storage.')

    const size = Number(rootfsSize)
    if (!Number.isInteger(size) || size < 1) errs.push('Root filesystem size must be at least 1 GiB.')

    const c = cores.trim() === '' ? null : Number(cores)
    if (c != null && (!Number.isInteger(c) || c < 1 || c > 8192)) errs.push('Cores must be between 1 and 8192.')
    const m = memory.trim() === '' ? null : Number(memory)
    if (m != null && (!Number.isInteger(m) || m < 16)) errs.push('Memory must be at least 16 MiB.')
    const s = swap.trim() === '' ? null : Number(swap)
    if (s != null && (!Number.isInteger(s) || s < 0)) errs.push('Swap must be 0 MiB or more.')

    if (ip.trim() && !validIpMode(ip)) errs.push(`'${ip}' is not dhcp, manual, or a valid IPv4 CIDR (10.0.0.5/24).`)
    if (gw.trim() && !isIpv4(gw)) errs.push(`Gateway '${gw}' is not a valid IPv4 address.`)
    const tag = vlan.trim() === '' ? null : Number(vlan)
    if (tag != null && (!Number.isInteger(tag) || tag < 1 || tag > 4094)) errs.push('VLAN tag must be between 1 and 4094.')
    if (mac.trim() && !/^([0-9A-Fa-f]{2}:){5}[0-9A-Fa-f]{2}$/.test(mac.trim()))
      errs.push(`'${mac}' is not a valid MAC address (aa:bb:cc:dd:ee:ff).`)
    const r = rate.trim() === '' ? null : Number(rate)
    if (r != null && (!Number.isFinite(r) || r < 0)) errs.push('Rate limit must be 0 or more.')

    if (!password.trim() && !sshKeys.trim())
      errs.push('Set a root password or an SSH public key so you can log in.')

    return errs
  }, [vmid, existingVmIds, template, rootfsStorage, rootfsSize, cores, memory, swap, ip, gw, vlan, mac, rate, password, sshKeys])

  const submit = () => {
    setError(null)
    const spec: ProxmoxLxcCreate = {
      vmId: Number(vmid),
      osTemplate: template,
      rootfsStorage,
      rootfsSizeGib: Number(rootfsSize),
      hostname: hostname.trim() || null,
      description: description.trim() || null,
      tags: tags.trim() || null,
      password: password.trim() || null,
      sshPublicKeys: sshKeys.trim() || null,
      cores: cores.trim() === '' ? null : Number(cores),
      memoryMib: memory.trim() === '' ? null : Number(memory),
      swapMib: swap.trim() === '' ? null : Number(swap),
      net0: {
        key: 'net0',
        name: netName.trim() || null,
        bridge: bridge.trim() || null,
        hwaddr: mac.trim() || null,
        ip: ip.trim() || null,
        gw: gw.trim() || null,
        ip6: ip6.trim() || null,
        gw6: gw6.trim() || null,
        tag: vlan.trim() === '' ? null : Number(vlan),
        rate: rate.trim() === '' ? null : Number(rate),
        firewall: firewall || null,
      },
      nameserver: nameserver.trim() || null,
      searchDomain: searchDomain.trim() || null,
      unprivileged,
      nesting,
      onboot,
      start,
      addToHa,
    }
    create.mutate(spec, {
      onError: (e) => setError(getApiErrorMessage(e) ?? 'Failed to create the container'),
      onSuccess: () => onClose(),
    })
  }

  const busy = create.isPending

  return (
    <Dialog open onOpenChange={(open) => { if (!open && !busy) onClose() }}>
      <DialogContent className="container-modal-content">
        <DialogHeader>
          <DialogTitle className="container-modal-title">
            <Boxes className="h-4 w-4" />
            <span>New LXC</span>
          </DialogTitle>
          <DialogDescription className="container-modal-image">
            Create a container on {connection.nodeName} · {connection.name}
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
                  Hostname
                  <Input value={hostname} onChange={(e) => setHostname(e.target.value)} placeholder="e.g. wireguard" />
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
              <h3 className="container-modal-section-title">Template &amp; root filesystem</h3>
              <div className="container-modal-edit-grid lxc-create-rootfs-grid">
                <label className="service-modal-label lxc-create-field-template">
                  Template <span className="service-modal-label-help">(pick one or enter a volid)</span>
                  {/* Themed select of discovered templates + a "Custom" escape
                      hatch that swaps in a styled text input — avoids the native
                      <datalist> popup, which can't be themed. */}
                  {customTemplate ? (
                    <>
                      <Input
                        value={template}
                        onChange={(e) => setTemplateChoice(e.target.value)}
                        placeholder="local:vztmpl/…​.tar.zst"
                      />
                      <button
                        type="button"
                        className="lxc-create-link"
                        onClick={() => { setCustomTemplate(false); setTemplateChoice(null) }}
                      >
                        ← Choose from list
                      </button>
                    </>
                  ) : (
                    <select
                      className="proxmox-select"
                      value={template}
                      onChange={(e) => {
                        if (e.target.value === '__custom__') { setCustomTemplate(true); setTemplateChoice('') }
                        else setTemplateChoice(e.target.value)
                      }}
                    >
                      {templates.isLoading && <option value="">Loading…</option>}
                      {!templates.isLoading && (templates.data?.length ?? 0) === 0 && <option value="">No templates found</option>}
                      {templates.data?.map((t) => (
                        <option key={t.volid} value={t.volid}>{t.volid}</option>
                      ))}
                      <option value="__custom__">Custom volid…</option>
                    </select>
                  )}
                </label>
                <label className="service-modal-label lxc-create-field-storage">
                  Root FS storage
                  <select className="proxmox-select" value={rootfsStorage} onChange={(e) => setRootfsStorageChoice(e.target.value)}>
                    {storage.isLoading && <option value="">Loading…</option>}
                    {!storage.isLoading && rootfsStorages.length === 0 && <option value="">No container storage</option>}
                    {rootfsStorages.map((s) => (
                      <option key={s.storage} value={s.storage}>{s.storage}</option>
                    ))}
                  </select>
                </label>
                <label className="service-modal-label lxc-create-field-size">
                  Root FS size (GiB)
                  <Input type="number" min={1} value={rootfsSize} onChange={(e) => setRootfsSize(e.target.value)} />
                </label>
              </div>
              {templates.error && (
                <p className="container-modal-empty">Couldn't load templates — check the host is reachable.</p>
              )}
            </section>

            <section className="container-modal-section">
              <h3 className="container-modal-section-title">Access</h3>
              <div className="container-modal-edit-grid">
                <label className="service-modal-label">
                  Root password
                  <span className="docker-secret-row">
                    <Input
                      type={revealPassword ? 'text' : 'password'}
                      value={password}
                      onChange={(e) => setPassword(e.target.value)}
                      placeholder="write-only"
                    />
                    <Button type="button" variant="outline" size="icon" onClick={() => setRevealPassword((v) => !v)}
                      title={revealPassword ? 'Hide' : 'Show'}>
                      {revealPassword ? <EyeOff className="h-3.5 w-3.5" /> : <Eye className="h-3.5 w-3.5" />}
                    </Button>
                  </span>
                </label>
              </div>
              <label className="service-modal-label mt-2">
                SSH public keys <span className="service-modal-label-help">(one per line)</span>
                <Textarea rows={2} value={sshKeys} onChange={(e) => setSshKeys(e.target.value)}
                  placeholder="ssh-ed25519 AAAA… user@host" />
              </label>
              <p className="container-modal-empty">Set at least one so you can log in to the new container.</p>
            </section>

            <section className="container-modal-section">
              <h3 className="container-modal-section-title">Resources</h3>
              <div className="container-modal-edit-grid">
                <label className="service-modal-label">
                  Cores (vCPU)
                  <Input type="number" min={1} max={8192} value={cores} onChange={(e) => setCores(e.target.value)} />
                </label>
                <label className="service-modal-label">
                  Memory (MiB)
                  <Input type="number" min={16} value={memory} onChange={(e) => setMemory(e.target.value)} />
                </label>
                <label className="service-modal-label">
                  Swap (MiB)
                  <Input type="number" min={0} value={swap} onChange={(e) => setSwap(e.target.value)} />
                </label>
              </div>
            </section>

            <section className="container-modal-section">
              <h3 className="container-modal-section-title">Network (net0)</h3>
              <div className="container-modal-edit-grid">
                <label className="service-modal-label">
                  Name
                  <Input value={netName} onChange={(e) => setNetName(e.target.value)} placeholder="eth0" />
                </label>
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
                <label className="service-modal-label">
                  Rate limit (MB/s)
                  <Input type="number" value={rate} onChange={(e) => setRate(e.target.value)} placeholder="—" />
                </label>
                <label className="service-modal-label">
                  IPv4
                  <Input value={ip} onChange={(e) => setIp(e.target.value)} placeholder="dhcp / 10.0.0.5/24" />
                </label>
                <label className="service-modal-label">
                  Gateway (IPv4)
                  <Input value={gw} onChange={(e) => setGw(e.target.value)} placeholder="10.0.0.1" />
                </label>
                <label className="service-modal-label">
                  IPv6
                  <Input value={ip6} onChange={(e) => setIp6(e.target.value)} placeholder="dhcp / auto / CIDR" />
                </label>
                <label className="service-modal-label">
                  Gateway (IPv6)
                  <Input value={gw6} onChange={(e) => setGw6(e.target.value)} placeholder="fe80::1" />
                </label>
              </div>
              <label className="service-modal-checkbox-label service-modal-label mt-2">
                <input type="checkbox" checked={firewall} onChange={(e) => setFirewall(e.target.checked)} /> Enable firewall on this interface
              </label>
            </section>

            <section className="container-modal-section">
              <h3 className="container-modal-section-title">DNS</h3>
              <div className="container-modal-edit-grid">
                <label className="service-modal-label">
                  DNS servers <span className="service-modal-label-help">(blank = host default)</span>
                  <Input value={nameserver} onChange={(e) => setNameserver(e.target.value)} placeholder="1.1.1.1 8.8.8.8" />
                </label>
                <label className="service-modal-label">
                  Search domain
                  <Input value={searchDomain} onChange={(e) => setSearchDomain(e.target.value)} placeholder="lan" />
                </label>
              </div>
            </section>

            <section className="container-modal-section">
              <h3 className="container-modal-section-title">Options</h3>
              <label className="service-modal-checkbox-label service-modal-label">
                <input type="checkbox" checked={unprivileged} onChange={(e) => setUnprivileged(e.target.checked)} /> Unprivileged container
              </label>
              <label className="service-modal-checkbox-label service-modal-label mt-2">
                <input type="checkbox" checked={nesting} onChange={(e) => setNesting(e.target.checked)} /> Nesting <span className="service-modal-label-help">(run Docker / nested LXC inside)</span>
              </label>
              <label className="service-modal-checkbox-label service-modal-label mt-2">
                <input type="checkbox" checked={onboot} onChange={(e) => setOnboot(e.target.checked)} /> Start at boot
              </label>
              <label className="service-modal-checkbox-label service-modal-label mt-2">
                <input type="checkbox" checked={start} onChange={(e) => setStart(e.target.checked)} /> Start after create
              </label>
              <label className="service-modal-checkbox-label service-modal-label mt-2">
                <input type="checkbox" checked={addToHa} onChange={(e) => setAddToHa(e.target.checked)} /> Add to HA <span className="service-modal-label-help">(register with the cluster HA manager)</span>
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
                {busy ? 'Creating…' : 'Create container'}
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

// ── client-side validation helpers (mirror the V6.9 editor / server validator) ──

function isIpv4(v: string): boolean {
  const parts = v.trim().split('.')
  return parts.length === 4 && parts.every((p) => /^\d{1,3}$/.test(p) && Number(p) <= 255)
}

function validIpMode(v: string): boolean {
  const t = v.trim()
  if (t === '' || t === 'dhcp' || t === 'manual') return true
  const slash = t.indexOf('/')
  if (slash < 0) return false
  const prefix = Number(t.slice(slash + 1))
  return Number.isInteger(prefix) && prefix >= 0 && prefix <= 32 && isIpv4(t.slice(0, slash))
}
