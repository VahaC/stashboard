import axios, { AxiosError, type AxiosInstance } from 'axios'
import { useAuthStore } from './auth-store'

export const api: AxiosInstance = axios.create({ baseURL: '/' })

api.interceptors.request.use((config) => {
  const token = useAuthStore.getState().accessToken
  if (token) config.headers.Authorization = `Bearer ${token}`
  return config
})

let refreshing: Promise<string | null> | null = null

api.interceptors.response.use(
  (r) => r,
  async (error: AxiosError) => {
    const original = error.config as (typeof error.config) & { _retry?: boolean }
    if (error.response?.status !== 401 || !original || original._retry) {
      return Promise.reject(error)
    }
    const { refreshToken, setSession, clear } = useAuthStore.getState()
    if (!refreshToken) {
      clear()
      return Promise.reject(error)
    }

    if (!refreshing) {
      refreshing = (async () => {
        try {
          const resp = await axios.post('/api/auth/refresh', { refreshToken })
          setSession({
            accessToken: resp.data.accessToken,
            refreshToken: resp.data.refreshToken,
            user: resp.data.user,
          })
          return resp.data.accessToken as string
        } catch {
          clear()
          return null
        } finally {
          refreshing = null
        }
      })()
    }

    const newToken = await refreshing
    if (!newToken) return Promise.reject(error)
    original._retry = true
    original.headers = original.headers ?? {}
    ;(original.headers as Record<string, string>).Authorization = `Bearer ${newToken}`
    return api.request(original)
  }
)
