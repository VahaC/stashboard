import type {
  ComposeProjectCreateRequest,
  ComposeServiceCreateRequest,
  ServiceTemplate,
} from '@/lib/types'

/**
 * V7.5 — pure helpers behind the "From template" wizard tab: building the logo
 * URL, seeding/validating the per-deployment variable values, and resolving a
 * template's `${KEY}` placeholders into the exact {@link ComposeProjectCreateRequest}
 * the from-scratch flow already posts. Kept apart from the component so the
 * substitution logic is unit-testable on its own.
 */

/** dashboard-icons (homarr-labs) CDN, pinned to the SVG set. */
const ICON_BASE = 'https://cdn.jsdelivr.net/gh/homarr-labs/dashboard-icons/svg'

/**
 * The logo URL for a template's `icon`, or `null` when it has none. A full URL
 * (`http(s)://…`) or a root-relative path (`/favicon.svg`) is used verbatim — so
 * a template can point at a bundled/local asset (e.g. Stashboard's own icon);
 * anything else is treated as a dashboard-icons slug.
 */
export function templateIconUrl(icon: string | null): string | null {
  if (!icon) return null
  if (/^https?:\/\//.test(icon) || icon.startsWith('/')) return icon
  return `${ICON_BASE}/${icon}.svg`
}

/** A short, URL-safe random secret for `generate` variables (passwords/tokens). */
export function generateSecret(length = 24): string {
  const bytes = new Uint8Array(length)
  crypto.getRandomValues(bytes)
  // base64url without padding — safe to drop into env values.
  return btoa(String.fromCharCode(...bytes))
    .replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '')
    .slice(0, length)
}

/** The starting value map: each variable's `default` (or empty string). */
export function defaultVariableValues(template: ServiceTemplate): Record<string, string> {
  const values: Record<string, string> = {}
  for (const v of template.variables) values[v.key] = v.default ?? ''
  return values
}

/** Required variables left blank — the create actions stay disabled until empty. */
export function missingRequiredVariables(
  template: ServiceTemplate, values: Record<string, string>,
): string[] {
  return template.variables
    .filter((v) => v.required && (values[v.key] ?? '').trim().length === 0)
    .map((v) => v.key)
}

const PLACEHOLDER = /\$\{([A-Za-z0-9_]+)\}/g

/** Replaces every `${KEY}` with its value (unknown keys collapse to empty). */
export function substitute(text: string, values: Record<string, string>): string {
  return text.replace(PLACEHOLDER, (_, key: string) => values[key] ?? '')
}

function resolveService(
  svc: ComposeServiceCreateRequest, values: Record<string, string>,
): ComposeServiceCreateRequest {
  const sub = (s: string | null): string | null => (s === null ? null : substitute(s, values))
  return {
    ...svc,
    image: sub(svc.image),
    restart: sub(svc.restart),
    command: sub(svc.command),
    entrypoint: sub(svc.entrypoint),
    user: sub(svc.user),
    workingDir: sub(svc.workingDir),
    ports: svc.ports.map((p) => substitute(p, values)),
    volumes: svc.volumes.map((v) => substitute(v, values)),
    environment: svc.environment.map((e) => ({
      name: substitute(e.name, values),
      value: e.value === null ? null : substitute(e.value, values),
    })),
    labels: svc.labels.map((l) => ({
      name: substitute(l.name, values),
      value: l.value === null ? null : substitute(l.value, values),
    })),
  }
}

export interface TemplateProjectFields {
  projectName: string
  directory: string
  fileName: string | null
  createDirectory: boolean
  run: boolean
}

/**
 * Joins the entered (stacks-root) directory with the project name into the
 * project's own folder — POSIX paths, since every Docker host this talks to is
 * Linux (remote host for SSH, the Stashboard container for a local socket). The
 * template flow treats the directory field as the parent that holds all stacks,
 * so each project lands in its own `<parent>/<project>` subfolder rather than
 * dropping the Compose file straight into the shared parent.
 */
export function joinProjectDir(parent: string, projectName: string): string {
  const base = parent.trim().replace(/\/+$/, '')
  const name = projectName.trim()
  if (name.length === 0) return base
  return base.length === 0 ? name : `${base}/${name}`
}

/** Resolves the template + entered values into the create-project payload. */
export function buildCreateRequest(
  template: ServiceTemplate,
  values: Record<string, string>,
  fields: TemplateProjectFields,
): ComposeProjectCreateRequest {
  return {
    projectName: fields.projectName.trim(),
    directory: joinProjectDir(fields.directory, fields.projectName),
    fileName: fields.fileName?.trim() || null,
    createDirectory: fields.createDirectory,
    run: fields.run,
    services: template.services.map((s) => resolveService(s, values)),
  }
}
