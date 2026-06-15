import { useLayoutEffect, useRef, useState } from 'react'
import { createPortal } from 'react-dom'

interface FloatingMenuProps {
  pos: { x: number; y: number }
  onClose: () => void
  children: React.ReactNode
}

/**
 * Context-menu that appears at `pos` and stays fully within the viewport.
 * Renders a transparent overlay behind itself so clicking anywhere outside
 * (including the trigger button) fires onClose.
 */
export function FloatingMenu({ pos, onClose, children }: FloatingMenuProps) {
  const ref = useRef<HTMLDivElement>(null)
  const [adj, setAdj] = useState(pos)

  useLayoutEffect(() => {
    const el = ref.current
    if (!el) return
    const { width, height } = el.getBoundingClientRect()
    setAdj({
      x: pos.x + width > window.innerWidth ? pos.x - width : pos.x,
      y: pos.y + height > window.innerHeight ? pos.y - height : pos.y,
    })
  }, [pos.x, pos.y])

  return createPortal(
    <>
      <div style={{ position: 'fixed', inset: 0, zIndex: 49 }} onMouseDown={onClose} />
      <div
        ref={ref}
        className="cgroup-menu"
        style={{ left: adj.x, top: adj.y }}
        onClick={(e) => e.stopPropagation()}
      >
        {children}
      </div>
    </>,
    document.body
  )
}
