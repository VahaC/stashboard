import type { ComposeLintFinding } from './types'

/** V7.7 — aggregate health derived from a project's linter findings, for the
 *  Health badge next to the project name. */
export interface LintSummary {
  errors: number
  warnings: number
  /** Overall verdict: `error` if any error, else `warning` if any warning, else
   *  `ok`. */
  level: 'ok' | 'warning' | 'error'
}

export function summarizeFindings(findings: ComposeLintFinding[]): LintSummary {
  const errors = findings.filter((f) => f.severity === 'Error').length
  const warnings = findings.filter((f) => f.severity === 'Warning').length
  return {
    errors,
    warnings,
    level: errors > 0 ? 'error' : warnings > 0 ? 'warning' : 'ok',
  }
}

/** Findings that render on a given service card (`service === name`). */
export function findingsForService(findings: ComposeLintFinding[], name: string): ComposeLintFinding[] {
  return findings.filter((f) => f.service === name)
}

/** Project-level findings (no owning service), e.g. a top-level `version:`. */
export function projectLevelFindings(findings: ComposeLintFinding[]): ComposeLintFinding[] {
  return findings.filter((f) => f.service === null)
}
