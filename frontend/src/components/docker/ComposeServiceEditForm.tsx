import { useState } from 'react'
import { Button } from '@/components/ui/button'
import { useEditComposeService } from '@/lib/queries'
import type { ComposeProject, ComposeService } from '@/lib/types'
import { getApiErrorMessage } from '@/lib/utils'
import { ComposeServiceFields } from './ComposeServiceFields'
import {
  formStateToRequestFields,
  serviceToFormState,
  type ServiceFormState,
} from './compose-service-form'

/**
 * V7.1.1 — the editable basic-fields form for one Compose service, living
 * inside a tab of the {@link ComposeProjectModal}. Covers the 80 % of Compose
 * edits reached for daily — image / ports / volumes / env / labels / restart /
 * command / entrypoint / user / working_dir / resources. Save sends the whole
 * desired state in one atomic write — the backend diffs per key, validates with
 * `docker compose config -q` and renames over the original, so an untouched
 * field is a guaranteed zero-diff in the YAML.
 *
 * V7.4 — the field inputs now live in the shared {@link ComposeServiceFields}
 * so the "Add service" wizard renders the identical controls.
 */

export interface ComposeServiceEditFormProps {
  connectionId: string
  /** Discovered project name (the route segment, not the file's `name:`). */
  project: string
  projectData: ComposeProject
  service: ComposeService
  /** V7.2 — name of a running container on this connection, for the resources
   *  panel's host-capacity stats sample; `null` when none is deployed. */
  capacityContainerName: string | null
  /** Called after a successful save (the parent modal stays open — the query
   *  cache already carries the freshly-parsed project). */
  onSaved?: () => void
}

export function ComposeServiceEditForm({
  connectionId, project, projectData, service, capacityContainerName, onSaved,
}: ComposeServiceEditFormProps) {
  const edit = useEditComposeService(connectionId, project)

  const [state, setState] = useState<ServiceFormState>(() => serviceToFormState(service))
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)

  const flash = (msg: string) => {
    setNotice(msg)
    setTimeout(() => setNotice(null), 2500)
  }

  const save = async () => {
    setError(null)
    try {
      await edit.mutateAsync({
        serviceName: service.name,
        data: formStateToRequestFields(state),
      })
      flash('Saved.')
      onSaved?.()
    } catch (e: unknown) {
      setError(getApiErrorMessage(e) ?? 'Failed to save the Compose file')
    }
  }

  // Discard unsaved edits — restore every field to the last-saved state.
  const revert = () => {
    setState(serviceToFormState(service))
    setError(null)
  }

  return (
    <div className="compose-edit-body">
      <ComposeServiceFields
        connectionId={connectionId}
        project={project}
        projectData={projectData}
        value={state}
        onChange={setState}
        selfServiceName={service.name}
        idSuffix={service.name}
        capacityContainerName={capacityContainerName}
      />

      {error && (
        <pre className="compose-edit-error" role="alert">{error}</pre>
      )}

      <div className="compose-edit-actions">
        {notice && <span className="compose-raw-toast" role="status">{notice}</span>}
        <Button type="button" size="sm" onClick={save} disabled={edit.isPending}>
          {edit.isPending ? 'Validating & saving…' : 'Save changes'}
        </Button>
        <Button
          type="button"
          variant="ghost"
          size="sm"
          onClick={revert}
          disabled={edit.isPending}
          title="Discard unsaved changes"
        >
          Cancel
        </Button>
      </div>
    </div>
  )
}
