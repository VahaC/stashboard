import { describe, it, expect } from 'vitest'
import type { ServiceTemplate } from '@/lib/types'
import {
  buildCreateRequest,
  defaultVariableValues,
  generateSecret,
  joinProjectDir,
  missingRequiredVariables,
  substitute,
  templateIconUrl,
} from './service-templates'

const template: ServiceTemplate = {
  id: 'wordpress',
  name: 'WordPress + MariaDB',
  description: 'CMS with database.',
  category: 'Apps',
  icon: 'wordpress',
  projectName: 'wordpress',
  notes: null,
  documentation: null,
  variables: [
    { key: 'DB_PASSWORD', label: 'DB password', type: 'password', hint: null, default: null, required: true, generate: true },
    { key: 'PORT', label: 'Port', type: 'port', hint: null, default: '8080', required: false, generate: false },
  ],
  services: [
    {
      name: 'db', image: 'mariadb:11', restart: 'unless-stopped',
      ports: [], volumes: ['./db:/var/lib/mysql'],
      environment: [{ name: 'MARIADB_PASSWORD', value: '${DB_PASSWORD}' }],
      labels: [], command: null, entrypoint: null, user: null, workingDir: null,
      resources: null as never,
    },
    {
      name: 'wordpress', image: 'wordpress:6', restart: 'unless-stopped',
      ports: ['${PORT}:80'], volumes: [],
      environment: [
        { name: 'WORDPRESS_DB_PASSWORD', value: '${DB_PASSWORD}' },
        { name: 'PASSTHROUGH', value: null },
      ],
      labels: [], command: null, entrypoint: null, user: null, workingDir: null,
      resources: null as never,
    },
  ],
}

describe('service-templates helpers', () => {
  it('builds the dashboard-icons URL, or null without a slug', () => {
    expect(templateIconUrl('redis')).toBe(
      'https://cdn.jsdelivr.net/gh/homarr-labs/dashboard-icons/svg/redis.svg')
    expect(templateIconUrl(null)).toBeNull()
  })

  it('uses a full URL or root-relative icon path verbatim', () => {
    expect(templateIconUrl('/favicon.svg')).toBe('/favicon.svg')
    expect(templateIconUrl('https://example.test/logo.png')).toBe('https://example.test/logo.png')
  })

  it('seeds variable values from defaults', () => {
    expect(defaultVariableValues(template)).toEqual({ DB_PASSWORD: '', PORT: '8080' })
  })

  it('flags blank required variables only', () => {
    expect(missingRequiredVariables(template, { DB_PASSWORD: '', PORT: '8080' })).toEqual(['DB_PASSWORD'])
    expect(missingRequiredVariables(template, { DB_PASSWORD: 'x', PORT: '' })).toEqual([])
  })

  it('substitutes placeholders, leaving unknown keys empty', () => {
    expect(substitute('${A}-${B}', { A: '1' })).toBe('1-')
  })

  it('generates a non-empty url-safe secret of the requested length', () => {
    const s = generateSecret(20)
    expect(s).toHaveLength(20)
    expect(s).toMatch(/^[A-Za-z0-9_-]+$/)
    expect(generateSecret()).not.toBe(generateSecret())
  })

  it('nests the project under its own folder in the parent directory', () => {
    expect(joinProjectDir('/opt/stacks', 'immich')).toBe('/opt/stacks/immich')
    // Trailing slashes on the parent collapse to a single separator.
    expect(joinProjectDir('/opt/stacks/', 'immich')).toBe('/opt/stacks/immich')
    expect(joinProjectDir('/opt/stacks///', 'immich')).toBe('/opt/stacks/immich')
    // A blank project name leaves the parent untouched (UI keeps actions disabled).
    expect(joinProjectDir('/opt/stacks', '')).toBe('/opt/stacks')
  })

  it('resolves the whole project request across multiple services', () => {
    const req = buildCreateRequest(
      template,
      { DB_PASSWORD: 's3cret', PORT: '9090' },
      { projectName: 'wp', directory: '/opt/wp', fileName: null, createDirectory: true, run: true },
    )
    expect(req.projectName).toBe('wp')
    // The entered directory is the parent; the project lands in its own subfolder.
    expect(req.directory).toBe('/opt/wp/wp')
    expect(req.services).toHaveLength(2)
    expect(req.services[0].environment[0].value).toBe('s3cret')
    expect(req.services[1].ports).toEqual(['9090:80'])
    expect(req.services[1].environment[0].value).toBe('s3cret')
    // Pass-through env (null value) survives substitution untouched.
    expect(req.services[1].environment[1].value).toBeNull()
  })
})
