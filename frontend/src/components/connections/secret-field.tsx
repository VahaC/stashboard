/* eslint-disable react-refresh/only-export-components */
import { HelpCircle } from 'lucide-react'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Textarea } from '@/components/ui/textarea'
import { cn } from '@/lib/utils'
import type { SecretValueAction, SecretValueUpsert } from '@/lib/types'
// Both connection editors (Docker host + Proxmox host) render their secret
// fields with these classes; importing the stylesheet here keeps the widget
// styled on every route that uses it, including the lazy-loaded Proxmox page.
import '@/styles/service-modal.css'

/**
 * Shared tri-state secret field used by every connection editor. `Keep`
 * preserves the server-side encrypted value (only offered when one exists),
 * `Set` replaces it, `Clear` drops it.
 */
export interface SecretField {
  action: SecretValueAction
  value: string
  reveal: boolean
}

/** Initial state for a secret: `Keep` when the server already holds a value,
 *  otherwise `Set` (there's nothing to keep). */
export const existingSecret = (hasValue: boolean): SecretField =>
  hasValue
    ? { action: 'Keep', value: '', reveal: false }
    : { action: 'Set', value: '', reveal: false }

/** Project a {@link SecretField} into the wire `SecretValueUpsert`. Only a
 *  `Set` carries a value; `Keep` / `Clear` send `null`. */
export const toUpsert = (s: SecretField): SecretValueUpsert => ({
  action: s.action,
  value: s.action === 'Set' ? s.value : null,
})

interface SecretFieldRowProps {
  label: string
  tooltip?: string
  field: SecretField
  hasExisting: boolean
  secret?: boolean
  multiline?: boolean
  fullwidth?: boolean
  error?: string | null
  onChange: (next: SecretField) => void
}

/** One labelled secret input with the Keep/Set/Clear action selector. */
export function SecretFieldRow({ label, tooltip, field, hasExisting, secret, multiline, fullwidth, error, onChange }: SecretFieldRowProps) {
  return (
    <div className={cn('service-modal-field', multiline && fullwidth && 'docker-section-field-full')}>
      <Label className="service-modal-label">
        {label}
        {tooltip && (
          <span title={tooltip} className="docker-label-help">
            <HelpCircle className="h-3 w-3 inline" />
          </span>
        )}
      </Label>
      <div className="docker-secret-row">
        <select
          className={error ? 'border-destructive' : ''}
          value={field.action}
          onChange={(e) => onChange({ ...field, action: e.target.value as SecretValueAction, value: '' })}
        >
          {hasExisting && <option value="Keep">Keep</option>}
          <option value="Set">Set</option>
          {hasExisting && <option value="Clear">Clear</option>}
        </select>

        {field.action === 'Set' ? (
          multiline ? (
            <Textarea
              rows={3}
              value={field.value}
              onChange={(e) => onChange({ ...field, value: e.target.value })}
              placeholder={secret ? '-----BEGIN PRIVATE KEY-----' : ''}
              className={error ? 'border-destructive' : ''}
            />
          ) : (
            <Input
              type={secret && !field.reveal ? 'password' : 'text'}
              value={field.value}
              onChange={(e) => onChange({ ...field, value: e.target.value })}
              className={error ? 'border-destructive' : ''}
            />
          )
        ) : field.action === 'Keep' ? (
          <span className="docker-secret-placeholder">Using saved value</span>
        ) : (
          <span className="docker-secret-placeholder">Will be cleared on save</span>
        )}
      </div>
      {error && <p className="service-modal-field-error">{error}</p>}
    </div>
  )
}

const normalizeFieldKey = (key: string) => key.replace(/[^a-z0-9]/gi, '').toLowerCase()

/** Resolve a server-returned validation error for any of the supplied field
 *  names, tolerant of casing / punctuation differences between the API's
 *  PascalCase keys and the form's camelCase names. */
export const pickFieldError = (errors: Record<string, string>, ...keys: string[]) => {
  for (const key of keys) {
    if (errors[key]) return errors[key]
    if (errors[key.toLowerCase()]) return errors[key.toLowerCase()]

    const normalized = normalizeFieldKey(key)
    const found = Object.entries(errors).find(([k]) => normalizeFieldKey(k) === normalized)
    if (found?.[1]) return found[1]
  }
  return null
}
