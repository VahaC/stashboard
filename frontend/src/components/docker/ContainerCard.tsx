import { useState } from 'react'
import { Link } from 'react-router-dom'
import { Activity, Bell, FileText, Info, MoreHorizontal, Play, RefreshCw, Search, Server, Square, SquareChevronRight, Trash2 } from 'lucide-react'
import { Button } from '@/components/ui/button'
import type { DockerContainerCard, DockerContainerProxmoxLink, DockerWatch } from '@/lib/types'
import { EntityCard } from '@/components/shared/EntityCard'
import { ContainerIcon } from './atoms/ContainerIcon'
import { FloatingMenu } from '@/components/shared/FloatingMenu'
import { UpdateStatusBadge } from './atoms/UpdateStatusBadge'
import { ContainerLifecycleActions } from './atoms/ContainerLifecycleActions'
import { RemoveConfirmDialog } from './atoms/RemoveConfirmDialog'
import type { ContainerModalTab } from '@/components/ContainerModal'

export type ContainerCardVariant = 'docker-page' | 'service-modal'

export type ContainerCardProps = {
  card: DockerContainerCard
  /** Compact link to a Stashboard watch if one tracks this container. */
  linkedWatch?: DockerWatch | null
  /** Decides default modal tab and action density. */
  variant: ContainerCardVariant
  /** Feature flag from /api/features. */
  allowRemoval: boolean
  /** V8.2 — true when the connection is SSH-based, the only transport that
   *  can delete files on the host. Gates the "also delete the project
   *  folder" opt-in in the remove-confirm dialog. */
  isSshHost: boolean
  /** When true, the lifecycle/diagnostics action rows are disabled. */
  busy: boolean
  /** Optional inline error from the last action attempt. */
  error?: string
  /** Caller decides what to do when a tab button is clicked. The
   *  variant decides what tab the bare-body click maps to. */
  onOpen: (tab: ContainerModalTab) => void
  onAction: (
    action: 'start' | 'stop' | 'restart' | 'remove',
    options?: { deleteProjectFolder?: boolean },
  ) => void | Promise<void>
  onComposeClick?: (project: string) => void
}

/**
 * Shared container card. Renders the same shape on `/docker` and inside
 * `ServiceModal → Docker`, with a single compact action row — diagnostics on
 * the left, lifecycle + overflow on the right. Layout matches mockup 5a of
 * the design doc.
 *
 * Click semantics:
 * - Card body click → `onOpen('overview')` (docker-page) or
 *   `onOpen('watch')` (service-modal).
 * - Logs / Stats → `onOpen(tab)` with propagation stopped.
 * - Inspect / Remove live behind the `⋯` overflow menu.
 */
export function ContainerCard({
  card, linkedWatch = null, variant, allowRemoval, isSshHost, busy, error, onOpen, onAction,
}: ContainerCardProps) {
  const stateLower = card.state.toLowerCase()
  // A container stuck in a restart loop reports state "restarting", not
  // "running" — but it's still live and must be stoppable, otherwise it can
  // never reach "exited" to be removed.
  const running = stateLower === 'running' || stateLower === 'restarting'
  const stopped = stateLower === 'exited' || stateLower === 'dead'
  const notFound = stateLower === 'not found'
  const defaultTab: ContainerModalTab = notFound ? 'watch' : variant === 'service-modal' ? 'watch' : 'overview'
  const [removeDialogOpen, setRemoveDialogOpen] = useState(false)
  const [menuPos, setMenuPos] = useState<{ x: number; y: number } | null>(null)
  const openMenu = (e: { preventDefault(): void; stopPropagation(): void; clientX: number; clientY: number }) => { e.preventDefault(); e.stopPropagation(); setMenuPos({ x: e.clientX, y: e.clientY }) }

  const requestRemove = () => setRemoveDialogOpen(true)
  const confirmRemove = async (opts: { deleteProjectFolder: boolean }) => {
    await (onAction('remove', opts) as Promise<void>)
    setRemoveDialogOpen(false)
  }
  const composeProjectName = isSshHost ? card.composeProject : null

  // Docker reports a container's `Image` as the tag (`mariadb:11`) only while
  // that tag still points to the same local image. After Stashboard pulls a
  // newer image with the same tag, the running container's reported `Image`
  // becomes a bare `sha256:…` (the now-dangling old image) — a strong update
  // signal, but unreadable on the card. When tracked by a watch, prefer the
  // watch's stable `imageReference`; keep the raw value as the tooltip.
  const isDangling = card.image.startsWith('sha256:')
  const imageDisplay = isDangling && linkedWatch?.imageReference ? linkedWatch.imageReference : card.image

  return (
    <EntityCard
      state={card.state}
      name={card.name}
      icon={<ContainerIcon dataUri={card.iconDataUri} name={card.name} />}
      headerExtra={variant === 'service-modal' && linkedWatch
        ? <span className="cc-watch-chip">{linkedWatch.label}</span>
        : undefined}
      updateBadge={linkedWatch
        ? <UpdateStatusBadge
            status={linkedWatch.updateStatus}
            title={`Tracked${linkedWatch.label ? ` as "${linkedWatch.label}"` : ''}`}
            className="cc-update-badge"
          />
        : undefined}
      subtitle={imageDisplay}
      subtitleTitle={isDangling && linkedWatch?.imageReference ? card.image : undefined}
      statusLine={card.status ? renderStatusWithHealth(card.status) : undefined}
      chips={card.ports.length > 0
        ? card.ports.map((p, idx) => (
          <span key={idx} className="cc-chip docker-instances-card-port">
            {p.publicPort ? `${p.publicPort}→` : ''}{p.privatePort}/{p.type}
          </span>
        ))
        : undefined}
      extraMeta={card.proxmoxLink ? <ProxmoxRunsOnChip link={card.proxmoxLink} /> : undefined}
      onActivate={() => onOpen(defaultTab)}
      onContextMenu={openMenu}
      actionsLeft={!notFound && (
        <>
          <Button type="button" variant="ghost" size="sm" onClick={() => onOpen('overview')} title="Overview">
            <Info className="h-3.5 w-3.5" />
          </Button>
          <Button type="button" variant="ghost" size="sm" onClick={() => onOpen('inspect')} title="Inspect container">
            <Search className="h-3.5 w-3.5" />
          </Button>
          <Button type="button" variant="ghost" size="sm" onClick={() => onOpen('logs')} title="Live container logs">
            <FileText className="h-3.5 w-3.5" />
          </Button>
          <Button type="button" variant="ghost" size="sm" onClick={() => onOpen('stats')} title="Live container stats">
            <Activity className="h-3.5 w-3.5" />
          </Button>
          <Button
            type="button"
            variant="ghost"
            size="sm"
            onClick={() => onOpen('watch')}
            title={linkedWatch ? 'Edit update tracking' : 'Track this container for updates'}
          >
            <Bell className="h-3.5 w-3.5" />
          </Button>
          <Button
            type="button"
            variant="ghost"
            size="sm"
            onClick={() => onOpen('exec')}
            title="Open a shell inside this container (exec)"
          >
            <SquareChevronRight className="h-3.5 w-3.5" />
          </Button>
        </>
      )}
      actionsRight={
        <>
          {!notFound && (
            <ContainerLifecycleActions
              containerName={card.name}
              running={running}
              restarting={stateLower === 'restarting'}
              allowRemoval={false}
              busy={busy}
              density="full"
              onAction={onAction}
            />
          )}
          {stopped || notFound ? (
            <Button
              type="button"
              variant="outline"
              size="sm"
              className="ui-button-danger"
              disabled={busy}
              onClick={requestRemove}
            >
              <Trash2 className="h-3.5 w-3.5" />
              <span className="label-text">Remove</span>
            </Button>
          ) : (
            <CardOverflow
              allowRemoval={allowRemoval}
              busy={busy}
              onRequestRemove={requestRemove}
            />
          )}
        </>
      }
      error={error}
    >
      <RemoveConfirmDialog
        open={removeDialogOpen}
        containerName={card.name}
        image={card.image}
        status={card.status || card.state}
        ghost={notFound}
        composeProjectName={composeProjectName}
        onConfirm={confirmRemove}
        onCancel={() => setRemoveDialogOpen(false)}
      />
      {menuPos && (
        <FloatingMenu pos={menuPos} onClose={() => setMenuPos(null)}>
          {!notFound && (
            <>
              <button className="cgroup-menu-item" onClick={() => { setMenuPos(null); onOpen('overview') }}><Info className="h-3.5 w-3.5" /> Overview</button>
              <button className="cgroup-menu-item" onClick={() => { setMenuPos(null); onOpen('inspect') }}><Search className="h-3.5 w-3.5" /> Inspect</button>
              <button className="cgroup-menu-item" onClick={() => { setMenuPos(null); onOpen('logs') }}><FileText className="h-3.5 w-3.5" /> Logs</button>
              <button className="cgroup-menu-item" onClick={() => { setMenuPos(null); onOpen('stats') }}><Activity className="h-3.5 w-3.5" /> Stats</button>
              <button className="cgroup-menu-item" onClick={() => { setMenuPos(null); onOpen('watch') }}><Bell className="h-3.5 w-3.5" /> Watch</button>
              <button className="cgroup-menu-item" onClick={() => { setMenuPos(null); onOpen('exec') }}><SquareChevronRight className="h-3.5 w-3.5" /> Exec</button>
              <div className="cgroup-menu-sep" />
              {running && <>
                <button className="cgroup-menu-item" disabled={busy} onClick={() => { setMenuPos(null); void onAction('stop') }}><Square className="h-3.5 w-3.5" /> Stop</button>
                {stateLower !== 'restarting' && (
                  <button className="cgroup-menu-item" disabled={busy} onClick={() => { setMenuPos(null); void onAction('restart') }}><RefreshCw className="h-3.5 w-3.5" /> Restart</button>
                )}
              </>}
              {stopped && <button className="cgroup-menu-item" disabled={busy} onClick={() => { setMenuPos(null); void onAction('start') }}><Play className="h-3.5 w-3.5" /> Start</button>}
            </>
          )}
          {(stopped || notFound || (running && allowRemoval)) && (
            <>
              {!notFound && <div className="cgroup-menu-sep" />}
              <button className="cgroup-menu-item cgroup-menu-item--danger" disabled={busy} onClick={() => { setMenuPos(null); requestRemove() }}><Trash2 className="h-3.5 w-3.5" /> Remove</button>
            </>
          )}
        </FloatingMenu>
      )}
    </EntityCard>
  )
}

// V7.9 — "on <guest>" chip linking the container to the Proxmox guest it runs
// inside. Deep-links to the guest's modal on the Proxmox page; stops propagation
// so clicking it navigates instead of opening the container modal.
function ProxmoxRunsOnChip({ link }: { link: DockerContainerProxmoxLink }) {
  const kind = link.guestType === 'Qemu' || link.guestType === 2 ? 'VM' : 'LXC'
  return (
    <Link
      to={`/proxmox?connection=${link.proxmoxConnectionId}&vmid=${link.vmId}`}
      className="cc-chip cc-proxmox-chip"
      onClick={(e) => e.stopPropagation()}
      title={`Runs on Proxmox ${kind} ${link.vmId} — open LXC / VM`}
    >
      <Server className="h-3 w-3" />
      on {link.guestName}
    </Link>
  )
}

// Color the trailing "(healthy)" / "(unhealthy)" / "(starting)" segment of
// a Docker status string so the most-glance-worthy bit (the health) stands
// out without changing the muted treatment of the rest.
function renderStatusWithHealth(status: string) {
  const m = status.match(/^(.*?)(\(healthy\)|\(unhealthy\)|\(health: starting\)|\(starting\))(.*)$/)
  if (!m) return status
  const [, before, tag, after] = m
  const cls = tag === '(healthy)' ? 'healthy' : tag === '(unhealthy)' ? 'unhealthy' : 'starting'
  return <>{before}<span className={cls}>{tag}</span>{after}</>
}

interface CardOverflowProps {
  allowRemoval: boolean
  busy: boolean
  onRequestRemove: () => void
}

function CardOverflow({
  allowRemoval, busy, onRequestRemove,
}: CardOverflowProps) {
  if (!allowRemoval) return null

  return (
    <details className="cc-overflow">
      <summary aria-label="More actions">
        <MoreHorizontal className="h-3.5 w-3.5" />
      </summary>
      <div className="cc-overflow-menu">
        <button
          type="button"
          className="cc-overflow-item cc-overflow-item-danger"
          disabled={busy}
          onClick={onRequestRemove}
        >
          <Trash2 className="h-3.5 w-3.5" /> Remove
        </button>
      </div>
    </details>
  )
}
