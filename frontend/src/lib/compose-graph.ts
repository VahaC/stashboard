import type { ComposeService } from './types'

/** V7.7 — pure layered layout for the Compose dependency graph (the "Graph"
 *  tab). Nodes are services, directed edges are `depends_on` (dependent →
 *  dependency). Kept free of React/SVG so the layering + positioning is unit
 *  tested directly. A simple Sugiyama-style layering is plenty for the "a dozen
 *  services" scale the feature targets; cycles (already flagged by the linter)
 *  are tolerated with an iteration cap so layout never hangs. */

export interface GraphNode {
  name: string
  /** Dependency depth: leaf dependencies (no `depends_on`) are layer 0, a
   *  service that depends on them is layer 1, and so on. */
  layer: number
  x: number
  y: number
  w: number
  h: number
}

export interface GraphEdge {
  /** The dependent service (`depends_on` is declared here). */
  from: string
  /** The service it depends on. */
  to: string
}

export interface GraphLayout {
  nodes: GraphNode[]
  edges: GraphEdge[]
  width: number
  height: number
}

export const NODE_W = 160
export const NODE_H = 72
const GAP_X = 28
const GAP_Y = 80
/** Outer canvas padding. Generous at the top so a network group box's label
 *  (drawn just inside the box's top edge) never spills outside the SVG. */
const PAD = 32

/** Longest-dependency-chain depth per service, with a cycle-safe iteration cap.
 *  Edges to services that don't exist in the project are ignored. */
function computeLayers(services: ComposeService[]): Map<string, number> {
  const names = new Set(services.map((s) => s.name))
  const deps = new Map<string, string[]>(
    services.map((s) => [s.name, s.dependsOn.filter((d) => names.has(d) && d !== s.name)]),
  )
  const layer = new Map<string, number>(services.map((s) => [s.name, 0]))

  // Relax layer = max(dep layer) + 1 until stable; cap at N passes so a cycle
  // can't loop forever (the linter already surfaces the cycle separately).
  for (let pass = 0; pass < services.length; pass++) {
    let changed = false
    for (const s of services) {
      const want = (deps.get(s.name) ?? []).reduce((max, d) => Math.max(max, (layer.get(d) ?? 0) + 1), 0)
      if (want > (layer.get(s.name) ?? 0)) {
        layer.set(s.name, want)
        changed = true
      }
    }
    if (!changed) break
  }
  return layer
}

/** Lays out the services into a layered graph. Layer 0 (leaf dependencies) sits
 *  at the bottom; dependents stack above, so `depends_on` arrows read downward
 *  ("depends on what's below"). Within a layer, nodes are ordered by name for a
 *  stable, deterministic layout. */
export function layoutGraph(services: ComposeService[]): GraphLayout {
  if (services.length === 0) return { nodes: [], edges: [], width: 0, height: 0 }

  const layer = computeLayers(services)
  const maxLayer = Math.max(...services.map((s) => layer.get(s.name) ?? 0))

  // Group by layer, name-sorted, then place each layer in its own row.
  const byLayer = new Map<number, string[]>()
  for (const s of services) {
    const l = layer.get(s.name) ?? 0
    const row = byLayer.get(l) ?? []
    row.push(s.name)
    byLayer.set(l, row)
  }
  for (const row of byLayer.values()) row.sort((a, b) => a.localeCompare(b))

  const widestRow = Math.max(...[...byLayer.values()].map((r) => r.length))
  const contentW = widestRow * NODE_W + (widestRow - 1) * GAP_X

  const nodes: GraphNode[] = []
  for (const [l, row] of byLayer) {
    const rowW = row.length * NODE_W + (row.length - 1) * GAP_X
    const offsetX = PAD + (contentW - rowW) / 2
    const y = PAD + (maxLayer - l) * (NODE_H + GAP_Y)
    row.forEach((name, i) => {
      nodes.push({ name, layer: l, x: offsetX + i * (NODE_W + GAP_X), y, w: NODE_W, h: NODE_H })
    })
  }

  const names = new Set(services.map((s) => s.name))
  const edges: GraphEdge[] = []
  for (const s of services) {
    for (const dep of s.dependsOn) {
      if (names.has(dep) && dep !== s.name) edges.push({ from: s.name, to: dep })
    }
  }

  return {
    nodes,
    edges,
    width: contentW + PAD * 2,
    height: PAD * 2 + (maxLayer + 1) * NODE_H + maxLayer * GAP_Y,
  }
}

export interface NetworkBox {
  name: string
  x: number
  y: number
  w: number
  h: number
}

/** Bounding boxes (one per shared network actually referenced by ≥2 services)
 *  enclosing the member nodes, for the translucent "group box" overlay. Only
 *  networks shared by more than one service are boxed — a single-member box adds
 *  noise without conveying a relationship. */
export function networkBoxes(layout: GraphLayout, services: ComposeService[]): NetworkBox[] {
  const pos = new Map(layout.nodes.map((n) => [n.name, n]))
  const members = new Map<string, GraphNode[]>()
  for (const s of services) {
    const node = pos.get(s.name)
    if (!node) continue
    for (const net of s.networks) {
      const list = members.get(net) ?? []
      list.push(node)
      members.set(net, list)
    }
  }

  // Even inset on all sides; the network name label floats just above the box's
  // top edge (see the graph component), so it needs no asymmetric headroom.
  const PADDING = 12
  const boxes: NetworkBox[] = []
  for (const [name, ns] of members) {
    if (ns.length < 2) continue
    const minX = Math.min(...ns.map((n) => n.x)) - PADDING
    const minY = Math.min(...ns.map((n) => n.y)) - PADDING
    const maxX = Math.max(...ns.map((n) => n.x + n.w)) + PADDING
    const maxY = Math.max(...ns.map((n) => n.y + n.h)) + PADDING
    boxes.push({ name, x: minX, y: minY, w: maxX - minX, h: maxY - minY })
  }
  return boxes.sort((a, b) => a.name.localeCompare(b.name))
}

/** Named-volume usage: each top-level volume → the services mounting it, for
 *  the graph's side legend. A volume nobody mounts is omitted. */
export function volumeUsage(
  volumeNames: string[],
  services: ComposeService[],
): Array<{ name: string; services: string[] }> {
  const result: Array<{ name: string; services: string[] }> = []
  for (const name of volumeNames) {
    const users = services
      .filter((s) => s.volumes.some((v) => v.split(':')[0] === name))
      .map((s) => s.name)
    if (users.length > 0) result.push({ name, services: users })
  }
  return result
}
