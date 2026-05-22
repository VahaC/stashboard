import { create } from 'zustand'
import { persist } from 'zustand/middleware'

export type Theme = 'system' | 'light' | 'dark'

interface ThemeState {
  /** User-selected preference. `system` follows the OS via prefers-color-scheme. */
  theme: Theme
  /** Whether the syncing-to-server step is currently in flight. */
  setTheme: (t: Theme) => void
}

/**
 * Local cache of the user's theme preference. Source of truth lives on the server
 * (`UserEntity.Theme`) once authenticated, but this store keeps the UI snappy and
 * works even before login (system default is the obvious choice).
 *
 * Persisted to localStorage so the very-first paint after refresh has the right
 * theme — no flash of wrong-theme content.
 */
export const useThemeStore = create<ThemeState>()(
  persist(
    (set) => ({
      theme: 'system',
      setTheme: (t) => set({ theme: t }),
    }),
    { name: 'stashboard-theme' }
  )
)

/** Resolve the effective scheme ('light' | 'dark') for a given preference. */
export function resolveScheme(theme: Theme): 'light' | 'dark' {
  if (theme === 'system') {
    return window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light'
  }
  return theme
}

/**
 * Mutate <html class="dark"> to match the given preference.
 * Idempotent — safe to call on every render or media-query change.
 */
export function applyTheme(theme: Theme) {
  const scheme = resolveScheme(theme)
  document.documentElement.classList.toggle('dark', scheme === 'dark')
}
