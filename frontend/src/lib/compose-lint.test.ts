import { describe, it, expect } from 'vitest'
import { findingsForService, projectLevelFindings, summarizeFindings } from './compose-lint'
import type { ComposeLintFinding } from './types'

const f = (
  rule: string,
  severity: ComposeLintFinding['severity'],
  service: string | null,
): ComposeLintFinding => ({ rule, severity, service, message: `${rule} on ${service ?? 'project'}` })

describe('summarizeFindings — V7.7', () => {
  it('reports ok when there are no findings', () => {
    expect(summarizeFindings([])).toEqual({ errors: 0, warnings: 0, level: 'ok' })
  })

  it('counts errors and warnings and escalates to error', () => {
    const s = summarizeFindings([
      f('latest-tag', 'Warning', 'a'),
      f('port-collision', 'Error', 'a'),
      f('latest-tag', 'Warning', 'b'),
    ])
    expect(s).toEqual({ errors: 1, warnings: 2, level: 'error' })
  })

  it('reports warning when only warnings exist', () => {
    expect(summarizeFindings([f('latest-tag', 'Warning', 'a')]).level).toBe('warning')
  })
})

describe('finding partitioning — V7.7', () => {
  const findings = [
    f('latest-tag', 'Warning', 'web'),
    f('port-collision', 'Error', 'web'),
    f('deprecated-key', 'Warning', null),
  ]

  it('selects per-service findings', () => {
    expect(findingsForService(findings, 'web')).toHaveLength(2)
    expect(findingsForService(findings, 'db')).toHaveLength(0)
  })

  it('selects project-level findings', () => {
    expect(projectLevelFindings(findings)).toHaveLength(1)
    expect(projectLevelFindings(findings)[0].rule).toBe('deprecated-key')
  })
})
