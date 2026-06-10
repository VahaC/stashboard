import { describe, it, expect } from 'vitest'
import {
  computeProxmoxTotals,
  connectionSwitcherStats,
  findDeepLinkGuest,
  guestMatchesFilters,
  isBulkUpdateEligible,
  isProxmoxNode,
  isProxmoxQemu,
  isSnoozeActive,
  type ProxmoxGuestFilters,
} from './proxmox-page'
import type { ProxmoxConnection, ProxmoxGuest } from './types'

/**
 * V6.10 — predicates + roll-ups backing the Proxmox page's Docker-parity
 * surface: search / state / monitoring filters, the summary strip totals, the
 * switcher chip stats, and the `?connection=&vmid=` deep link.
 */

const guest = (over: Partial<ProxmoxGuest> = {}): ProxmoxGuest => ({
  vmId: 100,
  name: 'web',
  guestType: 'Lxc',
  isRunning: true,
  pendingUpdates: 0,
  lastError: null,
  ipAddress: null,
  uptimeSeconds: null,
  cpuCores: null,
  memoryBytes: null,
  diskBytes: null,
  tags: [],
  lastCheckedUtc: null,
  monitoringEnabled: true,
  monitoringSnoozedUntil: null,
  ...over,
})

const node = (over: Partial<ProxmoxGuest> = {}): ProxmoxGuest =>
  guest({ vmId: 0, name: 'pve', guestType: 'Node', isRunning: true, ...over })

const connection = (over: Partial<ProxmoxConnection> = {}): ProxmoxConnection =>
  ({
    id: 'c1',
    name: 'Host 1',
    lastError: null,
    guests: [],
    ...over,
  }) as ProxmoxConnection

const vm = (over: Partial<ProxmoxGuest> = {}): ProxmoxGuest =>
  guest({ vmId: 200, name: 'win11', guestType: 'Qemu', ...over })

const filters = (over: Partial<ProxmoxGuestFilters> = {}): ProxmoxGuestFilters => ({
  search: '',
  state: 'all',
  monitoring: 'all',
  type: 'all',
  ...over,
})

describe('isProxmoxNode', () => {
  it('recognises both the string and numeric encodings', () => {
    expect(isProxmoxNode('Node')).toBe(true)
    expect(isProxmoxNode(0)).toBe(true)
    expect(isProxmoxNode('Lxc')).toBe(false)
    expect(isProxmoxNode(1)).toBe(false)
  })
})

describe('isProxmoxQemu', () => {
  it('recognises both the string and numeric encodings (V6.14)', () => {
    expect(isProxmoxQemu('Qemu')).toBe(true)
    expect(isProxmoxQemu(2)).toBe(true)
    expect(isProxmoxQemu('Lxc')).toBe(false)
    expect(isProxmoxQemu(1)).toBe(false)
    expect(isProxmoxQemu('Node')).toBe(false)
  })
})

describe('guestMatchesFilters', () => {
  it('matches by name substring, case-insensitively', () => {
    const g = guest({ name: 'PiHole' })
    expect(guestMatchesFilters(g, filters({ search: 'hole' }))).toBe(true)
    expect(guestMatchesFilters(g, filters({ search: 'PIHOLE' }))).toBe(true)
    expect(guestMatchesFilters(g, filters({ search: 'nginx' }))).toBe(false)
  })

  it('ignores surrounding whitespace in the search term', () => {
    expect(guestMatchesFilters(guest({ name: 'web' }), filters({ search: '  web  ' }))).toBe(true)
  })

  it('filters by running / stopped state', () => {
    const running = guest({ isRunning: true })
    const stopped = guest({ isRunning: false })
    expect(guestMatchesFilters(running, filters({ state: 'running' }))).toBe(true)
    expect(guestMatchesFilters(running, filters({ state: 'stopped' }))).toBe(false)
    expect(guestMatchesFilters(stopped, filters({ state: 'stopped' }))).toBe(true)
    expect(guestMatchesFilters(stopped, filters({ state: 'running' }))).toBe(false)
  })

  it('filters by monitoring enabled / disabled', () => {
    const on = guest({ monitoringEnabled: true })
    const off = guest({ monitoringEnabled: false })
    expect(guestMatchesFilters(on, filters({ monitoring: 'enabled' }))).toBe(true)
    expect(guestMatchesFilters(on, filters({ monitoring: 'disabled' }))).toBe(false)
    expect(guestMatchesFilters(off, filters({ monitoring: 'disabled' }))).toBe(true)
    expect(guestMatchesFilters(off, filters({ monitoring: 'enabled' }))).toBe(false)
  })

  it('"updates" chip needs monitoring on AND a positive pending count', () => {
    expect(guestMatchesFilters(guest({ pendingUpdates: 3 }), filters({ monitoring: 'updates' }))).toBe(true)
    expect(guestMatchesFilters(guest({ pendingUpdates: 0 }), filters({ monitoring: 'updates' }))).toBe(false)
    expect(guestMatchesFilters(guest({ pendingUpdates: null }), filters({ monitoring: 'updates' }))).toBe(false)
    // monitoring off ⇒ never an "updates" match even with a stale count
    expect(
      guestMatchesFilters(guest({ pendingUpdates: 3, monitoringEnabled: false }), filters({ monitoring: 'updates' })),
    ).toBe(false)
  })

  it('applies every active filter together (AND)', () => {
    const g = guest({ name: 'db', isRunning: true, monitoringEnabled: true, pendingUpdates: 2 })
    expect(guestMatchesFilters(g, filters({ search: 'db', state: 'running', monitoring: 'updates' }))).toBe(true)
    expect(guestMatchesFilters(g, filters({ search: 'db', state: 'stopped', monitoring: 'updates' }))).toBe(false)
  })

  it('filters by guest type (LXC vs VM) — V6.14', () => {
    const ct = guest({ guestType: 'Lxc' })
    const machine = vm()
    expect(guestMatchesFilters(ct, filters({ type: 'lxc' }))).toBe(true)
    expect(guestMatchesFilters(ct, filters({ type: 'vm' }))).toBe(false)
    expect(guestMatchesFilters(machine, filters({ type: 'vm' }))).toBe(true)
    expect(guestMatchesFilters(machine, filters({ type: 'lxc' }))).toBe(false)
    // 'all' keeps both
    expect(guestMatchesFilters(ct, filters({ type: 'all' }))).toBe(true)
    expect(guestMatchesFilters(machine, filters({ type: 'all' }))).toBe(true)
  })
})

describe('computeProxmoxTotals', () => {
  it('aggregates objects / running / stopped / updates across hosts', () => {
    const conns = [
      connection({
        id: 'a',
        guests: [
          node({ pendingUpdates: 1 }),
          guest({ vmId: 101, isRunning: true, pendingUpdates: 2 }),
          guest({ vmId: 102, isRunning: false, pendingUpdates: null }),
        ],
      }),
      connection({
        id: 'b',
        guests: [node({ pendingUpdates: 0 }), guest({ vmId: 201, isRunning: true, pendingUpdates: 3 })],
      }),
    ]
    const totals = computeProxmoxTotals(conns)
    expect(totals).toEqual({ objects: 5, running: 4, stopped: 1, updates: 6, hosts: 2 })
    // internal consistency the summary tiles rely on
    expect(totals.running + totals.stopped).toBe(totals.objects)
  })

  it('is zeroed for no hosts', () => {
    expect(computeProxmoxTotals([])).toEqual({ objects: 0, running: 0, stopped: 0, updates: 0, hosts: 0 })
  })
})

describe('connectionSwitcherStats', () => {
  it('counts running/total + pending updates for one host', () => {
    const c = connection({
      guests: [
        node({ pendingUpdates: 1 }),
        guest({ vmId: 101, isRunning: true, pendingUpdates: 0 }),
        guest({ vmId: 102, isRunning: false, pendingUpdates: 4 }),
      ],
    })
    expect(connectionSwitcherStats(c)).toEqual({ running: 2, total: 3, updates: 5, online: true })
  })

  it('marks a host with a connection error offline', () => {
    expect(connectionSwitcherStats(connection({ lastError: 'auth failed', guests: [] })).online).toBe(false)
  })
})

describe('findDeepLinkGuest', () => {
  const conns = [
    connection({ id: 'c1', guests: [node(), guest({ vmId: 101, name: 'web' })] }),
    connection({ id: 'c2', guests: [node(), guest({ vmId: 202, name: 'db' })] }),
  ]

  it('resolves the matching LXC in the named connection', () => {
    const hit = findDeepLinkGuest(conns, 'c2', '202')
    expect(hit?.connection.id).toBe('c2')
    expect(hit?.guest.name).toBe('db')
  })

  it('never resolves to a node card', () => {
    expect(findDeepLinkGuest(conns, 'c1', '0')).toBeNull()
  })

  it('returns null for missing / malformed params', () => {
    expect(findDeepLinkGuest(conns, null, '101')).toBeNull()
    expect(findDeepLinkGuest(conns, 'c1', null)).toBeNull()
    expect(findDeepLinkGuest(conns, 'c1', 'abc')).toBeNull()
    expect(findDeepLinkGuest(conns, 'nope', '101')).toBeNull()
    expect(findDeepLinkGuest(conns, 'c1', '999')).toBeNull()
  })
})

describe('isSnoozeActive', () => {
  const now = Date.parse('2026-06-06T12:00:00Z')

  it('is false when not snoozed', () => {
    expect(isSnoozeActive(guest({ monitoringSnoozedUntil: null }), now)).toBe(false)
  })

  it('is true while the window is in the future', () => {
    expect(isSnoozeActive(guest({ monitoringSnoozedUntil: '2026-06-06T14:00:00Z' }), now)).toBe(true)
  })

  it('is false once the window has elapsed', () => {
    expect(isSnoozeActive(guest({ monitoringSnoozedUntil: '2026-06-06T10:00:00Z' }), now)).toBe(false)
  })
})

describe('isBulkUpdateEligible', () => {
  const now = Date.parse('2026-06-06T12:00:00Z')

  it('is true for a running, monitored, un-snoozed LXC with pending updates', () => {
    expect(isBulkUpdateEligible(guest({ pendingUpdates: 3 }), now)).toBe(true)
  })

  it('includes the node row when it has pending updates (host-wide update all)', () => {
    expect(isBulkUpdateEligible(node({ pendingUpdates: 3 }), now)).toBe(true)
    expect(isBulkUpdateEligible(node({ pendingUpdates: 0 }), now)).toBe(false)
  })

  it('excludes stopped, monitoring-off, snoozed, and up-to-date guests', () => {
    expect(isBulkUpdateEligible(guest({ pendingUpdates: 3, isRunning: false }), now)).toBe(false)
    expect(isBulkUpdateEligible(guest({ pendingUpdates: 3, monitoringEnabled: false }), now)).toBe(false)
    expect(isBulkUpdateEligible(guest({ pendingUpdates: 3, monitoringSnoozedUntil: '2026-06-06T14:00:00Z' }), now)).toBe(false)
    expect(isBulkUpdateEligible(guest({ pendingUpdates: 0 }), now)).toBe(false)
    expect(isBulkUpdateEligible(guest({ pendingUpdates: null }), now)).toBe(false)
  })
})
