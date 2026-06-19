import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query'
import { api } from './api'
import { accountApi } from './account-api'
import { readFileAsDataUrl } from './utils'
import type {
  Category,
  ComposeAllocation,
  ComposeApplyResponse,
  ComposeFile,
  ComposeFileDiff,
  ComposeFileSaveResponse,
  ComposeHistoryEntry,
  ComposeHistoryFile,
  ComposeImageTags,
  ComposeHostNetwork,
  ComposeProject,
  ComposeProjectCreateRequest,
  ComposeProjectCreateResponse,
  ComposeResourceEditRequest,
  ComposeResourceEditResponse,
  ComposeResourceKind,
  ComposeRestoreResponse,
  ComposeServiceCreateRequest,
  ComposeServiceEditRequest,
  ComposeServiceEditResponse,
  ComposeUpResponse,
  ComposeVolumeUsage,
  DockerConnection,
  DockerConnectionPingRequest,
  DockerConnectionPingResponse,
  DockerConnectionUpsert,
  DockerContainerActionResponse,
  DockerContainerCard,
  DockerContainerInfo,
  DockerContainerInspect,
  DockerImageStorage,
  DockerPruneRun,
  DockerProjectUpdateResponse,
  DockerUpdateAttempt,
  DockerUpdateCommandResponse,
  DockerWatch,
  DockerWatchTestRequest,
  DockerWatchTestResponse,
  DockerWatchUpdateResponse,
  DockerWatchUpsert,
  Service,
  ServiceTemplate,
  ServiceUpsert,
  StashboardFeatures,
  Tag,
  TelegramSettings,
} from './types'

export const qk = {
  services: ['services'] as const,
  service: (id: string) => ['services', id] as const,
  serviceTemplates: ['service-templates'] as const,
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
  /** V5.5 — storage / prune summary for the V3.5 Storage widget. */
  dockerImageStorage: (connectionId: string) =>
    ['docker', 'connections', connectionId, 'instance', 'images', 'storage'] as const,
  /** V7.0 — parsed Compose file for the viewer (project-scoped since V7.1). */
  composeProject: (connectionId: string, project: string) =>
    ['docker', 'connections', connectionId, 'compose', project] as const,
  /** V7.1 — registry tag list for the image dropdown in the edit modal. */
  composeImageTags: (connectionId: string, image: string) =>
    ['docker', 'connections', connectionId, 'compose-image-tags', image] as const,
  /** V7.2 — host capacity already reserved by other containers. */
  composeAllocation: (connectionId: string, project: string) =>
    ['docker', 'connections', connectionId, 'compose-allocation', project] as const,
  /** V7.3 — networks already defined on the host (subnet-overlap warning). */
  composeHostNetworks: (connectionId: string, project: string) =>
    ['docker', 'connections', connectionId, 'compose-host-networks', project] as const,
  /** V7.3 — on-disk usage of the host's named volumes. */
  composeVolumeUsage: (connectionId: string, project: string) =>
    ['docker', 'connections', connectionId, 'compose-volume-usage', project] as const,
  /** V7.4 — the project's raw Compose file text for the "Raw YAML" tab. */
  composeFile: (connectionId: string, project: string) =>
    ['docker', 'connections', connectionId, 'compose-file', project] as const,
  /** V7.6 — kept revisions of the project's Compose file (History tab). */
  composeHistory: (connectionId: string, project: string) =>
    ['docker', 'connections', connectionId, 'compose-history', project] as const,
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

/** Set a custom service logo. The image is sent as a base64 data URI in a JSON
 *  body (read in the browser via FileReader), so it lives purely in the database
 *  and survives backup / restore — mirroring the container / guest icon upload. */
export const useUploadLogo = () => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async ({ id, file }: { id: string; file: File }) => {
      const dataUri = await readFileAsDataUrl(file)
      const resp = await api.post<{ dataUri: string }>(`/api/services/${id}/logo`, { dataUri })
      return resp.data.dataUri
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

/** V7.9 — unlink a Proxmox guest from a service. Also invalidates the Proxmox
 *  connections cache so the guest's "Linked service" dropdown reflects the change. */
export const useRemoveProxmoxLink = (serviceId: string) => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async (target: { proxmoxConnectionId: string; vmId: number }) =>
      (await api.delete<Service>(
        `/api/services/${serviceId}/proxmox-links/${target.proxmoxConnectionId}/${target.vmId}`
      )).data,
    onSuccess: (updated) => {
      qc.setQueryData<Service[]>(qk.services, (prev) =>
        prev ? prev.map((s) => (s.id === updated.id ? updated : s)) : prev
      )
      qc.invalidateQueries({ queryKey: qk.services })
      qc.invalidateQueries({ queryKey: ['proxmox', 'connections'] })
    },
  })
}

/** V7.9 — set a Docker container's "runs on" Proxmox guest cross-link. */
export const useSetContainerProxmoxLink = (connectionId: string) => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async (args: { containerName: string; proxmoxConnectionId: string; vmId: number }) => {
      const url = `/api/docker/connections/${connectionId}/instance/containers/${encodeURIComponent(args.containerName)}/proxmox-link`
      await api.put(url, { proxmoxConnectionId: args.proxmoxConnectionId, vmId: args.vmId })
    },
    onSuccess: () => qc.invalidateQueries({ queryKey: qk.dockerInstanceContainers(connectionId) }),
  })
}

/** V7.9 — clear a Docker container's "runs on" Proxmox guest cross-link. */
export const useRemoveContainerProxmoxLink = (connectionId: string) => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async (containerName: string) => {
      const url = `/api/docker/connections/${connectionId}/instance/containers/${encodeURIComponent(containerName)}/proxmox-link`
      await api.delete(url)
    },
    onSuccess: () => qc.invalidateQueries({ queryKey: qk.dockerInstanceContainers(connectionId) }),
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

/** V7.0 — parsed Compose file of one discovered project, for the V7.1.1
 *  Compose modal (project-scoped since V7.1). */
export const useComposeProject = (connectionId: string | null, project: string | null) =>
  useQuery({
    queryKey: connectionId && project ? qk.composeProject(connectionId, project) : ['compose-project-disabled'],
    enabled: connectionId !== null && project !== null,
    retry: false, // 400 / 404 / 422 are stable config states, not transient errors
    queryFn: async (): Promise<ComposeProject> =>
      (await api.get<ComposeProject>(
        `/api/docker/connections/${connectionId}/compose/${encodeURIComponent(project!)}`)).data,
  })

/** V7.1 — saves one service's edited fields (atomic, compose-validated). On
 *  success the query cache is primed with the re-parsed project the backend
 *  returned, so the page refreshes without a second GET. */
export const useEditComposeService = (connectionId: string, project: string) => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async (args: { serviceName: string; data: ComposeServiceEditRequest }) =>
      (await api.put<ComposeServiceEditResponse>(
        `/api/docker/connections/${connectionId}/compose/${encodeURIComponent(project)}/services/${encodeURIComponent(args.serviceName)}`,
        args.data)).data,
    onSuccess: (response) => {
      qc.setQueryData<ComposeProject>(qk.composeProject(connectionId, project), response.project)
    },
  })
}

/** V7.4 — appends a brand-new service to the project's Compose file (the "Add
 *  service" wizard). Same atomic, compose-validated save path as the editor;
 *  the re-parsed project is primed into the cache on success so the new service
 *  shows up as a normal tab. */
export const useCreateComposeService = (connectionId: string, project: string) => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async (data: ComposeServiceCreateRequest) =>
      (await api.post<ComposeServiceEditResponse>(
        `/api/docker/connections/${connectionId}/compose/${encodeURIComponent(project)}/services`,
        data)).data,
    onSuccess: (response) => {
      qc.setQueryData<ComposeProject>(qk.composeProject(connectionId, project), response.project)
      // The raw-file tab's cached text is now stale.
      qc.invalidateQueries({ queryKey: qk.composeFile(connectionId, project) })
    },
  })
}

/** V7.4.1 — bootstrap a brand-new Compose project from scratch (the "New project"
 *  dialog on the Docker page). On success the container list is invalidated so
 *  the new project's group appears (after `up`) without waiting for a poll. */
export const useCreateComposeProject = (connectionId: string) => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async (data: ComposeProjectCreateRequest): Promise<ComposeProjectCreateResponse> =>
      (await api.post<ComposeProjectCreateResponse>(
        `/api/docker/connections/${connectionId}/compose/create-project`, data)).data,
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: qk.dockerInstanceContainers(connectionId) })
    },
  })
}

/** V7.5 — the starter-recipe catalogue for the "From template" wizard tab. The
 *  catalogue is static per server run, so it is cached aggressively and shared
 *  across connections. */
export const useServiceTemplates = (enabled: boolean) =>
  useQuery({
    queryKey: qk.serviceTemplates,
    queryFn: async (): Promise<ServiceTemplate[]> =>
      (await api.get<ServiceTemplate[]>('/api/templates')).data,
    enabled,
    staleTime: Infinity,
  })

/** V7.4 — the project's raw Compose file text, fetched lazily (enabled) when
 *  the user opens the "Raw YAML" tab. */
export const useComposeFile = (connectionId: string, project: string, enabled: boolean) =>
  useQuery({
    queryKey: qk.composeFile(connectionId, project),
    enabled,
    retry: false,
    queryFn: async (): Promise<ComposeFile> =>
      (await api.get<ComposeFile>(
        `/api/docker/connections/${connectionId}/compose/${encodeURIComponent(project)}/file`)).data,
  })

/** V7.4 — replaces the whole Compose file with hand-edited text (the "Raw YAML"
 *  tab). On success the parsed project + raw file caches are invalidated so the
 *  service tabs and the editor refresh from disk. */
export const useSaveComposeFile = (connectionId: string, project: string) => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async (content: string) =>
      (await api.put<ComposeFileSaveResponse>(
        `/api/docker/connections/${connectionId}/compose/${encodeURIComponent(project)}/file`,
        { content })).data,
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: qk.composeProject(connectionId, project) })
      qc.invalidateQueries({ queryKey: qk.composeFile(connectionId, project) })
    },
  })
}

/** V7.4 — the "Save and run" CTA: `docker compose up -d` against the whole
 *  project (LocalSocket via the in-container CLI, Ssh on the remote host). The
 *  container list is invalidated on success so the freshly-started container's
 *  live state badge appears without waiting for the next poll. */
export const useComposeUp = (connectionId: string, project: string) => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async (): Promise<ComposeUpResponse> =>
      (await api.post<ComposeUpResponse>(
        `/api/docker/connections/${connectionId}/compose/${encodeURIComponent(project)}/up`)).data,
    onSuccess: (result) => {
      if (result.success) qc.invalidateQueries({ queryKey: qk.dockerInstanceContainers(connectionId) })
    },
  })
}

/** V7.6 — pre-save diff + `docker compose config -q` dry-run for the proposed
 *  text. Nothing is written; the result drives the confirm dialog. Also used to
 *  preview a history revision against the current file (pass the revision text). */
export const useComposeFileDiff = (connectionId: string, project: string) =>
  useMutation({
    mutationFn: async (content: string): Promise<ComposeFileDiff> =>
      (await api.post<ComposeFileDiff>(
        `/api/docker/connections/${connectionId}/compose/${encodeURIComponent(project)}/file/diff`,
        { content })).data,
  })

/** V7.6 — "Apply now": recreate the changed services (empty = whole project) so
 *  a just-saved change takes effect. Invalidates the container list on success
 *  so the recreated containers' live state badges refresh. */
export const useApplyComposeServices = (connectionId: string, project: string) => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async (services: string[]): Promise<ComposeApplyResponse> =>
      (await api.post<ComposeApplyResponse>(
        `/api/docker/connections/${connectionId}/compose/${encodeURIComponent(project)}/apply`,
        { services })).data,
    onSuccess: (result) => {
      if (result.success) qc.invalidateQueries({ queryKey: qk.dockerInstanceContainers(connectionId) })
    },
  })
}

/** V7.6 — kept revisions of the project's Compose file (History tab), newest
 *  first. Fetched lazily (enabled) when the tab is opened. */
export const useComposeHistory = (connectionId: string, project: string, enabled: boolean) =>
  useQuery({
    queryKey: qk.composeHistory(connectionId, project),
    enabled,
    retry: false,
    queryFn: async (): Promise<ComposeHistoryEntry[]> =>
      (await api.get<ComposeHistoryEntry[]>(
        `/api/docker/connections/${connectionId}/compose/${encodeURIComponent(project)}/history`)).data,
  })

/** V7.6 — fetches the raw text of one kept revision (for the restore-preview
 *  diff). A bare function, not a hook, since it's pulled on demand per row. */
export const fetchComposeHistoryFile = async (
  connectionId: string, project: string, id: string,
): Promise<ComposeHistoryFile> =>
  (await api.get<ComposeHistoryFile>(
    `/api/docker/connections/${connectionId}/compose/${encodeURIComponent(project)}/history/${encodeURIComponent(id)}`)).data

/** V7.6 — restores a kept revision (re-validated + written like any save). On
 *  success the parsed project + raw file + history caches refresh. */
export const useRestoreComposeHistory = (connectionId: string, project: string) => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async (id: string): Promise<ComposeRestoreResponse> =>
      (await api.post<ComposeRestoreResponse>(
        `/api/docker/connections/${connectionId}/compose/${encodeURIComponent(project)}/history/${encodeURIComponent(id)}/restore`)).data,
    onSuccess: (response) => {
      qc.setQueryData<ComposeProject>(qk.composeProject(connectionId, project), response.project)
      qc.invalidateQueries({ queryKey: qk.composeFile(connectionId, project) })
      qc.invalidateQueries({ queryKey: qk.composeHistory(connectionId, project) })
    },
  })
}

/** V7.3 — adds/replaces (`PUT`) or removes (`DELETE`) one top-level resource
 *  entry. Same atomic, compose-validated save path as the service editor; the
 *  re-parsed project is primed into the cache on success. */
export const useEditComposeResource = (connectionId: string, project: string) => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async (args: {
      kind: ComposeResourceKind
      name: string
      data: ComposeResourceEditRequest
    }) =>
      (await api.put<ComposeResourceEditResponse>(
        `/api/docker/connections/${connectionId}/compose/${encodeURIComponent(project)}/resources/${args.kind}/${encodeURIComponent(args.name)}`,
        args.data)).data,
    onSuccess: (response) => {
      qc.setQueryData<ComposeProject>(qk.composeProject(connectionId, project), response.project)
    },
  })
}

export const useDeleteComposeResource = (connectionId: string, project: string) => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async (args: { kind: ComposeResourceKind; name: string }) =>
      (await api.delete<ComposeResourceEditResponse>(
        `/api/docker/connections/${connectionId}/compose/${encodeURIComponent(project)}/resources/${args.kind}/${encodeURIComponent(args.name)}`)).data,
    onSuccess: (response) => {
      qc.setQueryData<ComposeProject>(qk.composeProject(connectionId, project), response.project)
    },
  })
}

/** V7.3 — networks already defined on the host, for the network editor's
 *  subnet-overlap warning. Fetched lazily (enabled) and cached server-side. */
export const useComposeHostNetworks = (
  connectionId: string, project: string, enabled: boolean,
) =>
  useQuery({
    queryKey: qk.composeHostNetworks(connectionId, project),
    enabled,
    staleTime: 60_000,
    retry: false,
    queryFn: async (): Promise<ComposeHostNetwork[]> =>
      (await api.get<ComposeHostNetwork[]>(
        `/api/docker/connections/${connectionId}/compose/${encodeURIComponent(project)}/host-networks`)).data,
  })

/** V7.3 — on-disk usage of the host's named volumes (best-effort). */
export const useComposeVolumeUsage = (
  connectionId: string, project: string, enabled: boolean,
) =>
  useQuery({
    queryKey: qk.composeVolumeUsage(connectionId, project),
    enabled,
    staleTime: 60_000,
    retry: false,
    queryFn: async (): Promise<ComposeVolumeUsage[]> =>
      (await api.get<ComposeVolumeUsage[]>(
        `/api/docker/connections/${connectionId}/compose/${encodeURIComponent(project)}/volume-usage`)).data,
  })

/** V7.1 — registry tags for the image dropdown in the edit modal. Fetched
 *  lazily (enabled flag) so opening the modal doesn't hit the registry until
 *  the user reaches for the dropdown. */
export const useComposeImageTags = (connectionId: string, image: string | null, enabled: boolean) =>
  useQuery({
    queryKey: image ? qk.composeImageTags(connectionId, image) : ['compose-image-tags-disabled'],
    enabled: enabled && !!image,
    staleTime: 5 * 60_000, // tag lists move slowly; don't refetch per keystroke
    retry: false,
    queryFn: async (): Promise<ComposeImageTags> =>
      (await api.get<ComposeImageTags>(
        `/api/docker/connections/${connectionId}/compose/image-tags`,
        { params: { image } })).data,
  })

/** V7.2 — host capacity already reserved by other running containers, for the
 *  resources editor's over-commit panel. Cached server-side (~60 s); a short
 *  client `staleTime` avoids refetching while the modal is open. */
export const useComposeAllocation = (connectionId: string | null, project: string | null) =>
  useQuery({
    queryKey: connectionId && project
      ? qk.composeAllocation(connectionId, project)
      : ['compose-allocation-disabled'],
    enabled: connectionId !== null && project !== null,
    staleTime: 60_000,
    retry: false,
    queryFn: async (): Promise<ComposeAllocation> =>
      (await api.get<ComposeAllocation>(
        `/api/docker/connections/${connectionId}/compose/${encodeURIComponent(project!)}/allocation`)).data,
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

// ── V5.5 — image storage + prune ───────────────────────────────────────────

/** V5.5 — image storage summary used by the V3.5 Storage widget. Hits the
 *  Docker daemon every time (no caching client-side beyond the React-Query
 *  default) because dangling counts change on every recreate. */
export const useDockerImageStorage = (connectionId: string | null) =>
  useQuery({
    queryKey: connectionId
      ? qk.dockerImageStorage(connectionId)
      : ['docker-image-storage-disabled'],
    enabled: Boolean(connectionId),
    queryFn: async (): Promise<DockerImageStorage> =>
      (await api.get<DockerImageStorage>(
        `/api/docker/connections/${connectionId}/instance/images/storage`)).data,
    staleTime: 5_000,
  })

/** V5.5 — manual "Prune now" mutation. `includeUnused` overrides the
 *  connection's persisted opt-in for a single run. */
export const usePruneImages = (connectionId: string) => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async (args: { includeUnused: boolean }): Promise<DockerPruneRun> => {
      try {
        const resp = await api.post<{ run: DockerPruneRun }>(
          `/api/docker/connections/${connectionId}/instance/images/prune`,
          { includeUnused: args.includeUnused },
        )
        return resp.data.run
      } catch (err) {
        // Backend returns the same response shape on 502 (host unreachable
        // or daemon error). Surface the audit row so the dialog can render
        // "Failed — Docker host unreachable" instead of a generic toast.
        const data = (err as { response?: { data?: { run?: DockerPruneRun } } })?.response?.data
        if (data?.run) return data.run
        throw err
      }
    },
    onSettled: () => {
      void qc.invalidateQueries({ queryKey: qk.dockerImageStorage(connectionId) })
    },
  })
}

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

/** V5.4 — bulk "Update project" mutation. Pulls + recreates every service in
 *  the Compose project in one shot (compose-aware when available, falling
 *  back to per-service raw recreate ordered by depends_on).
 *
 *  The endpoint returns a structured `DockerProjectUpdateResponse` on **both**
 *  full-success (200) and partial-failure (502) — the body carries the same
 *  shape with per-service rows so the UI can render the real errors. Axios
 *  throws on 5xx by default, so we unwrap the body here when the failure
 *  carries the expected envelope and treat it as a normal mutation success.
 *  A request that didn't even reach the server (404, network error, …)
 *  still falls through to the mutation's onError. */
export const useDockerProjectUpdate = (connectionId: string) => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async (projectName: string) => {
      const url = `/api/docker/connections/${connectionId}/instance/projects/${encodeURIComponent(projectName)}/update`
      try {
        const resp = await api.post<DockerProjectUpdateResponse>(url)
        return resp.data
      } catch (err) {
        const body = (err as { response?: { data?: unknown } })?.response?.data
        if (isProjectUpdateResponse(body)) return body
        throw err
      }
    },
    onSuccess: () => {
      // The cards' digests / status badges all change after a bulk update.
      void qc.invalidateQueries({ queryKey: qk.dockerInstanceContainers(connectionId) })
      void qc.invalidateQueries({ queryKey: qk.connectionWatches(connectionId) })
      // Service-aggregated update status feeds the dashboard cards.
      void qc.invalidateQueries({ queryKey: qk.services })
    },
  })
}

const isProjectUpdateResponse = (value: unknown): value is DockerProjectUpdateResponse =>
  typeof value === 'object' && value !== null
    && 'parent' in value && 'services' in value && 'mode' in value
    && Array.isArray((value as DockerProjectUpdateResponse).services)

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

/** V7.8 — set a custom icon for a container; invalidates the card list so the new
 *  avatar shows up. The image is sent as a base64 data URI in a JSON body (not
 *  multipart) — read in the browser via FileReader. */
export const useUploadContainerIcon = (connectionId: string) => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async ({ containerName, file }: { containerName: string; file: File }) => {
      const dataUri = await readFileAsDataUrl(file)
      const url = `/api/docker/connections/${connectionId}/instance/containers/${encodeURIComponent(containerName)}/icon`
      await api.post(url, { dataUri })
    },
    onSuccess: () => qc.invalidateQueries({ queryKey: qk.dockerInstanceContainers(connectionId) }),
  })
}

/** V7.8 — reset a container's icon back to Auto (official / placeholder). */
export const useResetContainerIcon = (connectionId: string) => {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async (containerName: string) => {
      const url = `/api/docker/connections/${connectionId}/instance/containers/${encodeURIComponent(containerName)}/icon`
      await api.delete(url)
    },
    onSuccess: () => qc.invalidateQueries({ queryKey: qk.dockerInstanceContainers(connectionId) }),
  })
}
