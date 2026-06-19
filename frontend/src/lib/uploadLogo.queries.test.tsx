import { describe, it, expect, vi, beforeEach } from 'vitest'
import { renderHook, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import type { ReactNode } from 'react'

// Mock the axios instance so the mutation hook resolves against canned data.
vi.mock('@/lib/api', () => ({ api: { post: vi.fn() } }))

import { api } from '@/lib/api'
import { useUploadLogo } from './queries'

/* eslint-disable @typescript-eslint/no-explicit-any */
const mockApi = api as unknown as { post: any }

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
})

describe('useUploadLogo', () => {
  it('POSTs the logo as a base64 data URI (JSON, not multipart) and refreshes services', async () => {
    mockApi.post.mockResolvedValue({ data: { dataUri: 'data:image/png;base64,STORED' } })
    const { Wrapper, invalidate } = wrapper()
    const { result } = renderHook(() => useUploadLogo(), { wrapper: Wrapper })

    const file = new File(['data'], 'logo.png', { type: 'image/png' })
    const returned = await result.current.mutateAsync({ id: 'svc1', file })

    const [url, body] = mockApi.post.mock.calls[0]
    expect(url).toBe('/api/services/svc1/logo')
    expect(body).not.toBeInstanceOf(FormData)
    expect(typeof body.dataUri).toBe('string')
    expect(body.dataUri.startsWith('data:image/png;base64,')).toBe(true)
    expect(returned).toBe('data:image/png;base64,STORED')
    await waitFor(() =>
      expect(invalidate).toHaveBeenCalledWith({ queryKey: ['services'] }),
    )
  })
})
