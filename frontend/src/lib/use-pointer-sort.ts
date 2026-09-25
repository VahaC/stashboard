import { useCallback, useEffect, useRef, useState } from 'react'

export interface PointerSort {
  /** The live (optimistic) order to render in, or the source order when idle. */
  order: string[]
  /** The id currently being dragged, for styling. */
  draggingId: string | null
  /** Attach to each sortable node's `onPointerDown` (pass its id). */
  start: (id: string, event: React.PointerEvent) => void
  /** True (once, then reset) if the press that just ended was an actual drag — call
   *  from the node's `onClick` to swallow the click that follows a drop, so a real
   *  click still opens the card while a drag doesn't. */
  wasDragged: () => boolean
}

/** Pointer travel (px) before a press becomes a drag. Below this it stays a click. */
const DRAG_THRESHOLD = 5

/**
 * V10.6 — pointer-based drag-to-reorder for a set of DOM nodes, each tagged with a
 * `data-sort-id` attribute. Works with mouse and touch (Pointer Events), reorders
 * live as you drag using `elementFromPoint`, and commits the final order on release.
 * A press only turns into a drag once the pointer travels past a small threshold, so
 * a plain click still passes through (the node opens as usual) — the click that
 * follows an actual drag is suppressed via `wasDragged()`.
 *
 * `sourceIds` is the order to fall back to when not mid-drag; when it changes
 * (data refetched, sort mode switched) any stale optimistic order is dropped.
 */
export function usePointerSort(
  sourceIds: string[],
  onCommit: (orderedIds: string[]) => void,
  attr = 'data-sort-id',
): PointerSort {
  const [optimistic, setOptimistic] = useState<string[] | null>(null)
  const [draggingId, setDraggingId] = useState<string | null>(null)
  const draggingRef = useRef<string | null>(null)
  const pendingRef = useRef<{ id: string; x: number; y: number } | null>(null)
  const suppressClickRef = useRef(false)
  const orderRef = useRef<string[]>(sourceIds)

  const order = optimistic ?? sourceIds

  // Keep the ref (read from pointer handlers) in sync with the rendered order —
  // in an effect, never during render.
  useEffect(() => {
    orderRef.current = order
  })

  // Drop any optimistic order once the underlying set of ids changes (add/remove/
  // refetch), so external updates always win when we're not actively dragging.
  const key = sourceIds.join(',')
  useEffect(() => {
    if (!draggingRef.current) setOptimistic(null)
  }, [key])

  const finish = useCallback(() => {
    const dragging = draggingRef.current
    pendingRef.current = null
    draggingRef.current = null
    setDraggingId(null)
    if (dragging) {
      // A real drag happened — swallow the click that fires next and commit the order.
      suppressClickRef.current = true
      if (orderRef.current.length > 0) onCommit(orderRef.current)
    }
  }, [onCommit])

  useEffect(() => {
    const onMove = (event: PointerEvent) => {
      // Promote a pending press to a drag only once it travels past the threshold.
      if (!draggingRef.current) {
        const pending = pendingRef.current
        if (!pending) return
        if (Math.hypot(event.clientX - pending.x, event.clientY - pending.y) < DRAG_THRESHOLD) return
        draggingRef.current = pending.id
        setDraggingId(pending.id)
        setOptimistic(orderRef.current)
      }

      const dragging = draggingRef.current
      const node = (document.elementFromPoint(event.clientX, event.clientY) as Element | null)
        ?.closest(`[${attr}]`)
      const overId = node?.getAttribute(attr)
      if (!overId || overId === dragging) return
      setOptimistic((prev) => {
        const arr = [...(prev ?? orderRef.current)]
        const from = arr.indexOf(dragging)
        const to = arr.indexOf(overId)
        if (from < 0 || to < 0) return prev
        arr.splice(from, 1)
        arr.splice(to, 0, dragging)
        orderRef.current = arr
        return arr
      })
    }
    const onUp = () => finish()

    window.addEventListener('pointermove', onMove)
    window.addEventListener('pointerup', onUp)
    window.addEventListener('pointercancel', onUp)
    return () => {
      window.removeEventListener('pointermove', onMove)
      window.removeEventListener('pointerup', onUp)
      window.removeEventListener('pointercancel', onUp)
    }
  }, [finish, attr])

  const start = useCallback((id: string, event: React.PointerEvent) => {
    // Left button / touch / pen only; ignore right-clicks. Record the press; the
    // move handler decides whether it becomes a drag.
    if (event.button !== 0) return
    suppressClickRef.current = false
    pendingRef.current = { id, x: event.clientX, y: event.clientY }
  }, [])

  const wasDragged = useCallback(() => {
    const dragged = suppressClickRef.current
    suppressClickRef.current = false
    return dragged
  }, [])

  return { order, draggingId, start, wasDragged }
}
