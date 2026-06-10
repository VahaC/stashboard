import type { ProxmoxConnection, ProxmoxGuest, ProxmoxGuestType } from './types'

/** V6.10 — pure filter/aggregation helpers backing the Proxmox page's
 *  Docker-parity surface (search, state/monitoring filters, summary strip,
 *  connection switcher, deep-link). Kept free of React so they can be unit
 *  tested directly, mirroring how the Docker page's predicates live inline. */

/** State filter segmented control, mirroring the Docker page. */
export type ProxmoxStateFilter = 'all' | 'running' | 'stopped'

/** Monitoring filter chips, driven by `monitoringEnabled` / `pendingUpdates`. */
export type ProxmoxMonitoringFilter = 'all' | 'enabled' | 'disabled' | 'updates'

/** V6.14 — guest-type filter: all guests, LXC containers only, or QEMU VMs only. */
export type ProxmoxTypeFilter = 'all' | 'lxc' | 'vm'

/** A node card vs an LXC/VM card. Mirrors the backend `ProxmoxGuestType` enum,
 *  tolerating both the string and numeric wire encodings. */
export const isProxmoxNode = (t: ProxmoxGuestType) => t === 'Node' || t === 0

/** V6.14 — a QEMU VM card (vs an LXC container). */
export const isProxmoxQemu = (t: ProxmoxGuestType) => t === 'Qemu' || t === 2

export interface ProxmoxGuestFilters {
  search: string
  state: ProxmoxStateFilter
  monitoring: ProxmoxMonitoringFilter
  /** V6.14 — LXC vs VM. */
  type: ProxmoxTypeFilter
}

/** Does this LXC guest pass the page filters? Nodes are never run through this
 *  — they render as the host summary above the grid (the Docker `host-card`
 *  analogue) and aren't part of the searchable/filterable card set. */
export function guestMatchesFilters(guest: ProxmoxGuest, filters: ProxmoxGuestFilters): boolean {
  const search = filters.search.trim().toLowerCase()
  if (search && !guest.name.toLowerCase().includes(search)) return false

  if (filters.state === 'running' && !guest.isRunning) return false
  if (filters.state === 'stopped' && guest.isRunning) return false

  // V6.14 — LXC vs VM. A VM is `Qemu`; everything else in the card set is an LXC.
  if (filters.type === 'vm' && !isProxmoxQemu(guest.guestType)) return false
  if (filters.type === 'lxc' && isProxmoxQemu(guest.guestType)) return false

  switch (filters.monitoring) {
    case 'enabled':
      return guest.monitoringEnabled
    case 'disabled':
      return !guest.monitoringEnabled
    case 'updates':
      return guest.monitoringEnabled && (guest.pendingUpdates ?? 0) > 0
    default:
      return true
  }
}

/** V6.11 — is the guest inside an active maintenance-window snooze at `now`
 *  (epoch ms)? A snoozed guest is muted on the dashboard and skipped by checks
 *  exactly like monitoring-off, until the window passes. */
export function isSnoozeActive(guest: ProxmoxGuest, now: number = Date.now()): boolean {
  return !!guest.monitoringSnoozedUntil && new Date(guest.monitoringSnoozedUntil).getTime() > now
}

/** V6.11 — is this guest eligible for the host-wide bulk "Update now"? It must be
 *  running, monitored, not snoozed, and currently carry a positive pending count
 *  (the chosen scope: only guests with pending updates). The node row qualifies
 *  too — a host-wide "Update all" sweeps the node together with its LXCs (it's
 *  always running + monitored, so the same predicate covers it). */
export function isBulkUpdateEligible(guest: ProxmoxGuest, now: number = Date.now()): boolean {
  return guest.isRunning
    && guest.monitoringEnabled
    && !isSnoozeActive(guest, now)
    && (guest.pendingUpdates ?? 0) > 0
}

export interface ProxmoxTotals {
  /** Every discovered card across hosts (nodes + LXCs). */
  objects: number
  running: number
  stopped: number
  /** Sum of pending package updates across every card. */
  updates: number
  hosts: number
}

/** Cross-host roll-up for the summary strip — the Proxmox analogue of the
 *  Docker `dock-summary` tiles. `objects === running + stopped` always holds,
 *  so the tiles stay internally consistent. */
export function computeProxmoxTotals(connections: ProxmoxConnection[]): ProxmoxTotals {
  let objects = 0
  let running = 0
  let stopped = 0
  let updates = 0
  for (const connection of connections) {
    for (const guest of connection.guests) {
      objects++
      if (guest.isRunning) running++
      else stopped++
      updates += guest.pendingUpdates ?? 0
    }
  }
  return { objects, running, stopped, updates, hosts: connections.length }
}

export interface ProxmoxConnStats {
  running: number
  total: number
  updates: number
  /** Best-effort online signal — a connection-level error dims the dot. */
  online: boolean
}

/** Per-host stats for one connection's switcher chip (running/total + updates),
 *  mirroring the Docker switcher's running/total + update badge. */
export function connectionSwitcherStats(connection: ProxmoxConnection): ProxmoxConnStats {
  let running = 0
  let updates = 0
  for (const guest of connection.guests) {
    if (guest.isRunning) running++
    updates += guest.pendingUpdates ?? 0
  }
  return { running, total: connection.guests.length, updates, online: !connection.lastError }
}

export interface ProxmoxDeepLinkTarget {
  connection: ProxmoxConnection
  guest: ProxmoxGuest
}

/** Resolve a `?connection=…&vmid=…` deep link to the LXC card it should open,
 *  or `null` when the params are absent / malformed / don't match a live guest.
 *  Nodes are excluded — the deep link targets the LXC modal specifically. */
export function findDeepLinkGuest(
  connections: ProxmoxConnection[],
  connectionId: string | null,
  vmid: string | null,
): ProxmoxDeepLinkTarget | null {
  if (!connectionId || !vmid) return null
  const id = Number(vmid)
  if (!Number.isInteger(id)) return null
  const connection = connections.find((c) => c.id === connectionId)
  if (!connection) return null
  const guest = connection.guests.find((g) => !isProxmoxNode(g.guestType) && g.vmId === id)
  if (!guest) return null
  return { connection, guest }
}
