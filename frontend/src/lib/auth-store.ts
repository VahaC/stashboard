import { create } from 'zustand'
import { persist } from 'zustand/middleware'
import type { User } from './types'

interface AuthState {
  accessToken: string | null
  refreshToken: string | null
  user: User | null
  setSession: (s: { accessToken: string; refreshToken: string; user: User }) => void
  clear: () => void
}

export const useAuthStore = create<AuthState>()(
  persist(
    (set) => ({
      accessToken: null,
      refreshToken: null,
      user: null,
      setSession: (s) => set({ accessToken: s.accessToken, refreshToken: s.refreshToken, user: s.user }),
      clear: () => set({ accessToken: null, refreshToken: null, user: null }),
    }),
    { name: 'stashboard-auth' }
  )
)
