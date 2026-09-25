import { api } from './api'
import type { PushDevice } from './types'

/**
 * V10.6 — web-push subscription flow. Service workers and the Push API only work
 * in a secure context (https:// or http://localhost); on plain http://<LAN-IP> the
 * browser exposes neither, so `isPushSupported` is false and the UI degrades.
 */
export function isPushSupported(): boolean {
  return (
    window.isSecureContext &&
    'serviceWorker' in navigator &&
    'PushManager' in window &&
    'Notification' in window
  )
}

/** Decodes a base64url VAPID key into the Uint8Array `pushManager.subscribe` wants. */
function urlBase64ToUint8Array(base64: string): Uint8Array<ArrayBuffer> {
  const padding = '='.repeat((4 - (base64.length % 4)) % 4)
  const normalized = (base64 + padding).replace(/-/g, '+').replace(/_/g, '/')
  const raw = atob(normalized)
  const output = new Uint8Array(new ArrayBuffer(raw.length))
  for (let i = 0; i < raw.length; i++) output[i] = raw.charCodeAt(i)
  return output
}

export const pushApi = {
  listDevices: () => api.get<PushDevice[]>('/api/push/subscriptions').then((r) => r.data),
  deleteDevice: (id: string) => api.delete(`/api/push/subscriptions/${id}`),
  sendTest: () =>
    api.post<{ attempted: number; delivered: number; pruned: number }>('/api/push/test').then((r) => r.data),

  /**
   * Subscribes the current device: requests notification permission, subscribes via
   * the registered service worker using the server's VAPID public key, and stores the
   * subscription server-side. Returns 'granted' | 'denied' | 'unsupported'.
   */
  async subscribeThisDevice(): Promise<'granted' | 'denied' | 'unsupported'> {
    if (!isPushSupported()) return 'unsupported'

    const permission = await Notification.requestPermission()
    if (permission !== 'granted') return 'denied'

    const registration = await navigator.serviceWorker.ready
    const { publicKey } = await api.get<{ publicKey: string }>('/api/push/vapid-public-key').then((r) => r.data)

    const subscription = await registration.pushManager.subscribe({
      userVisibleOnly: true,
      applicationServerKey: urlBase64ToUint8Array(publicKey),
    })

    const json = subscription.toJSON()
    await api.post('/api/push/subscriptions', {
      endpoint: json.endpoint,
      keys: { p256dh: json.keys?.p256dh, auth: json.keys?.auth },
    })
    return 'granted'
  },
}
