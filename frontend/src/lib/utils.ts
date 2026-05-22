import { clsx, type ClassValue } from 'clsx'
import { twMerge } from 'tailwind-merge'

export function cn(...inputs: ClassValue[]) {
  return twMerge(clsx(inputs))
}

interface ApiProblemDetails {
  title?: string
  detail?: string
  error?: string
  errors?: Record<string, string[]>
}

/**
 * Parses an ASP.NET Core ProblemDetails / ValidationProblemDetails error response.
 * Returns per-field errors (keys lowercased) and a fallback global error message.
 */
export function parseApiErrors(err: unknown): {
  fieldErrors: Record<string, string>
  globalError: string | null
} {
  const data = (err as { response?: { data?: ApiProblemDetails } })?.response?.data
  if (!data) return { fieldErrors: {}, globalError: 'An unexpected error occurred.' }

  if (data.errors && Object.keys(data.errors).length > 0) {
    const fieldErrors: Record<string, string> = {}
    for (const [key, messages] of Object.entries(data.errors)) {
      fieldErrors[key.toLowerCase()] = messages[0]
    }
    return { fieldErrors, globalError: null }
  }

  return { fieldErrors: {}, globalError: data.error ?? data.detail ?? data.title ?? 'An error occurred.' }
}

/**
 * Converts API errors into a single display message.
 * If ValidationProblemDetails contains field errors, joins them into one line.
 */
export function getApiErrorMessage(err: unknown, fallback: string | null = null): string | null {
  const { fieldErrors, globalError } = parseApiErrors(err)
  if (globalError) return globalError

  const messages = Object.values(fieldErrors).filter(Boolean)
  if (messages.length > 0) return messages.join(' ')

  return fallback
}
