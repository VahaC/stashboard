import { describe, it, expect, vi, beforeEach } from 'vitest'
import { renderHook, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import type { ReactNode } from 'react'

// Mock the axios instance so the mutation hooks resolve against canned data.
vi.mock('@/lib/api', () => ({ api: { post: vi.fn(), delete: vi.fn() } }))

import { api } from '@/lib/api'
import { useUploadContainerIcon, useResetContainerIcon } from './queries'

/* eslint-disable @typescript-eslint/no-explicit-any */
const mockApi = api as unknown as { post: any; delete: any }

function wrapper() {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  const invalidate = vi.spyOn(qc, 'invalidateQueries')
  const Wrapper = ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={qc}>{children}</QueryClientProvider>
  )
  return { Wrapper, invalidate }
}

beforeEach(() => {
  mockApi.post.mockReset()
  mockApi.delete.mockReset()
})

describe('useUploadContainerIcon', () => {
  it('POSTs the image as a base64 data URI (JSON) and refreshes the list', async () => {
    mockApi.post.mockResolvedValue({ data: null })
    const { Wrapper, invalidate } = wrapper()
    const { result } = renderHook(() => useUploadContainerIcon('conn1'), { wrapper: Wrapper })

    const file = new File(['data'], 'icon.png', { type: 'image/png' })
    await result.current.mutateAsync({ containerName: 'my container', file })

    const [url, body] = mockApi.post.mock.calls[0]
    expect(url).toBe('/api/docker/connections/conn1/instance/containers/my%20container/icon')
    expect(body).not.toBeInstanceOf(FormData)
    expect(typeof body.dataUri).toBe('string')
    expect(body.dataUri.startsWith('data:image/png;base64,')).toBe(true)
    await waitFor(() =>
      expect(invalidate).toHaveBeenCalledWith({
        queryKey: ['docker', 'connections', 'conn1', 'instance', 'containers'],
      }),
    )
  })
})

describe('useResetContainerIcon', () => {
  it('DELETEs the container icon endpoint and refreshes the list', async () => {
    mockApi.delete.mockResolvedValue({ data: null })
    const { Wrapper, invalidate } = wrapper()
    const { result } = renderHook(() => useResetContainerIcon('conn1'), { wrapper: Wrapper })

    await result.current.mutateAsync('jellyfin')

    expect(mockApi.delete).toHaveBeenCalledWith(
      '/api/docker/connections/conn1/instance/containers/jellyfin/icon',
    )
    await waitFor(() =>
      expect(invalidate).toHaveBeenCalledWith({
        queryKey: ['docker', 'connections', 'conn1', 'instance', 'containers'],
      }),
    )
  })
})
