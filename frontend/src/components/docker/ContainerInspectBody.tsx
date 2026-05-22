import { useState } from 'react'
import { Check, ChevronDown, ChevronRight, Copy } from 'lucide-react'
import type {
  DockerContainerInspect,
  DockerInspectEnvVar,
  DockerInspectMount,
  DockerInspectNetwork,
  DockerInspectPortBinding,
} from '@/lib/types'

export type ContainerInspectBodyProps = {
  data: DockerContainerInspect
}

/**
 * Shared "Inspect" body. Renders a structured snapshot of `docker inspect`
 * for one container — slim summary header, collapsible sections, and a Raw
 * JSON view with copy-to-clipboard.
 *
 * This is the single source of truth replacing the two duplicate
 * implementations that previously lived in `ContainerModal.tsx` and
 * `DockerWatchSection.tsx`.
 */
export function ContainerInspectBody({ data }: ContainerInspectBodyProps) {
  const [showRaw, setShowRaw] = useState(false)
  const [copied, setCopied] = useState(false)
  const json = JSON.stringify(data, null, 2)
  const copy = () => {
    void navigator.clipboard.writeText(json).then(() => {
      setCopied(true)
      setTimeout(() => setCopied(false), 1500)
    })
  }

  return (
    <div className="docker-inspect-body">
      <InspectSummary data={data} />

      <InspectSection title="Command" empty={!data.config.cmd.length && !data.config.entrypoint.length}>
        {data.config.entrypoint.length > 0 && (
          <div className="docker-inspect-kv">
            <span className="docker-inspect-key">entrypoint</span>
            <code className="docker-inspect-code">{data.config.entrypoint.join(' ')}</code>
          </div>
        )}
        {data.config.cmd.length > 0 && (
          <div className="docker-inspect-kv">
            <span className="docker-inspect-key">cmd</span>
            <code className="docker-inspect-code">{data.config.cmd.join(' ')}</code>
          </div>
        )}
        {data.config.workingDir && (
          <div className="docker-inspect-kv">
            <span className="docker-inspect-key">workdir</span>
            <code className="docker-inspect-code">{data.config.workingDir}</code>
          </div>
        )}
        {data.config.user && (
          <div className="docker-inspect-kv">
            <span className="docker-inspect-key">user</span>
            <code className="docker-inspect-code">{data.config.user}</code>
          </div>
        )}
      </InspectSection>

      <InspectSection
        title="Environment"
        count={data.config.env.length}
        empty={data.config.env.length === 0}
      >
        <EnvTable env={data.config.env} />
      </InspectSection>

      <InspectSection
        title="Labels"
        count={Object.keys(data.config.labels).length}
        empty={Object.keys(data.config.labels).length === 0}
      >
        <LabelsTable labels={data.config.labels} />
      </InspectSection>

      <InspectSection
        title="Ports"
        count={data.hostConfig.portBindings.length + data.config.exposedPorts.length}
        empty={data.hostConfig.portBindings.length === 0 && data.config.exposedPorts.length === 0}
      >
        <PortsTable
          bindings={data.hostConfig.portBindings}
          exposedOnly={data.config.exposedPorts.filter(
            (p) => !data.hostConfig.portBindings.some((b) => b.containerPort === p)
          )}
        />
      </InspectSection>

      <InspectSection
        title="Mounts"
        count={data.mounts.length}
        empty={data.mounts.length === 0}
      >
        <MountsTable mounts={data.mounts} />
      </InspectSection>

      <InspectSection
        title="Networks"
        count={Object.keys(data.networkSettings.networks).length}
        empty={Object.keys(data.networkSettings.networks).length === 0}
      >
        <NetworksTable
          networkMode={data.hostConfig.networkMode}
          networks={data.networkSettings.networks}
        />
      </InspectSection>

      <InspectSection title="Restart policy" empty={!data.hostConfig.restartPolicy}>
        {data.hostConfig.restartPolicy && (
          <div className="docker-inspect-kv">
            <span className="docker-inspect-key">{data.hostConfig.restartPolicy.name}</span>
            {data.hostConfig.restartPolicy.maximumRetryCount > 0 && (
              <span className="service-modal-label-help">
                max {data.hostConfig.restartPolicy.maximumRetryCount} retries
              </span>
            )}
          </div>
        )}
      </InspectSection>

      <InspectSection title="Health" empty={!data.state.health}>
        {data.state.health && <HealthTable health={data.state.health} />}
      </InspectSection>

      <div className="docker-inspect-raw">
        <div className="docker-inspect-raw-toggle">
          <button
            type="button"
            className="docker-inspect-raw-toggle-btn"
            onClick={() => setShowRaw((v) => !v)}
            aria-expanded={showRaw}
          >
            {showRaw ? <ChevronDown className="h-3.5 w-3.5" /> : <ChevronRight className="h-3.5 w-3.5" />}
            <span>Raw JSON</span>
          </button>
          <button
            type="button"
            className="service-modal-copy-btn docker-inspect-raw-copy"
            onClick={copy}
            title="Copy JSON"
          >
            {copied ? <Check className="h-3.5 w-3.5 service-modal-copy-success" /> : <Copy className="h-3.5 w-3.5" />}
          </button>
        </div>
        {showRaw && <pre className="docker-inspect-raw-body">{json}</pre>}
      </div>
    </div>
  )
}

function InspectSummary({ data }: { data: DockerContainerInspect }) {
  return (
    <dl className="docker-inspect-summary">
      <dt>Status</dt>
      <dd>
        <span
          className="docker-section-badge"
          data-status={data.state.running ? 'UpToDate' : 'Error'}
        >
          {data.state.status}
        </span>
        {data.state.exitCode !== 0 && !data.state.running && (
          <span className="service-modal-label-help"> exit {data.state.exitCode}</span>
        )}
        {data.state.startedUtc && data.state.running && (
          <span className="service-modal-label-help"> started {formatRelative(data.state.startedUtc)}</span>
        )}
      </dd>
      <dt>Image</dt>
      <dd><code className="docker-inspect-code">{data.image}</code></dd>
      <dt>Image id</dt>
      <dd><code className="docker-inspect-code docker-inspect-truncate">{data.imageId}</code></dd>
      {data.imageRepoDigests.length > 0 && (
        <>
          <dt>Repo digest</dt>
          <dd>
            {data.imageRepoDigests.map((d) => (
              <code key={d} className="docker-inspect-code docker-inspect-truncate">{d}</code>
            ))}
          </dd>
        </>
      )}
      {data.createdUtc && (
        <>
          <dt>Created</dt>
          <dd>{formatLocalDateTime(data.createdUtc)}</dd>
        </>
      )}
      {data.restartCount > 0 && (
        <>
          <dt>Restart count</dt>
          <dd>{data.restartCount}</dd>
        </>
      )}
      {data.state.error && (
        <>
          <dt>Last error</dt>
          <dd className="docker-test-result-error">{data.state.error}</dd>
        </>
      )}
    </dl>
  )
}

function InspectSection({
  title, count, empty, children,
}: {
  title: string
  count?: number
  empty?: boolean
  children: React.ReactNode
}) {
  const [open, setOpen] = useState(false)
  if (empty) {
    return (
      <div className="docker-inspect-section docker-inspect-section-empty">
        <span>{title}</span>
        <span className="service-modal-label-help">none</span>
      </div>
    )
  }
  return (
    <div className="docker-inspect-section">
      <button
        type="button"
        className="docker-inspect-section-header"
        onClick={() => setOpen((v) => !v)}
        aria-expanded={open}
      >
        {open ? <ChevronDown className="h-3.5 w-3.5" /> : <ChevronRight className="h-3.5 w-3.5" />}
        <span>{title}</span>
        {count !== undefined && count > 0 && (
          <span className="service-modal-label-help">{count}</span>
        )}
      </button>
      {open && <div className="docker-inspect-section-body">{children}</div>}
    </div>
  )
}

function EnvTable({ env }: { env: DockerInspectEnvVar[] }) {
  const sorted = [...env].sort((a, b) => a.key.localeCompare(b.key))
  return (
    <table className="docker-inspect-table">
      <tbody>
        {sorted.map((e) => (
          <tr key={e.key}>
            <th>{e.key}</th>
            <td>
              {e.masked
                ? <span className="docker-inspect-masked" title="Value hidden — key matches the secret heuristic">•••••• (masked)</span>
                : <code className="docker-inspect-code">{e.value || <span className="docker-inspect-empty">(empty)</span>}</code>}
            </td>
          </tr>
        ))}
      </tbody>
    </table>
  )
}

function LabelsTable({ labels }: { labels: Record<string, string> }) {
  const entries = Object.entries(labels).sort(([a], [b]) => a.localeCompare(b))
  return (
    <table className="docker-inspect-table">
      <tbody>
        {entries.map(([k, v]) => (
          <tr key={k}>
            <th>{k}</th>
            <td><code className="docker-inspect-code">{v || <span className="docker-inspect-empty">(empty)</span>}</code></td>
          </tr>
        ))}
      </tbody>
    </table>
  )
}

function PortsTable({
  bindings,
  exposedOnly,
}: {
  bindings: DockerInspectPortBinding[]
  exposedOnly: string[]
}) {
  return (
    <table className="docker-inspect-table">
      <tbody>
        {bindings.map((b, i) => (
          <tr key={`${b.containerPort}-${i}`}>
            <th>{b.containerPort}</th>
            <td>
              {b.hostPort
                ? <code className="docker-inspect-code">{b.hostIp ?? '0.0.0.0'}:{b.hostPort}</code>
                : <span className="service-modal-label-help">exposed, not published</span>}
            </td>
          </tr>
        ))}
        {exposedOnly.map((p) => (
          <tr key={`exposed-${p}`}>
            <th>{p}</th>
            <td><span className="service-modal-label-help">exposed, not published</span></td>
          </tr>
        ))}
      </tbody>
    </table>
  )
}

function MountsTable({ mounts }: { mounts: DockerInspectMount[] }) {
  return (
    <table className="docker-inspect-table">
      <thead>
        <tr>
          <th>type</th>
          <th>source</th>
          <th>destination</th>
          <th>mode</th>
        </tr>
      </thead>
      <tbody>
        {mounts.map((m, i) => (
          <tr key={`${m.destination}-${i}`}>
            <td>{m.type}</td>
            <td><code className="docker-inspect-code">{m.source ?? m.name ?? '—'}</code></td>
            <td><code className="docker-inspect-code">{m.destination}</code></td>
            <td>{m.readWrite ? 'rw' : 'ro'}{m.mode ? ` · ${m.mode}` : ''}</td>
          </tr>
        ))}
      </tbody>
    </table>
  )
}

function NetworksTable({
  networkMode, networks,
}: {
  networkMode: string | null
  networks: Record<string, DockerInspectNetwork>
}) {
  return (
    <>
      {networkMode && (
        <div className="docker-inspect-kv">
          <span className="docker-inspect-key">mode</span>
          <code className="docker-inspect-code">{networkMode}</code>
        </div>
      )}
      <table className="docker-inspect-table">
        <thead>
          <tr>
            <th>network</th>
            <th>address</th>
            <th>aliases</th>
          </tr>
        </thead>
        <tbody>
          {Object.entries(networks).map(([name, n]) => (
            <tr key={name}>
              <td>{name}</td>
              <td>
                <code className="docker-inspect-code">
                  {n.ipAddress ?? '—'}{n.ipPrefixLen != null && n.ipAddress ? `/${n.ipPrefixLen}` : ''}
                </code>
              </td>
              <td>{n.aliases.length > 0 ? n.aliases.join(', ') : <span className="service-modal-label-help">—</span>}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </>
  )
}

function HealthTable({
  health,
}: {
  health: NonNullable<DockerContainerInspect['state']['health']>
}) {
  return (
    <>
      <div className="docker-inspect-kv">
        <span className="docker-inspect-key">status</span>
        <span
          className="docker-section-badge"
          data-status={health.status === 'healthy' ? 'UpToDate' : health.status === 'unhealthy' ? 'Error' : 'Unknown'}
        >
          {health.status}
        </span>
        {health.failingStreak > 0 && (
          <span className="service-modal-label-help">failing streak {health.failingStreak}</span>
        )}
      </div>
      {health.log.length > 0 && (
        <table className="docker-inspect-table">
          <thead>
            <tr>
              <th>when</th>
              <th>exit</th>
              <th>output</th>
            </tr>
          </thead>
          <tbody>
            {health.log.slice(-5).reverse().map((entry, i) => (
              <tr key={i}>
                <td>{entry.endUtc ? formatLocalDateTime(entry.endUtc) : '—'}</td>
                <td>{entry.exitCode}</td>
                <td><code className="docker-inspect-code">{entry.output?.trim() || '—'}</code></td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </>
  )
}

const formatLocalDateTime = (iso: string) => {
  const d = new Date(iso)
  return d.toLocaleString(undefined, {
    weekday: 'short', month: 'short', day: 'numeric',
    hour: '2-digit', minute: '2-digit',
  })
}

const formatRelative = (iso: string | null) => {
  if (!iso) return 'never'
  const diffMs = Date.now() - new Date(iso).getTime()
  if (diffMs < 60_000) return 'just now'
  if (diffMs < 3_600_000) return `${Math.floor(diffMs / 60_000)} min ago`
  if (diffMs < 86_400_000) return `${Math.floor(diffMs / 3_600_000)} h ago`
  return `${Math.floor(diffMs / 86_400_000)} d ago`
}
