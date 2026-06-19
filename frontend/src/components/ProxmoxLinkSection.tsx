import { useState } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import { ExternalLink, Plus, Server, Unlink } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { useProxmoxConnections } from '@/lib/proxmox-queries'
import { useRemoveProxmoxLink, useUpsertService } from '@/lib/queries'
import { resolveDockerUpdateStatus, type ProxmoxGuestType, type Service, type ServiceUpsert } from '@/lib/types'
import { cn, parseApiErrors } from '@/lib/utils'

interface Props {
  service: Service
}

const guestKindLabel = (t: ProxmoxGuestType) => (t === 'Qemu' || t === 2 ? 'VM' : 'LXC')

/** Project a Service back into a ServiceUpsert so we can PUT a Proxmox-host
 *  assignment without dropping the rest of the fields (mirrors the Docker tab). */
function serviceToUpsert(s: Service, proxmoxConnectionId: string | null): ServiceUpsert {
  return {
    name: s.name,
    mainUrl: s.mainUrl,
    mainUrlHealthCheckEnabled: s.mainUrlHealthCheckEnabled,
    additionalUrl: s.additionalUrl,
    additionalUrlHealthCheckEnabled: s.additionalUrlHealthCheckEnabled,
    offlineNotificationsEnabled: s.offlineNotificationsEnabled,
    healthCheckUrl: s.healthCheckUrl,
    healthCheckMethod: s.healthCheckMethod,
    expectedStatusRange: s.expectedStatusRange,
    notes: s.notes,
    categoryId: s.categoryId,
    logoSource: s.logoSource,
    customLogoPath: s.customLogoPath,
    tags: s.tags,
    credentials: s.credentials.map((c) => ({ key: c.key, value: c.value, isSecret: c.isSecret })),
    dockerConnectionId: s.dockerConnectionId,
    proxmoxConnectionId,
  }
}

/**
 * V7.9 — the service modal's Proxmox tab. Structurally identical to the Docker
 * tab: a top "Proxmox host" picker (assigns the service's `proxmoxConnectionId`,
 * exactly like the Docker host picker) plus a read-only list of linked guests
 * below. Linking individual guests is done on the Proxmox page (each LXC/VM modal
 * has a "Linked service" dropdown), mirroring how containers are linked from the
 * Docker page.
 */
export function ProxmoxLinkSection({ service }: Props) {
  return (
    <div className="docker-section">
      <ProxmoxConnectionPicker service={service} />
      <LinkedGuestsSummary service={service} />
    </div>
  )
}

// ── Proxmox host picker (mirrors the Docker host picker) ─────────────────────

function ProxmoxConnectionPicker({ service }: Props) {
  const connectionsQuery = useProxmoxConnections()
  const upsertService = useUpsertService()
  const navigate = useNavigate()
  const [error, setError] = useState<string | null>(null)

  const connections = connectionsQuery.data ?? []
  const assigned = connections.find((c) => c.id === service.proxmoxConnectionId) ?? null

  const assign = async (connectionId: string | null) => {
    setError(null)
    try {
      await upsertService.mutateAsync({ id: service.id, data: serviceToUpsert(service, connectionId) })
    } catch (e: unknown) {
      const { globalError } = parseApiErrors(e)
      setError(globalError ?? 'Failed to assign Proxmox host')
    }
  }

  return (
    <div className="docker-connection-picker">
      <div className="docker-section-header">
        <span className="docker-section-title">Proxmox host</span>
      </div>
      {error && <p className="service-modal-error">{error}</p>}
      <div className="docker-connection-picker-row">
        <select
          className="service-modal-select"
          value={assigned?.id ?? ''}
          disabled={connectionsQuery.isLoading || upsertService.isPending}
          onChange={(e) => void assign(e.target.value || null)}
        >
          <option value="">
            {connectionsQuery.isLoading ? 'Loading hosts…' : '— no Proxmox host —'}
          </option>
          {connections.map((c) => (
            <option key={c.id} value={c.id}>{c.name} · {c.nodeName}</option>
          ))}
        </select>
        <Button
          type="button"
          variant="outline"
          size="sm"
          onClick={() => navigate('/proxmox')}
          aria-label="Add connection"
        >
          <Plus className="h-3.5 w-3.5" />
          <span className="docker-connection-button-label">Add connection</span>
        </Button>
      </div>

      {!assigned && connections.length === 0 && !connectionsQuery.isLoading && (
        <p className="text-sm text-[var(--muted-foreground)]">
          You don't have any Proxmox hosts yet. Add one on the Proxmox page — its LXCs
          and VMs are discovered automatically and can then be linked here.
        </p>
      )}
    </div>
  )
}

// ── Read-only linked-guests summary (mirrors "Tracked containers") ───────────

function LinkedGuestsSummary({ service }: Props) {
  const linked = service.linkedProxmoxGuests ?? []
  const unlink = useRemoveProxmoxLink(service.id)
  const hostHref = service.proxmoxConnectionId
    ? `/proxmox?connection=${service.proxmoxConnectionId}`
    : '/proxmox'

  return (
    <div className="docker-watch-list">
      <div className="docker-section-header">
        <span className="docker-section-title">
          Linked Proxmox LXCs / VMs
          {linked.length > 0 && (
            <span className="service-modal-label-help">({linked.length})</span>
          )}
        </span>
        <Link to={hostHref} className="docker-instances-card-watch-link">
          <Server className="h-3.5 w-3.5" />
          Manage on Proxmox page
        </Link>
      </div>

      {linked.length === 0 ? (
        <p className="text-sm text-[var(--muted-foreground)]">
          No Proxmox LXCs / VMs linked to this service yet. Open the{' '}
          <Link to={hostHref} className="docker-instances-card-watch-link">Proxmox page</Link>{' '}
          and use an LXC / VM's <strong>Linked service</strong> dropdown to attach it —
          its pending-update status then shows on this service's card. A service can link both
          Docker and Proxmox at once.
        </p>
      ) : (
        <ul className="docker-linked-watches">
          {linked.map((g) => {
            const status = resolveDockerUpdateStatus(g.updateStatus)
            return (
              <li key={`${g.proxmoxConnectionId}::${g.vmId}`} className="docker-linked-watch-row">
                <div className="docker-linked-watch-main">
                  <span className="docker-linked-watch-name">{g.name}</span>
                  <span
                    className={cn('docker-section-badge')}
                    data-status={status}
                    title={`Update status: ${status}`}
                  >
                    {status}
                  </span>
                  <span className="service-modal-label-help">
                    {guestKindLabel(g.guestType)} {g.vmId}
                  </span>
                </div>
                <div className="docker-linked-watch-meta">
                  <span className="service-modal-label-help">
                    {g.isRunning ? 'running' : 'stopped'}
                    {g.pendingUpdates != null ? ` · ${g.pendingUpdates} update${g.pendingUpdates === 1 ? '' : 's'} pending` : ''}
                    {!g.monitoringEnabled ? ' · monitoring off' : ''}
                  </span>
                  <button
                    type="button"
                    className="docker-instances-card-watch-link"
                    disabled={unlink.isPending}
                    onClick={() => unlink.mutate({ proxmoxConnectionId: g.proxmoxConnectionId, vmId: g.vmId })}
                    title="Unlink this LXC / VM from the service (the LXC / VM itself is untouched)"
                  >
                    <Unlink className="h-3 w-3" />
                    Unlink
                  </button>
                  <Link
                    to={`/proxmox?connection=${g.proxmoxConnectionId}&vmid=${g.vmId}`}
                    className="docker-instances-card-watch-link"
                  >
                    <ExternalLink className="h-3 w-3" />
                    Open LXC / VM
                  </Link>
                </div>
              </li>
            )
          })}
        </ul>
      )}
    </div>
  )
}
