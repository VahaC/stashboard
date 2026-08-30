import { useMemo, useState } from 'react'
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
  SquareChevronRight,
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
import { ContainerExecPanel } from '@/components/docker/ContainerExecPanel'
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
  useUploadContainerIcon,
  useResetContainerIcon,
} from '@/lib/queries'
import { ContainerIcon } from '@/components/docker/atoms/ContainerIcon'
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
export type ContainerModalTab = 'overview' | 'inspect' | 'logs' | 'stats' | 'watch' | 'exec'

interface ContainerModalProps {
  connectionId: string
  card: DockerContainerCard
  initialTab: ContainerModalTab
  allowRemoval: boolean
  /** V8.2 — true when the connection is SSH-based, the only transport that
   *  can delete files on the host. Gates the "also delete the project
   *  folder" opt-in in the remove-confirm dialog. */
  isSshHost: boolean
  busy: boolean
  actionError?: string
  /** V5.7 — per-connection container-exec opt-in. */
  allowExec: boolean
  /** V5.7 — global container-exec master switch (server feature flag). */
  allowExecGlobal: boolean
  /** Optional context — when present, the Watch tab gains full edit
   *  capabilities for the service's watch (or empty-state with a create
   *  CTA when no watch exists yet). */
  serviceContext?: { service: Service; watch: DockerWatch | null }
  onAction: (
    kind: 'start' | 'stop' | 'restart' | 'remove',
    options?: { deleteProjectFolder?: boolean },
  ) => void
  onClose: () => void
}

const TABS: ReadonlyArray<{ id: ContainerModalTab; label: string; icon: typeof Info }> = [
  { id: 'overview', label: 'Overview', icon: Info },
  { id: 'inspect', label: 'Inspect', icon: Search },
  { id: 'logs', label: 'Logs', icon: FileText },
  { id: 'stats', label: 'Stats', icon: Activity },
  { id: 'watch', label: 'Watch', icon: Bell },
  { id: 'exec', label: 'Exec', icon: SquareChevronRight },
]

export function ContainerModal({
  connectionId, card, initialTab, allowRemoval, isSshHost, busy, actionError, serviceContext,
  allowExec, allowExecGlobal, onAction, onClose,
}: ContainerModalProps) {
  const [tab, setTab] = useState<ContainerModalTab>(initialTab)
  // Reset the active tab when the parent opens the modal on a different tab.
  const [prevInitialTab, setPrevInitialTab] = useState(initialTab)
  if (initialTab !== prevInitialTab) {
    setPrevInitialTab(initialTab)
    setTab(initialTab)
  }

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
              isSshHost={isSshHost}
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
          {tab === 'exec' && (
            <ContainerExecPanel
              connectionId={connectionId}
              containerName={card.name}
              state={card.state}
              allowExec={allowExec}
              allowExecGlobal={allowExecGlobal}
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
  isSshHost: boolean
  busy: boolean
  actionError?: string
  serviceContext?: { service: Service; watch: DockerWatch | null }
  onAction: (
    kind: 'start' | 'stop' | 'restart' | 'remove',
    options?: { deleteProjectFolder?: boolean },
  ) => void
  onOpenWatch: () => void
}

function OverviewTab({
  connectionId, card, allowRemoval, isSshHost, busy, actionError, serviceContext, onAction, onOpenWatch,
}: OverviewTabProps) {
  const stateLower = card.state.toLowerCase()
  // A container stuck in a restart loop reports state "restarting", not
  // "running" — but it's still live and must be stoppable, otherwise it can
  // never reach "exited" to be removed.
  const running = stateLower === 'running' || stateLower === 'restarting'
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
  const composeProjectName = isSshHost ? card.composeProject : null

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

      <section className="container-modal-section">
        <h3 className="container-modal-section-title">Icon</h3>
        <ContainerIconSection connectionId={connectionId} card={card} />
      </section>

      {!notFound && (
        <section className="container-modal-section">
          <h3 className="container-modal-section-title">Lifecycle</h3>
          <div className="container-modal-actions">
            <ContainerLifecycleActions
              containerName={card.name}
              running={running}
              restarting={stateLower === 'restarting'}
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
        composeProjectName={composeProjectName}
        onConfirm={async (opts) => {
          await (onAction('remove', opts) as unknown as Promise<void>)
          setRemoveDialogOpen(false)
        }}
        onCancel={() => setRemoveDialogOpen(false)}
      />
    </div>
  )
}

// ── Icon management (overview) ───────────────────────────────────────────────

/**
 * V7.8 — set or clear a container's custom card icon. A preview of the current
 * avatar (custom upload → official → placeholder), an upload button reusing the
 * same `<input type="file" accept="image/*">` pattern as the service logo, and a
 * Reset-to-auto button that drops the override. Both mutations invalidate the
 * card list so the new avatar shows up immediately.
 */
function ContainerIconSection({
  connectionId, card,
}: {
  connectionId: string
  card: DockerContainerCard
}) {
  const upload = useUploadContainerIcon(connectionId)
  const reset = useResetContainerIcon(connectionId)
  const [error, setError] = useState<string | null>(null)
  const busy = upload.isPending || reset.isPending

  const onFile = async (e: React.ChangeEvent<HTMLInputElement>) => {
    const file = e.target.files?.[0]
    e.target.value = ''
    if (!file) return
    setError(null)
    try {
      await upload.mutateAsync({ containerName: card.name, file })
    } catch (err) {
      setError(getApiErrorMessage(err) ?? 'Upload failed')
    }
  }

  const onReset = async () => {
    setError(null)
    try {
      await reset.mutateAsync(card.name)
    } catch (err) {
      setError(getApiErrorMessage(err) ?? 'Reset failed')
    }
  }

  return (
    <div className="container-modal-icon-row">
      <ContainerIcon dataUri={card.iconDataUri} name={card.name} />
      <div className="container-modal-actions">
        <label className="service-modal-upload">
          Upload
          <input type="file" accept="image/*" className="sr-only" disabled={busy} onChange={onFile} />
        </label>
        <Button type="button" variant="outline" size="sm" disabled={busy} onClick={onReset}>
          Reset to auto
        </Button>
      </div>
      {error && (
        <p className="docker-instances-card-action-error">
          <AlertCircle className="h-3.5 w-3.5 inline" /> {error}
        </p>
      )}
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
