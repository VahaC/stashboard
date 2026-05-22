import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query'
import { api } from './api'
import { accountApi } from './account-api'
import type {
  Category,
  DockerConnection,
  DockerConnectionPingRequest,
  DockerConnectionPingResponse,
  DockerConnectionUpsert,
  DockerContainerActionResponse,
  DockerContainerCard,
  DockerContainerInfo,
  DockerContainerInspect,
  DockerUpdateAttempt,
  DockerUpdateCommandResponse,
  DockerWatch,
  DockerWatchTestRequest,
  DockerWatchTestResponse,
  DockerWatchUpdateResponse,
  DockerWatchUpsert,
  Service,
  ServiceUpsert,
  StashboardFeatures,
  Tag,
  TelegramSettings,
} from './types'

export const qk = {
  services: ['services'] as const,
  service: (id: string) => ['services', id] as const,
  categories: ['categories'] as const,
  tags: ['tags'] as const,
  dockerConnections: ['docker', 'connections'] as const,
  dockerConnection: (id: string) => ['docker', 'connections', id] as const,
  dockerContainers: (connectionId: string) => ['docker', 'connections', connectionId, 'containers'] as const,
  dockerUpdateCommand: (connectionId: string, container: string) =>
    ['docker', 'connections', connectionId, 'update-command', container] as const,
  dockerWatches: (serviceId: string) => ['services', serviceId, 'docker', 'watches'] as const,
  connectionWatches: (connectionId: string) =>
    ['docker', 'connections', connectionId, 'watches'] as const,
  connectionWatchUpdates: (connectionId: string, watchId: string) =>
    ['docker', 'connections', connectionId, 'watches', watchId, 'updates'] as const,
  dockerWatchUpdates: (serviceId: string, watchId: string) =>
    ['services', serviceId, 'docker', 'watches', watchId, 'updates'] as const,
  dockerWatchInspect: (serviceId: string, watchId: string) =>
    ['services', serviceId, 'docker', 'watches', watchId, 'inspect'] as const,
  dockerInstanceContainers: (connectionId: string) =>
    ['docker', 'connections', connectionId, 'instance', 'containers'] as const,
  dockerInstanceInspect: (connectionId: string, containerName: string) =>
    ['docker', 'connections', connectionId, 'instance', 'containers', containerName, 'inspect'] as const,
  features: ['features'] as const,
  telegramSettings: ['account', 'telegram'] as const,
}

export const useServices = () =>
  useQuery({
    queryKey: qk.services,
    queryFn: async () => (await api.get<Service[]>('/api/services')).data,
    refetchInterval: 30_000,
  })

export const useCategories = () =>
  useQuery({ queryKey: qk.categories, queryFn: async () => (await api.get<Category[]>('/api/categories')).data })

export const useTags = () =>
  useQuery({ queryKey: qk.tags, queryFn: async () => (await api.get<Tag[]>('/api/tags')).data })

export const useUpsertService = () => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async (args: { id?: string; data: ServiceUpsert }) => {
      const { id, data } = args
      const resp = id
        ? await api.put<Service>(`/api/services/${id}`, data)
        : await api.post<Service>('/api/services', data)
      return resp.data
    },
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: qk.services })
      qc.invalidateQueries({ queryKey: qk.categories })
      qc.invalidateQueries({ queryKey: qk.tags })
    },
  })
}

export const useDeleteService = () => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async (id: string) => api.delete(`/api/services/${id}`),
    onSuccess: () => qc.invalidateQueries({ queryKey: qk.services }),
  })
}

export const useCheckNow = () => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async (id: string) => (await api.post<Service>(`/api/services/${id}/check`)).data,
    onSuccess: (updated) => {
      qc.setQueryData<Service[]>(qk.services, (prev) =>
        prev ? prev.map((s) => (s.id === updated.id ? updated : s)) : prev
      )
    },
  })
}

export const useUploadLogo = () => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async ({ id, file }: { id: string; file: File }) => {
      const fd = new FormData()
      fd.append('file', file)
      const resp = await api.post<{ path: string }>(`/api/services/${id}/logo`, fd)
      return resp.data.path
    },
    onSuccess: () => qc.invalidateQueries({ queryKey: qk.services }),
  })
}

export const useRefreshFavicon = () => {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: async (id: string) => (await api.post<Service>(`/api/services/${id}/favicon/refresh`)).data,
    onSuccess: (updatedService) => {
      queryClient.setQueryData<Service[]>(qk.services, (previousServices) =>
        previousServices ? previousServices.map((service) => (service.id === updatedService.id ? updatedService : service)) : previousServices
      )
    },
  })
}

export const useUpsertCategory = () => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async (args: { id?: string; data: { name: string; color: string } }) => {
      const { id, data } = args
      return id
        ? (await api.put(`/api/categories/${id}`, data)).data
        : (await api.post('/api/categories', data)).data
    },
    onSuccess: () => qc.invalidateQueries({ queryKey: qk.categories }),
  })
}

export const useDeleteCategory = () => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async (id: string) => api.delete(`/api/categories/${id}`),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: qk.categories })
      qc.invalidateQueries({ queryKey: qk.services })
    },
  })
}

export const useCreateTag = () => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async (name: string) => (await api.post('/api/tags', { name })).data,
    onSuccess: () => qc.invalidateQueries({ queryKey: qk.tags }),
  })
}

export const useDeleteTag = () => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async (id: string) => api.delete(`/api/tags/${id}`),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: qk.tags })
      qc.invalidateQueries({ queryKey: qk.services })
    },
  })
}

// ── Docker connection (user-scoped, shared across services) ────────────────

/** Lists all of the current user's Docker connections. Powers the assignment
 *  dropdown on services and the management UI. */
export const useDockerConnections = () =>
  useQuery({
    queryKey: qk.dockerConnections,
    queryFn: async (): Promise<DockerConnection[]> =>
      (await api.get<DockerConnection[]>('/api/docker/connections')).data,
  })

export const useDockerConnection = (id: string | null) =>
  useQuery({
    queryKey: id ? qk.dockerConnection(id) : ['docker-connection-disabled'],
    enabled: id !== null,
    queryFn: async (): Promise<DockerConnection> =>
      (await api.get<DockerConnection>(`/api/docker/connections/${id}`)).data,
  })

export const useCreateDockerConnection = () => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async (data: DockerConnectionUpsert) =>
      (await api.post<DockerConnection>('/api/docker/connections', data)).data,
    onSuccess: () => qc.invalidateQueries({ queryKey: qk.dockerConnections }),
  })
}

export const useUpdateDockerConnection = () => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async (args: { id: string; data: DockerConnectionUpsert }) =>
      (await api.put<DockerConnection>(`/api/docker/connections/${args.id}`, args.data)).data,
    onSuccess: (conn) => {
      qc.setQueryData<DockerConnection>(qk.dockerConnection(conn.id), conn)
      qc.invalidateQueries({ queryKey: qk.dockerConnections })
      qc.invalidateQueries({ queryKey: qk.dockerContainers(conn.id) })
    },
  })
}

export const useDeleteDockerConnection = () => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async (id: string) => api.delete(`/api/docker/connections/${id}`),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: qk.dockerConnections })
      qc.invalidateQueries({ queryKey: qk.services })
    },
  })
}

export const useTestDockerConnectionPing = () =>
  useMutation({
    /** Pass `connectionId` to let "Keep" actions resolve against the saved
     *  TLS of an existing connection (used by the edit form). */
    mutationFn: async (args: { data: DockerConnectionPingRequest; connectionId?: string }) => {
      const url = args.connectionId
        ? `/api/docker/connections/test?connectionId=${args.connectionId}`
        : '/api/docker/connections/test'
      return (await api.post<DockerConnectionPingResponse>(url, args.data)).data
    },
  })

/** Lists containers visible to a specific Docker connection. */
export const useDockerContainers = (connectionId: string | null) =>
  useQuery({
    queryKey: connectionId ? qk.dockerContainers(connectionId) : ['docker-containers-disabled'],
    enabled: connectionId !== null,
    queryFn: async (): Promise<DockerContainerInfo[]> =>
      (await api.get<DockerContainerInfo[]>(`/api/docker/connections/${connectionId}/containers`)).data,
    staleTime: 30_000,
  })

/** Fetches the generated update command for a specific container on a
 *  connection. Lazy so we only hit the daemon when the user opens it. */
export const useDockerUpdateCommand = (connectionId: string | null, containerName: string | null) =>
  useQuery({
    queryKey: connectionId && containerName
      ? qk.dockerUpdateCommand(connectionId, containerName)
      : ['docker-update-command-disabled'],
    enabled: connectionId !== null && containerName !== null && containerName !== '',
    queryFn: async (): Promise<DockerUpdateCommandResponse> =>
      (await api.get<DockerUpdateCommandResponse>(
        `/api/docker/connections/${connectionId}/containers/${encodeURIComponent(containerName!)}/update-command`)).data,
    staleTime: 60_000,
  })

// ── Docker watch hooks ─────────────────────────────────────────────────────

/**
 * Loads the list of Docker watches attached to a service. Returns an empty
 * array when the service has none configured. Multi-watch since the service
 * may track e.g. both an app container and a database container.
 */
export const useDockerWatches = (serviceId: string | null) =>
  useQuery({
    queryKey: serviceId ? qk.dockerWatches(serviceId) : ['docker-watches-disabled'],
    enabled: serviceId !== null,
    queryFn: async (): Promise<DockerWatch[]> =>
      (await api.get<DockerWatch[]>(`/api/services/${serviceId}/docker/watches`)).data,
  })

export const useCreateDockerWatch = (serviceId: string) => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async (data: DockerWatchUpsert) =>
      (await api.post<DockerWatch>(`/api/services/${serviceId}/docker/watches`, data)).data,
    onSuccess: (watch) => {
      qc.setQueryData<DockerWatch[]>(qk.dockerWatches(serviceId),
        (prev) => prev ? [...prev, watch] : [watch])
      qc.invalidateQueries({ queryKey: qk.services })
    },
  })
}

export const useUpdateDockerWatch = (serviceId: string) => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async (args: { watchId: string; data: DockerWatchUpsert }) =>
      (await api.put<DockerWatch>(`/api/services/${serviceId}/docker/watches/${args.watchId}`, args.data)).data,
    onSuccess: (watch) => {
      qc.setQueryData<DockerWatch[]>(qk.dockerWatches(serviceId),
        (prev) => prev ? prev.map((w) => w.id === watch.id ? watch : w) : prev)
      qc.invalidateQueries({ queryKey: qk.services })
    },
  })
}

export const useDeleteDockerWatch = (serviceId: string) => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async (watchId: string) =>
      api.delete(`/api/services/${serviceId}/docker/watches/${watchId}`),
    onSuccess: (_, watchId) => {
      qc.setQueryData<DockerWatch[]>(qk.dockerWatches(serviceId),
        (prev) => prev ? prev.filter((w) => w.id !== watchId) : prev)
      qc.invalidateQueries({ queryKey: qk.services })
    },
  })
}

export const useCheckDockerNow = (serviceId: string) => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async (watchId: string) =>
      (await api.post<DockerWatch>(`/api/services/${serviceId}/docker/watches/${watchId}/check`)).data,
    onSuccess: (watch) => {
      qc.setQueryData<DockerWatch[]>(qk.dockerWatches(serviceId),
        (prev) => prev ? prev.map((w) => w.id === watch.id ? watch : w) : prev)
      qc.invalidateQueries({ queryKey: qk.services })
    },
  })
}

export const useTestDockerWatch = (serviceId: string) =>
  useMutation({
    /** Pass `watchId` to let "Keep" actions resolve against the saved
     *  registry credentials of an existing watch. */
    mutationFn: async (args: { data: DockerWatchTestRequest; watchId?: string }) => {
      const url = args.watchId
        ? `/api/services/${serviceId}/docker/watches/test?watchId=${args.watchId}`
        : `/api/services/${serviceId}/docker/watches/test`
      return (await api.post<DockerWatchTestResponse>(url, args.data)).data
    },
  })

/** V2.6 — generate or rotate the per-watch webhook token. Same endpoint for
 *  both — calling it again invalidates the previous URL. */
export const useRotateDockerWebhook = (serviceId: string) => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async (watchId: string) =>
      (await api.post<DockerWatch>(`/api/services/${serviceId}/docker/watches/${watchId}/webhook/rotate`)).data,
    onSuccess: (watch) => {
      qc.setQueryData<DockerWatch[]>(qk.dockerWatches(serviceId),
        (prev) => prev ? prev.map((w) => w.id === watch.id ? watch : w) : prev)
    },
  })
}

/** V2.7 — one-click "Update now". Pulls the latest image and recreates
 *  the container; the response carries both the audit-log row that was
 *  written and the refreshed watch (with status flipped back to
 *  `UpToDate` on a successful recreate + re-check). */
export const useUpdateDockerNow = (serviceId: string) => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async (watchId: string) =>
      (await api.post<DockerWatchUpdateResponse>(
        `/api/services/${serviceId}/docker/watches/${watchId}/update`)).data,
    onSuccess: ({ attempt, watch }) => {
      qc.setQueryData<DockerWatch[]>(qk.dockerWatches(serviceId),
        (prev) => prev ? prev.map((w) => w.id === watch.id ? watch : w) : prev)
      // Prepend the new attempt so the "Update history" panel updates
      // immediately without an extra round-trip.
      qc.setQueryData<DockerUpdateAttempt[]>(qk.dockerWatchUpdates(serviceId, watch.id),
        (prev) => prev ? [attempt, ...prev] : [attempt])
      qc.invalidateQueries({ queryKey: qk.services })
    },
  })
}

/**
 * V3.1 — fetches a slimmed snapshot of the container's `docker inspect`
 * payload for the "Inspect" tab. Lazy: pass `enabled = false` to keep the
 * query dormant while the panel is collapsed so we don't hit the Docker
 * daemon for every modal open.
 */
export const useDockerWatchInspect = (serviceId: string, watchId: string | null, enabled = true) =>
  useQuery({
    queryKey: watchId ? qk.dockerWatchInspect(serviceId, watchId) : ['docker-watch-inspect-disabled'],
    enabled: enabled && Boolean(watchId),
    queryFn: async (): Promise<DockerContainerInspect> =>
      (await api.get<DockerContainerInspect>(
        `/api/services/${serviceId}/docker/watches/${watchId}/inspect`)).data,
    // The inspect payload can shift on every container restart; refresh on
    // panel re-open rather than caching indefinitely, but keep a 10-second
    // floor so flipping tabs back-and-forth isn't expensive.
    staleTime: 10_000,
  })

/** V2.7 — per-watch "Update history" — newest first, capped at 50 rows. */
export const useDockerWatchUpdates = (serviceId: string, watchId: string | null, enabled = true) =>
  useQuery({
    queryKey: watchId ? qk.dockerWatchUpdates(serviceId, watchId) : ['docker-watch-updates-disabled'],
    enabled: enabled && Boolean(watchId),
    queryFn: async (): Promise<DockerUpdateAttempt[]> =>
      (await api.get<DockerUpdateAttempt[]>(
        `/api/services/${serviceId}/docker/watches/${watchId}/updates`)).data,
  })

/** V2.6 — clear the per-watch webhook token. Disables webhook delivery
 *  for the watch; schedule-driven checks continue. */
export const useDeleteDockerWebhook = (serviceId: string) => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async (watchId: string) =>
      (await api.delete<DockerWatch>(`/api/services/${serviceId}/docker/watches/${watchId}/webhook`)).data,
    onSuccess: (watch) => {
      qc.setQueryData<DockerWatch[]>(qk.dockerWatches(serviceId),
        (prev) => prev ? prev.map((w) => w.id === watch.id ? watch : w) : prev)
    },
  })
}

/**
 * Loads the current user's Telegram settings — used by the Docker watch form
 * to decide whether the "Notify via Telegram" toggle should be available or
 * disabled with a hint pointing at the Account page.
 */
export const useTelegramSettings = () =>
  useQuery({
    queryKey: qk.telegramSettings,
    queryFn: async (): Promise<TelegramSettings> => accountApi.getTelegramSettings(),
    // Keep the cache around for a couple of minutes so multiple Docker
    // sections opened in quick succession don't refetch.
    staleTime: 2 * 60_000,
  })

// ── V3.5 — Docker instances page ─────────────────────────────────────────

/** Server-side feature flags. Loaded once per session; the Docker
 *  instances page uses this to gate the Remove container button. */
export const useFeatures = () =>
  useQuery({
    queryKey: qk.features,
    queryFn: async (): Promise<StashboardFeatures> =>
      (await api.get<StashboardFeatures>('/api/features')).data,
    staleTime: 30 * 60_000,
  })

/** V3.5 — list every container on the connected daemon plus the
 *  user's matching watch (if any). Auto-refreshes on a 10 s interval
 *  so the page reflects state changes (uptime, status, new containers)
 *  without the user having to hit Refresh. */
export const useDockerInstanceContainers = (connectionId: string | null) =>
  useQuery({
    queryKey: connectionId
      ? qk.dockerInstanceContainers(connectionId)
      : ['docker-instance-containers-disabled'],
    enabled: Boolean(connectionId),
    queryFn: async (): Promise<DockerContainerCard[]> =>
      (await api.get<DockerContainerCard[]>(
        `/api/docker/connections/${connectionId}/instance/containers`)).data,
    refetchInterval: 10_000,
    staleTime: 5_000,
  })

/** V3.5 — instance-scoped Inspect query. Mirrors `useDockerWatchInspect`
 *  but addresses the container by `(connectionId, containerName)` so the
 *  V3.5 container modal works for un-tracked containers too. Lazy. */
export const useDockerInstanceInspect = (
  connectionId: string | null,
  containerName: string | null,
  enabled = true,
) =>
  useQuery({
    queryKey: connectionId && containerName
      ? qk.dockerInstanceInspect(connectionId, containerName)
      : ['docker-instance-inspect-disabled'],
    enabled: enabled && Boolean(connectionId) && Boolean(containerName),
    queryFn: async (): Promise<DockerContainerInspect> =>
      (await api.get<DockerContainerInspect>(
        `/api/docker/connections/${connectionId}/instance/containers/${encodeURIComponent(containerName!)}/inspect`)).data,
    staleTime: 10_000,
  })

// ── V3.6 — connection-scoped Docker watches (containers as standalone) ─────

/** Lists every tracked container (watch) on a connection. Powers the update
 *  badges on the Docker page cards and the modal Watch tab. */
export const useConnectionWatches = (connectionId: string | null) =>
  useQuery({
    queryKey: connectionId ? qk.connectionWatches(connectionId) : ['connection-watches-disabled'],
    enabled: connectionId !== null,
    queryFn: async (): Promise<DockerWatch[]> =>
      (await api.get<DockerWatch[]>(`/api/docker/connections/${connectionId}/watches`)).data,
    // Refresh so the Docker-page update badges reflect the background scan
    // without the user having to reload.
    refetchInterval: 30_000,
  })

const invalidateConnectionWatchCaches = (qc: ReturnType<typeof useQueryClient>, connectionId: string) => {
  void qc.invalidateQueries({ queryKey: qk.connectionWatches(connectionId) })
  void qc.invalidateQueries({ queryKey: qk.dockerInstanceContainers(connectionId) })
  // The linked service's aggregated Docker status / linked-watch summary
  // depends on watches, so refresh the services list too.
  void qc.invalidateQueries({ queryKey: qk.services })
}

export const useCreateConnectionWatch = (connectionId: string) => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async (data: DockerWatchUpsert) =>
      (await api.post<DockerWatch>(`/api/docker/connections/${connectionId}/watches`, data)).data,
    onSuccess: () => invalidateConnectionWatchCaches(qc, connectionId),
  })
}

export const useUpdateConnectionWatch = (connectionId: string) => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async (args: { watchId: string; data: DockerWatchUpsert }) =>
      (await api.put<DockerWatch>(
        `/api/docker/connections/${connectionId}/watches/${args.watchId}`, args.data)).data,
    onSuccess: () => invalidateConnectionWatchCaches(qc, connectionId),
  })
}

export const useUnlinkConnectionWatch = () => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async (args: { connectionId: string; watchId: string }) =>
      (await api.delete<DockerWatch>(
        `/api/docker/connections/${args.connectionId}/watches/${args.watchId}/service-link`)).data,
    onSuccess: () => {
      void qc.invalidateQueries({ queryKey: qk.services })
    },
  })
}

export const useDeleteConnectionWatch = (connectionId: string) => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async (watchId: string) =>
      api.delete(`/api/docker/connections/${connectionId}/watches/${watchId}`),
    onSuccess: () => invalidateConnectionWatchCaches(qc, connectionId),
  })
}

export const useCheckConnectionWatch = (connectionId: string) => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async (watchId: string) =>
      (await api.post<DockerWatch>(`/api/docker/connections/${connectionId}/watches/${watchId}/check`)).data,
    onSuccess: () => invalidateConnectionWatchCaches(qc, connectionId),
  })
}

export const useTestConnectionWatch = (connectionId: string) =>
  useMutation({
    mutationFn: async (args: { data: DockerWatchTestRequest; watchId?: string }) => {
      const url = args.watchId
        ? `/api/docker/connections/${connectionId}/watches/test?watchId=${args.watchId}`
        : `/api/docker/connections/${connectionId}/watches/test`
      return (await api.post<DockerWatchTestResponse>(url, args.data)).data
    },
  })

export const useUpdateConnectionWatchNow = (connectionId: string) => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async (watchId: string) =>
      (await api.post<DockerWatchUpdateResponse>(
        `/api/docker/connections/${connectionId}/watches/${watchId}/update`)).data,
    onSuccess: ({ attempt, watch }) => {
      qc.setQueryData<DockerUpdateAttempt[]>(qk.connectionWatchUpdates(connectionId, watch.id),
        (prev) => prev ? [attempt, ...prev] : [attempt])
      invalidateConnectionWatchCaches(qc, connectionId)
    },
  })
}

export const useRotateConnectionWebhook = (connectionId: string) => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async (watchId: string) =>
      (await api.post<DockerWatch>(
        `/api/docker/connections/${connectionId}/watches/${watchId}/webhook/rotate`)).data,
    onSuccess: () => void qc.invalidateQueries({ queryKey: qk.connectionWatches(connectionId) }),
  })
}

export const useDeleteConnectionWebhook = (connectionId: string) => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async (watchId: string) =>
      (await api.delete<DockerWatch>(
        `/api/docker/connections/${connectionId}/watches/${watchId}/webhook`)).data,
    onSuccess: () => void qc.invalidateQueries({ queryKey: qk.connectionWatches(connectionId) }),
  })
}

/** V3.6 — per-watch update / action history (connection-scoped). */
export const useConnectionWatchUpdates = (connectionId: string, watchId: string | null, enabled = true) =>
  useQuery({
    queryKey: watchId ? qk.connectionWatchUpdates(connectionId, watchId) : ['connection-watch-updates-disabled'],
    enabled: enabled && Boolean(watchId),
    queryFn: async (): Promise<DockerUpdateAttempt[]> =>
      (await api.get<DockerUpdateAttempt[]>(
        `/api/docker/connections/${connectionId}/watches/${watchId}/updates`)).data,
  })

/** V3.5 — per-action lifecycle mutation. The same hook covers
 *  Start / Stop / Restart / Remove; the caller passes the verb. */
export const useDockerContainerAction = (connectionId: string) => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async (args: { containerName: string; action: 'start' | 'stop' | 'restart' | 'remove' }) => {
      const { containerName, action } = args
      const url = `/api/docker/connections/${connectionId}/instance/containers/${encodeURIComponent(containerName)}`
      if (action === 'remove') {
        return (await api.delete<DockerContainerActionResponse>(url)).data
      }
      return (await api.post<DockerContainerActionResponse>(`${url}/${action}`)).data
    },
    onSuccess: () => {
      // Refresh the card list so the new state shows up; the audit
      // attempt itself isn't surfaced on the page yet.
      void qc.invalidateQueries({ queryKey: qk.dockerInstanceContainers(connectionId) })
    },
  })
}
