import type {
  ProxmoxLxcMountChange,
  ProxmoxLxcNetChange,
  ProxmoxLxcRootfsChange,
} from '@/lib/types'

/**
 * V6.9 — the front-end mirror of the backend `ProxmoxLxcConfigCodec`. It parses
 * the compound Proxmox `net<n>` / `mp<n>` / `rootfs` option lines into the
 * structured edit models for the guided row editors, and formats them back so
 * the "raw" expander can show the *exact* line the server will generate (the
 * formatters here intentionally match the C# canonical field order).
 *
 * Options the structured model does not name are preserved verbatim in `extra`
 * so a guided edit is never lossy, and `hasUnknownOptions` lets the UI surface
 * the advanced/raw fallback when a line carries something it can't show as a
 * field.
 */

const NET_KNOWN = ['name', 'bridge', 'ip', 'gw', 'ip6', 'gw6', 'tag', 'firewall', 'rate', 'mtu', 'hwaddr', 'link_down']
const MOUNT_KNOWN = ['mp', 'size', 'ro', 'backup', 'quota', 'acl', 'shared', 'replicate', 'mountoptions']
const ROOTFS_KNOWN = ['size', 'ro', 'quota', 'acl', 'replicate', 'mountoptions']

interface Split {
  positional: string | null
  pairs: Record<string, string>
  extra: string | null
}

function split(raw: string, hasPositional: boolean, knownKeys: string[]): Split {
  const known = new Set(knownKeys)
  const pairs: Record<string, string> = {}
  const extra: string[] = []
  let positional: string | null = null
  let first = true

  for (const token of (raw ?? '').split(',')) {
    const t = token.trim()
    if (t.length === 0) { first = false; continue }
    const eq = t.indexOf('=')
    if (eq < 0) {
      if (first && hasPositional) positional = t
      else extra.push(t)
    } else {
      const k = t.slice(0, eq).trim()
      const v = t.slice(eq + 1).trim()
      if (known.has(k)) pairs[k] = v
      else extra.push(`${k}=${v}`)
    }
    first = false
  }

  return { positional, pairs, extra: extra.length === 0 ? null : extra.join(',') }
}

const num = (v: string | undefined): number | null =>
  v != null && v.trim() !== '' && Number.isFinite(Number(v)) ? Number(v) : null
const bool = (v: string | undefined): boolean | null =>
  v == null ? null : v.trim() === '1'
const str = (v: string | undefined): string | null => (v == null ? null : v)

// ── net<n> ────────────────────────────────────────────────────────────────────

export function parseNet(key: string, raw: string): ProxmoxLxcNetChange {
  const { pairs, extra } = split(raw, false, NET_KNOWN)
  return {
    key,
    name: str(pairs.name),
    bridge: str(pairs.bridge),
    ip: str(pairs.ip),
    gw: str(pairs.gw),
    ip6: str(pairs.ip6),
    gw6: str(pairs.gw6),
    tag: num(pairs.tag),
    firewall: bool(pairs.firewall),
    rate: num(pairs.rate),
    mtu: num(pairs.mtu),
    hwaddr: str(pairs.hwaddr),
    linkDown: bool(pairs.link_down),
    extra,
  }
}

export function formatNet(c: ProxmoxLxcNetChange): string {
  if (c.raw && c.raw.trim() !== '') return c.raw.trim()
  const b = new OptionBuilder()
  b.add('name', c.name)
  b.add('hwaddr', c.hwaddr)
  b.add('bridge', c.bridge)
  b.add('ip', c.ip)
  b.add('gw', c.gw)
  b.add('ip6', c.ip6)
  b.add('gw6', c.gw6)
  b.addNum('tag', c.tag)
  b.addBool('firewall', c.firewall)
  b.addNum('rate', c.rate)
  b.addNum('mtu', c.mtu)
  b.addBool('link_down', c.linkDown)
  b.addExtra(c.extra)
  return b.build()
}

// ── mp<n> ─────────────────────────────────────────────────────────────────────

export function parseMount(key: string, raw: string): ProxmoxLxcMountChange {
  const { positional, pairs, extra } = split(raw, true, MOUNT_KNOWN)
  return {
    key,
    volume: positional,
    mountPoint: str(pairs.mp),
    size: str(pairs.size),
    readOnly: bool(pairs.ro),
    backup: bool(pairs.backup),
    quota: bool(pairs.quota),
    acl: bool(pairs.acl),
    shared: bool(pairs.shared),
    replicate: bool(pairs.replicate),
    mountOptions: str(pairs.mountoptions),
    extra,
  }
}

export function formatMount(c: ProxmoxLxcMountChange): string {
  if (c.raw && c.raw.trim() !== '') return c.raw.trim()
  const b = new OptionBuilder()
  b.addPositional(c.volume)
  b.add('mp', c.mountPoint)
  b.add('size', c.size)
  b.add('mountoptions', c.mountOptions)
  b.addBool('ro', c.readOnly)
  b.addBool('backup', c.backup)
  b.addBool('quota', c.quota)
  b.addBool('acl', c.acl)
  b.addBool('shared', c.shared)
  b.addBool('replicate', c.replicate)
  b.addExtra(c.extra)
  return b.build()
}

// ── rootfs ──────────────────────────────────────────────────────────────────────

export function parseRootfs(raw: string): ProxmoxLxcRootfsChange {
  const { positional, pairs, extra } = split(raw, true, ROOTFS_KNOWN)
  return {
    volume: positional,
    size: str(pairs.size),
    readOnly: bool(pairs.ro),
    quota: bool(pairs.quota),
    acl: bool(pairs.acl),
    replicate: bool(pairs.replicate),
    mountOptions: str(pairs.mountoptions),
    extra,
  }
}

export function formatRootfs(c: ProxmoxLxcRootfsChange): string {
  if (c.raw && c.raw.trim() !== '') return c.raw.trim()
  const b = new OptionBuilder()
  b.addPositional(c.volume)
  b.add('size', c.size)
  b.add('mountoptions', c.mountOptions)
  b.addBool('ro', c.readOnly)
  b.addBool('quota', c.quota)
  b.addBool('acl', c.acl)
  b.addBool('replicate', c.replicate)
  b.addExtra(c.extra)
  return b.build()
}

/** True when a parsed line carried options Stashboard does not model as fields —
 *  the cue to surface the advanced/raw fallback rather than risk a lossy edit. */
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
