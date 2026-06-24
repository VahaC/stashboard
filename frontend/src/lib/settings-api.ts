import { api } from './api'
import type { ContainerExecSettings, HealthCheckSettings, HostShellSettings, ImagePruneSettings, ProxmoxCloneSettings, ProxmoxConsoleSettings, ProxmoxCreateSettings, ProxmoxDestroySettings, ProxmoxRestoreSettings, ProxmoxUpdateApplySettings } from './types'

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
  /** V6.6 — global LXC-console toggle. */
  getProxmoxConsoleSettings: () =>
    api.get<ProxmoxConsoleSettings>('/api/settings/proxmox-console').then((r) => r.data),
  updateProxmoxConsoleSettings: (settings: ProxmoxConsoleSettings) =>
    api.put('/api/settings/proxmox-console', settings),
  /** V6.7.1 — global Proxmox "Update now" toggle. */
  getProxmoxUpdatesSettings: () =>
    api.get<ProxmoxUpdateApplySettings>('/api/settings/proxmox-updates').then((r) => r.data),
  updateProxmoxUpdatesSettings: (settings: ProxmoxUpdateApplySettings) =>
    api.put('/api/settings/proxmox-updates', settings),
  /** V6.13 — global destroy-LXC toggle. */
  getProxmoxDestroySettings: () =>
    api.get<ProxmoxDestroySettings>('/api/settings/proxmox-destroy').then((r) => r.data),
  updateProxmoxDestroySettings: (settings: ProxmoxDestroySettings) =>
    api.put('/api/settings/proxmox-destroy', settings),
  /** V6.13.1 — global create-LXC toggle. */
  getProxmoxCreateSettings: () =>
    api.get<ProxmoxCreateSettings>('/api/settings/proxmox-create').then((r) => r.data),
  updateProxmoxCreateSettings: (settings: ProxmoxCreateSettings) =>
    api.put('/api/settings/proxmox-create', settings),
  /** V8.0 — global clone/snapshot toggle. */
  getProxmoxCloneSettings: () =>
    api.get<ProxmoxCloneSettings>('/api/settings/proxmox-clone').then((r) => r.data),
  updateProxmoxCloneSettings: (settings: ProxmoxCloneSettings) =>
    api.put('/api/settings/proxmox-clone', settings),
  /** V8.1 — global restore-LXC toggle. */
  getProxmoxRestoreSettings: () =>
    api.get<ProxmoxRestoreSettings>('/api/settings/proxmox-restore').then((r) => r.data),
  updateProxmoxRestoreSettings: (settings: ProxmoxRestoreSettings) =>
    api.put('/api/settings/proxmox-restore', settings),
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
