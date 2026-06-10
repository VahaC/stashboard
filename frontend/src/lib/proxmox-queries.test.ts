import { describe, it, expect } from 'vitest'
import type { Query } from '@tanstack/react-query'
import { backoffRefetch, telemetryPollMs } from './proxmox-queries'

/**
 * V6.8.2 — the per-connection telemetry poll interval + failure-backoff helpers
 * that back the "configurable interval (default 15-30s) with failure backoff"
 * requirement.
 */
describe('telemetryPollMs', () => {
  it('defaults to 20s when unset', () => {
    expect(telemetryPollMs(null)).toBe(20_000)
    expect(telemetryPollMs(undefined)).toBe(20_000)
  })

  it('clamps to the 5..300s band', () => {
    expect(telemetryPollMs(3)).toBe(5_000)
    expect(telemetryPollMs(600)).toBe(300_000)
    expect(telemetryPollMs(45)).toBe(45_000)
  })
})

describe('backoffRefetch', () => {
  const withFailures = (n: number) =>
    ({ state: { fetchFailureCount: n } } as unknown as Query<unknown, unknown>)

  it('polls at the base interval while healthy', () => {
    expect(backoffRefetch(10_000)(withFailures(0))).toBe(10_000)
  })

  it('doubles per consecutive failure', () => {
    const fn = backoffRefetch(10_000)
    expect(fn(withFailures(1))).toBe(20_000)
    expect(fn(withFailures(2))).toBe(40_000)
  })

  it('caps the backoff at 2 minutes', () => {
    expect(backoffRefetch(10_000)(withFailures(10))).toBe(120_000)
  })
})
