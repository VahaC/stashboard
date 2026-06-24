import type { ProxmoxQemuDiskChange, ProxmoxQemuNetChange } from '@/lib/types'

/**
 * V8.5 — the front-end mirror of the backend `ProxmoxQemuConfigCodec`. It parses the
 * compound Proxmox QEMU `net<n>` and disk (`scsi<n>` / `virtio<n>` / …) option lines
 * into the structured edit models for the guided row editors, and formats them back so
 * the "raw" expander can show the *exact* line the server will generate (the formatters
 * here intentionally match the C# canonical field order).
 *
 * A VM NIC differs from an LXC one: its first token is the device *model* carrying the
 * MAC as its value (`virtio=AA:BB:CC:DD:EE:FF,bridge=vmbr0,tag=10`), and a VM has no
 * in-config IP. Options the structured model doesn't name are preserved verbatim in
 * `extra` so a guided edit is never lossy.
 */

export const QEMU_NET_MODELS = [
  'virtio', 'e1000', 'e1000e', 'rtl8139', 'vmxnet3', 'ne2k_pci', 'pcnet', 'i82551', 'i82557b', 'i82559er', 'ne2k_isa',
] as const

/** Curated QEMU OS-type hints (Proxmox `ostype`), friendly label → value. Mirrors the
 *  set the Proxmox web UI offers; the value is what `…/qemu` accepts. Shared by the V8.4
 *  create modal and the V8.5 VM Config editor so create + edit offer the same set. */
export const OS_TYPES: { value: string; label: string }[] = [
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

const NET_KNOWN = ['bridge', 'tag', 'firewall', 'rate', 'mtu', 'queues', 'link_down']
const DISK_KNOWN = ['size', 'ssd', 'discard', 'cache']

const num = (v: string | undefined): number | null =>
  v != null && v.trim() !== '' && Number.isFinite(Number(v)) ? Number(v) : null
const bool = (v: string | undefined): boolean | null => (v == null ? null : v.trim() === '1')
const str = (v: string | undefined): string | null => (v == null ? null : v)

// ── net<n> ──────────────────────────────────────────────────────────────────────

export function parseQemuNet(key: string, raw: string): ProxmoxQemuNetChange {
  const models = new Set<string>(QEMU_NET_MODELS)
  const known = new Set(NET_KNOWN)
  const pairs: Record<string, string> = {}
  const extra: string[] = []
  let model: string | null = null
  let mac: string | null = null

  for (const token of (raw ?? '').split(',')) {
    const t = token.trim()
    if (t.length === 0) continue
    const eq = t.indexOf('=')
    if (eq < 0) {
      if (model === null && models.has(t)) model = t
      else extra.push(t)
      continue
    }
    const k = t.slice(0, eq).trim()
    const v = t.slice(eq + 1).trim()
    if (model === null && models.has(k)) { model = k; mac = v }
    else if (known.has(k)) pairs[k] = v
    else extra.push(`${k}=${v}`)
  }

  return {
    key,
    model,
    macAddr: mac,
    bridge: str(pairs.bridge),
    tag: num(pairs.tag),
    firewall: bool(pairs.firewall),
    rate: num(pairs.rate),
    mtu: num(pairs.mtu),
    queues: num(pairs.queues),
    linkDown: bool(pairs.link_down),
    extra: extra.length === 0 ? null : extra.join(','),
  }
}

export function formatQemuNet(c: ProxmoxQemuNetChange): string {
  if (c.raw && c.raw.trim() !== '') return c.raw.trim()
  const b = new OptionBuilder()
  const model = c.model && c.model.trim() !== '' ? c.model.trim() : 'virtio'
  b.addPositional(c.macAddr && c.macAddr.trim() !== '' ? `${model}=${c.macAddr.trim()}` : model)
  b.add('bridge', c.bridge)
  b.addNum('tag', c.tag)
  b.addBool('firewall', c.firewall)
  b.addNum('rate', c.rate)
  b.addNum('mtu', c.mtu)
  b.addNum('queues', c.queues)
  b.addBool('link_down', c.linkDown)
  b.addExtra(c.extra)
  return b.build()
}

// ── disk (scsi<n> / virtio<n> / sata<n> / ide<n>) ─────────────────────────────────

export function parseQemuDisk(key: string, raw: string): ProxmoxQemuDiskChange {
  const known = new Set(DISK_KNOWN)
  const pairs: Record<string, string> = {}
  const extra: string[] = []
  let volume: string | null = null
  let first = true

  for (const token of (raw ?? '').split(',')) {
    const t = token.trim()
    if (t.length === 0) { first = false; continue }
    const eq = t.indexOf('=')
    if (eq < 0) {
      if (first && volume === null) volume = t
      else extra.push(t)
    } else {
      const k = t.slice(0, eq).trim()
      const v = t.slice(eq + 1).trim()
      if (known.has(k)) pairs[k] = v
      else extra.push(`${k}=${v}`)
    }
    first = false
  }

  return {
    key,
    volume,
    size: str(pairs.size),
    discard: pairs.discard != null ? pairs.discard.trim() === 'on' : null,
    ssd: bool(pairs.ssd),
    cache: str(pairs.cache),
    extra: extra.length === 0 ? null : extra.join(','),
  }
}

export function formatQemuDisk(c: ProxmoxQemuDiskChange): string {
  if (c.raw && c.raw.trim() !== '') return c.raw.trim()
  const b = new OptionBuilder()
  b.addPositional(c.volume)
  b.add('size', c.size)
  if (c.discard === true) b.addPositional('discard=on')
  if (c.ssd === true) b.addPositional('ssd=1')
  b.add('cache', c.cache)
  b.addExtra(c.extra)
  return b.build()
}

/** A disk line is a CD-ROM drive when it carries `media=cdrom` — handled by the CD-ROM
 *  editor rather than the disk grow/move/flags editor. */
export function isCdromLine(value: string): boolean {
  return value.split(',').some((t) => t.trim() === 'media=cdrom')
}

/** The positional volid of an `ide2`-style CD-ROM line, or '' when the drive is empty
 *  (`none` / `cdrom`). */
export function cdromVolid(value: string): string {
  const first = value.split(',')[0]?.trim() ?? ''
  if (first === '' || first === 'none' || first === 'cdrom') return ''
  return first
}

export function hasUnknownOptions(extra: string | null | undefined): boolean {
  return !!extra && extra.trim() !== ''
}

class OptionBuilder {
  private parts: string[] = []

  addPositional(value: string | null | undefined) {
    if (value && value.trim() !== '') this.parts.push(value.trim())
  }
  add(key: string, value: string | null | undefined) {
    if (value && value.trim() !== '') this.parts.push(`${key}=${value.trim()}`)
  }
  addNum(key: string, value: number | null | undefined) {
    if (value != null) this.parts.push(`${key}=${value}`)
  }
  addBool(key: string, value: boolean | null | undefined) {
    if (value != null) this.parts.push(`${key}=${value ? '1' : '0'}`)
  }
  addExtra(extra: string | null | undefined) {
    if (!extra || extra.trim() === '') return
    for (const token of extra.split(',')) {
      const t = token.trim()
      if (t) this.parts.push(t)
    }
  }
  build(): string {
    return this.parts.join(',')
  }
}
