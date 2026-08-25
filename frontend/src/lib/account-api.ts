import { api } from './api'
import type { DashboardPreferences, EmailSettings, EmailSettingsUpdate, Profile, TelegramSettings } from './types'
import type { Theme } from './theme-store'

export interface TwoFactorEnrollment {
  otpauthUri: string
  manualKey: string
}

export interface RecoveryCodes {
  recoveryCodes: string[]
}

export const accountApi = {
  // Anonymous flows
  forgotPassword: (email: string) => api.post('/api/account/forgot-password', { email }),
  resetPassword: (email: string, token: string, newPassword: string) =>
    api.post('/api/account/reset-password', { email, token, newPassword }),
  confirmEmail: (email: string, token: string) =>
    api.post('/api/account/confirm-email', { email, token }),
  resendConfirmation: (email: string) =>
    api.post('/api/account/resend-confirmation', { email }),

  // Authenticated flows
  getProfile: () => api.get<Profile>('/api/account/profile').then((r) => r.data),
  updateProfile: (displayName: string | null, theme?: Theme) =>
    api.patch('/api/account/profile', { displayName, theme: theme ?? null }),
  /** Cheap one-column update — used by the in-app theme switcher. */
  setTheme: (theme: Theme) => api.put('/api/account/theme', { theme }),
  getDashboardPreferences: () => api.get<DashboardPreferences>('/api/account/dashboard-preferences').then((response) => response.data),
  updateDashboardPreferences: (sortMode: DashboardPreferences['sortMode'], groupByCategory: boolean) =>
    api.put('/api/account/dashboard-preferences', { sortMode, groupByCategory }),
  getTelegramSettings: () => api.get<TelegramSettings>('/api/account/telegram').then((r) => r.data),
  updateTelegramSettings: (settings: TelegramSettings) => api.put('/api/account/telegram', settings),
  getEmailSettings: () => api.get<EmailSettings>('/api/account/email-settings').then((r) => r.data),
  updateEmailSettings: (settings: EmailSettingsUpdate) => api.put('/api/account/email-settings', settings),
  changePassword: (currentPassword: string, newPassword: string) =>
    api.post('/api/account/change-password', { currentPassword, newPassword }),
  changeEmail: (newEmail: string, currentPassword: string) =>
    api.post('/api/account/change-email', { newEmail, currentPassword }),
  confirmEmailChange: (token: string) =>
    api.post('/api/account/confirm-email-change', { token }),
  deleteAccount: (currentPassword: string) =>
    api.delete('/api/account', { data: { currentPassword } }),

  // Two-factor authentication (TOTP)
  enrollTwoFactor: () =>
    api.post<TwoFactorEnrollment>('/api/account/2fa/enroll').then((r) => r.data),
  enableTwoFactor: (code: string) =>
    api.post<RecoveryCodes>('/api/account/2fa/enable', { code }).then((r) => r.data),
  disableTwoFactor: (currentPassword: string) =>
    api.post('/api/account/2fa/disable', { currentPassword }),
  regenerateRecoveryCodes: (currentPassword: string) =>
    api.post<RecoveryCodes>('/api/account/2fa/recovery-codes', { currentPassword }).then((r) => r.data),
}
