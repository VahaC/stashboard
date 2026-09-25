import { act, renderHook } from '@testing-library/react'
import { beforeAll, describe, expect, it, vi } from 'vitest'
import { usePointerSort } from './use-pointer-sort'

// jsdom has no layout engine, so elementFromPoint doesn't exist. These tests exercise
// the click-vs-drag threshold + wasDragged logic, not the (browser-only) hit-testing.
beforeAll(() => {
  document.elementFromPoint = vi.fn(() => null)
})

/** Dispatch a window pointer event carrying the coordinates the hook reads. */
function pointer(type: 'pointermove' | 'pointerup', x = 0, y = 0) {
  const event = new Event(type)
  Object.assign(event, { clientX: x, clientY: y })
  window.dispatchEvent(event)
}

const press = { button: 0, clientX: 0, clientY: 0 } as unknown as React.PointerEvent

describe('usePointerSort', () => {
  it('treats a press with no movement as a click (never a drag)', () => {
    const commit = vi.fn()
    const { result } = renderHook(() => usePointerSort(['a', 'b', 'c'], commit))

    act(() => result.current.start('a', press))
    act(() => pointer('pointerup', 0, 0))

    expect(result.current.draggingId).toBeNull()
    expect(commit).not.toHaveBeenCalled()
    // The click that follows must be allowed through so the card opens.
    expect(result.current.wasDragged()).toBe(false)
  })

  it('ignores tiny jitter below the drag threshold', () => {
    const commit = vi.fn()
    const { result } = renderHook(() => usePointerSort(['a', 'b'], commit))

    act(() => result.current.start('a', press))
    act(() => pointer('pointermove', 2, 2)) // < 5px
    act(() => pointer('pointerup', 2, 2))

    expect(result.current.draggingId).toBeNull()
    expect(commit).not.toHaveBeenCalled()
    expect(result.current.wasDragged()).toBe(false)
  })

  it('promotes to a drag past the threshold and swallows the trailing click', () => {
    const commit = vi.fn()
    const { result } = renderHook(() => usePointerSort(['a', 'b', 'c'], commit))

    act(() => result.current.start('a', press))
    act(() => pointer('pointermove', 20, 20)) // > 5px → real drag begins

    expect(result.current.draggingId).toBe('a')

    act(() => pointer('pointerup', 20, 20))

    expect(commit).toHaveBeenCalledTimes(1)
    // First call after a drag suppresses the click; it resets immediately after.
    expect(result.current.wasDragged()).toBe(true)
    expect(result.current.wasDragged()).toBe(false)
  })
})
