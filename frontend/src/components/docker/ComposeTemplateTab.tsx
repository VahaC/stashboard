import { useMemo, useState } from 'react'
import { AlertTriangle, ArrowLeft, ExternalLink, RefreshCw, Search } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { useCreateComposeProject, useServiceTemplates } from '@/lib/queries'
import type { ServiceTemplate } from '@/lib/types'
import { getApiErrorMessage } from '@/lib/utils'
import {
  buildCreateRequest,
  defaultVariableValues,
  generateSecret,
  missingRequiredVariables,
  templateIconUrl,
} from './service-templates'

/**
 * V7.5 — the "From template" tab of the new-project wizard. Two views share the
 * tab: a searchable grid of starter recipes grouped by category, and — once one
 * is picked — a compact config panel for the per-deployment bits (project
 * name + directory, then the template's declared variables: volume host paths,
 * env values, exposed ports). Picking and filling these resolves the template's
 * `${KEY}` placeholders and posts the result to the same `create-project`
 * endpoint the from-scratch tab uses; the parent then opens the new project.
 */

const PROJECT_NAME = /^[a-z0-9][a-z0-9_-]*$/

export interface ComposeTemplateTabProps {
  connectionId: string
  onCreated: (projectName: string, started: boolean) => void
}

export function ComposeTemplateTab({ connectionId, onCreated }: ComposeTemplateTabProps) {
  const { data: templates, isLoading, isError } = useServiceTemplates(true)
  const [selected, setSelected] = useState<ServiceTemplate | null>(null)

  if (isLoading) return <p className="compose-edit-hint">Loading templates…</p>
  if (isError || !templates) {
    return (
      <p className="compose-edit-warning" role="alert">
        <AlertTriangle className="h-3 w-3 inline" /> Could not load the template catalogue.
      </p>
    )
  }
  if (templates.length === 0)
    return <p className="compose-edit-hint">No templates are available on this server.</p>

  if (selected) {
    return (
      <ComposeTemplateConfig
        connectionId={connectionId}
        template={selected}
        onBack={() => setSelected(null)}
        onCreated={onCreated}
      />
    )
  }

  return <ComposeTemplateGrid templates={templates} onPick={setSelected} />
}

// ── grid / picker ───────────────────────────────────────────────────────────

function ComposeTemplateGrid({
  templates, onPick,
}: { templates: ServiceTemplate[]; onPick: (t: ServiceTemplate) => void }) {
  const [query, setQuery] = useState('')

  const groups = useMemo(() => {
    const q = query.trim().toLowerCase()
    const filtered = q
      ? templates.filter((t) =>
        t.name.toLowerCase().includes(q)
        || t.description.toLowerCase().includes(q)
        || t.category.toLowerCase().includes(q))
      : templates
    const byCategory = new Map<string, ServiceTemplate[]>()
    for (const t of filtered) {
      const list = byCategory.get(t.category) ?? []
      list.push(t)
      byCategory.set(t.category, list)
    }
    return [...byCategory.entries()]
  }, [templates, query])

  return (
    <div className="docker-template-tab">
      <div className="docker-template-search">
        <Search className="h-3.5 w-3.5" />
        <input
          value={query}
          onChange={(e) => setQuery(e.target.value)}
          placeholder="Search templates…"
          aria-label="Search templates"
        />
      </div>

      {groups.length === 0 && (
        <p className="compose-edit-hint">No templates match “{query}”.</p>
      )}

      {groups.map(([category, items]) => (
        <div key={category} className="docker-template-group">
          <h4 className="docker-template-category">{category}</h4>
          <div className="docker-template-grid">
            {items.map((t) => (
              <button
                key={t.id}
                type="button"
                className="docker-template-card"
                onClick={() => onPick(t)}
              >
                <TemplateLogo template={t} />
                <span className="docker-template-card-body">
                  <span className="docker-template-card-name">{t.name}</span>
                  <span className="docker-template-card-desc">{t.description}</span>
                </span>
              </button>
            ))}
          </div>
        </div>
      ))}
    </div>
  )
}

/** The template's dashboard-icon logo, falling back to a coloured letter badge
 *  when the slug is missing or the CDN image fails to load. */
function TemplateLogo({ template }: { template: ServiceTemplate }) {
  const url = templateIconUrl(template.icon)
  const [failed, setFailed] = useState(false)
  if (url && !failed) {
    return (
      <img
        src={url}
        alt=""
        className="docker-template-icon"
        loading="lazy"
        onError={() => setFailed(true)}
      />
    )
  }
  return (
    <span className="docker-template-icon docker-template-icon-fallback" aria-hidden="true">
      {template.name.charAt(0).toUpperCase()}
    </span>
  )
}

// ── selected-template config panel ──────────────────────────────────────────

function ComposeTemplateConfig({
  connectionId, template, onBack, onCreated,
}: {
  connectionId: string
  template: ServiceTemplate
  onBack: () => void
  onCreated: (projectName: string, started: boolean) => void
}) {
  const create = useCreateComposeProject(connectionId)

  const [projectName, setProjectName] = useState(template.projectName)
  const [directory, setDirectory] = useState('')
  const [fileName, setFileName] = useState('docker-compose.yml')
  const [createDir, setCreateDir] = useState(true)
  const [values, setValues] = useState<Record<string, string>>(() => defaultVariableValues(template))
  const [error, setError] = useState<string | null>(null)

  const busy = create.isPending

  const projectNameError = useMemo(() => {
    const t = projectName.trim()
    if (t.length === 0) return null
    if (!PROJECT_NAME.test(t))
      return 'Lowercase letters, digits, dot, underscore or hyphen; must start with a letter or digit.'
    return null
  }, [projectName])

  const missing = missingRequiredVariables(template, values)

  const canSave =
    projectName.trim().length > 0 && !projectNameError
    && directory.trim().length > 0
    && missing.length === 0 && !busy

  const submit = async (run: boolean) => {
    setError(null)
    try {
      const result = await create.mutateAsync(
        buildCreateRequest(template, values, {
          projectName, directory, fileName, createDirectory: createDir, run,
        }),
      )
      if (run && !result.started) {
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
    <div className="compose-tab">
      <div className="docker-template-config-head">
        <Button type="button" variant="ghost" size="sm" onClick={onBack} className="docker-template-back">
          <ArrowLeft className="h-3.5 w-3.5" /> Templates
        </Button>
        <TemplateLogo template={template} />
        <div className="docker-template-config-title">
          <span className="docker-template-card-name">{template.name}</span>
          <span className="docker-template-card-desc">{template.description}</span>
        </div>
        {template.documentation && (
          <a
            className="docker-template-doc-link"
            href={template.documentation}
            target="_blank"
            rel="noreferrer"
          >
            Docs <ExternalLink className="h-3 w-3 inline" />
          </a>
        )}
      </div>

      <div className="compose-edit-body">
        <div className="compose-edit-grid">
          <div className="service-modal-field">
            <Label className="service-modal-label">Project name</Label>
            <Input
              value={projectName}
              onChange={(e) => setProjectName(e.target.value)}
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
              placeholder="/opt/stacks/myapp"
              className="font-mono text-[12px]"
              aria-label="Directory"
            />
            <p className="compose-edit-hint">
              Path as this connection sees it — inside the Stashboard container for a local
              socket, on the remote host for SSH. Relative volume paths below are created here.
            </p>
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
        </div>

        {template.variables.length > 0 && (
          <div className="docker-template-vars">
            <h4 className="compose-tab-details-title">Deployment settings</h4>
            <div className="compose-edit-grid">
              {template.variables.map((v) => (
                <div className="service-modal-field" key={v.key}>
                  <Label className="service-modal-label">
                    {v.label}
                    {v.required && <span className="docker-template-required"> *</span>}
                  </Label>
                  <div className="docker-template-var-input">
                    <Input
                      value={values[v.key] ?? ''}
                      type={v.type === 'password' ? 'password' : 'text'}
                      onChange={(e) => setValues((s) => ({ ...s, [v.key]: e.target.value }))}
                      className="font-mono text-[12px]"
                      aria-label={v.label}
                    />
                    {v.generate && (
                      <Button
                        type="button"
                        variant="outline"
                        size="sm"
                        onClick={() => setValues((s) => ({ ...s, [v.key]: generateSecret() }))}
                        title="Generate a random secret"
                      >
                        <RefreshCw className="h-3.5 w-3.5" />
                      </Button>
                    )}
                  </div>
                  {v.hint && <p className="compose-edit-hint">{v.hint}</p>}
                </div>
              ))}
            </div>
          </div>
        )}

        <div className="docker-template-services">
          <span className="compose-edit-hint">Includes:</span>
          {template.services.map((s) => (
            <span key={s.name} className="docker-template-service-chip" title={s.image ?? ''}>
              {s.name}
            </span>
          ))}
        </div>

        {template.notes && (
          <p className="docker-template-notes">{template.notes}</p>
        )}

        {error && <pre className="compose-edit-error" role="alert">{error}</pre>}

        <div className="compose-edit-actions">
          <Button type="button" size="sm" onClick={() => submit(true)} disabled={!canSave}>
            {busy ? 'Creating…' : 'Create and run'}
          </Button>
          <Button
            type="button" variant="outline" size="sm"
            onClick={() => submit(false)}
            disabled={!canSave}
            title="Write the file without starting anything"
          >
            Create only
          </Button>
        </div>
      </div>
    </div>
  )
}
