/**
 * Registers the service worker that makes Stashboard an installable PWA (V10.6).
 *
 * Only runs in a production build and in a secure context — service workers (and
 * therefore install + web push) require https:// or http://localhost. On plain
 * http://<LAN-IP> the browser silently refuses to register, so we skip quietly;
 * the Settings UI surfaces the HTTPS requirement to the user separately.
 */
export function registerServiceWorker(): void {
  if (!import.meta.env.PROD) return
  if (!('serviceWorker' in navigator)) return
  if (!window.isSecureContext) return

  window.addEventListener('load', () => {
    navigator.serviceWorker.register('/sw.js').catch(() => {
      // Registration failures are non-fatal — the app works without the SW.
    })
  })
}
