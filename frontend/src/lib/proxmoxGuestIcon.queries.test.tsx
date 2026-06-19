import { describe, it, expect, vi, beforeEach } from 'vitest'
import { renderHook, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import type { ReactNode } from 'react'

vi.mock('@/lib/api', () => ({ api: { get: vi.fn(), post: vi.fn(), delete: vi.fn() } }))

import { api } from '@/lib/api'
import { useProxmoxGuestIcons, useUploadGuestIcon, useResetGuestIcon } from './proxmox-queries'

/* eslint-disable @typescript-eslint/no-explicit-any */
const mockApi = api as unknown as { get: any; post: any; delete: any }

function wrapper() {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  const invalidate = vi.spyOn(qc, 'invalidateQueries')
  const Wrapper = ({ children }: { children: ReactNode }) => (
    <QueryClientProvider client={qc}>{children}</QueryClientProvider>
  )
  return { Wrapper, invalidate }
}

beforeEach(() => {
  mockApi.get.mockReset()
  mockApi.post.mockReset()
  mockApi.delete.mockReset()
})

describe('useProxmoxGuestIcons', () => {
  it('fetches the vmId → data-URI map', async () => {
    mockApi.get.mockResolvedValue({ data: { '101': 'data:image/webp;base64,DEB' } })
    const { Wrapper } = wrapper()
    const { result } = renderHook(() => useProxmoxGuestIcons('c1'), { wrapper: Wrapper })

    await waitFor(() => expect(result.current.isSuccess).toBe(true))
    expect(mockApi.get).toHaveBeenCalledWith('/api/proxmox/connections/c1/guest-icons')
    expect(result.current.data).toEqual({ '101': 'data:image/webp;base64,DEB' })
  })
})

describe('useUploadGuestIcon / useResetGuestIcon', () => {
  it('upload POSTs the image as a base64 data URI (JSON) and refreshes the map', async () => {
    mockApi.post.mockResolvedValue({ data: null })
    const { Wrapper, invalidate } = wrapper()
    const { result } = renderHook(() => useUploadGuestIcon('c1'), { wrapper: Wrapper })

    const file = new File(['x'], 'g.png', { type: 'image/png' })
    await result.current.mutateAsync({ vmId: 101, file })

    const [url, body] = mockApi.post.mock.calls[0]
    expect(url).toBe('/api/proxmox/connections/c1/guests/101/icon')
    expect(body).not.toBeInstanceOf(FormData)
    expect(body.dataUri.startsWith('data:image/png;base64,')).toBe(true)
    await waitFor(() =>
      expect(invalidate).toHaveBeenCalledWith({ queryKey: ['proxmox', 'connections', 'c1', 'guest-icons'] }))
  })

  it('reset DELETEs the guest icon endpoint and refreshes the map', async () => {
    mockApi.delete.mockResolvedValue({ data: null })
    const { Wrapper, invalidate } = wrapper()
    const { result } = renderHook(() => useResetGuestIcon('c1'), { wrapper: Wrapper })

    await result.current.mutateAsync(101)

    expect(mockApi.delete).toHaveBeenCalledWith('/api/proxmox/connections/c1/guests/101/icon')
    await waitFor(() =>
      expect(invalidate).toHaveBeenCalledWith({ queryKey: ['proxmox', 'connections', 'c1', 'guest-icons'] }))
  })
})
