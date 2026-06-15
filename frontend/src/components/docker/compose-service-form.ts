import type {
  ComposeEnvVar,
  ComposeResourceConstraints,
  ComposeService,
  ComposeServiceEditRequest,
} from '@/lib/types'

/**
 * V7.4 — shared state model + (de)serialisation for the Compose service forms.
 * Lives apart from {@link ComposeServiceFields} so the edit and create forms
 * import the same helpers without tripping the fast-refresh "components only"
 * rule on the component module.
 */

// ── port rows ──────────────────────────────────────────────────────────────

/** A `host:container[/proto]` pair gets the structured 3-input row; anything
 *  fancier (IP binds, ranges-with-IP, long-form leftovers) stays raw text. */
export interface PortRow {
  /** Raw editing fallback for shapes the structured row can't express. */
  raw: string | null
  host: string
  container: string
  proto: string
}

const SIMPLE_PORT = /^(\d+(?:-\d+)?):(\d+(?:-\d+)?)$/

export function parsePortRow(p: string): PortRow {
  const slash = p.indexOf('/')
  const main = slash < 0 ? p : p.slice(0, slash)
  const proto = slash < 0 ? '' : p.slice(slash + 1)
  const m = SIMPLE_PORT.exec(main)
  if (!m || (proto !== '' && proto !== 'tcp' && proto !== 'udp'))
    return { raw: p, host: '', container: '', proto: '' }
  return { raw: null, host: m[1], container: m[2], proto }
}

export function renderPortRow(r: PortRow): string {
  if (r.raw !== null) return r.raw.trim()
  const main = `${r.host.trim()}:${r.container.trim()}`
  return r.proto ? `${main}/${r.proto}` : main
}

// ── env / label rows ───────────────────────────────────────────────────────

export interface PairRow {
  name: string
  /** `null` = pass-through (`- KEY`, value inherited from the host env). */
  value: string | null
}

// ── form state ─────────────────────────────────────────────────────────────

/** The full editable state of one service's basic fields. */
export interface ServiceFormState {
  image: string
  restart: string
  command: string
  entrypoint: string
  user: string
  workingDir: string
  ports: PortRow[]
  volumes: string[]
  env: PairRow[]
  labels: PairRow[]
  resources: ComposeResourceConstraints
}

/** A fresh, empty form (the "Add service" starting point). Resources default
 *  to the modern `deploy` convention so a first limit lands in that form. */
export function emptyServiceFormState(): ServiceFormState {
  return {
    image: '',
    restart: '',
    command: '',
    entrypoint: '',
    user: '',
    workingDir: '',
    ports: [],
    volumes: [],
    env: [],
    labels: [],
    resources: {
      convention: 'deploy',
      cpuLimit: null,
      cpuReservation: null,
      memLimit: null,
      memReservation: null,
      pidsLimit: null,
      cpuShares: null,
      oomKillDisable: null,
      oomScoreAdj: null,
      shmSize: null,
      ulimits: [],
    },
  }
}

/** Seeds the form from an existing service (the edit flow). */
export function serviceToFormState(service: ComposeService): ServiceFormState {
  return {
    image: service.image ?? '',
    restart: service.restart ?? '',
    command: service.command ?? '',
    entrypoint: service.entrypoint ?? '',
    user: service.user ?? '',
    workingDir: service.workingDir ?? '',
    ports: service.ports.map(parsePortRow),
    volumes: [...service.volumes],
    env: service.environment.map((e) => ({ ...e })),
    labels: service.labels.map((l) => ({ ...l })),
    resources: { ...service.resources, ulimits: service.resources.ulimits.map((u) => ({ ...u })) },
  }
}

/** Assembles the wire fields shared by the edit and create requests. */
export function formStateToRequestFields(s: ServiceFormState): ComposeServiceEditRequest {
  return {
    image: s.image.trim() || null,
    restart: s.restart.trim() || null,
    ports: s.ports.map(renderPortRow).filter((p) => p.length > 0),
    volumes: s.volumes.map((v) => v.trim()).filter((v) => v.length > 0),
    environment: s.env
      .filter((e) => e.name.trim().length > 0)
      .map((e): ComposeEnvVar => ({ name: e.name.trim(), value: e.value })),
    labels: s.labels
      .filter((l) => l.name.trim().length > 0)
      .map((l): ComposeEnvVar => ({ name: l.name.trim(), value: l.value })),
    command: s.command.trim() || null,
    entrypoint: s.entrypoint.trim() || null,
    user: s.user.trim() || null,
    workingDir: s.workingDir.trim() || null,
    resources: {
      ...s.resources,
      // Legacy files have no CPU reservation key — never send one.
      cpuReservation: s.resources.convention === 'legacy' ? null : s.resources.cpuReservation,
      ulimits: s.resources.ulimits.filter((u) => u.name.trim().length > 0),
    },
  }
}
