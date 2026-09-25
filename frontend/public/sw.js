/*
 * Stashboard service worker — hand-rolled, minimal (V10.6).
 *
 * Goals: make the app installable and let the login/dashboard *chrome* load
 * offline. There is NO offline data sync — API calls still need the network.
 *
 * Cache strategy:
 *   - Navigations  → network-first, falling back to the cached app shell so an
 *     offline launch still boots React.
 *   - /assets/*    → cache-first: Vite fingerprints every bundle, so a hashed
 *     file is immutable and safe to serve from cache forever.
 *   - /api, /uploads, WebSockets and cross-origin → never touched by the SW.
 *
 * Updates are silent: a new release ships a fresh index.html (served no-cache)
 * that references new hashed bundles; bumping VERSION drops every stale cache on
 * activate, and skipWaiting + clients.claim hand control to the new SW at once.
 */
const VERSION = 'v1'
const SHELL_CACHE = `stashboard-shell-${VERSION}`
const ASSET_CACHE = `stashboard-assets-${VERSION}`

self.addEventListener('install', (event) => {
  event.waitUntil(
    caches.open(SHELL_CACHE)
      .then((cache) => cache.add('/'))
      .then(() => self.skipWaiting()),
  )
})

self.addEventListener('activate', (event) => {
  event.waitUntil((async () => {
    const keys = await caches.keys()
    await Promise.all(
      keys
        .filter((key) => key.startsWith('stashboard-') && !key.endsWith(`-${VERSION}`))
        .map((key) => caches.delete(key)),
    )
    await self.clients.claim()
  })())
})

self.addEventListener('fetch', (event) => {
  const request = event.request
  if (request.method !== 'GET') return

  const url = new URL(request.url)
  if (url.origin !== self.location.origin) return
  // Never cache or intercept live data / uploads / WebSocket upgrades.
  if (url.pathname.startsWith('/api') || url.pathname.startsWith('/uploads')) return

  // App-shell navigations: try the network, fall back to the cached shell.
  if (request.mode === 'navigate') {
    event.respondWith((async () => {
      try {
        return await fetch(request)
      } catch {
        const cache = await caches.open(SHELL_CACHE)
        const cached = await cache.match('/')
        return cached ?? Response.error()
      }
    })())
    return
  }

  // Fingerprinted, immutable bundles: serve from cache, populate on first hit.
  if (url.pathname.startsWith('/assets/')) {
    event.respondWith((async () => {
      const cache = await caches.open(ASSET_CACHE)
      const hit = await cache.match(request)
      if (hit) return hit
      const response = await fetch(request)
      if (response.ok) cache.put(request, response.clone())
      return response
    })())
  }
})

// ── Web push (V10.6) ───────────────────────────────────────────────────────
// The backend sends a JSON payload { title, body, url, tag }. Show it as an OS
// notification; a click focuses an existing tab (deep-linking via ?service=…)
// or opens a new one.
self.addEventListener('push', (event) => {
  let payload = {}
  try {
    payload = event.data ? event.data.json() : {}
  } catch {
    payload = { title: 'Stashboard', body: event.data ? event.data.text() : '' }
  }

  const title = payload.title || 'Stashboard'
  const options = {
    body: payload.body || '',
    icon: '/icons/icon-192.png',
    badge: '/icons/icon-192.png',
    tag: payload.tag || undefined,
    data: { url: payload.url || '/' },
  }
  event.waitUntil(self.registration.showNotification(title, options))
})

self.addEventListener('notificationclick', (event) => {
  event.notification.close()
  const target = (event.notification.data && event.notification.data.url) || '/'
  event.waitUntil((async () => {
    const clientList = await self.clients.matchAll({ type: 'window', includeUncontrolled: true })
    for (const client of clientList) {
      if ('focus' in client) {
        client.navigate(target).catch(() => {})
        return client.focus()
      }
    }
    if (self.clients.openWindow) return self.clients.openWindow(target)
  })())
})
