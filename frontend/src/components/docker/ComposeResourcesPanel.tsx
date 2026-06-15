import { useState } from 'react'
import {
  AlertTriangle, Database, FileText, HardDrive, KeyRound, Network, Pencil, Plus, Trash2, X,
} from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { cn } from '@/lib/utils'
import {
  useComposeHostNetworks,
  useComposeVolumeUsage,
  useDeleteComposeResource,
  useEditComposeResource,
} from '@/lib/queries'
import type {
  ComposeEnvVar,
  ComposeFileResource,
  ComposeNetwork,
  ComposeProject,
  ComposeResourceEditRequest,
  ComposeResourceKind,
  ComposeVolume,
} from '@/lib/types'
import { getApiErrorMessage } from '@/lib/utils'

/**
 * V7.3 — CRUD panel for one of a project's top-level resource sections
 * (networks / volumes / secrets / configs). Backed by the same
 * comment-preserving YAML writer as the V7.1 service editor: each save rewrites
 * just the edited entry's lines, validates with `docker compose config -q` and
 * renames over the original atomically. Networks warn on subnet overlap with the
 * host; volumes surface their on-disk size from `docker system df`.
 */

export interface ComposeResourcesPanelProps {
  connectionId: string
  project: string
  projectData: ComposeProject
  kind: ComposeResourceKind
  readOnly: boolean
}

type AnyEntry = ComposeNetwork | ComposeVolume | ComposeFileResource

const NOUN: Record<ComposeResourceKind, string> = {
  networks: 'network',
  volumes: 'volume',
  secrets: 'secret',
  configs: 'config',
}

/** Per-section label, icon, and a plain-language "what is this / when do I need
 *  it" line shown at the top of each sub-tab. */
const KIND_INFO: Record<ComposeResourceKind, {
  label: string
  Icon: typeof Network
  blurb: string
}> = {
  networks: {
    label: 'Networks',
    Icon: Network,
    blurb: 'How the project’s containers reach each other. With none defined, Docker '
      + 'puts them all on one default network — add one only to pin a fixed subnet/gateway '
      + 'or isolate traffic.',
  },
  volumes: {
    label: 'Volumes',
    Icon: Database,
    blurb: 'Named storage that survives a container being recreated — databases, uploads, '
      + 'app data. Each volume shows its current size on the host disk.',
  },
  secrets: {
    label: 'Secrets',
    Icon: KeyRound,
    blurb: 'Sensitive files (passwords, API keys, certificates) Docker mounts into containers '
      + 'from a host file, or references from an external Docker secret.',
  },
  configs: {
    label: 'Configs',
    Icon: FileText,
    blurb: 'Non-sensitive config files mounted into containers — same idea as secrets, for '
      + 'things like an nginx.conf.',
  },
}

const KIND_ORDER: ComposeResourceKind[] = ['networks', 'volumes', 'secrets', 'configs']

/**
 * V7.3 — the "Shared resources" tab: the Compose top-level elements that are
 * declared once and shared by every container (networks / volumes / secrets /
 * configs). One sub-tab per kind; each opens its CRUD list with a short
 * explanation at the top. Kept as its own top-level tab (separate from the
 * per-container tabs) so it never gets confused with editing a container.
 */
export interface ComposeSharedResourcesTabProps {
  connectionId: string
  project: string
  projectData: ComposeProject
  readOnly: boolean
}

export function ComposeSharedResourcesTab({
  connectionId, project, projectData, readOnly,
}: ComposeSharedResourcesTabProps) {
  const count = (k: ComposeResourceKind) =>
    k === 'networks' ? projectData.networks.length
      : k === 'volumes' ? projectData.volumes.length
        : k === 'secrets' ? projectData.secrets.length
          : projectData.configs.length

  // Land on the first section that actually has entries, else Networks.
  const [kind, setKind] = useState<ComposeResourceKind>(
    () => KIND_ORDER.find((k) => count(k) > 0) ?? 'networks',
  )

  return (
    <div className="compose-cross">
      <p className="compose-cross-intro">
        These settings are defined once for the whole project and shared across all its
        containers. Pick a section to view or edit it.
      </p>

      <nav className="container-modal-tabs compose-subtabs" role="tablist" aria-label="Project settings">
        {KIND_ORDER.map((k) => {
          const { label, Icon } = KIND_INFO[k]
          return (
            <button
              key={k}
              type="button"
              role="tab"
              aria-selected={k === kind}
              className={cn('container-modal-tab', k === kind && 'container-modal-tab-active')}
              onClick={() => setKind(k)}
            >
              <Icon className="h-3.5 w-3.5" />
              {label}
              <span className="compose-tab-count">{count(k)}</span>
            </button>
          )
        })}
      </nav>

      <ComposeResourcesPanel
        key={kind}
        connectionId={connectionId}
        project={project}
        projectData={projectData}
        kind={kind}
        readOnly={readOnly}
      />
    </div>
  )
}

export function ComposeResourcesPanel({
  connectionId, project, projectData, kind, readOnly,
}: ComposeResourcesPanelProps) {
  const entries: AnyEntry[] =
    kind === 'networks' ? projectData.networks
      : kind === 'volumes' ? projectData.volumes
        : kind === 'secrets' ? projectData.secrets
          : projectData.configs

  // `null` = nothing open; `''` = the "add" form; otherwise the entry's name.
  const [editing, setEditing] = useState<string | null>(null)
  const noun = NOUN[kind]

  return (
    <div className="compose-tab">
      <p className="compose-resource-blurb">{KIND_INFO[kind].blurb}</p>

      <div className="compose-resource-toolbar">
        <span className="compose-tab-name">{entries.length} {noun}{entries.length === 1 ? '' : 's'}</span>
        {!readOnly && editing !== '' && (
          <Button type="button" variant="outline" size="sm" onClick={() => setEditing('')}>
            <Plus className="h-3.5 w-3.5" />
            <span className="label-text">Add {noun}</span>
          </Button>
        )}
      </div>

      {editing === '' && (
        <ResourceForm
          connectionId={connectionId}
          project={project}
          projectData={projectData}
          kind={kind}
          entry={null}
          onClose={() => setEditing(null)}
        />
      )}

      {entries.length === 0 && editing !== '' && (
        <p className="container-modal-empty">
          No {noun}s declared in this Compose file.
        </p>
      )}

      <div className="compose-resource-list">
        {entries.map((entry) => (
          editing === entry.name ? (
            <ResourceForm
              key={entry.name}
              connectionId={connectionId}
              project={project}
              projectData={projectData}
              kind={kind}
              entry={entry}
              onClose={() => setEditing(null)}
            />
          ) : (
            <ResourceRow
              key={entry.name}
              connectionId={connectionId}
              project={project}
              kind={kind}
              entry={entry}
              readOnly={readOnly}
              onEdit={() => setEditing(entry.name)}
            />
          )
        ))}
      </div>
    </div>
  )
}

// ── one read-only entry row, with edit / delete ─────────────────────────────

interface RowProps {
  connectionId: string
  project: string
  kind: ComposeResourceKind
  entry: AnyEntry
  readOnly: boolean
  onEdit: () => void
}

function ResourceRow({ connectionId, project, kind, entry, readOnly, onEdit }: RowProps) {
  const del = useDeleteComposeResource(connectionId, project)
  const [confirming, setConfirming] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const sizeText = useVolumeSizeText(connectionId, project, kind, entry)

  const remove = async () => {
    setError(null)
    try {
      await del.mutateAsync({ kind, name: entry.name })
    } catch (e: unknown) {
      setError(getApiErrorMessage(e) ?? 'Failed to delete')
      setConfirming(false)
    }
  }

  return (
    <div className="compose-resource-entry">
      <div className="compose-resource-entry-head">
        <code className="container-modal-code">{entry.name}</code>
        {entry.external && <span className="cc-chip">external</span>}
        {sizeText && (
          <span className="compose-resource-size">
            <HardDrive className="h-3 w-3 inline" /> {sizeText}
          </span>
        )}
        <div className="compose-resource-entry-actions">
          {!readOnly && !confirming && (
            <>
              <Button type="button" variant="ghost" size="sm" onClick={onEdit} title={`Edit ${entry.name}`}>
                <Pencil className="h-3.5 w-3.5" />
              </Button>
              <Button type="button" variant="ghost" size="sm" onClick={() => setConfirming(true)} title={`Delete ${entry.name}`}>
                <Trash2 className="h-3.5 w-3.5" />
              </Button>
            </>
          )}
          {confirming && (
            <>
              <Button type="button" variant="destructive" size="sm" onClick={remove} disabled={del.isPending}>
                {del.isPending ? 'Removing…' : 'Confirm delete'}
              </Button>
              <Button type="button" variant="ghost" size="sm" onClick={() => setConfirming(false)}>
                <X className="h-3.5 w-3.5" />
              </Button>
            </>
          )}
        </div>
      </div>
      <ResourceSummary kind={kind} entry={entry} />
      {error && <pre className="compose-edit-error" role="alert">{error}</pre>}
    </div>
  )
}

function ResourceSummary({ kind, entry }: { kind: ComposeResourceKind; entry: AnyEntry }) {
  const rows: Array<[string, string]> = []
  if (entry.nameOverride) rows.push(['name', entry.nameOverride])
  if (kind === 'networks') {
    const n = entry as ComposeNetwork
    if (n.driver) rows.push(['driver', n.driver])
    if (n.subnet) rows.push(['subnet', n.subnet])
    if (n.gateway) rows.push(['gateway', n.gateway])
    for (const o of n.driverOpts) rows.push([`opt ${o.name}`, o.value ?? ''])
  } else if (kind === 'volumes') {
    const v = entry as ComposeVolume
    if (v.driver) rows.push(['driver', v.driver])
    for (const o of v.driverOpts) rows.push([`opt ${o.name}`, o.value ?? ''])
  } else {
    const f = entry as ComposeFileResource
    if (f.file) rows.push(['file', f.file])
  }
  if (rows.length === 0) return null
  return (
    <div className="compose-resource-summary">
      {rows.map(([k, v]) => (
        <span key={k} className="compose-resource-summary-item">
          <span className="compose-resource-summary-key">{k}</span>
          <code className="container-modal-code">{v}</code>
        </span>
      ))}
    </div>
  )
}

// ── add / edit form ─────────────────────────────────────────────────────────

interface FormProps {
  connectionId: string
  project: string
  projectData: ComposeProject
  kind: ComposeResourceKind
  /** `null` when adding a new entry. */
  entry: AnyEntry | null
  onClose: () => void
}

function ResourceForm({ connectionId, project, projectData, kind, entry, onClose }: FormProps) {
  const edit = useEditComposeResource(connectionId, project)
  const isNew = entry === null
  const noun = NOUN[kind]

  const net = kind === 'networks' ? (entry as ComposeNetwork | null) : null
  const vol = kind === 'volumes' ? (entry as ComposeVolume | null) : null
  const file = kind === 'secrets' || kind === 'configs' ? (entry as ComposeFileResource | null) : null

  const [name, setName] = useState(entry?.name ?? '')
  const [external, setExternal] = useState(entry?.external ?? false)
  const [nameOverride, setNameOverride] = useState(entry?.nameOverride ?? '')
  const [driver, setDriver] = useState(net?.driver ?? vol?.driver ?? '')
  const [subnet, setSubnet] = useState(net?.subnet ?? '')
  const [gateway, setGateway] = useState(net?.gateway ?? '')
  const [filePath, setFilePath] = useState(file?.file ?? '')
  const [opts, setOpts] = useState<ComposeEnvVar[]>(
    () => (net?.driverOpts ?? vol?.driverOpts ?? []).map((o) => ({ ...o })),
  )
  const [error, setError] = useState<string | null>(null)

  // Subnet-overlap warning — host networks are fetched lazily, only on the
  // network form, and cached server-side. (React Compiler memoises these
  // derived values; no manual useMemo needed.)
  const hostNetworks = useComposeHostNetworks(connectionId, project, kind === 'networks')
  const overlap = findSubnetOverlap(kind, subnet, hostNetworks.data ?? [])

  const existingList: AnyEntry[] =
    kind === 'networks' ? projectData.networks
      : kind === 'volumes' ? projectData.volumes
        : kind === 'secrets' ? projectData.secrets
          : projectData.configs
  const existingNames = new Set(existingList.map((e) => e.name))

  const nameError = isNew && name.trim() && existingNames.has(name.trim())
    ? `A ${noun} named "${name.trim()}" already exists.`
    : null
  const fileRequired = (kind === 'secrets' || kind === 'configs') && !external && !filePath.trim()

  const save = async () => {
    setError(null)
    const data: ComposeResourceEditRequest = {
      external,
      nameOverride: nameOverride.trim() || null,
      driver: external ? null : driver.trim() || null,
      subnet: external ? null : subnet.trim() || null,
      gateway: external ? null : gateway.trim() || null,
      file: external ? null : filePath.trim() || null,
      driverOpts: external ? [] : opts
        .filter((o) => o.name.trim().length > 0)
        .map((o): ComposeEnvVar => ({ name: o.name.trim(), value: o.value ?? '' })),
    }
    try {
      await edit.mutateAsync({ kind, name: name.trim(), data })
      onClose()
    } catch (e: unknown) {
      setError(getApiErrorMessage(e) ?? 'Failed to save the Compose file')
    }
  }

  const showDriverFields = (kind === 'networks' || kind === 'volumes') && !external
  const showFileField = (kind === 'secrets' || kind === 'configs') && !external

  return (
    <div className="compose-resource-form">
      <div className="compose-edit-grid">
        <div className="service-modal-field">
          <Label className="service-modal-label">Name {isNew ? '' : '(rename: delete & re-add)'}</Label>
          <Input
            value={name}
            onChange={(e) => setName(e.target.value)}
            disabled={!isNew}
            placeholder={kind === 'networks' ? 'frontend' : kind === 'volumes' ? 'db_data' : 'db_password'}
            className="font-mono text-[12px]"
          />
          {nameError && <p className="compose-edit-warning" role="alert"><AlertTriangle className="h-3 w-3 inline" /> {nameError}</p>}
        </div>
        <div className="service-modal-field compose-res-checkbox-field">
          <label className="compose-res-checkbox">
            <input type="checkbox" checked={external} onChange={(e) => setExternal(e.target.checked)} />
            External (created outside Compose)
          </label>
        </div>
      </div>

      {showDriverFields && (
        <div className="compose-edit-grid">
          <div className="service-modal-field">
            <Label className="service-modal-label">Driver</Label>
            <Input
              value={driver}
              onChange={(e) => setDriver(e.target.value)}
              placeholder={kind === 'networks' ? 'bridge' : 'local'}
              list={`compose-${kind}-driver`}
              className="font-mono text-[12px]"
            />
            <datalist id={`compose-${kind}-driver`}>
              {(kind === 'networks' ? ['bridge', 'overlay', 'macvlan', 'host', 'none'] : ['local']).map((d) => (
                <option key={d} value={d} />
              ))}
            </datalist>
          </div>
          {kind === 'networks' && (
            <>
              <div className="service-modal-field">
                <Label className="service-modal-label">Subnet</Label>
                <Input
                  value={subnet}
                  onChange={(e) => setSubnet(e.target.value)}
                  placeholder="172.20.0.0/24"
                  className="font-mono text-[12px]"
                />
              </div>
              <div className="service-modal-field">
                <Label className="service-modal-label">Gateway</Label>
                <Input
                  value={gateway}
                  onChange={(e) => setGateway(e.target.value)}
                  placeholder="172.20.0.1"
                  className="font-mono text-[12px]"
                />
              </div>
            </>
          )}
        </div>
      )}

      {kind === 'networks' && overlap && (
        <p className="compose-edit-warning" role="alert">
          <AlertTriangle className="h-3 w-3 inline" /> Subnet overlaps host network
          {' '}<strong>{overlap.network}</strong> ({overlap.subnet}).
        </p>
      )}

      {showFileField && (
        <div className="service-modal-field">
          <Label className="service-modal-label">File (host path)</Label>
          <Input
            value={filePath}
            onChange={(e) => setFilePath(e.target.value)}
            placeholder="./secrets/db_password.txt"
            className="font-mono text-[12px]"
          />
          {fileRequired && (
            <p className="compose-edit-hint">
              <AlertTriangle className="h-3 w-3 inline" /> A non-external {noun} needs a file path.
            </p>
          )}
        </div>
      )}

      {showDriverFields && (
        <div className="service-modal-field">
          <Label className="service-modal-label">Driver options</Label>
          {opts.map((o, i) => (
            <div key={i} className="compose-edit-row">
              <Input
                value={o.name}
                onChange={(e) => setOpts(opts.map((x, j) => j === i ? { ...x, name: e.target.value } : x))}
                placeholder="type"
                className="font-mono text-[12px] compose-edit-pair-name"
                aria-label={`Option key ${i + 1}`}
              />
              <Input
                value={o.value ?? ''}
                onChange={(e) => setOpts(opts.map((x, j) => j === i ? { ...x, value: e.target.value } : x))}
                placeholder="nfs"
                className="font-mono text-[12px]"
                aria-label={`Option value ${i + 1}`}
              />
              <Button type="button" variant="ghost" size="sm" onClick={() => setOpts(opts.filter((_, j) => j !== i))} title="Remove option">
                <Trash2 className="h-3.5 w-3.5" />
              </Button>
            </div>
          ))}
          <Button type="button" variant="outline" size="sm" onClick={() => setOpts([...opts, { name: '', value: '' }])}>
            <Plus className="h-3.5 w-3.5" />
            <span className="label-text">Add option</span>
          </Button>
        </div>
      )}

      <div className="service-modal-field">
        <Label className="service-modal-label">
          {external ? 'External name (real resource name)' : 'Name override (optional)'}
        </Label>
        <Input
          value={nameOverride}
          onChange={(e) => setNameOverride(e.target.value)}
          placeholder={external ? 'my_existing_resource' : '(defaults to project-prefixed name)'}
          className="font-mono text-[12px]"
        />
      </div>

      {error && <pre className="compose-edit-error" role="alert">{error}</pre>}

      <div className="compose-edit-actions">
        <Button
          type="button"
          size="sm"
          onClick={save}
          disabled={edit.isPending || !name.trim() || !!nameError || fileRequired}
        >
          {edit.isPending ? 'Validating & saving…' : isNew ? `Add ${noun}` : 'Save changes'}
        </Button>
        <Button type="button" variant="ghost" size="sm" onClick={onClose} disabled={edit.isPending}>
          Cancel
        </Button>
      </div>
    </div>
  )
}

// ── volume size lookup ──────────────────────────────────────────────────────

/** Resolves a compose volume entry to its on-disk size text (e.g. "4.2 GiB"),
 *  matching the parsed entry to the host's `docker system df` view by the real
 *  Docker volume name (external/override name, or the `<project>_<name>` prefix
 *  Compose applies). Returns null for non-volume kinds or unknown sizes. */
function useVolumeSizeText(
  connectionId: string,
  project: string,
  kind: ComposeResourceKind,
  entry: AnyEntry,
): string | null {
  const usage = useComposeVolumeUsage(connectionId, project, kind === 'volumes')
  if (kind !== 'volumes') return null

  const candidates = entry.external || entry.nameOverride
    ? [entry.nameOverride ?? entry.name]
    : [`${project}_${entry.name}`, entry.name]
  const match = (usage.data ?? []).find((u) => candidates.includes(u.name))
  if (!match || match.sizeBytes === null) return null
  return formatBytes(match.sizeBytes)
}

function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`
  const units = ['KiB', 'MiB', 'GiB', 'TiB']
  let value = bytes / 1024
  let i = 0
  while (value >= 1024 && i < units.length - 1) {
    value /= 1024
    i++
  }
  return `${value.toFixed(value >= 100 || value % 1 === 0 ? 0 : 1)} ${units[i]}`
}

/** First host network whose subnet overlaps the typed one, or null. */
function findSubnetOverlap(
  kind: ComposeResourceKind,
  subnet: string,
  hostNetworks: Array<{ name: string; subnets: string[] }>,
): { network: string; subnet: string } | null {
  if (kind !== 'networks' || !subnet.trim()) return null
  for (const hn of hostNetworks) {
    for (const s of hn.subnets) {
      if (cidrOverlap(subnet.trim(), s)) return { network: hn.name, subnet: s }
    }
  }
  return null
}

// ── CIDR overlap (IPv4) ─────────────────────────────────────────────────────

/** True when two IPv4 CIDR blocks overlap. Non-IPv4 / unparseable inputs return
 *  false (no warning rather than a false alarm). */
function cidrOverlap(a: string, b: string): boolean {
  const pa = parseCidr(a)
  const pb = parseCidr(b)
  if (!pa || !pb) return false
  const mask = Math.min(pa.prefix, pb.prefix)
  return sameNetwork(pa.addr, pb.addr, mask)
}

function parseCidr(cidr: string): { addr: number; prefix: number } | null {
  const m = /^(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})\/(\d{1,2})$/.exec(cidr.trim())
  if (!m) return null
  const octets = [m[1], m[2], m[3], m[4]].map(Number)
  if (octets.some((o) => o > 255)) return null
  const prefix = Number(m[5])
  if (prefix > 32) return null
  const addr = ((octets[0] << 24) | (octets[1] << 16) | (octets[2] << 8) | octets[3]) >>> 0
  return { addr, prefix }
}

function sameNetwork(a: number, b: number, prefix: number): boolean {
  if (prefix === 0) return true
  const mask = (0xffffffff << (32 - prefix)) >>> 0
  return (a & mask) === (b & mask)
}
