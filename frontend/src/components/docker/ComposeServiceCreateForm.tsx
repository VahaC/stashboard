import { useMemo, useState } from 'react'
import { AlertTriangle } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { useComposeUp, useCreateComposeService } from '@/lib/queries'
import type { ComposeProject } from '@/lib/types'
import { getApiErrorMessage } from '@/lib/utils'
import { ComposeServiceFields } from './ComposeServiceFields'
import {
  emptyServiceFormState,
  formStateToRequestFields,
  type ServiceFormState,
} from './compose-service-form'

/**
 * V7.4 — the "Add service" wizard. Set up a brand-new service with the same
 * field controls as the existing-service editor (shared {@link
 * ComposeServiceFields}), plus a service name. **Save and run** appends the
 * block to the project's Compose file and then runs `docker compose up -d`
 * against the whole project so the new container comes up alongside its
 * siblings; the modal then switches to the new service's tab where every field
 * is editable exactly like an existing service. **Save only** writes the file
 * without starting anything.
 */

const SERVICE_NAME = /^[a-zA-Z0-9._-]+$/

export interface ComposeServiceCreateFormProps {
  connectionId: string
  project: string
  projectData: ComposeProject
  capacityContainerName: string | null
  /** Switches the modal to the newly-created service's tab. */
  onCreated: (serviceName: string) => void
}

export function ComposeServiceCreateForm({
  connectionId, project, projectData, capacityContainerName, onCreated,
}: ComposeServiceCreateFormProps) {
  const create = useCreateComposeService(connectionId, project)
  const up = useComposeUp(connectionId, project)

  const [name, setName] = useState('')
  const [state, setState] = useState<ServiceFormState>(() => emptyServiceFormState())
  const [error, setError] = useState<string | null>(null)

  const busy = create.isPending || up.isPending

  const nameError = useMemo(() => {
    const trimmed = name.trim()
    if (trimmed.length === 0) return null // not an error yet, just disables save
    if (!SERVICE_NAME.test(trimmed)) return 'Use only letters, digits, dot, underscore or hyphen.'
    if (projectData.services.some((s) => s.name === trimmed)) return `A service named "${trimmed}" already exists.`
    return null
  }, [name, projectData.services])

  const imageMissing = state.image.trim().length === 0
  const canSave = name.trim().length > 0 && !nameError && !imageMissing && !busy

  const submit = async (run: boolean) => {
    setError(null)
    const trimmed = name.trim()
    try {
      await create.mutateAsync({ name: trimmed, ...formStateToRequestFields(state) })
    } catch (e: unknown) {
      setError(getApiErrorMessage(e) ?? 'Failed to add the service to the Compose file')
      return
    }

    if (!run) {
      onCreated(trimmed)
      return
    }

    try {
      const result = await up.mutateAsync()
      if (result.success) {
        onCreated(trimmed)
        return
      }
      setError(
        `Service "${trimmed}" was saved to the file, but "docker compose up -d" failed: `
        + (result.error ?? 'unknown error') + '. Open its tab to retry.',
      )
    } catch (e: unknown) {
      setError(
        `Service "${trimmed}" was saved, but starting the project failed: `
        + (getApiErrorMessage(e) ?? 'unknown error') + '. Open its tab to retry.',
      )
    }
  }

  return (
    <div className="compose-tab">
      <div className="compose-edit-body">
        <div className="service-modal-field">
          <Label className="service-modal-label">Service name</Label>
          <Input
            value={name}
            onChange={(e) => setName(e.target.value)}
            placeholder="my-service"
            className="font-mono text-[12px]"
            aria-label="Service name"
          />
          {nameError && (
            <p className="compose-edit-warning" role="alert">
              <AlertTriangle className="h-3 w-3 inline" /> {nameError}
            </p>
          )}
        </div>
      </div>

      <ComposeServiceFields
        connectionId={connectionId}
        project={project}
        projectData={projectData}
        value={state}
        onChange={setState}
        selfServiceName={null}
        idSuffix="new-service"
        capacityContainerName={capacityContainerName}
      />

      {imageMissing && (
        <p className="compose-edit-hint">
          <AlertTriangle className="h-3 w-3 inline" /> An image is required to add a service.
        </p>
      )}

      {error && (
        <pre className="compose-edit-error" role="alert">{error}</pre>
      )}

      <div className="compose-edit-actions">
        <Button type="button" size="sm" onClick={() => submit(true)} disabled={!canSave}>
          {up.isPending ? 'Starting…' : create.isPending ? 'Saving…' : 'Save and run'}
        </Button>
        <Button
          type="button"
          variant="outline"
          size="sm"
          onClick={() => submit(false)}
          disabled={!canSave}
          title="Append the service to the file without starting it"
        >
          Save only
        </Button>
      </div>
    </div>
  )
}
