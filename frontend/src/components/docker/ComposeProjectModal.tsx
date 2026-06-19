import { Fragment, useMemo, useState } from 'react'
import { AlertCircle, FileCode, FileText, History, Layers, Lock, Plus } from 'lucide-react'
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog'
import { StateBadge } from '@/components/shared/StateBadge'
import {
  useComposeProject,
  useDockerConnection,
  useDockerInstanceContainers,
} from '@/lib/queries'
import type {
  ComposeProject,
  ComposeService,
  DockerContainerCard,
} from '@/lib/types'
import { getApiErrorMessage } from '@/lib/utils'
import { cn } from '@/lib/utils'
import { ComposeHistoryTab } from './ComposeHistoryTab'
import { ComposeRawFileEditor } from './ComposeRawFileEditor'
import { ComposeServiceCreateForm } from './ComposeServiceCreateForm'
import { ComposeSharedResourcesTab } from './ComposeResourcesPanel'
import { ComposeServiceEditForm } from './ComposeServiceEditForm'
import '@/styles/docker-instances.css'

/** V7.3/V7.4 — the active top-level tab: one container, the shared resources
 *  (Compose top-level networks / volumes / secrets / configs), the "Add
 *  service" wizard, or the raw-file editor. */
type ActivePanel =
  | { type: 'container'; name: string }
  | { type: 'cross' }
  | { type: 'add' }
  | { type: 'raw' }
  | { type: 'history' }

/**
 * V7.1.1 — the Compose viewer/editor, reworked from the standalone
 * `/projects/{id}/compose/{project}` page into a modal opened straight from the
 * Docker page. The button only appears for compose **projects** (the group
 * header) and for **single** compose-managed containers (their card) — a
 * non-compose container has no such button. The modal carries a compact
 * project-level header strip and then one **tab per service**: the tab shows
 * the V7.1 editable basic-fields form plus a read-only block for the fields the
 * form doesn't cover (depends_on / networks / limits / …), with the matched
 * live container's runtime state badge.
 */

export interface ComposeProjectModalProps {
  connectionId: string
  /** Discovered project name (the container label, not the file's `name:`). */
  project: string
  onClose: () => void
}

export function ComposeProjectModal({ connectionId, project, onClose }: ComposeProjectModalProps) {
  const connection = useDockerConnection(connectionId)
  const compose = useComposeProject(connectionId, project)
  // Live containers on the same connection, joined by the compose-service label
  // so each service tab can wear the real runtime state badge.
  const containers = useDockerInstanceContainers(connectionId)

  const projectData = compose.data
  const services = projectData?.services ?? []
  const [active, setActive] = useState<ActivePanel | null>(null)
  // Default to the first container; fall back to cross-container settings for a
  // container-less file.
  const resolved: ActivePanel = active
    ?? (services[0] ? { type: 'container', name: services[0].name } : { type: 'cross' })
  const activeService = resolved.type === 'container'
    ? services.find((s) => s.name === resolved.name) ?? services[0] ?? null
    : null
  const activeName = activeService?.name ?? null

  const containerByService = useMemo(() => {
    const map = new Map<string, DockerContainerCard>()
    for (const c of containers.data ?? []) {
      if (!c.composeService) continue
      // Prefer containers from this very project so a service name shared across
      // stacks doesn't mismatch.
      const existing = map.get(c.composeService)
      if (existing && existing.composeProject === project) continue
      map.set(c.composeService, c)
    }
    return map
  }, [containers.data, project])

  const containerFor = (svc: ComposeService): DockerContainerCard | null =>
    containerByService.get(svc.name)
      ?? (svc.containerName
        ? (containers.data ?? []).find((c) => c.name === svc.containerName)
        : undefined)
      ?? null

  // V7.2 — any running container on this host gives the resources panel its
  // host-capacity sample (onlineCpus + memoryLimitBytes) over the V3.5 stats
  // stream; null when the host has nothing running to sample.
  const capacityContainerName = useMemo(
    () => (containers.data ?? []).find((c) => c.state === 'running')?.name ?? null,
    [containers.data],
  )

  const readOnly = (projectData?.unsupportedFeatures.length ?? 0) > 0

  return (
    <Dialog open onOpenChange={(open) => { if (!open) onClose() }}>
      <DialogContent className="container-modal-content compose-project-modal">
        <DialogHeader>
          <DialogTitle className="container-modal-title">
            <FileCode className="h-4 w-4" />
            <span>{project}</span>
            {connection.data ? <span className="compose-modal-host">@ {connection.data.name}</span> : null}
          </DialogTitle>
          <DialogDescription className="container-modal-image">
            {projectData
              ? `${projectData.fileName} · ${projectData.projectPath}`
              : 'Compose file discovered from the containers’ working_dir label.'}
          </DialogDescription>
        </DialogHeader>

        {(compose.isLoading || connection.isLoading) && (
          <p className="container-modal-empty">Loading Compose project…</p>
        )}
        {compose.error && (
          <p className="container-modal-error">
            <AlertCircle className="h-3.5 w-3.5 inline" />{' '}
            {getApiErrorMessage(compose.error) ?? 'Failed to load the Compose project'}
          </p>
        )}

        {projectData && (
          <>
            {readOnly && <ReadOnlyBanner project={projectData} />}

            {/* One tab per container, then a separate tab for the resources
                shared across the whole project (networks / volumes / …). */}
            <nav className="container-modal-tabs containers" role="tablist" aria-label="Containers and shared resources">
              {services.map((svc) => {
                const container = containerFor(svc)
                const selected = resolved.type === 'container' && svc.name === activeName
                return (
                  <button
                    key={svc.name}
                    type="button"
                    role="tab"
                    aria-selected={selected}
                    className={cn('container-modal-tab', selected && 'container-modal-tab-active')}
                    onClick={() => setActive({ type: 'container', name: svc.name })}
                  >
                    <StateBadge state={container?.state ?? 'not deployed'} size="sm" />
                    {svc.name}
                  </button>
                )
              })}
              <button
                type="button"
                role="tab"
                aria-selected={resolved.type === 'cross'}
                className={cn(
                  'container-modal-tab compose-cross-tab',
                  resolved.type === 'cross' && 'container-modal-tab-active',
                )}
                onClick={() => setActive({ type: 'cross' })}
              >
                <Layers className="h-3.5 w-3.5" />
                Shared resources
              </button>
              {!readOnly && (
                <button
                  type="button"
                  role="tab"
                  aria-selected={resolved.type === 'add'}
                  className={cn(
                    'container-modal-tab compose-add-tab',
                    resolved.type === 'add' && 'container-modal-tab-active',
                  )}
                  onClick={() => setActive({ type: 'add' })}
                >
                  <Plus className="h-3.5 w-3.5" />
                  Add service
                </button>
              )}
              <button
                type="button"
                role="tab"
                aria-selected={resolved.type === 'raw'}
                className={cn(
                  'container-modal-tab compose-raw-tab',
                  resolved.type === 'raw' && 'container-modal-tab-active',
                )}
                onClick={() => setActive({ type: 'raw' })}
              >
                <FileText className="h-3.5 w-3.5" />
                Raw YAML
              </button>
              <button
                type="button"
                role="tab"
                aria-selected={resolved.type === 'history'}
                className={cn(
                  'container-modal-tab compose-history-tab-btn',
                  resolved.type === 'history' && 'container-modal-tab-active',
                )}
                onClick={() => setActive({ type: 'history' })}
              >
                <History className="h-3.5 w-3.5" />
                History
              </button>
            </nav>

            <div className="container-modal-body" role="tabpanel">
              {resolved.type === 'cross' ? (
                <ComposeSharedResourcesTab
                  connectionId={connectionId}
                  project={project}
                  projectData={projectData}
                  readOnly={readOnly}
                />
              ) : resolved.type === 'add' ? (
                <ComposeServiceCreateForm
                  connectionId={connectionId}
                  project={project}
                  projectData={projectData}
                  capacityContainerName={capacityContainerName}
                  onCreated={(name) => setActive({ type: 'container', name })}
                />
              ) : resolved.type === 'raw' ? (
                <ComposeRawFileEditor connectionId={connectionId} project={project} />
              ) : resolved.type === 'history' ? (
                <ComposeHistoryTab connectionId={connectionId} project={project} />
              ) : activeService ? (
                <ComposeServiceTab
                  key={activeService.name}
                  connectionId={connectionId}
                  project={project}
                  projectData={projectData}
                  service={activeService}
                  readOnly={readOnly}
                  container={containerFor(activeService)}
                  capacityContainerName={capacityContainerName}
                />
              ) : null}
            </div>
          </>
        )}
      </DialogContent>
    </Dialog>
  )
}

// ── Read-only banner ───────────────────────────────────────────────────────

function ReadOnlyBanner({ project }: { project: ComposeProject }) {
  return (
    <p className="compose-banner" role="status">
      <Lock className="h-3.5 w-3.5 inline" />{' '}
      <strong>Read-only</strong> — file uses {project.unsupportedFeatures.join(', ')}.
      Editing stays disabled until round-trip support lands.
    </p>
  )
}

// ── One service tab ────────────────────────────────────────────────────────

interface ComposeServiceTabProps {
  connectionId: string
  project: string
  projectData: ComposeProject
  service: ComposeService
  readOnly: boolean
  container: DockerContainerCard | null
  capacityContainerName: string | null
}

function ComposeServiceTab({
  connectionId, project, projectData, service, readOnly, container, capacityContainerName,
}: ComposeServiceTabProps) {
  return (
    <div className="compose-tab">
      <div className="compose-tab-head">
        <StateBadge state={container?.state ?? 'not deployed'} />
        <span className="compose-tab-name">{service.name}</span>
        {service.image && <code className="container-modal-code">{service.image}</code>}
      </div>

      {readOnly
        ? <ServiceDetails service={service} all />
        : (
          <>
            <ComposeServiceEditForm
              connectionId={connectionId}
              project={project}
              projectData={projectData}
              service={service}
              capacityContainerName={capacityContainerName}
            />
            <ServiceDetails service={service} />
          </>
        )}
    </div>
  )
}

// ── Read-only details ──────────────────────────────────────────────────────
//
// `all` (read-only project) shows everything; otherwise only the fields the
// editable form above doesn't already cover, so nothing is shown twice.

function ServiceDetails({ service, all = false }: { service: ComposeService; all?: boolean }) {
  const rows: Array<{ label: string; value: React.ReactNode }> = []

  if (service.containerName) {
    rows.push({ label: 'Container', value: <code className="container-modal-code">{service.containerName}</code> })
  }
  if (service.dependsOn.length > 0) rows.push({ label: 'Depends on', value: service.dependsOn.join(', ') })
  if (service.networks.length > 0) rows.push({ label: 'Networks', value: service.networks.join(', ') })
  if (service.envFiles.length > 0) {
    rows.push({
      label: 'Env files',
      value: service.envFiles.map((f, i) => (
        <code key={i} className="container-modal-code compose-detail-item">{f}</code>
      )),
    })
  }
  if (all) {
    if (service.image) rows.unshift({ label: 'Image', value: <code className="container-modal-code">{service.image}</code> })
    if (service.restart) rows.push({ label: 'Restart', value: service.restart })
    if (service.command) {
      rows.push({ label: 'Command', value: <code className="container-modal-code compose-detail-item">{service.command}</code> })
    }
    if (service.entrypoint) {
      rows.push({ label: 'Entrypoint', value: <code className="container-modal-code compose-detail-item">{service.entrypoint}</code> })
    }
    if (service.user) rows.push({ label: 'User', value: <code className="container-modal-code">{service.user}</code> })
    if (service.workingDir) rows.push({ label: 'Working dir', value: <code className="container-modal-code">{service.workingDir}</code> })
    if (service.ports.length > 0) {
      rows.push({
        label: 'Ports',
        value: service.ports.map((p, i) => <code key={i} className="container-modal-code compose-detail-item">{p}</code>),
      })
    }
    if (service.volumes.length > 0) {
      rows.push({
        label: 'Mounts',
        value: service.volumes.map((v, i) => <code key={i} className="container-modal-code compose-detail-item">{v}</code>),
      })
    }
    if (service.environment.length > 0) {
      rows.push({
        label: 'Environment',
        value: service.environment.map((e) => (
          <code key={e.name} className="container-modal-code compose-detail-item">
            {e.name}{e.value !== null ? `=${e.value}` : ''}
          </code>
        )),
      })
    }
    if (service.labels.length > 0) {
      rows.push({
        label: 'Labels',
        value: service.labels.map((l) => (
          <code key={l.name} className="container-modal-code compose-detail-item">
            {l.name}{l.value !== null ? `=${l.value}` : ''}
          </code>
        )),
      })
    }
    const r = service.resources
    const limits = formatResources(r.cpuLimit, r.memLimit, r.pidsLimit)
    const reservations = formatResources(r.cpuReservation, r.memReservation, null)
    if (limits) rows.push({ label: 'Limits', value: limits })
    if (reservations) rows.push({ label: 'Reservations', value: reservations })
    if (r.cpuShares !== null) rows.push({ label: 'CPU shares', value: String(r.cpuShares) })
    if (r.shmSize) rows.push({ label: 'shm_size', value: r.shmSize })
    if (r.oomKillDisable !== null) rows.push({ label: 'OOM kill disable', value: String(r.oomKillDisable) })
    if (r.oomScoreAdj !== null) rows.push({ label: 'OOM score adj', value: String(r.oomScoreAdj) })
    if (r.ulimits.length > 0) {
      rows.push({
        label: 'ulimits',
        value: r.ulimits.map((u) => (
          <code key={u.name} className="container-modal-code compose-detail-item">
            {u.name}={u.soft === u.hard ? u.soft : `${u.soft ?? ''}:${u.hard ?? ''}`}
          </code>
        )),
      })
    }
  }

  if (rows.length === 0) return null

  return (
    <div className="compose-tab-details">
      {!all && <h4 className="compose-tab-details-title">More (read-only)</h4>}
      <dl className="container-modal-summary">
        {rows.map((r) => (
          <Fragment key={r.label}>
            <dt>{r.label}</dt>
            <dd>{r.value}</dd>
          </Fragment>
        ))}
      </dl>
    </div>
  )
}

function formatResources(cpus: string | null, memory: string | null, pids: string | null): string | null {
  const parts: string[] = []
  if (cpus) parts.push(`${cpus} CPU`)
  if (memory) parts.push(memory)
  if (pids) parts.push(`${pids} pids`)
  return parts.length > 0 ? parts.join(' · ') : null
}
