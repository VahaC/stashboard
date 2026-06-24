import { useMutation, useQuery, useQueryClient, type Query } from '@tanstack/react-query'
import { api } from './api'
import { readFileAsDataUrl } from './utils'
import type {
  ProxmoxConnection,
  ProxmoxConnectionPingRequest,
  ProxmoxConnectionPingResponse,
  ProxmoxConnectionUpsert,
  ProxmoxDiskSelfTest,
  ProxmoxCloneAudit,
  ProxmoxLxcAction,
  ProxmoxLxcClone,
  ProxmoxLxcConfigUpdate,
  ProxmoxLxcCreate,
  ProxmoxLxcDetail,
  ProxmoxSnapshot,
  ProxmoxSnapshotCreate,
  ProxmoxLxcRrdPoint,
  ProxmoxLxcStatus,
  ProxmoxNextId,
  ProxmoxTemplate,
  ProxmoxBackup,
  ProxmoxLxcRestore,
  ProxmoxNodeAlert,
  ProxmoxNodeAlertSettings,
  ProxmoxNodeAlertSettingsUpdate,
  ProxmoxNodeCpuStats,
  ProxmoxNodeDisk,
  ProxmoxNodeDiskIo,
  ProxmoxNodeDiskSmart,
  ProxmoxNodeInterfaceStats,
  ProxmoxNodeNetworkInterface,
  ProxmoxNodeRrdPoint,
  ProxmoxNodeSensors,
  ProxmoxNodeStatus,
  ProxmoxNodeStorage,
  ProxmoxNodeThinPools,
  ProxmoxTask,
} from './types'

/** V6.3 — RRD timeframes the Stats tab can request. */
export type ProxmoxRrdTimeframe = 'hour' | 'day' | 'week'

/** V6.14 — which guest API surface a hook targets: LXC containers (`lxc`) or
 *  QEMU VMs (`qemu`). Both expose the same status / rrddata / status-action
 *  endpoints, so the hooks differ only in this path segment. */
export type ProxmoxGuestKind = 'lxc' | 'qemu'

// ── V6.8.2 — per-connection telemetry poll interval + failure backoff ─────────

/** App default when a host has no override; within the roadmap's 15-30s band. */
const DEFAULT_POLL_SECONDS = 20
/** Backoff ceiling so an unreachable host settles at ~one poll / 2 min. */
const MAX_BACKOFF_MS = 120_000

/** V6.8.2 — a host's telemetry poll interval in ms: its override (clamped 5..300s)
 *  or the app default. */
export const telemetryPollMs = (pollSeconds: number | null | undefined): number =>
  (pollSeconds == null ? DEFAULT_POLL_SECONDS : Math.min(300, Math.max(5, pollSeconds))) * 1000

/** V6.8.2 — a react-query `refetchInterval` that polls at `baseMs` but backs off
 *  (doubling per consecutive failure, capped at {@link MAX_BACKOFF_MS}) while the
 *  query is failing, then snaps back once a fetch succeeds. Satisfies the
 *  "per-connection interval with failure backoff" requirement. */
export const backoffRefetch =
  <T>(baseMs: number) =>
  (query: Query<T, unknown>): number => {
    const failures = query.state.fetchFailureCount
    if (failures <= 0) return baseMs
    return Math.min(baseMs * 2 ** failures, MAX_BACKOFF_MS)
  }

/** V6.0 — Proxmox host query keys + hooks. Kept in their own module so the
 *  already-large `queries.ts` stays focused on the Docker/service surface. */
export const proxmoxQk = {
  connections: ['proxmox', 'connections'] as const,
  connection: (id: string) => ['proxmox', 'connections', id] as const,
  // V6.14 — keyed by guest kind (lxc / qemu) so an LXC and a VM never share a
  // cache entry. Defaults to 'lxc' for the existing call sites.
  lxcConfig: (id: string, vmId: number, kind: ProxmoxGuestKind = 'lxc') =>
    ['proxmox', 'connections', id, kind, vmId, 'config'] as const,
  // V6.13.1 — create form.
  nextId: (id: string) => ['proxmox', 'connections', id, 'lxc', 'nextid'] as const,
  templates: (id: string) => ['proxmox', 'connections', id, 'lxc', 'templates'] as const,
  // V8.1 — restore form's backup-archive dropdown.
  backups: (id: string) => ['proxmox', 'connections', id, 'lxc', 'backups'] as const,
  // V8.0 — snapshots + clone/snapshot audit (modal tabs).
  snapshots: (id: string, vmId: number) => ['proxmox', 'connections', id, 'lxc', vmId, 'snapshots'] as const,
  cloneAudit: (id: string, vmId: number) => ['proxmox', 'connections', id, 'lxc', vmId, 'clone-audit'] as const,
  lxcRrd: (id: string, vmId: number, tf: string, kind: ProxmoxGuestKind = 'lxc') =>
    ['proxmox', 'connections', id, kind, vmId, 'rrddata', tf] as const,
  lxcTasks: (id: string, vmId: number, kind: ProxmoxGuestKind = 'lxc') =>
    ['proxmox', 'connections', id, kind, vmId, 'tasks'] as const,
  taskLog: (id: string, upid: string) => ['proxmox', 'connections', id, 'tasks', upid, 'log'] as const,
  // V6.8 — PVE node card.
  nodeStatus: (id: string) => ['proxmox', 'connections', id, 'node', 'status'] as const,
  nodeRrd: (id: string, tf: string) => ['proxmox', 'connections', id, 'node', 'rrddata', tf] as const,
  nodeStorage: (id: string) => ['proxmox', 'connections', id, 'node', 'storage'] as const,
  nodeDisks: (id: string) => ['proxmox', 'connections', id, 'node', 'disks'] as const,
  nodeDiskSmart: (id: string, disk: string) => ['proxmox', 'connections', id, 'node', 'disks', 'smart', disk] as const,
  nodeNetwork: (id: string) => ['proxmox', 'connections', id, 'node', 'network'] as const,
  nodeSensors: (id: string) => ['proxmox', 'connections', id, 'node', 'sensors'] as const,
  // V6.8.2 — deep telemetry (SSH collectors).
  nodeCpu: (id: string) => ['proxmox', 'connections', id, 'node', 'cpu'] as const,
  nodeDiskIo: (id: string) => ['proxmox', 'connections', id, 'node', 'diskio'] as const,
  nodeThinPools: (id: string) => ['proxmox', 'connections', id, 'node', 'thinpools'] as const,
  nodeInterfaces: (id: string) => ['proxmox', 'connections', id, 'node', 'interfaces'] as const,
  nodeDiskSelfTest: (id: string, disk: string) =>
    ['proxmox', 'connections', id, 'node', 'disks', 'selftest', disk] as const,
  // V6.8.1 — node alerting.
  nodeAlertSettings: (id: string) => ['proxmox', 'connections', id, 'node', 'alerts', 'settings'] as const,
  nodeAlerts: (id: string) => ['proxmox', 'connections', id, 'node', 'alerts'] as const,
  // V7.8 — per-guest card icons (vmId → data URI).
  guestIcons: (id: string) => ['proxmox', 'connections', id, 'guest-icons'] as const,
}

// ── V7.8 — guest card icons ──────────────────────────────────────────────────

/** V7.8 — the resolved card avatars for a host's guests as a `vmId → data:URI`
 *  map (custom upload → official OS icon → omitted). Polls on the same cadence as
 *  the connection list so a freshly-scanned OS type / new upload appears. */
export const useProxmoxGuestIcons = (connectionId: string) =>
  useQuery({
    queryKey: proxmoxQk.guestIcons(connectionId),
    queryFn: async (): Promise<Record<string, string>> =>
      (await api.get<Record<string, string>>(`/api/proxmox/connections/${connectionId}/guest-icons`)).data,
    staleTime: 30_000,
  })

/** V7.8 — set a custom icon for a guest; refreshes the icon map. The image is
 *  sent as a base64 data URI in a JSON body (not multipart). */
export const useUploadGuestIcon = (connectionId: string) => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async ({ vmId, file }: { vmId: number; file: File }) => {
      const dataUri = await readFileAsDataUrl(file)
      await api.post(`/api/proxmox/connections/${connectionId}/guests/${vmId}/icon`, { dataUri })
    },
    onSuccess: () => qc.invalidateQueries({ queryKey: proxmoxQk.guestIcons(connectionId) }),
  })
}

/** V7.8 — reset a guest's icon back to Auto (OS / placeholder). */
export const useResetGuestIcon = (connectionId: string) => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async (vmId: number) => {
      await api.delete(`/api/proxmox/connections/${connectionId}/guests/${vmId}/icon`)
    },
    onSuccess: () => qc.invalidateQueries({ queryKey: proxmoxQk.guestIcons(connectionId) }),
  })
}

/** V6.8 — the node's health snapshot. Polled so the card + Overview track the
 *  host without a manual reload. V6.8.2 — the cadence is the host's configurable
 *  telemetry interval (default 20s) with failure backoff. */
export const useProxmoxNodeStatus = (connectionId: string, enabled = true, pollSeconds?: number | null) => {
  const base = telemetryPollMs(pollSeconds)
  return useQuery({
    queryKey: proxmoxQk.nodeStatus(connectionId),
    queryFn: async (): Promise<ProxmoxNodeStatus> =>
      (await api.get<ProxmoxNodeStatus>(`/api/proxmox/connections/${connectionId}/node/status`)).data,
    enabled,
    refetchInterval: backoffRefetch<ProxmoxNodeStatus>(base),
    staleTime: Math.min(base, 10_000),
  })
}

/** V6.8 — fetch the node's live status (used by the CPU/RAM Live poller). */
export const fetchProxmoxNodeStatus = async (connectionId: string): Promise<ProxmoxNodeStatus> =>
  (await api.get<ProxmoxNodeStatus>(`/api/proxmox/connections/${connectionId}/node/status`)).data

/** V6.8 — the node's RRD series for the CPU/RAM + Network sparklines. V6.8.2 —
 *  polled at the host's telemetry interval with failure backoff. */
export const useProxmoxNodeRrd = (
  connectionId: string, timeframe: ProxmoxRrdTimeframe, enabled = true, pollSeconds?: number | null,
) => {
  const base = telemetryPollMs(pollSeconds)
  return useQuery({
    queryKey: proxmoxQk.nodeRrd(connectionId, timeframe),
    queryFn: async (): Promise<ProxmoxNodeRrdPoint[]> =>
      (await api.get<ProxmoxNodeRrdPoint[]>(
        `/api/proxmox/connections/${connectionId}/node/rrddata?timeframe=${timeframe}`)).data,
    enabled,
    refetchInterval: backoffRefetch<ProxmoxNodeRrdPoint[]>(base),
    staleTime: Math.min(base, 15_000),
  })
}

/** V6.8 — per-storage-pool usage. */
export const useProxmoxNodeStorage = (connectionId: string, enabled = true) =>
  useQuery({
    queryKey: proxmoxQk.nodeStorage(connectionId),
    queryFn: async (): Promise<ProxmoxNodeStorage[]> =>
      (await api.get<ProxmoxNodeStorage[]>(`/api/proxmox/connections/${connectionId}/node/storage`)).data,
    enabled,
    staleTime: 15_000,
  })

// Reading SMART hits the physical drives (and can spin idle disks up), so it's
// capped at once per hour: `staleTime` 1h means reopening the tab serves cache
// instead of re-reading, and a long `gcTime` keeps that cache across modal
// close/reopen. A manual "Refresh now" (invalidateQueries) bypasses this.
const SMART_STALE_MS = 60 * 60 * 1000
const SMART_GC_MS = 2 * 60 * 60 * 1000

/** V6.8 — physical disks + SMART health summary. Capped at one physical read
 *  per hour (see {@link SMART_STALE_MS}); use a manual refresh to force one. */
export const useProxmoxNodeDisks = (connectionId: string, enabled = true) =>
  useQuery({
    queryKey: proxmoxQk.nodeDisks(connectionId),
    queryFn: async (): Promise<ProxmoxNodeDisk[]> =>
      (await api.get<ProxmoxNodeDisk[]>(`/api/proxmox/connections/${connectionId}/node/disks`)).data,
    enabled,
    staleTime: SMART_STALE_MS,
    gcTime: SMART_GC_MS,
  })

/** V6.8 — detailed SMART for one disk, fetched on demand when a disk row is
 *  expanded. Capped at one physical read per hour, like the disk list. */
export const useProxmoxNodeDiskSmart = (connectionId: string, disk: string | null) =>
  useQuery({
    queryKey: proxmoxQk.nodeDiskSmart(connectionId, disk ?? ''),
    queryFn: async (): Promise<ProxmoxNodeDiskSmart> =>
      (await api.get<ProxmoxNodeDiskSmart>(
        `/api/proxmox/connections/${connectionId}/node/disks/smart?disk=${encodeURIComponent(disk!)}`)).data,
    enabled: !!disk,
    staleTime: SMART_STALE_MS,
    gcTime: SMART_GC_MS,
  })

/** V6.8 — configured network interfaces. */
export const useProxmoxNodeNetwork = (connectionId: string, enabled = true) =>
  useQuery({
    queryKey: proxmoxQk.nodeNetwork(connectionId),
    queryFn: async (): Promise<ProxmoxNodeNetworkInterface[]> =>
      (await api.get<ProxmoxNodeNetworkInterface[]>(`/api/proxmox/connections/${connectionId}/node/network`)).data,
    enabled,
    staleTime: 30_000,
  })

/** V6.8 — CPU/board temperatures + fan RPMs (V6.8.2 — plus voltage/power) over SSH
 *  (Sensors tab). Polled at the host's telemetry interval with failure backoff. */
export const useProxmoxNodeSensors = (connectionId: string, enabled = true, pollSeconds?: number | null) => {
  const base = telemetryPollMs(pollSeconds)
  return useQuery({
    queryKey: proxmoxQk.nodeSensors(connectionId),
    queryFn: async (): Promise<ProxmoxNodeSensors> =>
      (await api.get<ProxmoxNodeSensors>(`/api/proxmox/connections/${connectionId}/node/sensors`)).data,
    enabled,
    refetchInterval: backoffRefetch<ProxmoxNodeSensors>(base),
    staleTime: Math.min(base, 15_000),
  })
}

// ── V6.8.2 — deep telemetry SSH collectors ────────────────────────────────────

/** V6.8.2 — per-core CPU utilisation + steal and MemAvailable over SSH. Polled at
 *  the host's telemetry interval with failure backoff. */
export const useProxmoxNodeCpu = (connectionId: string, enabled = true, pollSeconds?: number | null) => {
  const base = telemetryPollMs(pollSeconds)
  return useQuery({
    queryKey: proxmoxQk.nodeCpu(connectionId),
    queryFn: async (): Promise<ProxmoxNodeCpuStats> =>
      (await api.get<ProxmoxNodeCpuStats>(`/api/proxmox/connections/${connectionId}/node/cpu`)).data,
    enabled,
    refetchInterval: backoffRefetch<ProxmoxNodeCpuStats>(base),
    staleTime: Math.min(base, 10_000),
  })
}

/** V6.8.2 — per-disk IO throughput / IOPS / latency over SSH. Polled at the host's
 *  telemetry interval with failure backoff. */
export const useProxmoxNodeDiskIo = (connectionId: string, enabled = true, pollSeconds?: number | null) => {
  const base = telemetryPollMs(pollSeconds)
  return useQuery({
    queryKey: proxmoxQk.nodeDiskIo(connectionId),
    queryFn: async (): Promise<ProxmoxNodeDiskIo> =>
      (await api.get<ProxmoxNodeDiskIo>(`/api/proxmox/connections/${connectionId}/node/diskio`)).data,
    enabled,
    refetchInterval: backoffRefetch<ProxmoxNodeDiskIo>(base),
    staleTime: Math.min(base, 10_000),
  })
}

/** V6.8.2 — LVM-thin pool fill levels over SSH. Refreshed lazily (no hot poll —
 *  pools fill slowly); a manual refresh / tab reopen re-reads. */
export const useProxmoxNodeThinPools = (connectionId: string, enabled = true) =>
  useQuery({
    queryKey: proxmoxQk.nodeThinPools(connectionId),
    queryFn: async (): Promise<ProxmoxNodeThinPools> =>
      (await api.get<ProxmoxNodeThinPools>(`/api/proxmox/connections/${connectionId}/node/thinpools`)).data,
    enabled,
    staleTime: 30_000,
  })

/** V6.8.2 — per-interface throughput / errors / link over SSH. Polled at the
 *  host's telemetry interval with failure backoff. */
export const useProxmoxNodeInterfaces = (connectionId: string, enabled = true, pollSeconds?: number | null) => {
  const base = telemetryPollMs(pollSeconds)
  return useQuery({
    queryKey: proxmoxQk.nodeInterfaces(connectionId),
    queryFn: async (): Promise<ProxmoxNodeInterfaceStats> =>
      (await api.get<ProxmoxNodeInterfaceStats>(`/api/proxmox/connections/${connectionId}/node/interfaces`)).data,
    enabled,
    refetchInterval: backoffRefetch<ProxmoxNodeInterfaceStats>(base),
    staleTime: Math.min(base, 10_000),
  })
}

/** V6.8.2 — one disk's last SMART self-test + critical counters over SSH, fetched
 *  on demand when a disk row is expanded. Cached like the disk SMART read (the
 *  source spins the drive), so reopening serves cache. */
export const useProxmoxNodeDiskSelfTest = (connectionId: string, disk: string | null) =>
  useQuery({
    queryKey: proxmoxQk.nodeDiskSelfTest(connectionId, disk ?? ''),
    queryFn: async (): Promise<ProxmoxDiskSelfTest> =>
      (await api.get<ProxmoxDiskSelfTest>(
        `/api/proxmox/connections/${connectionId}/node/disks/selftest?disk=${encodeURIComponent(disk!)}`)).data,
    enabled: !!disk,
    staleTime: SMART_STALE_MS,
    gcTime: SMART_GC_MS,
  })

/** V6.2 — reads one guest's config + live status for the modal's Config tab.
 *  V6.14 — `kind` selects the LXC or QEMU surface (VM config is read-only). */
export const useProxmoxLxcConfig = (
  connectionId: string, vmId: number, enabled = true, kind: ProxmoxGuestKind = 'lxc',
) =>
  useQuery({
    queryKey: proxmoxQk.lxcConfig(connectionId, vmId, kind),
    queryFn: async (): Promise<ProxmoxLxcDetail> =>
      (await api.get<ProxmoxLxcDetail>(`/api/proxmox/connections/${connectionId}/${kind}/${vmId}/config`)).data,
    enabled,
    staleTime: 10_000,
  })

/** V6.3 — RRD series for the Stats tab; refreshes so the sparklines advance.
 *  V6.14 — `kind` selects the LXC or QEMU series (identical sample shape). */
export const useProxmoxLxcRrd = (
  connectionId: string, vmId: number, timeframe: ProxmoxRrdTimeframe, enabled = true,
  kind: ProxmoxGuestKind = 'lxc',
) =>
  useQuery({
    queryKey: proxmoxQk.lxcRrd(connectionId, vmId, timeframe, kind),
    queryFn: async (): Promise<ProxmoxLxcRrdPoint[]> =>
      (await api.get<ProxmoxLxcRrdPoint[]>(
        `/api/proxmox/connections/${connectionId}/${kind}/${vmId}/rrddata?timeframe=${timeframe}`)).data,
    enabled,
    refetchInterval: 30_000,
    staleTime: 15_000,
  })

/** V6.3 — recent tasks scoped to one guest. V6.14 — `kind` selects LXC or QEMU
 *  (tasks are vmid-scoped, so the two only differ by path segment). */
export const useProxmoxLxcTasks = (
  connectionId: string, vmId: number, enabled = true, kind: ProxmoxGuestKind = 'lxc',
) =>
  useQuery({
    queryKey: proxmoxQk.lxcTasks(connectionId, vmId, kind),
    queryFn: async (): Promise<ProxmoxTask[]> =>
      (await api.get<ProxmoxTask[]>(`/api/proxmox/connections/${connectionId}/${kind}/${vmId}/tasks`)).data,
    enabled,
    staleTime: 10_000,
  })

/** V6.3 — one task's log, fetched on demand when a task row is expanded. */
export const useProxmoxTaskLog = (connectionId: string, upid: string | null) =>
  useQuery({
    queryKey: proxmoxQk.taskLog(connectionId, upid ?? ''),
    queryFn: async (): Promise<string> =>
      (await api.get<{ log: string }>(
        `/api/proxmox/connections/${connectionId}/tasks/log?upid=${encodeURIComponent(upid!)}`)).data.log,
    enabled: !!upid,
    staleTime: 60_000,
  })

/** V6.4 — fetch one guest's live status (used by the real-time Stats poller).
 *  V6.14 — `kind` selects the LXC or QEMU endpoint. */
export const fetchProxmoxLxcStatus = async (
  connectionId: string, vmId: number, kind: ProxmoxGuestKind = 'lxc',
): Promise<ProxmoxLxcStatus> =>
  (await api.get<ProxmoxLxcStatus>(`/api/proxmox/connections/${connectionId}/${kind}/${vmId}/status`)).data

/** V6.4 — start / stop / shutdown / reboot a guest. The endpoint optimistically
 *  updates the guest's persisted running state from the verb and returns the
 *  refreshed host, so the card flips immediately rather than waiting for the next
 *  scheduled scan. V6.14 — `kind` routes to the LXC or QEMU lifecycle path. */
export const useProxmoxLxcAction = (connectionId: string, kind: ProxmoxGuestKind = 'lxc') => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async (args: { vmId: number; action: ProxmoxLxcAction }) =>
      (await api.post<ProxmoxConnection>(
        `/api/proxmox/connections/${connectionId}/${kind}/${args.vmId}/status/${args.action}`)).data,
    onSuccess: (updated) => {
      qc.setQueryData<ProxmoxConnection[]>(proxmoxQk.connections, (prev) =>
        prev ? prev.map((c) => (c.id === updated.id ? updated : c)) : prev)
    },
  })
}

/** V6.13.1 — light live-status sync for a host's LXC cards. Polls a cheap
 *  endpoint (one `GET /nodes/{node}/lxc`, no SSH) that updates each guest's
 *  running state / uptime / resources, so a container started or stopped outside
 *  Stashboard shows up within the poll interval instead of waiting for the next
 *  scheduled scan. Persists server-side and writes the refreshed host straight
 *  into the connections cache so the card flips. Backs off while a host is
 *  unreachable. */
export const useSyncProxmoxLxcStatuses = (connectionId: string, enabled = true) => {
  const qc = useQueryClient()
  return useQuery({
    queryKey: ['proxmox', 'connections', connectionId, 'lxc', 'sync'] as const,
    queryFn: async (): Promise<ProxmoxConnection> => {
      const updated = (await api.post<ProxmoxConnection>(
        `/api/proxmox/connections/${connectionId}/lxc/sync`)).data
      qc.setQueryData<ProxmoxConnection[]>(proxmoxQk.connections, (prev) =>
        prev ? prev.map((c) => (c.id === updated.id ? updated : c)) : prev)
      return updated
    },
    enabled,
    refetchInterval: backoffRefetch<ProxmoxConnection>(20_000),
    staleTime: 10_000,
  })
}

/** V6.13 — destroy a (stopped) LXC. Irreversible; gated server-side (global
 *  switch + per-host opt-in + stopped guest). On success the host list refreshes
 *  so the destroyed card disappears. V6.14 — `kind` routes to the LXC or QEMU
 *  destroy endpoint. */
export const useDestroyProxmoxLxc = (connectionId: string, kind: ProxmoxGuestKind = 'lxc') => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async (vmId: number) =>
      api.delete(`/api/proxmox/connections/${connectionId}/${kind}/${vmId}`),
    onSuccess: () => { void qc.invalidateQueries({ queryKey: proxmoxQk.connections }) },
  })
}

/** V6.13.1 — the next free guest id from the cluster, to default the create
 *  form's vmid. Only fetched while the create modal is open. */
export const useProxmoxNextVmId = (connectionId: string, enabled = true) =>
  useQuery({
    queryKey: proxmoxQk.nextId(connectionId),
    queryFn: async (): Promise<ProxmoxNextId> =>
      (await api.get<ProxmoxNextId>(`/api/proxmox/connections/${connectionId}/lxc/nextid`)).data,
    enabled,
    staleTime: 0,
    gcTime: 0,
  })

/** V6.13.1 — container templates across the node's template-capable storages,
 *  for the create form's template dropdown. */
export const useProxmoxTemplates = (connectionId: string, enabled = true) =>
  useQuery({
    queryKey: proxmoxQk.templates(connectionId),
    queryFn: async (): Promise<ProxmoxTemplate[]> =>
      (await api.get<ProxmoxTemplate[]>(`/api/proxmox/connections/${connectionId}/lxc/templates`)).data,
    enabled,
    staleTime: 30_000,
  })

/** V6.13.1 — create a new LXC from a template. Gated server-side (global switch +
 *  per-host opt-in). On success the host is re-scanned server-side and the
 *  refreshed host returned, so the new card appears immediately. */
export const useCreateProxmoxLxc = (connectionId: string) => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async (spec: ProxmoxLxcCreate) =>
      (await api.post<ProxmoxConnection>(`/api/proxmox/connections/${connectionId}/lxc`, spec)).data,
    onSuccess: (updated) => {
      qc.setQueryData<ProxmoxConnection[]>(proxmoxQk.connections, (prev) =>
        prev ? prev.map((c) => (c.id === updated.id ? updated : c)) : prev)
    },
  })
}

/** V8.1 — restorable LXC backup archives across the node's backup-capable storages,
 *  for the restore form's archive dropdown. */
export const useProxmoxBackups = (connectionId: string, enabled = true) =>
  useQuery({
    queryKey: proxmoxQk.backups(connectionId),
    queryFn: async (): Promise<ProxmoxBackup[]> =>
      (await api.get<ProxmoxBackup[]>(`/api/proxmox/connections/${connectionId}/lxc/backups`)).data,
    enabled,
    staleTime: 30_000,
  })

/** V8.1 — restore a new LXC from a vzdump backup archive. Gated server-side (global
 *  switch + per-host opt-in); an overwrite restore double-confirms. On success the
 *  host is re-scanned server-side and the refreshed host returned, so the restored
 *  card appears immediately. */
export const useRestoreProxmoxLxc = (connectionId: string) => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async (spec: ProxmoxLxcRestore) =>
      (await api.post<ProxmoxConnection>(`/api/proxmox/connections/${connectionId}/lxc/restore`, spec)).data,
    onSuccess: (updated) => {
      qc.setQueryData<ProxmoxConnection[]>(proxmoxQk.connections, (prev) =>
        prev ? prev.map((c) => (c.id === updated.id ? updated : c)) : prev)
    },
  })
}

/** V8.0 — clone an existing LXC into a new one. Gated server-side (global switch +
 *  per-host opt-in). On success the host is re-scanned server-side and the refreshed
 *  host returned, so the cloned card appears immediately. */
export const useCloneProxmoxLxc = (connectionId: string, sourceVmId: number) => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async (spec: ProxmoxLxcClone) =>
      (await api.post<ProxmoxConnection>(
        `/api/proxmox/connections/${connectionId}/lxc/${sourceVmId}/clone`, spec)).data,
    onSuccess: (updated) => {
      qc.setQueryData<ProxmoxConnection[]>(proxmoxQk.connections, (prev) =>
        prev ? prev.map((c) => (c.id === updated.id ? updated : c)) : prev)
      void qc.invalidateQueries({ queryKey: proxmoxQk.cloneAudit(connectionId, sourceVmId) })
    },
  })
}

/** V8.0 — an LXC's snapshots for the Snapshots tab. */
export const useProxmoxSnapshots = (connectionId: string, vmId: number, enabled = true) =>
  useQuery({
    queryKey: proxmoxQk.snapshots(connectionId, vmId),
    queryFn: async (): Promise<ProxmoxSnapshot[]> =>
      (await api.get<ProxmoxSnapshot[]>(
        `/api/proxmox/connections/${connectionId}/lxc/${vmId}/snapshots`)).data,
    enabled,
    staleTime: 10_000,
  })

/** V8.0 — take a snapshot; refreshes the snapshot list + the guest's audit. */
export const useCreateProxmoxSnapshot = (connectionId: string, vmId: number) => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async (spec: ProxmoxSnapshotCreate) =>
      api.post(`/api/proxmox/connections/${connectionId}/lxc/${vmId}/snapshots`, spec),
    onSuccess: () => {
      void qc.invalidateQueries({ queryKey: proxmoxQk.snapshots(connectionId, vmId) })
      void qc.invalidateQueries({ queryKey: proxmoxQk.cloneAudit(connectionId, vmId) })
    },
  })
}

/** V8.0 — roll an LXC back to a snapshot. Discards newer state; the UI
 *  double-confirms. Refreshes the snapshot list + audit + host (running state). */
export const useRollbackProxmoxSnapshot = (connectionId: string, vmId: number) => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async (name: string) =>
      api.post(`/api/proxmox/connections/${connectionId}/lxc/${vmId}/snapshots/${encodeURIComponent(name)}/rollback`),
    onSuccess: () => {
      void qc.invalidateQueries({ queryKey: proxmoxQk.snapshots(connectionId, vmId) })
      void qc.invalidateQueries({ queryKey: proxmoxQk.cloneAudit(connectionId, vmId) })
      void qc.invalidateQueries({ queryKey: proxmoxQk.connections })
    },
  })
}

/** V8.0 — delete a snapshot. The UI double-confirms. Refreshes the list + audit. */
export const useDeleteProxmoxSnapshot = (connectionId: string, vmId: number) => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async (name: string) =>
      api.delete(`/api/proxmox/connections/${connectionId}/lxc/${vmId}/snapshots/${encodeURIComponent(name)}`),
    onSuccess: () => {
      void qc.invalidateQueries({ queryKey: proxmoxQk.snapshots(connectionId, vmId) })
      void qc.invalidateQueries({ queryKey: proxmoxQk.cloneAudit(connectionId, vmId) })
    },
  })
}

/** V8.0 — the clone/snapshot audit rows for one guest (modal Audit tab). */
export const useProxmoxCloneAudit = (connectionId: string, vmId: number, enabled = true) =>
  useQuery({
    queryKey: proxmoxQk.cloneAudit(connectionId, vmId),
    queryFn: async (): Promise<ProxmoxCloneAudit[]> =>
      (await api.get<ProxmoxCloneAudit[]>(
        `/api/proxmox/connections/${connectionId}/lxc/${vmId}/clone-audit`)).data,
    enabled,
    staleTime: 10_000,
  })

/** V6.5 — edit an LXC's parameters; refreshes the Config tab + host list. */
export const useUpdateProxmoxLxcConfig = (connectionId: string, vmId: number) => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async (update: ProxmoxLxcConfigUpdate) =>
      api.put(`/api/proxmox/connections/${connectionId}/lxc/${vmId}/config`, update),
    onSuccess: () => {
      void qc.invalidateQueries({ queryKey: proxmoxQk.lxcConfig(connectionId, vmId) })
      void qc.invalidateQueries({ queryKey: proxmoxQk.connections })
    },
  })
}

export const useProxmoxConnections = () =>
  useQuery({
    queryKey: proxmoxQk.connections,
    queryFn: async (): Promise<ProxmoxConnection[]> =>
      (await api.get<ProxmoxConnection[]>('/api/proxmox/connections')).data,
    // Refresh so the cards reflect the background scan without a manual reload.
    refetchInterval: 30_000,
  })

export const useCreateProxmoxConnection = () => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async (data: ProxmoxConnectionUpsert) =>
      (await api.post<ProxmoxConnection>('/api/proxmox/connections', data)).data,
    onSuccess: () => qc.invalidateQueries({ queryKey: proxmoxQk.connections }),
  })
}

export const useUpdateProxmoxConnection = () => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async (args: { id: string; data: ProxmoxConnectionUpsert }) =>
      (await api.put<ProxmoxConnection>(`/api/proxmox/connections/${args.id}`, args.data)).data,
    onSuccess: () => qc.invalidateQueries({ queryKey: proxmoxQk.connections }),
  })
}

export const useDeleteProxmoxConnection = () => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async (id: string) => api.delete(`/api/proxmox/connections/${id}`),
    onSuccess: () => qc.invalidateQueries({ queryKey: proxmoxQk.connections }),
  })
}

/** Runs an immediate scan of the host, bypassing the schedule. */
export const useCheckProxmoxNow = () => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async (id: string) =>
      (await api.post<ProxmoxConnection>(`/api/proxmox/connections/${id}/check`)).data,
    onSuccess: (updated) => {
      qc.setQueryData<ProxmoxConnection[]>(proxmoxQk.connections, (prev) =>
        prev ? prev.map((c) => (c.id === updated.id ? updated : c)) : prev)
    },
  })
}

/** V6.7 — toggle one LXC's update monitoring on/off. Optimistic: the card flips
 *  immediately and rolls back on error, mirroring the Docker watch toggle. */
export const useSetProxmoxLxcMonitoring = (connectionId: string) => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async (args: { vmId: number; enabled: boolean }) =>
      (await api.put<ProxmoxConnection>(
        `/api/proxmox/connections/${connectionId}/lxc/${args.vmId}/monitoring`,
        { enabled: args.enabled })).data,
    onMutate: async ({ vmId, enabled }) => {
      await qc.cancelQueries({ queryKey: proxmoxQk.connections })
      const previous = qc.getQueryData<ProxmoxConnection[]>(proxmoxQk.connections)
      qc.setQueryData<ProxmoxConnection[]>(proxmoxQk.connections, (prev) =>
        prev?.map((c) => c.id !== connectionId ? c : {
          ...c,
          guests: c.guests.map((g) => g.vmId === vmId
            ? { ...g, monitoringEnabled: enabled, pendingUpdates: enabled ? g.pendingUpdates : null }
            : g),
        }))
      return { previous }
    },
    onError: (_e, _vars, ctx) => {
      if (ctx?.previous) qc.setQueryData(proxmoxQk.connections, ctx.previous)
    },
    onSuccess: (updated) => {
      qc.setQueryData<ProxmoxConnection[]>(proxmoxQk.connections, (prev) =>
        prev ? prev.map((c) => (c.id === updated.id ? updated : c)) : prev)
    },
  })
}

/** V7.9 — link a guest to a single service (or unlink with `null`), the Proxmox
 *  analogue of a Docker watch's "Linked service" dropdown. Returns the refreshed
 *  host; also invalidates the services query so the dashboard badge + the
 *  service modal's read-only linked-guests list update. */
export const useSetProxmoxGuestService = (connectionId: string) => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async (args: { vmId: number; webResourceId: string | null }) =>
      (await api.put<ProxmoxConnection>(
        `/api/proxmox/connections/${connectionId}/guests/${args.vmId}/service`,
        { webResourceId: args.webResourceId })).data,
    onSuccess: (updated) => {
      qc.setQueryData<ProxmoxConnection[]>(proxmoxQk.connections, (prev) =>
        prev ? prev.map((c) => (c.id === updated.id ? updated : c)) : prev)
      void qc.invalidateQueries({ queryKey: ['services'] })
    },
  })
}

/** V6.11 — host-wide bulk monitoring toggle (Enable all / Disable all). One call
 *  flips every LXC on the host server-side and returns the refreshed host. */
export const useSetBulkProxmoxMonitoring = (connectionId: string) => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async (enabled: boolean) =>
      (await api.put<ProxmoxConnection>(
        `/api/proxmox/connections/${connectionId}/lxc/monitoring/bulk`, { enabled })).data,
    onSuccess: (updated) => {
      qc.setQueryData<ProxmoxConnection[]>(proxmoxQk.connections, (prev) =>
        prev ? prev.map((c) => (c.id === updated.id ? updated : c)) : prev)
    },
  })
}

/** V6.11 — snooze one LXC's monitoring for a maintenance window (pass an ISO UTC
 *  instant) or clear an active snooze (pass `null`). Optimistic so the card +
 *  Watch tab reflect the change immediately, rolling back on error. */
export const useSnoozeProxmoxLxc = (connectionId: string) => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async (args: { vmId: number; until: string | null }) =>
      (await api.put<ProxmoxConnection>(
        `/api/proxmox/connections/${connectionId}/lxc/${args.vmId}/snooze`,
        { until: args.until })).data,
    onMutate: async ({ vmId, until }) => {
      await qc.cancelQueries({ queryKey: proxmoxQk.connections })
      const previous = qc.getQueryData<ProxmoxConnection[]>(proxmoxQk.connections)
      qc.setQueryData<ProxmoxConnection[]>(proxmoxQk.connections, (prev) =>
        prev?.map((c) => c.id !== connectionId ? c : {
          ...c,
          guests: c.guests.map((g) => g.vmId === vmId
            ? { ...g, monitoringSnoozedUntil: until, pendingUpdates: until ? null : g.pendingUpdates }
            : g),
        }))
      return { previous }
    },
    onError: (_e, _vars, ctx) => {
      if (ctx?.previous) qc.setQueryData(proxmoxQk.connections, ctx.previous)
    },
    onSuccess: (updated) => {
      qc.setQueryData<ProxmoxConnection[]>(proxmoxQk.connections, (prev) =>
        prev ? prev.map((c) => (c.id === updated.id ? updated : c)) : prev)
    },
  })
}

/** V6.11 — generate / rotate the host's update-check webhook token. */
export const useRotateProxmoxWebhook = (connectionId: string) => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async () =>
      (await api.post<ProxmoxConnection>(`/api/proxmox/connections/${connectionId}/webhook/rotate`)).data,
    onSuccess: (updated) => {
      qc.setQueryData<ProxmoxConnection[]>(proxmoxQk.connections, (prev) =>
        prev ? prev.map((c) => (c.id === updated.id ? updated : c)) : prev)
    },
  })
}

/** V6.11 — drop the host's update-check webhook token (stops accepting deliveries). */
export const useDeleteProxmoxWebhook = (connectionId: string) => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async () =>
      (await api.delete<ProxmoxConnection>(`/api/proxmox/connections/${connectionId}/webhook`)).data,
    onSuccess: (updated) => {
      qc.setQueryData<ProxmoxConnection[]>(proxmoxQk.connections, (prev) =>
        prev ? prev.map((c) => (c.id === updated.id ? updated : c)) : prev)
    },
  })
}

/** V6.7 — "Check now" from an LXC's Watch tab. Proxmox checks are node-wide
 *  (one SSH/API sweep covers every guest), so this re-scans the whole node; a
 *  disabled guest returns a deterministic disabled outcome without scanning. */
export const useCheckProxmoxLxc = (connectionId: string) => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async (vmId: number) =>
      (await api.post<ProxmoxConnection>(
        `/api/proxmox/connections/${connectionId}/lxc/${vmId}/check`)).data,
    onSuccess: (updated) => {
      qc.setQueryData<ProxmoxConnection[]>(proxmoxQk.connections, (prev) =>
        prev ? prev.map((c) => (c.id === updated.id ? updated : c)) : prev)
    },
  })
}

/** V6.7.1 — the exact command a one-click "Update now" will run for a target
 *  (node when `vmId === 0`, else the LXC). Shown in the confirm dialog so the
 *  operator can see + copy what's about to execute, like the Docker panel. */
export const useProxmoxUpdateCommand = (connectionId: string, vmId: number) =>
  useQuery({
    queryKey: ['proxmox', 'connections', connectionId, 'update-command', vmId] as const,
    queryFn: async (): Promise<string> =>
      (await api.get<{ command: string }>(
        `/api/proxmox/connections/${connectionId}/update-command?vmId=${vmId}`)).data.command,
    staleTime: 60_000,
  })

export const useTestProxmoxConnection = () =>
  useMutation({
    /** Pass `connectionId` so "Keep" secrets resolve against the saved host. */
    mutationFn: async (args: { data: ProxmoxConnectionPingRequest; connectionId?: string }) => {
      const url = args.connectionId
        ? `/api/proxmox/connections/test?connectionId=${args.connectionId}`
        : '/api/proxmox/connections/test'
      return (await api.post<ProxmoxConnectionPingResponse>(url, args.data)).data
    },
  })

// ── V6.8.1 — node alerting ────────────────────────────────────────────────────

/** V6.8.1 — a node's alert configuration (enable flag, category mask, threshold
 *  overrides + the global defaults for placeholders). */
export const useProxmoxNodeAlertSettings = (connectionId: string, enabled = true) =>
  useQuery({
    queryKey: proxmoxQk.nodeAlertSettings(connectionId),
    queryFn: async (): Promise<ProxmoxNodeAlertSettings> =>
      (await api.get<ProxmoxNodeAlertSettings>(
        `/api/proxmox/connections/${connectionId}/node/alerts/settings`)).data,
    enabled,
    staleTime: 10_000,
  })

/** V6.8.1 — the node's currently-active alerts. Polled so the Alerts tab tracks
 *  the background evaluation loop without a manual reload. */
export const useProxmoxNodeAlerts = (connectionId: string, enabled = true) =>
  useQuery({
    queryKey: proxmoxQk.nodeAlerts(connectionId),
    queryFn: async (): Promise<ProxmoxNodeAlert[]> =>
      (await api.get<ProxmoxNodeAlert[]>(`/api/proxmox/connections/${connectionId}/node/alerts`)).data,
    enabled,
    refetchInterval: 30_000,
    staleTime: 15_000,
  })

/** V6.8.1 — upsert a node's alert configuration. The enable toggle is optimistic
 *  (flips immediately, rolls back on error), mirroring the Docker watch toggle. */
export const useUpdateProxmoxNodeAlertSettings = (connectionId: string) => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async (update: ProxmoxNodeAlertSettingsUpdate): Promise<ProxmoxNodeAlertSettings> =>
      (await api.put<ProxmoxNodeAlertSettings>(
        `/api/proxmox/connections/${connectionId}/node/alerts/settings`, update)).data,
    onMutate: async (update) => {
      await qc.cancelQueries({ queryKey: proxmoxQk.nodeAlertSettings(connectionId) })
      const previous = qc.getQueryData<ProxmoxNodeAlertSettings>(proxmoxQk.nodeAlertSettings(connectionId))
      if (previous) {
        qc.setQueryData<ProxmoxNodeAlertSettings>(proxmoxQk.nodeAlertSettings(connectionId), {
          ...previous,
          enabled: update.enabled,
          categories: update.categories,
          thresholds: update.thresholds,
        })
      }
      return { previous }
    },
    onError: (_e, _vars, ctx) => {
      if (ctx?.previous) qc.setQueryData(proxmoxQk.nodeAlertSettings(connectionId), ctx.previous)
    },
    onSuccess: (data) => {
      qc.setQueryData(proxmoxQk.nodeAlertSettings(connectionId), data)
    },
  })
}

/** V6.8.1 — run one alert evaluation immediately ("Check now" for alerts) and
 *  refresh the active set. */
export const useCheckProxmoxNodeAlerts = (connectionId: string) => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async (): Promise<ProxmoxNodeAlert[]> =>
      (await api.post<ProxmoxNodeAlert[]>(`/api/proxmox/connections/${connectionId}/node/alerts/check`)).data,
    onSuccess: (alerts) => {
      qc.setQueryData(proxmoxQk.nodeAlerts(connectionId), alerts)
    },
  })
}
