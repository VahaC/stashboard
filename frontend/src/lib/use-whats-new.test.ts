import { describe, it, expect, beforeEach } from 'vitest'
import { act, renderHook } from '@testing-library/react'
import { useWhatsNew } from './use-whats-new'

// vitest.config.ts stubs __APP_VERSION__ to '9.9.9'.
const KEY = 'stashboard:whatsNewSeenVersion'

describe('useWhatsNew', () => {
  beforeEach(() => localStorage.clear())

  it('auto-opens on a brand-new browser (no stored version)', () => {
    const { result } = renderHook(() => useWhatsNew())
    expect(result.current.open).toBe(true)
    // current version is persisted so it only opens once
    expect(localStorage.getItem(KEY)).toBe('9.9.9')
  })

  it('auto-opens after an upgrade (stored version differs)', () => {
    localStorage.setItem(KEY, '9.9.8')
    const { result } = renderHook(() => useWhatsNew())
    expect(result.current.open).toBe(true)
    expect(localStorage.getItem(KEY)).toBe('9.9.9')
  })

  it('stays closed once the current version has been seen', () => {
    localStorage.setItem(KEY, '9.9.9')
    const { result } = renderHook(() => useWhatsNew())
    expect(result.current.open).toBe(false)
  })

  it('can be opened and closed manually via setOpen', () => {
    localStorage.setItem(KEY, '9.9.9')
    const { result } = renderHook(() => useWhatsNew())
    expect(result.current.open).toBe(false)
    act(() => result.current.setOpen(true))
    expect(result.current.open).toBe(true)
    act(() => result.current.setOpen(false))
    expect(result.current.open).toBe(false)
  })
})
