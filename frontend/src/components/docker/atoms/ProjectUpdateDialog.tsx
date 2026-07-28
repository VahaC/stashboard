import type {
  DockerContainerCard,
  DockerProjectUpdateResponse,
  DockerWatch,
} from '@/lib/types'
import {
  UpdateProgressDialog,
  type UpdateOutcome,
  type UpdateTarget,
} from './UpdateProgressDialog'

/** Where the bulk-update behaviour is documented. */
const PROJECT_DOCS_URL =
  'https://github.com/VahaC/stashboard/blob/main/DOCKER_UPDATE_MONITORING_GUIDE.md#51b-compose-project-bulk-update-v54'

export interface ProjectUpdateDialogProps {
  open: boolean
  project: string
  containers: DockerContainerCard[]
  watchByContainer: Map<string, DockerWatch>
  onConfirm: () => Promise<DockerProjectUpdateResponse>
  onClose: () => void
}

/**
 * V5.4 — bulk "Update project" dialog. Thin adapter over
 * <see cref="UpdateProgressDialog"/>: maps the project's containers onto
 * the generic target shape, and the response's per-service rows onto
 * per-target outcomes so the user sees a checkbox-style checklist tick
 * off as the backend reports back.
 */
export function ProjectUpdateDialog({
  open, project, containers, watchByContainer, onConfirm, onClose,
}: ProjectUpdateDialogProps) {
  const targets: UpdateTarget[] = containers.map((c) => ({
    key: c.name,
    displayName: c.composeService ?? c.name,
    secondary: c.composeService ? c.name : null,
    image: c.image,
    tracked: watchByContainer.has(c.name),
  }))

  const handleConfirm = async (): Promise<UpdateOutcome[]> => {
    const response = await onConfirm()
    // V9.2 — if the project includes the Stashboard container itself, the whole
    // recreate is offloaded to a detached helper and reported as Scheduled with
    // no per-service rows. Render every target as "Scheduled".
    if (response.parent.status === 'Scheduled' || response.parent.status === 5) {
      return targets.map((t) => ({
        key: t.key,
        success: true,
        scheduled: true,
        error: response.parent.error,
      }))
    }
    return response.services.map((s) => ({
      key: s.containerName,
      success: s.status === 'Success' || s.status === 0,
      error: s.error,
    }))
  }

  return (
    <UpdateProgressDialog
      open={open}
      scope="project"
      subject={project}
      targets={targets}
      description={
        <>
          <p className="remove-confirm-warning">
            Stashboard will pull every image in <code className="container-modal-code">{project}</code> and recreate the services in
            dependency order. With a local socket + Compose project path configured (V5.2) this runs
            one <code>docker&nbsp;compose&nbsp;pull</code> + <code>up&nbsp;-d</code> against the project root;
            otherwise it falls back to per-service raw recreate ordered by the
            <code>&nbsp;com.docker.compose.depends_on</code> labels.
          </p>
          <p className="remove-confirm-warning warning-strong">
            <strong>Understand that this isn't risk-free.</strong> Every service in the project will be briefly
            unavailable while it's recreated. Untracked containers (no Stashboard watch) can only be
            pulled anonymously — private images need a watch to supply credentials.
          </p>
        </>
      }
      docs={{ href: PROJECT_DOCS_URL, label: 'Read how the bulk update dispatches (compose-aware vs. raw fallback)' }}
      onConfirm={handleConfirm}
      onClose={onClose}
    />
  )
}
