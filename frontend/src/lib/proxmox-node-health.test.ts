import { describe, it, expect } from 'vitest'
import { showConnectionError } from './proxmox-node-health'

// V7.2.1 — the connection-level scan-error banner must not contradict a live,
// online node card. A scan that ran while the host was momentarily down leaves a
// stale "API unreachable: No route to host" on `connection.lastError`; once the
// node-status poll succeeds again the banner is stale and is suppressed.
describe('showConnectionError', () => {
  it('shows the banner when there is an error and the node is unreachable', () => {
    expect(showConnectionError('Proxmox API unreachable: No route to host', false)).toBe(true)
  })

  it('suppresses a stale error once the node is reachable again', () => {
    expect(showConnectionError('Proxmox API unreachable: No route to host', true)).toBe(false)
  })

  it('shows nothing when there is no error', () => {
    expect(showConnectionError(null, false)).toBe(false)
    expect(showConnectionError(undefined, false)).toBe(false)
    expect(showConnectionError('', false)).toBe(false)
  })

  it('shows nothing when there is no error even if reachable', () => {
    expect(showConnectionError(null, true)).toBe(false)
  })
})
