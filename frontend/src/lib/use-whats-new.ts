import { useEffect, useState } from 'react'

const STORAGE_KEY = 'stashboard:whatsNewSeenVersion'
const CURRENT = __APP_VERSION__

function readSeen(): string | null {
  try {
    return localStorage.getItem(STORAGE_KEY)
  } catch {
    return null
  }
}

function writeSeen() {
  try {
    localStorage.setItem(STORAGE_KEY, CURRENT)
  } catch { /* storage unavailable */ }
}

/**
 * Drives the "What's new" modal. It opens automatically the first time a browser
 * loads a version whose notes it hasn't shown yet — including a brand-new user's
 * very first visit — and only once per release. The link in the sidebar can
 * re-open it any time via `setOpen`.
 */
export function useWhatsNew() {
  // Pure read in the initializer (StrictMode-safe): decided before the effect
  // below persists the current version, so it survives a double-invoke.
  const [open, setOpen] = useState(() => readSeen() !== CURRENT)

  // Mark this version seen once mounted, so the auto-open fires only once.
  useEffect(() => {
    writeSeen()
  }, [])

  return { open, setOpen }
}
