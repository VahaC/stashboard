import { api } from './api'
import type { HostShellSettings } from './types'

/** V5.3 — app-wide operational settings managed from the Settings page. */
export const settingsApi = {
  getHostShellSettings: () =>
    api.get<HostShellSettings>('/api/settings/host-shell').then((r) => r.data),
  updateHostShellSettings: (settings: HostShellSettings) =>
    api.put('/api/settings/host-shell', settings),
}
