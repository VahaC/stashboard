import { useMemo, useState } from 'react'
import { AlertTriangle, Eye, EyeOff, Plus, Tags, Trash2 } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { useComposeImageTags } from '@/lib/queries'
import type { ComposeProject } from '@/lib/types'
import { ComposeResourcesForm } from './ComposeResourcesForm'
import { type PortRow, type ServiceFormState, renderPortRow } from './compose-service-form'

/**
 * V7.4 — the shared editable basic-fields body for one Compose service,
 * extracted from {@link ComposeServiceEditForm} so the same inputs (and the
 * same tag dropdown / port-collision / volume / secret-mask behaviour) back
 * both editing an existing service and the V7.4 "Add service" wizard. Fully
 * controlled: the parent owns the {@link ServiceFormState} (see
 * ./compose-service-form) and the Save / revert lifecycle; this component only
 * renders fields and reports changes.
 */

/** Host-side port of a short-syntax mapping, for the collision check. Handles
 *  `8080:80`, `127.0.0.1:8080:80` and `/proto` suffixes; `null` when the
 *  string exposes no host port. */
function extractHostPort(port: string): string | null {
  const slash = port.indexOf('/')
  const main = slash < 0 ? port : port.slice(0, slash)
  const parts = main.split(':')
  if (parts.length === 2) return parts[0] || null
  if (parts.length === 3) return parts[1] || null
  return null
}

/** Keys that look like credentials get the password-style mask by default. */
const isSecretEnvName = (name: string): boolean =>
  /(_KEY|_TOKEN|_PASSWORD|_SECRET)$/i.test(name)

/** Splits `repo[:tag]` (digest pins included) so the tag dropdown can swap
 *  just the tag part. */
function splitImageTag(image: string): { repo: string; tag: string | null } {
  const at = image.indexOf('@')
  const base = at >= 0 ? image.slice(0, at) : image
  const lastColon = base.lastIndexOf(':')
  if (lastColon > base.lastIndexOf('/')) return { repo: base.slice(0, lastColon), tag: base.slice(lastColon + 1) }
  return { repo: base, tag: null }
}

const RESTART_POLICIES = ['no', 'always', 'on-failure', 'unless-stopped']

// ── the fields ───────────────────────────────────────────────────────────

export interface ComposeServiceFieldsProps {
  connectionId: string
  project: string
  projectData: ComposeProject
  value: ServiceFormState
  onChange: (next: ServiceFormState) => void
  /** Name of the service being edited, excluded from the port-collision scan;
   *  `null` for a new service (collides with every other service's host port). */
  selfServiceName: string | null
  /** Stable id suffix for the named-volumes datalist (must be unique per form). */
  idSuffix: string
  /** V7.2 — name of a running container on this connection, for the resources
   *  panel's host-capacity stats sample; `null` when none is deployed. */
  capacityContainerName: string | null
}

export function ComposeServiceFields({
  connectionId, project, projectData, value, onChange, selfServiceName, idSuffix, capacityContainerName,
}: ComposeServiceFieldsProps) {
  const patch = (p: Partial<ServiceFormState>) => onChange({ ...value, ...p })
  const { image, restart, command, entrypoint, user, workingDir, ports, volumes, env, labels, resources } = value

  const [revealed, setRevealed] = useState<Record<number, boolean>>({})

  // Tag dropdown — lazy: the registry isn't hit until the user asks for tags.
  const [tagsWanted, setTagsWanted] = useState(false)
  const tags = useComposeImageTags(connectionId, image.trim() || null, tagsWanted)

  // Host ports used by the OTHER services of this project (host → service).
  const foreignHostPorts = useMemo(() => {
    const map = new Map<string, string>()
    for (const svc of projectData.services) {
      if (svc.name === selfServiceName) continue
      for (const p of svc.ports) {
        const host = extractHostPort(p)
        if (host && !map.has(host)) map.set(host, svc.name)
      }
    }
    return map
  }, [projectData.services, selfServiceName])

  const portCollision = (row: PortRow, index: number): string | null => {
    const host = extractHostPort(renderPortRow(row))
    if (!host) return null
    const other = foreignHostPorts.get(host)
    if (other) return `Host port ${host} is already published by service "${other}".`
    const dup = ports.some((r, i) => i !== index && extractHostPort(renderPortRow(r)) === host)
    return dup ? `Host port ${host} is duplicated in this service.` : null
  }

  /** Absolute bind-mount sources outside the project directory get a warning
   *  (relative `./` binds and named volumes are the safe shapes). */
  const volumeWarning = (v: string): string | null => {
    const source = v.split(':')[0]?.trim() ?? ''
    if (!source.startsWith('/')) return null
    const root = projectData.projectPath.replace(/\/+$/, '')
    if (source === root || source.startsWith(root + '/')) return null
    return `Host path outside the project directory (${projectData.projectPath}).`
  }

  const { repo } = splitImageTag(image.trim())
  const namedVolumesListId = `compose-named-volumes-${idSuffix}`

  return (
    <div className="compose-edit-body">
      {/* ── Image ─────────────────────────────────────────────────── */}
      <div className="service-modal-field">
        <Label className="service-modal-label">Image</Label>
        <div className="compose-edit-inline">
          <Input
            value={image}
            onChange={(e) => patch({ image: e.target.value })}
            placeholder="nginx:1.27"
            className="font-mono text-[12px]"
          />
          <Button
            type="button"
            variant="outline"
            size="sm"
            onClick={() => setTagsWanted(true)}
            disabled={!image.trim() || tags.isFetching}
            title="List the registry's published tags for this image"
          >
            <Tags className="h-3.5 w-3.5" />
            <span className="label-text">{tags.isFetching ? 'Loading…' : 'Tags'}</span>
          </Button>
        </div>
        {tagsWanted && tags.data && tags.data.tags.length > 0 && (
          <select
            className="service-modal-select mt-1"
            value=""
            onChange={(e) => { if (e.target.value) patch({ image: `${repo}:${e.target.value}` }) }}
          >
            <option value="">Pick a tag for {tags.data.repository}…</option>
            {tags.data.tags.map((t) => <option key={t} value={t}>{t}</option>)}
          </select>
        )}
        {tagsWanted && tags.data?.error && (
          <p className="compose-edit-hint">
            <AlertTriangle className="h-3 w-3 inline" /> {tags.data.error} — type the tag manually.
          </p>
        )}
      </div>

      {/* ── Ports ─────────────────────────────────────────────────── */}
      <div className="service-modal-field">
        <Label className="service-modal-label">Ports (host → container)</Label>
        {ports.map((row, i) => (
          <div key={i} className="compose-edit-row">
            {row.raw !== null ? (
              <Input
                value={row.raw}
                onChange={(e) => patch({ ports: ports.map((r, j) => j === i ? { ...r, raw: e.target.value } : r) })}
                className="font-mono text-[12px]"
                aria-label={`Port mapping ${i + 1}`}
              />
            ) : (
              <>
                <Input
                  value={row.host}
                  onChange={(e) => patch({ ports: ports.map((r, j) => j === i ? { ...r, host: e.target.value } : r) })}
                  placeholder="8080"
                  inputMode="numeric"
                  className="font-mono text-[12px] compose-edit-port"
                  aria-label={`Host port ${i + 1}`}
                />
                <Input
                  value={row.container}
                  onChange={(e) => patch({ ports: ports.map((r, j) => j === i ? { ...r, container: e.target.value } : r) })}
                  placeholder="80"
                  inputMode="numeric"
                  className="font-mono text-[12px] compose-edit-port"
                  aria-label={`Container port ${i + 1}`}
                />
                <select
                  className="service-modal-select compose-edit-proto"
                  value={row.proto}
                  onChange={(e) => patch({ ports: ports.map((r, j) => j === i ? { ...r, proto: e.target.value } : r) })}
                  aria-label={`Protocol ${i + 1}`}
                >
                  <option value="">tcp (default)</option>
                  <option value="tcp">tcp</option>
                  <option value="udp">udp</option>
                </select>
              </>
            )}
            <Button
              type="button" variant="ghost" size="sm"
              onClick={() => patch({ ports: ports.filter((_, j) => j !== i) })}
              title="Remove this port mapping"
            >
              <Trash2 className="h-3.5 w-3.5" />
            </Button>
            {portCollision(row, i) && (
              <p className="compose-edit-warning" role="alert">
                <AlertTriangle className="h-3 w-3 inline" /> {portCollision(row, i)}
              </p>
            )}
          </div>
        ))}
        <Button
          type="button" variant="outline" size="sm"
          onClick={() => patch({ ports: [...ports, { raw: null, host: '', container: '', proto: '' }] })}
        >
          <Plus className="h-3.5 w-3.5" />
          <span className="label-text">Add port</span>
        </Button>
      </div>

      {/* ── Volumes ───────────────────────────────────────────────── */}
      <div className="service-modal-field">
        <Label className="service-modal-label">Volumes</Label>
        <datalist id={namedVolumesListId}>
          {projectData.volumes.map((v) => <option key={v.name} value={`${v.name}:`} />)}
        </datalist>
        {volumes.map((v, i) => (
          <div key={i} className="compose-edit-row">
            <Input
              value={v}
              onChange={(e) => patch({ volumes: volumes.map((x, j) => j === i ? e.target.value : x) })}
              placeholder="./config:/config or data:/var/lib/app"
              list={namedVolumesListId}
              className="font-mono text-[12px]"
              aria-label={`Volume ${i + 1}`}
            />
            <Button
              type="button" variant="ghost" size="sm"
              onClick={() => patch({ volumes: volumes.filter((_, j) => j !== i) })}
              title="Remove this volume"
            >
              <Trash2 className="h-3.5 w-3.5" />
            </Button>
            {volumeWarning(v) && (
              <p className="compose-edit-warning" role="alert">
                <AlertTriangle className="h-3 w-3 inline" /> {volumeWarning(v)}
              </p>
            )}
          </div>
        ))}
        <Button type="button" variant="outline" size="sm" onClick={() => patch({ volumes: [...volumes, ''] })}>
          <Plus className="h-3.5 w-3.5" />
          <span className="label-text">Add volume</span>
        </Button>
      </div>

      {/* ── Environment ───────────────────────────────────────────── */}
      <div className="service-modal-field">
        <Label className="service-modal-label">Environment</Label>
        {env.map((row, i) => {
          const masked = isSecretEnvName(row.name) && !revealed[i]
          return (
            <div key={i} className="compose-edit-row">
              <Input
                value={row.name}
                onChange={(e) => patch({ env: env.map((r, j) => j === i ? { ...r, name: e.target.value } : r) })}
                placeholder="TZ"
                className="font-mono text-[12px] compose-edit-pair-name"
                aria-label={`Variable name ${i + 1}`}
              />
              <Input
                type={masked ? 'password' : 'text'}
                value={row.value ?? ''}
                onChange={(e) => patch({ env: env.map((r, j) => j === i
                  ? { ...r, value: e.target.value === '' && r.value === null ? null : e.target.value }
                  : r) })}
                placeholder={row.value === null ? '(inherited from host env)' : 'value'}
                className="font-mono text-[12px]"
                aria-label={`Variable value ${i + 1}`}
              />
              {isSecretEnvName(row.name) && (
                <Button
                  type="button" variant="ghost" size="sm"
                  onClick={() => setRevealed({ ...revealed, [i]: !revealed[i] })}
                  title={masked ? 'Reveal value' : 'Mask value'}
                >
                  {masked ? <Eye className="h-3.5 w-3.5" /> : <EyeOff className="h-3.5 w-3.5" />}
                </Button>
              )}
              <Button
                type="button" variant="ghost" size="sm"
                onClick={() => patch({ env: env.filter((_, j) => j !== i) })}
                title="Remove this variable"
              >
                <Trash2 className="h-3.5 w-3.5" />
              </Button>
            </div>
          )
        })}
        <Button type="button" variant="outline" size="sm" onClick={() => patch({ env: [...env, { name: '', value: '' }] })}>
          <Plus className="h-3.5 w-3.5" />
          <span className="label-text">Add variable</span>
        </Button>
      </div>

      {/* ── Labels ────────────────────────────────────────────────── */}
      <div className="service-modal-field">
        <Label className="service-modal-label">Labels</Label>
        {labels.map((row, i) => (
          <div key={i} className="compose-edit-row">
            <Input
              value={row.name}
              onChange={(e) => patch({ labels: labels.map((r, j) => j === i ? { ...r, name: e.target.value } : r) })}
              placeholder="com.example.team"
              className="font-mono text-[12px] compose-edit-pair-name"
              aria-label={`Label name ${i + 1}`}
            />
            <Input
              value={row.value ?? ''}
              onChange={(e) => patch({ labels: labels.map((r, j) => j === i ? { ...r, value: e.target.value } : r) })}
              placeholder="value"
              className="font-mono text-[12px]"
              aria-label={`Label value ${i + 1}`}
            />
            <Button
              type="button" variant="ghost" size="sm"
              onClick={() => patch({ labels: labels.filter((_, j) => j !== i) })}
              title="Remove this label"
            >
              <Trash2 className="h-3.5 w-3.5" />
            </Button>
          </div>
        ))}
        <Button type="button" variant="outline" size="sm" onClick={() => patch({ labels: [...labels, { name: '', value: '' }] })}>
          <Plus className="h-3.5 w-3.5" />
          <span className="label-text">Add label</span>
        </Button>
      </div>

      {/* ── Restart / command / entrypoint / user / working_dir ───── */}
      <div className="compose-edit-grid">
        <div className="service-modal-field">
          <Label className="service-modal-label">Restart policy</Label>
          <select
            className="service-modal-select"
            value={restart}
            onChange={(e) => patch({ restart: e.target.value })}
          >
            <option value="">(none)</option>
            {RESTART_POLICIES.map((p) => <option key={p} value={p}>{p}</option>)}
            {restart && !RESTART_POLICIES.includes(restart) && (
              <option value={restart}>{restart}</option>
            )}
          </select>
        </div>
        <div className="service-modal-field">
          <Label className="service-modal-label">User</Label>
          <Input
            value={user}
            onChange={(e) => patch({ user: e.target.value })}
            placeholder="1000:1000"
            className="font-mono text-[12px]"
          />
        </div>
        <div className="service-modal-field docker-section-field-full">
          <Label className="service-modal-label">Command</Label>
          <Input
            value={command}
            onChange={(e) => patch({ command: e.target.value })}
            placeholder={'serve --port 8080 or ["serve", "--port", "8080"]'}
            className="font-mono text-[12px]"
          />
        </div>
        <div className="service-modal-field docker-section-field-full">
          <Label className="service-modal-label">Entrypoint</Label>
          <Input
            value={entrypoint}
            onChange={(e) => patch({ entrypoint: e.target.value })}
            placeholder="/entrypoint.sh"
            className="font-mono text-[12px]"
          />
        </div>
        <div className="service-modal-field">
          <Label className="service-modal-label">Working dir</Label>
          <Input
            value={workingDir}
            onChange={(e) => patch({ workingDir: e.target.value })}
            placeholder="/app"
            className="font-mono text-[12px]"
          />
        </div>
      </div>

      {/* ── Resource constraints (V7.2) ───────────────────────────── */}
      <ComposeResourcesForm
        connectionId={connectionId}
        project={project}
        value={resources}
        onChange={(next) => patch({ resources: next })}
        capacityContainerName={capacityContainerName}
      />
    </div>
  )
}
