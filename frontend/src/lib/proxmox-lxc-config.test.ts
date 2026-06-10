import { describe, it, expect } from 'vitest'
import {
  formatMount,
  formatNet,
  formatRootfs,
  hasUnknownOptions,
  parseMount,
  parseNet,
  parseRootfs,
} from './proxmox-lxc-config'

/** Order-independent view of an option line (the formatter emits canonical
 *  order; Proxmox doesn't care). */
const optionSet = (raw: string) => new Set(raw.split(',').map((s) => s.trim()).filter(Boolean))

describe('proxmox-lxc-config codec — V6.9', () => {
  it('parses a net<n> line into structured fields', () => {
    const n = parseNet('net0', 'name=eth0,bridge=vmbr0,ip=192.168.1.5/24,gw=192.168.1.1,firewall=1,tag=10,mtu=1400')
    expect(n.key).toBe('net0')
    expect(n.name).toBe('eth0')
    expect(n.bridge).toBe('vmbr0')
    expect(n.ip).toBe('192.168.1.5/24')
    expect(n.gw).toBe('192.168.1.1')
    expect(n.firewall).toBe(true)
    expect(n.tag).toBe(10)
    expect(n.mtu).toBe(1400)
    expect(n.extra).toBeNull()
  })

  it('preserves unknown net options as extra and flags them', () => {
    const n = parseNet('net0', 'name=eth0,bridge=vmbr0,trunks=2-4')
    expect(n.extra).toBe('trunks=2-4')
    expect(hasUnknownOptions(n.extra)).toBe(true)
    expect(hasUnknownOptions(null)).toBe(false)
  })

  it('round-trips a net line without losing options', () => {
    const raw = 'name=eth0,bridge=vmbr0,ip=dhcp,firewall=1,trunks=2-4'
    expect(optionSet(formatNet(parseNet('net0', raw)))).toEqual(optionSet(raw))
  })

  it('net raw mode wins verbatim', () => {
    expect(formatNet({ key: 'net0', raw: 'name=eth0,weird=1', name: 'ignored' })).toBe('name=eth0,weird=1')
  })

  it('parses a mp<n> line including the positional volume', () => {
    const m = parseMount('mp0', 'local-lvm:vm-101-disk-1,mp=/data,size=20G,backup=1,ro=0')
    expect(m.volume).toBe('local-lvm:vm-101-disk-1')
    expect(m.mountPoint).toBe('/data')
    expect(m.size).toBe('20G')
    expect(m.backup).toBe(true)
    expect(m.readOnly).toBe(false)
  })

  it('round-trips a bind mount, keeping the volume first', () => {
    const raw = '/host/path,mp=/inside,ro=1'
    const formatted = formatMount(parseMount('mp1', raw))
    expect(optionSet(formatted)).toEqual(optionSet(raw))
    expect(formatted.startsWith('/host/path,')).toBe(true)
  })

  it('parses + round-trips rootfs', () => {
    const r = parseRootfs('local-lvm:vm-101-disk-0,size=8G,acl=1')
    expect(r.volume).toBe('local-lvm:vm-101-disk-0')
    expect(r.size).toBe('8G')
    expect(r.acl).toBe(true)
    expect(optionSet(formatRootfs(r))).toEqual(optionSet('local-lvm:vm-101-disk-0,size=8G,acl=1'))
  })
})
