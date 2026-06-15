/**
 * V7.2 — Compose memory-size helpers shared by the resources editor and its
 * tests. Compose accepts sizes as a number of bytes or a `<n><unit>` string
 * (`b`/`k`/`m`/`g`, base-1024); these convert between that text and bytes for
 * the slider + the capacity panel.
 */

const SIZE_UNITS: Record<string, number> = {
  b: 1, k: 1024, kb: 1024, m: 1024 ** 2, mb: 1024 ** 2, g: 1024 ** 3, gb: 1024 ** 3,
}
export const MIB = 1024 ** 2

/** Parses a Compose size (`512m`, `2g`, `536870912`) to bytes; `null` when the
 *  string isn't a recognised size. */
export function parseComposeSize(s: string | null): number | null {
  if (!s) return null
  const m = /^(\d+(?:\.\d+)?)\s*([a-zA-Z]*)$/.exec(s.trim())
  if (!m) return null
  const n = parseFloat(m[1])
  const unit = m[2].toLowerCase()
  if (unit === '') return Math.round(n)
  const mult = SIZE_UNITS[unit]
  return mult ? Math.round(n * mult) : null
}

/** Renders bytes back to the tersest Compose size string (`g` / `m` / raw). */
export function bytesToComposeSize(bytes: number): string {
  if (bytes <= 0) return ''
  if (bytes % (1024 ** 3) === 0) return `${bytes / 1024 ** 3}g`
  if (bytes % MIB === 0) return `${bytes / MIB}m`
  return String(bytes)
}
