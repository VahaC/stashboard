import { api } from './api'
import type { ContainerExecSettings, HealthCheckSettings, HostShellSettings, ImagePruneSettings } from './types'

/** V5.3 — app-wide operational settings managed from the Settings page. */
export const settingsApi = {
  getHostShellSettings: () =>
    api.get<HostShellSettings>('/api/settings/host-shell').then((r) => r.data),
  updateHostShellSettings: (settings: HostShellSettings) =>
    api.put('/api/settings/host-shell', settings),
  /** V5.7 — global container-exec toggle. */
  getContainerExecSettings: () =>
    api.get<ContainerExecSettings>('/api/settings/container-exec').then((r) => r.data),
  updateContainerExecSettings: (settings: ContainerExecSettings) =>
    api.put('/api/settings/container-exec', settings),
  /** V5.5 — global image-prune toggle + schedule interval. */
  getImagePruneSettings: () =>
    api.get<ImagePruneSettings>('/api/settings/image-prune').then((r) => r.data),
  updateImagePruneSettings: (settings: ImagePruneSettings) =>
    api.put('/api/settings/image-prune', settings),
  /** V5.6 — offline-alert tuning: failure threshold + in-probe retries. */
  getHealthCheckSettings: () =>
    api.get<HealthCheckSettings>('/api/settings/health-check').then((r) => r.data),
  updateHealthCheckSettings: (settings: HealthCheckSettings) =>
    api.put('/api/settings/health-check', settings),
}
