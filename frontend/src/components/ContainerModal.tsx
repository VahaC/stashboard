import { useEffect, useMemo, useState } from 'react'
import { Link } from 'react-router-dom'
import {
  Activity,
  AlertCircle,
  Bell,
  Download,
  ExternalLink,
  FileText,
  Info,
  Search,
  TerminalSquare,
} from 'lucide-react'
import { Button } from '@/components/ui/button'
import { ContainerStateBadge } from '@/components/docker/atoms/ContainerStateBadge'
import { ContainerSummaryDL } from '@/components/docker/atoms/ContainerSummaryDL'
import { ContainerLifecycleActions } from '@/components/docker/atoms/ContainerLifecycleActions'
import { RemoveConfirmDialog } from '@/components/docker/atoms/RemoveConfirmDialog'
import { UpdateStatusBadge } from '@/components/docker/atoms/UpdateStatusBadge'
import { ContainerInspectBody } from '@/components/docker/ContainerInspectBody'
import { ContainerLogsPanel } from '@/components/docker/ContainerLogsPanel'
import { ContainerStatsPanel } from '@/components/docker/ContainerStatsPanel'
import { HostTerminalPanel } from '@/components/docker/HostTerminalPanel'
import { WatchTab } from '@/components/docker/WatchTab'
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog'
import {
  useConnectionWatches,
  useDockerInstanceInspect,
} from '@/lib/queries'
import type {
  DockerContainerCard,
  DockerWatch,
  Service,
} from '@/lib/types'
import { resolveDockerUpdateStatus } from '@/lib/types'
import { cn, getApiErrorMessage } from '@/lib/utils'

/**
 * V3.5 — modal opened from the Docker instances page when the user clicks
 * one of the per-card tab buttons (Inspect / Logs / Stats) or clicks the
 * card body. Four tabs:
 *
 * - **Overview** — container general info, lifecycle controls, and a
 *   linked-watch summary (when one exists) with a deep link into the
 *   full service modal for the heavier settings.
 * - **Inspect** — slimmed `docker inspect` snapshot. Reuses the V3.5
 *   instance-scoped endpoint so it works on un-tracked containers too.
 * - **Logs** — live NDJSON tail with the same controls as the V3.3
 *   watch panel (Pause / Resume / Stop / Download / stdout/stderr/
 *   timestamp toggles).
 * - **Stats** — per-second CPU / memory / network / block-I/O samples
 *   with inline-SVG sparklines, same shape as the V3.4 watch panel.
 *
 * The initial tab is passed in by the caller so each card button
 * opens the modal at the right view.
 */
export type ContainerModalTab = 'overview' | 'inspect' | 'logs' | 'stats' | 'watch' | 'terminal'

interface ContainerModalProps {
  connectionId: string
  card: DockerContainerCard
  initialTab: ContainerModalTab
  allowRemoval: boolean
  busy: boolean
  actionError?: string
  /** V5.3 — connection host type, so the Terminal tab can show the live shell
   *  for SSH or a disabled explainer otherwise. */
  hostType: 'LocalSocket' | 'TcpTls' | 'Ssh'
  /** V5.3 — per-connection host-terminal opt-in. */
  allowHostShell: boolean
  /** V5.3 — global host-terminal master switch (server feature flag). */
  allowHostShellGlobal: boolean
  /** Optional context — when present, the Watch tab gains full edit
   *  capabilities for the service's watch (or empty-state with a create
   *  CTA when no watch exists yet). */
  serviceContext?: { service: Service; watch: DockerWatch | null }
  onAction: (kind: 'start' | 'stop' | 'restart' | 'remove') => void
  onClose: () => void
}

const TABS: ReadonlyArray<{ id: ContainerModalTab; label: string; icon: typeof Info }> = [
  { id: 'overview', label: 'Overview', icon: Info },
  { id: 'inspect', label: 'Inspect', icon: Search },
  { id: 'logs', label: 'Logs', icon: FileText },
  { id: 'stats', label: 'Stats', icon: Activity },
  { id: 'watch', label: 'Watch', icon: Bell },
  { id: 'terminal', label: 'Terminal', icon: TerminalSquare },
]

export function ContainerModal({
  connectionId, card, initialTab, allowRemoval, busy, actionError, serviceContext,
  hostType, allowHostShell, allowHostShellGlobal, onAction, onClose,
}: ContainerModalProps) {
  const [tab, setTab] = useState<ContainerModalTab>(initialTab)

  useEffect(() => { setTab(initialTab) }, [initialTab])

  return (
    <Dialog open onOpenChange={(open) => { if (!open) onClose() }}>
      <DialogContent className="container-modal-content">
        <DialogHeader>
          <DialogTitle className="container-modal-title">
            <span>{card.name}</span>
            <ContainerStateBadge state={card.state} />
          </DialogTitle>
          <DialogDescription className="container-modal-image">{card.image}</DialogDescription>
        </DialogHeader>

        <nav className="container-modal-tabs" role="tablist">
          {TABS.map(({ id, label, icon: Icon }) => {
            const ghostDisabled =
              card.state.toLowerCase() === 'not found' &&
              (id === 'inspect' || id === 'logs' || id === 'stats')
            return (
              <button
                key={id}
                type="button"
                role="tab"
                aria-selected={tab === id}
                disabled={ghostDisabled}
                className={cn(
                  'container-modal-tab',
                  tab === id && 'container-modal-tab-active',
                  ghostDisabled && 'container-modal-tab-disabled',
                )}
                onClick={() => !ghostDisabled && setTab(id)}
                title={ghostDisabled ? 'Container not found on host' : undefined}
              >
                <Icon className="h-3.5 w-3.5" /> {label}
              </button>
            )
          })}
        </nav>

        <div className="container-modal-body" role="tabpanel">
          {tab === 'overview' && (
            <OverviewTab
              connectionId={connectionId}
              card={card}
              allowRemoval={allowRemoval}
              busy={busy}
              actionError={actionError}
              serviceContext={serviceContext}
              onAction={onAction}
              onOpenWatch={() => setTab('watch')}
            />
          )}
          {tab === 'inspect' && (
            <InspectTab connectionId={connectionId} containerName={card.name} enabled />
          )}
          {tab === 'logs' && (
            <ContainerLogsPanel connectionId={connectionId} containerName={card.name} />
          )}
          {tab === 'stats' && (
            <ContainerStatsPanel connectionId={connectionId} containerName={card.name} />
          )}
          {tab === 'watch' && (
            <WatchTab
              connectionId={connectionId}
              card={card}
              serviceContext={serviceContext}
            />
          )}
          {tab === 'terminal' && (
            <HostTerminalPanel
              connectionId={connectionId}
              hostType={hostType}
              allowHostShell={allowHostShell}
              allowHostShellGlobal={allowHostShellGlobal}
            />
          )}
        </div>
      </DialogContent>
    </Dialog>
  )
}

// ── Overview tab ───────────────────────────────────────────────────────────

interface OverviewTabProps {
  connectionId: string
  card: DockerContainerCard
  allowRemoval: boolean
  busy: boolean
  actionError?: string
  serviceContext?: { service: Service; watch: DockerWatch | null }
  onAction: (kind: 'start' | 'stop' | 'restart' | 'remove') => void
  onOpenWatch: () => void
}

function OverviewTab({
  connectionId, card, allowRemoval, busy, actionError, serviceContext, onAction, onOpenWatch,
}: OverviewTabProps) {
  const stateLower = card.state.toLowerCase()
  const running = stateLower === 'running'
  const stopped = stateLower === 'exited' || stateLower === 'dead'
  const notFound = stateLower === 'not found'
  const [removeDialogOpen, setRemoveDialogOpen] = useState(false)
  // V3.6 — resolve the tracked container (watch) for this card by name from
  // the connection's watch list so the section shows live update status /
  // last-checked rather than a stale stub.
  const watchesQuery = useConnectionWatches(connectionId)
  const linkedWatch = useMemo<DockerWatch | null>(() => {
    if (serviceContext?.watch && serviceContext.watch.containerName === card.name) return serviceContext.watch
    return watchesQuery.data?.find((w) => w.containerName === card.name) ?? null
  }, [watchesQuery.data, serviceContext, card.name])

  const updateAvailable =
    linkedWatch && resolveDockerUpdateStatus(linkedWatch.updateStatus) === 'UpdateAvailable'

  return (
    <div className="container-modal-overview">
      {updateAvailable && linkedWatch && (
        <section className="container-modal-section cm-update-banner">
          <div className="cm-update-banner-head">
            <UpdateStatusBadge status={linkedWatch.updateStatus} />
            <strong>An update is available for {linkedWatch.label || card.name}</strong>
          </div>
          <p className="text-sm">
            New digest detected for <code className="container-modal-code">{linkedWatch.imageReference}</code>.
            Pull the latest image and recreate the container, or open the Watch tab for full
            control (digest details, release notes, update history).
          </p>
          <div className="container-modal-actions">
            <Button type="button" size="sm" onClick={onOpenWatch}>
              <Download className="h-3.5 w-3.5" /> Open Watch tab
            </Button>
          </div>
        </section>
      )}

      <section className="container-modal-section">
        <h3 className="container-modal-section-title">Container</h3>
        <ContainerSummaryDL card={card} />
      </section>

      {!notFound && (
        <section className="container-modal-section">
          <h3 className="container-modal-section-title">Lifecycle</h3>
          <div className="container-modal-actions">
            <ContainerLifecycleActions
              containerName={card.name}
              running={running}
              allowRemoval={allowRemoval || stopped}
              busy={busy}
              onAction={onAction}
              onRemoveRequest={() => setRemoveDialogOpen(true)}
            />
          </div>
          {actionError && (
            <p className="docker-instances-card-action-error">
              <AlertCircle className="h-3.5 w-3.5 inline" /> {actionError}
            </p>
          )}
        </section>
      )}

      {linkedWatch && (
        <section className="container-modal-section">
          <h3 className="container-modal-section-title">Update tracking</h3>
          <dl className="container-modal-summary">
            <dt>Label</dt>
            <dd>{linkedWatch.label}</dd>
            <dt>Image reference</dt>
            <dd><code className="container-modal-code">{linkedWatch.imageReference}</code></dd>
            <dt>Update status</dt>
            <dd>{resolveDockerUpdateStatus(linkedWatch.updateStatus)}</dd>
            {linkedWatch.lastCheckedUtc && (
              <>
                <dt>Last checked</dt>
                <dd>{new Date(linkedWatch.lastCheckedUtc).toLocaleString()}</dd>
              </>
            )}
            <dt>Manage</dt>
            <dd className="container-modal-actions">
              <Button type="button" size="sm" variant="outline" onClick={onOpenWatch}>
                <Bell className="h-3 w-3" /> Open Watch tab
              </Button>
              {linkedWatch.webResourceId && (
                <Link
                  to={`/?service=${linkedWatch.webResourceId}`}
                  className="docker-instances-card-watch-link"
                >
                  <ExternalLink className="h-3 w-3" /> Open linked service
                </Link>
              )}
            </dd>
          </dl>
        </section>
      )}

      <RemoveConfirmDialog
        open={removeDialogOpen}
        containerName={card.name}
        image={card.image}
        status={card.status || card.state}
        ghost={notFound}
        onConfirm={async () => {
          await (onAction('remove') as unknown as Promise<void>)
          setRemoveDialogOpen(false)
        }}
        onCancel={() => setRemoveDialogOpen(false)}
      />
    </div>
  )
}

// ── Inspect tab ────────────────────────────────────────────────────────────

function InspectTab({
  connectionId, containerName, enabled,
}: {
  connectionId: string
  containerName: string
  enabled: boolean
}) {
  const query = useDockerInstanceInspect(connectionId, containerName, enabled)
  if (query.isLoading) return <p className="container-modal-empty">Loading from daemon…</p>
  if (query.error) {
    return (
      <p className="container-modal-error">
        <AlertCircle className="h-3.5 w-3.5 inline" /> {getApiErrorMessage(query.error) ?? 'Failed to inspect container'}
      </p>
    )
  }
  if (!query.data) return <p className="container-modal-empty">No data.</p>
  return <ContainerInspectBody data={query.data} />
}
