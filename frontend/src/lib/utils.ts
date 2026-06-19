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

/**
 * V5.5 — format a byte count for human display ("4.2 GiB", "812 MiB",
 * "0 B"). Uses 1024-base ("KiB") to match the way Docker reports image
 * sizes — keeps the UI numbers consistent with `docker system df`.
 */
/** V7.8 — read a File as a `data:<mime>;base64,...` URI. Used by the card-icon
 *  upload so the image travels in a JSON body (no multipart/form-data). */
export function readFileAsDataUrl(file: File): Promise<string> {
  return new Promise((resolve, reject) => {
    const reader = new FileReader()
    reader.onload = () => resolve(reader.result as string)
    reader.onerror = () => reject(reader.error ?? new Error('Failed to read file'))
    reader.readAsDataURL(file)
  })
}

export function formatBytes(bytes: number): string {
  if (!Number.isFinite(bytes) || bytes <= 0) return '0 B'
  const units = ['B', 'KiB', 'MiB', 'GiB', 'TiB']
  let value = bytes
  let i = 0
  while (value >= 1024 && i < units.length - 1) {
    value /= 1024
    i++
  }
  return `${value >= 10 || i === 0 ? value.toFixed(0) : value.toFixed(1)} ${units[i]}`
}
