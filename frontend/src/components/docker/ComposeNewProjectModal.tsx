import { useMemo, useState } from 'react'
import { AlertTriangle, FolderPlus } from 'lucide-react'
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { useCreateComposeProject } from '@/lib/queries'
import type { ComposeProject } from '@/lib/types'
import { cn, getApiErrorMessage } from '@/lib/utils'
import { ComposeServiceFields } from './ComposeServiceFields'
import { ComposeTemplateTab } from './ComposeTemplateTab'
import {
  emptyServiceFormState,
  formStateToRequestFields,
  type ServiceFormState,
} from './compose-service-form'
import { joinProjectDir } from './service-templates'
import '@/styles/docker-instances.css'

/**
 * V7.4.1 — the "New project" dialog: bootstrap a brand-new Compose project from
 * scratch. The user names the project, picks a directory (free-text path as the
 * connection's transport sees it), and defines a first service with the same
 * shared {@link ComposeServiceFields} controls as the editor. **Create and run**
 * writes the file (creating the directory when asked), then runs
 * `docker compose up -d`; the parent then opens the new project's modal. The
 * project becomes a normal discovered project on the Docker page.
 */

const PROJECT_NAME = /^[a-z0-9][a-z0-9_-]*$/
const SERVICE_NAME = /^[a-zA-Z0-9._-]+$/

export interface ComposeNewProjectModalProps {
  connectionId: string
  connectionName: string | null
  onClose: () => void
  /** Called after the file is written; `started` is true when `up -d` ran. */
  onCreated: (projectName: string, started: boolean) => void
}

export function ComposeNewProjectModal({
  connectionId, connectionName, onClose, onCreated,
}: ComposeNewProjectModalProps) {
  const create = useCreateComposeProject(connectionId)

  const [tab, setTab] = useState<'scratch' | 'template'>('scratch')
  const [projectName, setProjectName] = useState('')
  const [directory, setDirectory] = useState('')
  const [fileName, setFileName] = useState('docker-compose.yml')
  const [createDir, setCreateDir] = useState(true)
  const [serviceName, setServiceName] = useState('')
  const [state, setState] = useState<ServiceFormState>(() => emptyServiceFormState())
  const [error, setError] = useState<string | null>(null)

  const busy = create.isPending

  const projectNameError = useMemo(() => {
    const t = projectName.trim()
    if (t.length === 0) return null
    if (!PROJECT_NAME.test(t)) return 'Lowercase letters, digits, dot, underscore or hyphen; must start with a letter or digit.'
    return null
  }, [projectName])

  const serviceNameError = useMemo(() => {
    const t = serviceName.trim()
    if (t.length === 0) return null
    if (!SERVICE_NAME.test(t)) return 'Use only letters, digits, dot, underscore or hyphen.'
    return null
  }, [serviceName])

  const imageMissing = state.image.trim().length === 0

  const canSave =
    projectName.trim().length > 0 && !projectNameError
    && directory.trim().length > 0
    && serviceName.trim().length > 0 && !serviceNameError
    && !imageMissing && !busy

  // A synthetic project so the shared fields' volume datalist / path warnings
  // have a project directory to reason about (there are no services yet).
  const syntheticProject: ComposeProject = useMemo(() => ({
    projectName: projectName.trim() || null,
    fileName: fileName.trim() || 'docker-compose.yml',
    projectPath: joinProjectDir(directory, projectName),
    services: [],
    networks: [],
    volumes: [],
    secrets: [],
    configs: [],
    unsupportedFeatures: [],
    lint: [],
  }), [projectName, fileName, directory])

  const submit = async () => {
    setError(null)
    try {
      // Always `up -d`: Stashboard only surfaces a project once its containers
      // carry the compose labels. Writing the file without running it would leave
      // a stack that exists on disk but is invisible (and unmanageable) here.
      const result = await create.mutateAsync({
        projectName: projectName.trim(),
        directory: joinProjectDir(directory, projectName),
        fileName: fileName.trim() || null,
        createDirectory: createDir,
        run: true,
        services: [{ name: serviceName.trim(), ...formStateToRequestFields(state) }],
      })
      if (!result.started) {
        setError(
          `The Compose file was created at ${result.directory}, but `
          + `"docker compose up -d" failed: ${result.startError ?? 'unknown error'}.`,
        )
        return
      }
      onCreated(result.projectName, result.started)
    } catch (e: unknown) {
      setError(getApiErrorMessage(e) ?? 'Failed to create the project')
    }
  }

  return (
    <Dialog open onOpenChange={(open) => { if (!open) onClose() }}>
      <DialogContent className="container-modal-content compose-project-modal">
        <DialogHeader>
          <DialogTitle className="container-modal-title">
            <FolderPlus className="h-4 w-4" />
            <span>New Compose project</span>
            {connectionName ? <span className="compose-modal-host">@ {connectionName}</span> : null}
          </DialogTitle>
          <DialogDescription className="container-modal-image">
            Writes a new compose file and (optionally) brings it up.
          </DialogDescription>
        </DialogHeader>

        <nav className="container-modal-tabs" role="tablist" aria-label="New project source">
          <button
            type="button"
            role="tab"
            aria-selected={tab === 'scratch'}
            className={cn('container-modal-tab', tab === 'scratch' && 'container-modal-tab-active')}
            onClick={() => setTab('scratch')}
          >
            From scratch
          </button>
          <button
            type="button"
            role="tab"
            aria-selected={tab === 'template'}
            className={cn('container-modal-tab', tab === 'template' && 'container-modal-tab-active')}
            onClick={() => setTab('template')}
          >
            From template
          </button>
        </nav>

        <div className="container-modal-body" role="tabpanel">
          {tab === 'template' ? (
            <ComposeTemplateTab connectionId={connectionId} onCreated={onCreated} onClose={onClose} />
          ) : (
          <div className="compose-tab">
            <div className="compose-edit-body">
              <div className="compose-edit-grid">
                <div className="service-modal-field">
                  <Label className="service-modal-label">Project name</Label>
                  <Input
                    value={projectName}
                    onChange={(e) => setProjectName(e.target.value)}
                    placeholder="myapp"
                    className="font-mono text-[12px]"
                    aria-label="Project name"
                  />
                  {projectNameError && (
                    <p className="compose-edit-warning" role="alert">
                      <AlertTriangle className="h-3 w-3 inline" /> {projectNameError}
                    </p>
                  )}
                </div>
                <div className="service-modal-field">
                  <Label className="service-modal-label">File name</Label>
                  <Input
                    value={fileName}
                    onChange={(e) => setFileName(e.target.value)}
                    placeholder="docker-compose.yml"
                    className="font-mono text-[12px]"
                    aria-label="File name"
                  />
                </div>
                <div className="service-modal-field docker-section-field-full">
                  <Label className="service-modal-label">Directory</Label>
                  <Input
                    value={directory}
                    onChange={(e) => setDirectory(e.target.value)}
                    placeholder="/opt/stacks"
                    className="font-mono text-[12px]"
                    aria-label="Directory"
                  />
                  <p className="compose-edit-hint">
                    Parent directory as this connection sees it — inside the Stashboard container
                    for a local socket, on the remote host for SSH. A subfolder named after the
                    project is created here, holding the Compose file and its relative volumes.
                  </p>
                  {directory.trim().length > 0 && projectName.trim().length > 0 && !projectNameError && (
                    <p className="compose-edit-hint">
                      Will create:{' '}
                      <code>{`${joinProjectDir(directory, projectName)}/${fileName.trim() || 'docker-compose.yml'}`}</code>
                    </p>
                  )}
                </div>
                <div className="service-modal-field docker-section-field-full">
                  <label className="compose-new-checkbox">
                    <input
                      type="checkbox"
                      checked={createDir}
                      onChange={(e) => setCreateDir(e.target.checked)}
                    />
                    <span>Create the directory if it doesn’t exist</span>
                  </label>
                </div>
                <div className="service-modal-field">
                  <Label className="service-modal-label">First service name</Label>
                  <Input
                    value={serviceName}
                    onChange={(e) => setServiceName(e.target.value)}
                    placeholder="web"
                    className="font-mono text-[12px]"
                    aria-label="Service name"
                  />
                  {serviceNameError && (
                    <p className="compose-edit-warning" role="alert">
                      <AlertTriangle className="h-3 w-3 inline" /> {serviceNameError}
                    </p>
                  )}
                </div>
              </div>
            </div>

            <ComposeServiceFields
              connectionId={connectionId}
              project="new-project"
              projectData={syntheticProject}
              value={state}
              onChange={setState}
              selfServiceName={null}
              idSuffix="new-project"
              capacityContainerName={null}
            />

            {imageMissing && (
              <p className="compose-edit-hint">
                <AlertTriangle className="h-3 w-3 inline" /> An image is required for the first service.
              </p>
            )}

            {error && (
              <pre className="compose-edit-error" role="alert">{error}</pre>
            )}

            <div className="compose-edit-actions">
              <Button type="button" variant="outline" size="sm" className="mr-auto" onClick={onClose} disabled={busy}>
                Cancel
              </Button>
              <Button type="button" size="sm" onClick={() => submit()} disabled={!canSave}>
                {busy ? 'Creating…' : 'Create and run'}
              </Button>
            </div>
          </div>
          )}
        </div>
      </DialogContent>
    </Dialog>
  )
}
