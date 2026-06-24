import { useQuery } from '@tanstack/react-query'
import { useSearchParams } from 'react-router-dom'
import { ScrollText } from 'lucide-react'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Badge } from '@/components/ui/badge'
import {
  auditApi,
  endReasonLabel,
  formatBytes,
  formatDuration,
  formatTimestamp,
  type ComposeChangeAudit,
  type ConsoleSession,
  type ExecSession,
  type HostShellSession,
  type ProxmoxCreateAudit,
  type ProxmoxRestoreAudit,
  type ProxmoxDestroyAudit,
  type ProxmoxMonitoringAudit,
  type ProxmoxUpdateSession,
  type PruneRun,
  type UpdateAttempt,
} from '@/lib/audit-api'
import '@/styles/audit.css'

/**
 * V5.8 — Session audit viewer. Read-only surface over the audit rows V5.3
 * (host terminal) and V5.7 (container exec) already persist, plus the V2.7
 * update-attempt and V5.5 image-prune logs — all four trails in one place. The
 * shell / exec tables are the genuine gap this phase closes; the latter two
 * already have a read path elsewhere and are folded in here for convenience.
 */

type TabKey = 'host' | 'exec' | 'console' | 'pmupdates' | 'pmmonitoring' | 'pmcreate' | 'pmrestore' | 'pmdestroy' | 'compose' | 'updates' | 'prune'

const TABS: { key: TabKey; label: string }[] = [
  { key: 'host', label: 'Host terminal' },
  { key: 'exec', label: 'Container exec' },
  { key: 'console', label: 'LXC console' },
  { key: 'pmupdates', label: 'Proxmox updates' },
  { key: 'pmmonitoring', label: 'LXC monitoring' },
  { key: 'pmcreate', label: 'LXC create' },
  { key: 'pmrestore', label: 'LXC restore' },
  { key: 'pmdestroy', label: 'LXC destroy' },
  { key: 'compose', label: 'Compose changes' },
  { key: 'updates', label: 'Update attempts' },
  { key: 'prune', label: 'Image prune' },
]

const COMPOSE_CHANGE_LABELS: Record<string, string> = {
  Save: 'Save',
  Restore: 'Restore',
  Apply: 'Apply',
}

const MONITORING_CHANGE_LABELS: Record<string, string> = {
  Enabled: 'Enabled',
  Disabled: 'Disabled',
  Snoozed: 'Snoozed',
  SnoozeCleared: 'Snooze cleared',
}

const UPDATE_STATUS_LABELS: Record<string, string> = {
  Success: 'Success',
  PullFailed: 'Pull failed',
  RecreateFailed: 'Recreate failed',
  HostUnreachable: 'Host unreachable',
  ContainerNotFound: 'Container not found',
}

const PRUNE_STATUS_LABELS: Record<string, string> = {
  Success: 'Success',
  NothingToPrune: 'Nothing to prune',
  HostUnreachable: 'Host unreachable',
  Failed: 'Failed',
  Skipped: 'Skipped',
}

function ActiveBadge() {
  return <Badge variant="default" data-testid="active-badge">Active</Badge>
}

function ErrorLine({ error }: { error: string | null }) {
  if (!error) return null
  return <div className="audit-error" title={error}>{error}</div>
}

function StateMessage({ children }: { children: React.ReactNode }) {
  return <p className="audit-state">{children}</p>
}

// ── tables ────────────────────────────────────────────────────────────────────

function HostShellTable({ connectionId }: { connectionId: string | null }) {
  const { data, isLoading, isError } = useQuery({
    queryKey: ['audit', 'host', connectionId ?? 'all'],
    queryFn: () => auditApi.getHostShellSessions({ connectionId }),
  })
  if (isLoading) return <StateMessage>Loading…</StateMessage>
  if (isError) return <StateMessage>Failed to load host-terminal sessions.</StateMessage>
  if (!data || data.length === 0) return <StateMessage>No host-terminal sessions recorded yet.</StateMessage>
  return (
    <div className="audit-table-wrap">
      <table className="audit-table">
        <thead>
          <tr>
            <th>Connection / host</th><th>Started</th><th>Ended</th><th>Duration</th>
            <th>In</th><th>Out</th><th>End reason</th>
          </tr>
        </thead>
        <tbody>
          {data.map((r: HostShellSession) => (
            <tr key={r.id}>
              <td>
                {r.connectionName ?? '(deleted)'}
                <div className="audit-sub">{r.sshUsername ? `${r.sshUsername}@` : ''}{r.sshHost ?? ''}</div>
              </td>
              <td>{formatTimestamp(r.startedUtc)}</td>
              <td>{r.active ? <ActiveBadge /> : formatTimestamp(r.endedUtc)}</td>
              <td>{formatDuration(r.startedUtc, r.endedUtc)}</td>
              <td>{formatBytes(r.bytesFromClient)}</td>
              <td>{formatBytes(r.bytesToClient)}</td>
              <td>{endReasonLabel(r.endReason)}<ErrorLine error={r.error} /></td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}

function ExecTable({ connectionId }: { connectionId: string | null }) {
  const { data, isLoading, isError } = useQuery({
    queryKey: ['audit', 'exec', connectionId ?? 'all'],
    queryFn: () => auditApi.getExecSessions({ connectionId }),
  })
  if (isLoading) return <StateMessage>Loading…</StateMessage>
  if (isError) return <StateMessage>Failed to load container-exec sessions.</StateMessage>
  if (!data || data.length === 0) return <StateMessage>No container-exec sessions recorded yet.</StateMessage>
  return (
    <div className="audit-table-wrap">
      <table className="audit-table">
        <thead>
          <tr>
            <th>Connection</th><th>Container</th><th>Command</th><th>Started</th><th>Ended</th>
            <th>Duration</th><th>In</th><th>Out</th><th>End reason</th>
          </tr>
        </thead>
        <tbody>
          {data.map((r: ExecSession) => (
            <tr key={r.id}>
              <td>{r.connectionName ?? '(deleted)'}</td>
              <td>{r.containerName ?? '—'}</td>
              <td><code>{r.command ?? '—'}</code></td>
              <td>{formatTimestamp(r.startedUtc)}</td>
              <td>{r.active ? <ActiveBadge /> : formatTimestamp(r.endedUtc)}</td>
              <td>{formatDuration(r.startedUtc, r.endedUtc)}</td>
              <td>{formatBytes(r.bytesFromClient)}</td>
              <td>{formatBytes(r.bytesToClient)}</td>
              <td>{endReasonLabel(r.endReason)}<ErrorLine error={r.error} /></td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}

function ConsoleTable({ connectionId }: { connectionId: string | null }) {
  const { data, isLoading, isError } = useQuery({
    queryKey: ['audit', 'console', connectionId ?? 'all'],
    queryFn: () => auditApi.getConsoleSessions({ connectionId }),
  })
  if (isLoading) return <StateMessage>Loading…</StateMessage>
  if (isError) return <StateMessage>Failed to load LXC-console sessions.</StateMessage>
  if (!data || data.length === 0) return <StateMessage>No LXC-console sessions recorded yet.</StateMessage>
  return (
    <div className="audit-table-wrap">
      <table className="audit-table">
        <thead>
          <tr>
            <th>Host / node</th><th>Container</th><th>Command</th><th>Started</th><th>Ended</th>
            <th>Duration</th><th>In</th><th>Out</th><th>End reason</th>
          </tr>
        </thead>
        <tbody>
          {data.map((r: ConsoleSession) => (
            <tr key={r.id}>
              <td>
                {r.connectionName ?? '(deleted)'}
                <div className="audit-sub">{r.nodeName ?? ''}</div>
              </td>
              <td>{r.guestName ? `${r.guestName} (CT ${r.vmId})` : `CT ${r.vmId}`}</td>
              <td><code>{r.command ?? '—'}</code></td>
              <td>{formatTimestamp(r.startedUtc)}</td>
              <td>{r.active ? <ActiveBadge /> : formatTimestamp(r.endedUtc)}</td>
              <td>{formatDuration(r.startedUtc, r.endedUtc)}</td>
              <td>{formatBytes(r.bytesFromClient)}</td>
              <td>{formatBytes(r.bytesToClient)}</td>
              <td>{endReasonLabel(r.endReason)}<ErrorLine error={r.error} /></td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}

function ProxmoxUpdatesTable({ connectionId }: { connectionId: string | null }) {
  const { data, isLoading, isError } = useQuery({
    queryKey: ['audit', 'pmupdates', connectionId ?? 'all'],
    queryFn: () => auditApi.getProxmoxUpdateSessions({ connectionId }),
  })
  if (isLoading) return <StateMessage>Loading…</StateMessage>
  if (isError) return <StateMessage>Failed to load Proxmox update runs.</StateMessage>
  if (!data || data.length === 0) return <StateMessage>No Proxmox update runs recorded yet.</StateMessage>
  return (
    <div className="audit-table-wrap">
      <table className="audit-table">
        <thead>
          <tr>
            <th>Host / node</th><th>Target</th><th>Started</th><th>Ended</th>
            <th>Duration</th><th>Exit</th><th>Output</th><th>End reason</th>
          </tr>
        </thead>
        <tbody>
          {data.map((r: ProxmoxUpdateSession) => (
            <tr key={r.id}>
              <td>
                {r.connectionName ?? '(deleted)'}
                <div className="audit-sub">{r.nodeName ?? ''}</div>
              </td>
              <td>
                {r.targetType === 'Node'
                  ? `node (${r.nodeName ?? r.targetName ?? '—'})`
                  : r.targetName ? `${r.targetName} (CT ${r.vmId})` : `CT ${r.vmId}`}
              </td>
              <td>{formatTimestamp(r.startedUtc)}</td>
              <td>{r.active ? <ActiveBadge /> : formatTimestamp(r.endedUtc)}</td>
              <td>{formatDuration(r.startedUtc, r.endedUtc)}</td>
              <td>{r.exitStatus == null ? '—' : r.exitStatus}</td>
              <td>{formatBytes(r.bytesToClient)}</td>
              <td>{endReasonLabel(r.endReason)}<ErrorLine error={r.error} /></td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}

function ProxmoxMonitoringTable({ connectionId }: { connectionId: string | null }) {
  const { data, isLoading, isError } = useQuery({
    queryKey: ['audit', 'pmmonitoring', connectionId ?? 'all'],
    queryFn: () => auditApi.getProxmoxMonitoringAudits({ connectionId }),
  })
  if (isLoading) return <StateMessage>Loading…</StateMessage>
  if (isError) return <StateMessage>Failed to load LXC monitoring changes.</StateMessage>
  if (!data || data.length === 0) return <StateMessage>No LXC monitoring changes recorded yet.</StateMessage>
  return (
    <div className="audit-table-wrap">
      <table className="audit-table">
        <thead>
          <tr>
            <th>Host / node</th><th>Container</th><th>Change</th><th>New state</th><th>Scope</th><th>When</th>
          </tr>
        </thead>
        <tbody>
          {data.map((r: ProxmoxMonitoringAudit) => (
            <tr key={r.id}>
              <td>
                {r.connectionName ?? '(deleted)'}
                <div className="audit-sub">{r.nodeName ?? ''}</div>
              </td>
              <td>{r.guestName ? `${r.guestName} (CT ${r.vmId})` : `CT ${r.vmId}`}</td>
              <td>{MONITORING_CHANGE_LABELS[r.changeType] ?? r.changeType}</td>
              <td>
                {r.changeType === 'Snoozed' && r.snoozedUntil
                  ? `snoozed until ${formatTimestamp(r.snoozedUntil)}`
                  : r.monitoringEnabled ? 'monitoring on' : 'monitoring off'}
              </td>
              <td>{r.bulk ? 'Bulk (all)' : 'Single'}</td>
              <td>{formatTimestamp(r.changedUtc)}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}

function ProxmoxDestroyTable({ connectionId }: { connectionId: string | null }) {
  const { data, isLoading, isError } = useQuery({
    queryKey: ['audit', 'pmdestroy', connectionId ?? 'all'],
    queryFn: () => auditApi.getProxmoxDestroyAudits({ connectionId }),
  })
  if (isLoading) return <StateMessage>Loading…</StateMessage>
  if (isError) return <StateMessage>Failed to load LXC destroys.</StateMessage>
  if (!data || data.length === 0) return <StateMessage>No LXC destroys recorded yet.</StateMessage>
  return (
    <div className="audit-table-wrap">
      <table className="audit-table">
        <thead>
          <tr>
            <th>Host / node</th><th>Container</th><th>Result</th><th>When</th>
          </tr>
        </thead>
        <tbody>
          {data.map((r: ProxmoxDestroyAudit) => (
            <tr key={r.id}>
              <td>
                {r.connectionName ?? '(deleted)'}
                <div className="audit-sub">{r.nodeName ?? ''}</div>
              </td>
              <td>{r.guestName ? `${r.guestName} (CT ${r.vmId})` : `CT ${r.vmId}`}</td>
              <td>{r.success ? 'Destroyed' : 'Failed'}<ErrorLine error={r.error} /></td>
              <td>{formatTimestamp(r.destroyedUtc)}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}

function ProxmoxCreateTable({ connectionId }: { connectionId: string | null }) {
  const { data, isLoading, isError } = useQuery({
    queryKey: ['audit', 'pmcreate', connectionId ?? 'all'],
    queryFn: () => auditApi.getProxmoxCreateAudits({ connectionId }),
  })
  if (isLoading) return <StateMessage>Loading…</StateMessage>
  if (isError) return <StateMessage>Failed to load LXC creates.</StateMessage>
  if (!data || data.length === 0) return <StateMessage>No LXC creates recorded yet.</StateMessage>
  return (
    <div className="audit-table-wrap">
      <table className="audit-table">
        <thead>
          <tr>
            <th>Host / node</th><th>Container</th><th>Template</th><th>Result</th><th>When</th>
          </tr>
        </thead>
        <tbody>
          {data.map((r: ProxmoxCreateAudit) => (
            <tr key={r.id}>
              <td>
                {r.connectionName ?? '(deleted)'}
                <div className="audit-sub">{r.nodeName ?? ''}</div>
              </td>
              <td>{r.hostname ? `${r.hostname} (CT ${r.vmId})` : `CT ${r.vmId}`}</td>
              <td><code>{r.template ?? '—'}</code></td>
              <td>{r.success ? 'Created' : 'Failed'}<ErrorLine error={r.error} /></td>
              <td>{formatTimestamp(r.createdAtUtc)}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}

function ProxmoxRestoreTable({ connectionId }: { connectionId: string | null }) {
  const { data, isLoading, isError } = useQuery({
    queryKey: ['audit', 'pmrestore', connectionId ?? 'all'],
    queryFn: () => auditApi.getProxmoxRestoreAudits({ connectionId }),
  })
  if (isLoading) return <StateMessage>Loading…</StateMessage>
  if (isError) return <StateMessage>Failed to load LXC restores.</StateMessage>
  if (!data || data.length === 0) return <StateMessage>No LXC restores recorded yet.</StateMessage>
  return (
    <div className="audit-table-wrap">
      <table className="audit-table">
        <thead>
          <tr>
            <th>Host / node</th><th>Container</th><th>Backup</th><th>Mode</th><th>Result</th><th>When</th>
          </tr>
        </thead>
        <tbody>
          {data.map((r: ProxmoxRestoreAudit) => (
            <tr key={r.id}>
              <td>
                {r.connectionName ?? '(deleted)'}
                <div className="audit-sub">{r.nodeName ?? ''}</div>
              </td>
              <td>CT {r.vmId}</td>
              <td><code>{r.backupVolid ?? '—'}</code></td>
              <td>{r.overwrote ? 'Overwrite' : 'New'}</td>
              <td>{r.success ? 'Restored' : 'Failed'}<ErrorLine error={r.error} /></td>
              <td>{formatTimestamp(r.createdAtUtc)}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}

function ComposeChangesTable({ connectionId }: { connectionId: string | null }) {
  const { data, isLoading, isError } = useQuery({
    queryKey: ['audit', 'compose', connectionId ?? 'all'],
    queryFn: () => auditApi.getComposeChanges({ connectionId }),
  })
  if (isLoading) return <StateMessage>Loading…</StateMessage>
  if (isError) return <StateMessage>Failed to load Compose changes.</StateMessage>
  if (!data || data.length === 0) return <StateMessage>No Compose changes recorded yet.</StateMessage>
  return (
    <div className="audit-table-wrap">
      <table className="audit-table">
        <thead>
          <tr>
            <th>Connection</th><th>Project / file</th><th>Change</th><th>Services</th><th>Result</th><th>When</th>
          </tr>
        </thead>
        <tbody>
          {data.map((r: ComposeChangeAudit) => (
            <tr key={r.id}>
              <td>{r.connectionName ?? '(deleted)'}</td>
              <td>
                {r.composeProject}
                <div className="audit-sub">{r.fileName ?? ''}</div>
              </td>
              <td>{COMPOSE_CHANGE_LABELS[r.changeType] ?? r.changeType}</td>
              <td>{r.changedServices.length > 0 ? <code>{r.changedServices.join(', ')}</code> : '—'}</td>
              <td>{r.success ? 'OK' : 'Failed'}<ErrorLine error={r.error} /></td>
              <td>{formatTimestamp(r.changedUtc)}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}

function UpdatesTable({ connectionId }: { connectionId: string | null }) {
  const { data, isLoading, isError } = useQuery({
    queryKey: ['audit', 'updates', connectionId ?? 'all'],
    queryFn: () => auditApi.getUpdateAttempts({ connectionId }),
  })
  if (isLoading) return <StateMessage>Loading…</StateMessage>
  if (isError) return <StateMessage>Failed to load update attempts.</StateMessage>
  if (!data || data.length === 0) return <StateMessage>No update attempts recorded yet.</StateMessage>
  return (
    <div className="audit-table-wrap">
      <table className="audit-table">
        <thead>
          <tr><th>Container</th><th>Image</th><th>Status</th><th>Completed</th><th>Health</th></tr>
        </thead>
        <tbody>
          {data.map((r: UpdateAttempt) => (
            <tr key={r.id}>
              <td>{r.containerName}</td>
              <td><code>{r.imageReference}</code></td>
              <td>{UPDATE_STATUS_LABELS[r.status] ?? r.status}<ErrorLine error={r.error} /></td>
              <td>{formatTimestamp(r.completedUtc)}</td>
              <td>{r.healthVerified ? '✓' : '—'}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}

function PruneTable({ connectionId }: { connectionId: string | null }) {
  const { data, isLoading, isError } = useQuery({
    queryKey: ['audit', 'prune', connectionId ?? 'all'],
    queryFn: () => auditApi.getPruneRuns({ connectionId }),
  })
  if (isLoading) return <StateMessage>Loading…</StateMessage>
  if (isError) return <StateMessage>Failed to load prune runs.</StateMessage>
  if (!data || data.length === 0) return <StateMessage>No image-prune runs recorded yet.</StateMessage>
  return (
    <div className="audit-table-wrap">
      <table className="audit-table">
        <thead>
          <tr><th>Trigger</th><th>Scope</th><th>Status</th><th>Images deleted</th><th>Reclaimed</th><th>Started</th></tr>
        </thead>
        <tbody>
          {data.map((r: PruneRun) => (
            <tr key={r.id}>
              <td>{r.trigger}</td>
              <td>{r.includedUnused ? 'Dangling + unused' : 'Dangling only'}</td>
              <td>{PRUNE_STATUS_LABELS[r.status] ?? r.status}<ErrorLine error={r.error} /></td>
              <td>{r.imagesDeleted}</td>
              <td>{formatBytes(r.spaceReclaimedBytes)}</td>
              <td>{formatTimestamp(r.startedUtc)}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}

// ── page ──────────────────────────────────────────────────────────────────────

export function AuditLog() {
  const [searchParams, setSearchParams] = useSearchParams()
  const tabParam = searchParams.get('tab') as TabKey | null
  const activeTab: TabKey = TABS.some((t) => t.key === tabParam) ? (tabParam as TabKey) : 'host'
  const connectionId = searchParams.get('connectionId')

  const setTab = (key: TabKey) => {
    const next = new URLSearchParams(searchParams)
    next.set('tab', key)
    setSearchParams(next, { replace: true })
  }

  const clearConnectionFilter = () => {
    const next = new URLSearchParams(searchParams)
    next.delete('connectionId')
    setSearchParams(next, { replace: true })
  }

  return (
    <div className="audit-page">
      <h1 className="text-2xl font-semibold">Audit</h1>

      <Card>
        <CardHeader>
          <CardTitle className="flex items-center gap-2">
            <ScrollText className="h-5 w-5" /> Session activity
          </CardTitle>
          <CardDescription>
            Every host-terminal and container-exec session Stashboard has recorded —
            who ran it, against what, for how long, and how it ended. The update-attempt
            and image-prune logs are included for convenience. Read-only.
          </CardDescription>
        </CardHeader>
        <CardContent>
          {connectionId && (
            <div className="audit-filter" data-testid="connection-filter">
              Filtered to a single connection.{' '}
              <button type="button" className="audit-link-button" onClick={clearConnectionFilter}>
                Clear filter
              </button>
            </div>
          )}

          <div className="audit-tabs" role="tablist">
            {TABS.map((t) => (
              <button
                key={t.key}
                type="button"
                role="tab"
                aria-selected={activeTab === t.key}
                className={activeTab === t.key ? 'audit-tab audit-tab-active' : 'audit-tab'}
                onClick={() => setTab(t.key)}
              >
                {t.label}
              </button>
            ))}
          </div>

          <div role="tabpanel">
            {activeTab === 'host' && <HostShellTable connectionId={connectionId} />}
            {activeTab === 'exec' && <ExecTable connectionId={connectionId} />}
            {activeTab === 'console' && <ConsoleTable connectionId={connectionId} />}
            {activeTab === 'pmupdates' && <ProxmoxUpdatesTable connectionId={connectionId} />}
            {activeTab === 'pmmonitoring' && <ProxmoxMonitoringTable connectionId={connectionId} />}
            {activeTab === 'pmcreate' && <ProxmoxCreateTable connectionId={connectionId} />}
            {activeTab === 'pmrestore' && <ProxmoxRestoreTable connectionId={connectionId} />}
            {activeTab === 'pmdestroy' && <ProxmoxDestroyTable connectionId={connectionId} />}
            {activeTab === 'compose' && <ComposeChangesTable connectionId={connectionId} />}
            {activeTab === 'updates' && <UpdatesTable connectionId={connectionId} />}
            {activeTab === 'prune' && <PruneTable connectionId={connectionId} />}
          </div>
        </CardContent>
      </Card>
    </div>
  )
}
