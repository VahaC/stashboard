import { describe, it, expect } from 'vitest'
import { layoutGraph, networkBoxes, volumeUsage, NODE_W } from './compose-graph'
import type { ComposeService } from './types'

/** Minimal ComposeService factory — only the fields the graph helpers read. */
function svc(name: string, partial: Partial<ComposeService> = {}): ComposeService {
  return {
    name,
    image: partial.image ?? `${name}:1`,
    containerName: null,
    restart: null,
    ports: partial.ports ?? [],
    volumes: partial.volumes ?? [],
    environment: [],
    envFiles: [],
    dependsOn: partial.dependsOn ?? [],
    networks: partial.networks ?? [],
    resources: {
      convention: 'deploy', cpuLimit: null, cpuReservation: null, memLimit: null,
      memReservation: null, pidsLimit: null, cpuShares: null, oomKillDisable: null,
      oomScoreAdj: null, shmSize: null, ulimits: [],
    },
    labels: [],
    command: null,
    entrypoint: null,
    user: null,
    workingDir: null,
  }
}

describe('layoutGraph — V7.7', () => {
  it('layers dependencies below their dependents', () => {
    const layout = layoutGraph([svc('web', { dependsOn: ['db'] }), svc('db')])
    const byName = new Map(layout.nodes.map((n) => [n.name, n]))
    expect(byName.get('db')!.layer).toBe(0)
    expect(byName.get('web')!.layer).toBe(1)
    // layer 0 (db) renders below layer 1 (web): larger y.
    expect(byName.get('db')!.y).toBeGreaterThan(byName.get('web')!.y)
  })

  it('emits one edge per intra-project depends_on', () => {
    const layout = layoutGraph([
      svc('web', { dependsOn: ['db', 'cache'] }),
      svc('db'),
      svc('cache'),
    ])
    expect(layout.edges).toHaveLength(2)
    expect(layout.edges).toContainEqual({ from: 'web', to: 'db' })
    expect(layout.edges).toContainEqual({ from: 'web', to: 'cache' })
  })

  it('ignores depends_on pointing at a missing service', () => {
    const layout = layoutGraph([svc('web', { dependsOn: ['ghost'] })])
    expect(layout.edges).toHaveLength(0)
  })

  it('does not hang on a cycle and keeps every node', () => {
    const layout = layoutGraph([
      svc('a', { dependsOn: ['b'] }),
      svc('b', { dependsOn: ['a'] }),
    ])
    expect(layout.nodes).toHaveLength(2)
    expect(layout.edges).toHaveLength(2)
  })

  it('returns an empty layout for no services', () => {
    expect(layoutGraph([])).toEqual({ nodes: [], edges: [], width: 0, height: 0 })
  })

  it('orders same-layer nodes by name for a deterministic layout', () => {
    const layout = layoutGraph([svc('zeta'), svc('alpha'), svc('mid')])
    const sameLayer = layout.nodes.filter((n) => n.layer === 0).sort((a, b) => a.x - b.x)
    expect(sameLayer.map((n) => n.name)).toEqual(['alpha', 'mid', 'zeta'])
    // contiguous columns
    expect(sameLayer[1].x - sameLayer[0].x).toBeGreaterThanOrEqual(NODE_W)
  })
})

describe('networkBoxes — V7.7', () => {
  it('boxes only networks shared by 2+ services', () => {
    const services = [
      svc('a', { networks: ['shared', 'solo'] }),
      svc('b', { networks: ['shared'] }),
    ]
    const layout = layoutGraph(services)
    const boxes = networkBoxes(layout, services)
    expect(boxes.map((b) => b.name)).toEqual(['shared'])
    expect(boxes[0].w).toBeGreaterThan(0)
    expect(boxes[0].h).toBeGreaterThan(0)
  })
})

describe('volumeUsage — V7.7', () => {
  it('maps each named volume to the services mounting it, skipping unused', () => {
    const services = [
      svc('a', { volumes: ['data:/var/lib', './local:/x'] }),
      svc('b', { volumes: ['data:/data'] }),
    ]
    const usage = volumeUsage(['data', 'unused'], services)
    expect(usage).toEqual([{ name: 'data', services: ['a', 'b'] }])
  })
})
