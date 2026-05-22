import { useEffect } from 'react'
import { accountApi } from './account-api'
import { useAuthStore } from './auth-store'
import { applyTheme, useThemeStore, type Theme } from './theme-store'

/**
 * Keeps `<html class="dark">` in sync with the current preference and, when the
 * preference is `system`, also reacts to the OS toggling between light/dark.
 *
 * Mount once near the top of the app (e.g. inside the QueryClientProvider).
 */
export function useThemeSync() {
  const theme = useThemeStore((s) => s.theme)

  useEffect(() => {
    applyTheme(theme)

    if (theme !== 'system') return
    const mq = window.matchMedia('(prefers-color-scheme: dark)')
    const handler = () => applyTheme('system')
    mq.addEventListener('change', handler)
    return () => mq.removeEventListener('change', handler)
  }, [theme])
}

/** Convenience: read + write theme in one shot. */
export function useTheme(): [Theme, (t: Theme) => void] {
  const theme = useThemeStore((s) => s.theme)
  const setTheme = useThemeStore((s) => s.setTheme)
  return [theme, setTheme]
}

/**
 * Fetches the user's profile every time we transition from logged-out → logged-in
 * and reconciles the server's theme into the local store. Server is the source of
 * truth across devices, so adopting it on sign-in keeps the experience consistent.
 *
 * Intentionally silent on failure — if the request 401s or the API is briefly down,
 * the local cache (or the system default) is fine.
 */
export function useServerThemeSync() {
  const accessToken = useAuthStore((s) => s.accessToken)
  const setLocal = useThemeStore((s) => s.setTheme)

  useEffect(() => {
    if (!accessToken) return
    accountApi.getProfile()
      .then((p) => {
        if (p.theme === 'system' || p.theme === 'light' || p.theme === 'dark') {
          setLocal(p.theme as Theme)
        }
      })
      .catch(() => undefined)
  }, [accessToken, setLocal])
}
