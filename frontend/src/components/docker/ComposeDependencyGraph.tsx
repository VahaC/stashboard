import { useMemo } from 'react'
import { AlertCircle, AlertTriangle, Boxes, Network, Workflow } from 'lucide-react'
import { StateBadge } from '@/components/shared/StateBadge'
import type { ComposeLintFinding, ComposeProject } from '@/lib/types'
import {
  NODE_H,
  NODE_W,
  layoutGraph,
  networkBoxes,
  volumeUsage,
  type GraphNode,
} from '@/lib/compose-graph'
import { findingsForService } from '@/lib/compose-lint'
import { cn } from '@/lib/utils'

/** V7.7 — the "Graph" tab: a lightweight SVG dependency graph of the project.
 *  Nodes are services, edges are `depends_on` (arrow points to the dependency),
 *  shared networks are translucent group boxes behind their members, and named
 *  volumes are listed in a side legend. Built with hand-rolled SVG + foreignObject
 *  node cards so it reuses the existing StateBadge / card styling instead of
 *  pulling in a graph library. Read-only — a picture for reasoning, not editing. */

export interface ComposeDependencyGraphProps {
  projectData: ComposeProject
  /** Live runtime state per service name (for the node's state pill). */
  stateByService: Map<string, string>
  /** Click a node to jump to that service's tab. */
  onSelectService: (name: string) => void
}

/** Translucent palette for the network group boxes; cycles if there are more
 *  networks than colours. */
const NETWORK_COLORS = [
  '#3b82f6', // blue
  '#10b981', // green
  '#f59e0b', // amber
  '#a855f7', // purple
  '#ef4444', // red
  '#14b8a6', // teal
]

export function ComposeDependencyGraph({
  projectData,
  stateByService,
  onSelectService,
}: ComposeDependencyGraphProps) {
  const { services, volumes, lint } = projectData

  const layout = useMemo(() => layoutGraph(services), [services])
  const boxes = useMemo(() => networkBoxes(layout, services), [layout, services])
  const volUsage = useMemo(
    () => volumeUsage(volumes.map((v) => v.name), services),
    [volumes, services],
  )
  const nodeByName = useMemo(
    () => new Map(layout.nodes.map((n) => [n.name, n])),
    [layout],
  )
  const colorByNetwork = useMemo(() => {
    const map = new Map<string, string>()
    boxes.forEach((b, i) => map.set(b.name, NETWORK_COLORS[i % NETWORK_COLORS.length]))
    return map
  }, [boxes])

  if (services.length === 0) {
    return <p className="container-modal-empty">No services to graph.</p>
  }

  return (
    <div className="compose-graph">
      <div className="compose-graph-canvas" role="img" aria-label="Service dependency graph">
        <svg
          width={layout.width}
          height={layout.height}
          viewBox={`0 0 ${layout.width} ${layout.height}`}
          className="compose-graph-svg"
        >
          <defs>
            <marker
              id="compose-graph-arrow"
              viewBox="0 0 10 10"
              refX="9"
              refY="5"
              markerWidth="7"
              markerHeight="7"
              orient="auto-start-reverse"
            >
              <path d="M 0 0 L 10 5 L 0 10 z" className="compose-graph-arrowhead" />
            </marker>
          </defs>

          {/* Network group boxes, behind everything. */}
          {boxes.map((b) => {
            const color = colorByNetwork.get(b.name)!
            return (
              <g key={`net-${b.name}`}>
                <rect
                  x={b.x}
                  y={b.y}
                  width={b.w}
                  height={b.h}
                  rx={10}
                  className="compose-graph-netbox"
                  style={{ stroke: color, fill: color }}
                />
                <text x={b.x + 2} y={b.y - 5} className="compose-graph-netlabel" style={{ fill: color }}>
                  {b.name}
                </text>
              </g>
            )
          })}

          {/* depends_on edges. */}
          {layout.edges.map((e) => {
            const from = nodeByName.get(e.from)
            const to = nodeByName.get(e.to)
            if (!from || !to) return null
            return (
              <path
                key={`${e.from}->${e.to}`}
                d={edgePath(from, to)}
                className="compose-graph-edge"
                markerEnd="url(#compose-graph-arrow)"
              />
            )
          })}

          {/* Service nodes. */}
          {layout.nodes.map((n) => {
            const svc = services.find((s) => s.name === n.name)!
            const nodeFindings = findingsForService(lint, n.name)
            return (
              <foreignObject key={n.name} x={n.x} y={n.y} width={n.w} height={n.h}>
                <NodeCard
                  name={n.name}
                  image={svc.image}
                  state={stateByService.get(n.name) ?? 'not deployed'}
                  findings={nodeFindings}
                  onClick={() => onSelectService(n.name)}
                />
              </foreignObject>
            )
          })}
        </svg>
      </div>

      <aside className="compose-graph-legend">
        <LegendSection icon={<Workflow className="h-3.5 w-3.5" />} title="Edges">
          <p className="compose-graph-legend-hint">Arrows point from a service to what it depends on.</p>
        </LegendSection>

        {boxes.length > 0 && (
          <LegendSection icon={<Network className="h-3.5 w-3.5" />} title="Shared networks">
            <ul className="compose-graph-legend-list">
              {boxes.map((b) => (
                <li key={b.name}>
                  <span
                    className="compose-graph-legend-swatch"
                    style={{ backgroundColor: colorByNetwork.get(b.name)! }}
                  />
                  {b.name}
                </li>
              ))}
            </ul>
          </LegendSection>
        )}

        {volUsage.length > 0 && (
          <LegendSection icon={<Boxes className="h-3.5 w-3.5" />} title="Named volumes">
            <ul className="compose-graph-legend-list">
              {volUsage.map((v) => (
                <li key={v.name}>
                  <code className="container-modal-code">{v.name}</code>
                  <span className="compose-graph-legend-hint"> · {v.services.join(', ')}</span>
                </li>
              ))}
            </ul>
          </LegendSection>
        )}
      </aside>
    </div>
  )
}

function NodeCard({
  name,
  image,
  state,
  findings,
  onClick,
}: {
  name: string
  image: string | null
  state: string
  findings: ComposeLintFinding[]
  onClick: () => void
}) {
  const errors = findings.filter((f) => f.severity === 'Error').length
  const warnings = findings.filter((f) => f.severity === 'Warning').length
  const title = findings.length > 0 ? findings.map((f) => f.message).join('\n') : undefined
  return (
    <button
      type="button"
      className={cn('compose-graph-node', errors > 0 && 'has-error', errors === 0 && warnings > 0 && 'has-warning')}
      onClick={onClick}
      title={title}
    >
      <span className="compose-graph-node-head">
        <span className="compose-graph-node-name">{name}</span>
        {errors > 0 ? (
          <AlertCircle className="h-3.5 w-3.5 compose-graph-node-icon error" />
        ) : warnings > 0 ? (
          <AlertTriangle className="h-3.5 w-3.5 compose-graph-node-icon warning" />
        ) : null}
      </span>
      {image && <span className="compose-graph-node-image">{image}</span>}
      <StateBadge state={state} size="sm" />
    </button>
  )
}

function LegendSection({
  icon,
  title,
  children,
}: {
  icon: React.ReactNode
  title: string
  children: React.ReactNode
}) {
  return (
    <section className="compose-graph-legend-section">
      <h4 className="compose-graph-legend-title">
        {icon}
        {title}
      </h4>
      {children}
    </section>
  )
}

/** A cubic curve from the bottom of the dependent node down to the top of the
 *  dependency node (layers stack with dependencies below). */
function edgePath(from: GraphNode, to: GraphNode): string {
  const x1 = from.x + NODE_W / 2
  const y1 = from.y + NODE_H
  const x2 = to.x + NODE_W / 2
  const y2 = to.y
  const midY = (y1 + y2) / 2
  return `M ${x1} ${y1} C ${x1} ${midY}, ${x2} ${midY}, ${x2} ${y2}`
}
