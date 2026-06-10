import { useEffect, useState } from 'react'

/**
 * Re-renders the calling component on a fixed interval and returns the current
 * epoch-ms. Used to make "Refreshed Xs ago" labels actually tick between data
 * polls (otherwise they freeze at the value computed on the last render). The
 * timer is local to the component, so only that component re-renders.
 */
export function useNowTick(intervalMs = 1000): number {
  const [now, setNow] = useState(() => Date.now())
  useEffect(() => {
    const id = window.setInterval(() => setNow(Date.now()), intervalMs)
    return () => window.clearInterval(id)
  }, [intervalMs])
  return now
}
