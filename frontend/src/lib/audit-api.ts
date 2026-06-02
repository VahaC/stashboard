import { api } from './api'

/**
 * V5.8 — typed client for the read-only session audit viewer endpoints. These
 * surface the rows V5.3 (host terminal) and V5.7 (container exec) already
 * persist, plus the V2.7 update-attempt and V5.5 image-prune logs. No write
 * verbs — audit rows are immutable from the UI. Mirrors the other small
 * hand-rolled API clients (settings-api, account-api).
 */

// End reasons are shared between host-shell and exec sessions. The API serialises
// enums as strings (Program.cs adds JsonStringEnumConverter).
export type SessionEndReason =
  | 'Active'
  | 'ClosedByClient'
  | 'RemoteClosed'
  | 'IdleTimeout'
  | 'ClosedByServer'
  | 'Error'
  | 'Interrupted'

export interface HostShellSession {
  id: string
  dockerConnectionId: string | null
  connectionName: string | null
  sshHost: string | null
  sshUsername: string | null
  startedUtc: string
  endedUtc: string | null
  bytesFromClient: number
  bytesToClient: number
  endReason: SessionEndReason
  error: string | null
  active: boolean
}

export interface ExecSession {
  id: string
  dockerConnectionId: string | null
  connectionName: string | null
  containerName: string | null
  command: string | null
  startedUtc: string
  endedUtc: string | null
  bytesFromClient: number
  bytesToClient: number
  endReason: SessionEndReason
  error: string | null
  active: boolean
}

export interface UpdateAttempt {
  id: string
  status: string
  actionType: string
  imageReference: string
  containerName: string
  error: string | null
  completedUtc: string
  healthVerified: boolean
}

export interface PruneRun {
  id: string
  trigger: string
  status: string
  includedUnused: boolean
  imagesDeleted: number
  spaceReclaimedBytes: number
  startedUtc: string
  completedUtc: string | null
  error: string | null
}

interface PageParams {
  connectionId?: string | null
  skip?: number
  take?: number
}

function params({ connectionId, skip, take }: PageParams) {
  const p: Record<string, string | number> = { take: take ?? 200 }
  if (skip) p.skip = skip
  if (connectionId) p.connectionId = connectionId
  return p
}

export const auditApi = {
  async getHostShellSessions(opts: PageParams = {}): Promise<HostShellSession[]> {
    return (await api.get<HostShellSession[]>('/api/docker/host-shell/sessions', { params: params(opts) })).data
  },
  async getExecSessions(opts: PageParams = {}): Promise<ExecSession[]> {
    return (await api.get<ExecSession[]>('/api/docker/container-exec/sessions', { params: params(opts) })).data
  },
  async getUpdateAttempts(opts: PageParams = {}): Promise<UpdateAttempt[]> {
    return (await api.get<UpdateAttempt[]>('/api/docker/update-attempts', { params: params(opts) })).data
  },
  async getPruneRuns(opts: PageParams = {}): Promise<PruneRun[]> {
    return (await api.get<PruneRun[]>('/api/docker/prune-runs', { params: params(opts) })).data
  },
}

// ── formatting helpers (exported for reuse / readability) ─────────────────────

const END_REASON_LABELS: Record<string, string> = {
  Active: 'Active',
  ClosedByClient: 'Closed by client',
  RemoteClosed: 'Remote closed',
  IdleTimeout: 'Idle timeout',
  ClosedByServer: 'Closed by server',
  Error: 'Error',
  Interrupted: 'Interrupted (server restart)',
}

export function endReasonLabel(reason: string): string {
  return END_REASON_LABELS[reason] ?? reason
}

export function formatBytes(bytes: number): string {
  if (!Number.isFinite(bytes) || bytes < 0) return '—'
  if (bytes < 1024) return `${bytes} B`
  const units = ['KiB', 'MiB', 'GiB', 'TiB']
  let value = bytes / 1024
  let i = 0
  while (value >= 1024 && i < units.length - 1) {
    value /= 1024
    i += 1
  }
  return `${value.toFixed(value >= 10 ? 0 : 1)} ${units[i]}`
}

export function formatDuration(startIso: string, endIso: string | null): string {
  if (!endIso) return '—'
  const ms = new Date(endIso).getTime() - new Date(startIso).getTime()
  if (!Number.isFinite(ms) || ms < 0) return '—'
  const totalSeconds = Math.round(ms / 1000)
  const h = Math.floor(totalSeconds / 3600)
  const m = Math.floor((totalSeconds % 3600) / 60)
  const s = totalSeconds % 60
  if (h > 0) return `${h}h ${m}m ${s}s`
  if (m > 0) return `${m}m ${s}s`
  return `${s}s`
}

export function formatTimestamp(iso: string | null): string {
  if (!iso) return '—'
  const d = new Date(iso)
  return Number.isNaN(d.getTime()) ? '—' : d.toLocaleString()
}
