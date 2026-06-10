import type { ReactNode } from 'react'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { cn } from '@/lib/utils'
import { SecretFieldRow, pickFieldError, type SecretField } from './secret-field'
import '@/styles/service-modal.css'

interface SshCredentialFieldsProps {
  host: string
  onHostChange: (value: string) => void
  port: string
  onPortChange: (value: string) => void
  username: string
  onUsernameChange: (value: string) => void
  usernamePlaceholder?: string
  privateKey: SecretField
  onPrivateKeyChange: (next: SecretField) => void
  hasPrivateKey: boolean
  passphrase: SecretField
  onPassphraseChange: (next: SecretField) => void
  hasPassphrase: boolean
  /** Server-returned field errors keyed by field name (camel or Pascal). */
  errors?: Record<string, string>
  /** Optional fields rendered between the username and the key rows
   *  (e.g. the Docker host's remote socket path). */
  children?: ReactNode
}

/**
 * The SSH credential block shared by every connection editor: host, port,
 * username, an optional slot, then the private key + passphrase secret rows.
 * Both the Docker host form (SSH transport) and the Proxmox host form use it
 * verbatim so the SSH experience is identical in both places.
 */
export function SshCredentialFields({
  host,
  onHostChange,
  port,
  onPortChange,
  username,
  onUsernameChange,
  usernamePlaceholder = 'root',
  privateKey,
  onPrivateKeyChange,
  hasPrivateKey,
  passphrase,
  onPassphraseChange,
  hasPassphrase,
  errors = {},
  children,
}: SshCredentialFieldsProps) {
  const hostError = pickFieldError(errors, 'sshHost')
  const portError = pickFieldError(errors, 'sshPort')
  const usernameError = pickFieldError(errors, 'sshUsername')
  const privateKeyError = pickFieldError(errors, 'sshPrivateKey')
  const passphraseError = pickFieldError(errors, 'sshPrivateKeyPassphrase')

  return (
    <>
      <div className="service-modal-field two-column">
        <div className="ssh-host-field">
          <Label className="service-modal-label">SSH host</Label>
          <Input
            placeholder="vps.example.com"
            value={host}
            onChange={(e) => onHostChange(e.target.value)}
            className={cn('font-mono text-[12px]', hostError && 'border-destructive')}
          />
          {hostError && <p className="service-modal-field-error">{hostError}</p>}
        </div>

        <div className="ssh-port-field">
          <Label className="service-modal-label">SSH port</Label>
          <Input
            type="number"
            min={1}
            max={65535}
            placeholder="22"
            value={port}
            onChange={(e) => onPortChange(e.target.value)}
            className={cn('font-mono text-[12px]', portError && 'border-destructive')}
          />
          {portError && <p className="service-modal-field-error">{portError}</p>}
        </div>
      </div>

      <div className="service-modal-field">
        <Label className="service-modal-label">SSH username</Label>
        <Input
          placeholder={usernamePlaceholder}
          value={username}
          onChange={(e) => onUsernameChange(e.target.value)}
          className={cn('font-mono text-[12px]', usernameError && 'border-destructive')}
        />
        {usernameError && <p className="service-modal-field-error">{usernameError}</p>}
      </div>

      {children}

      <SecretFieldRow
        label="SSH private key (PEM)"
        field={privateKey}
        hasExisting={hasPrivateKey}
        multiline
        secret
        error={privateKeyError}
        onChange={onPrivateKeyChange}
      />
      <SecretFieldRow
        label="Private key passphrase (optional)"
        field={passphrase}
        hasExisting={hasPassphrase}
        secret
        error={passphraseError}
        onChange={onPassphraseChange}
      />
    </>
  )
}
