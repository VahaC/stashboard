import * as React from 'react'
import { Eye, EyeOff } from 'lucide-react'
import { Input } from '@/components/ui/input'
import { cn } from '@/lib/utils'

export interface PasswordInputProps extends Omit<React.InputHTMLAttributes<HTMLInputElement>, 'type'> {
  containerClassName?: string
  actionsClassName?: string
  toggleButtonClassName?: string
  endAdornment?: React.ReactNode
  revealed?: boolean
  defaultRevealed?: boolean
  onRevealedChange?: (revealed: boolean) => void
}

export const PasswordInput = React.forwardRef<HTMLInputElement, PasswordInputProps>(function PasswordInput(
  {
    className,
    containerClassName,
    actionsClassName,
    toggleButtonClassName,
    endAdornment,
    revealed,
    defaultRevealed = false,
    onRevealedChange,
    ...props
  },
  ref
) {
  const [internalRevealed, setInternalRevealed] = React.useState(defaultRevealed)
  const isControlled = revealed !== undefined
  const isRevealed = isControlled ? revealed : internalRevealed

  const toggle = () => {
    const next = !isRevealed
    if (!isControlled) setInternalRevealed(next)
    onRevealedChange?.(next)
  }

  return (
    <div className={cn('relative', containerClassName)}>
      <Input
        {...props}
        ref={ref}
        type={isRevealed ? 'text' : 'password'}
        className={cn('pr-10', className)}
      />
      <div className={cn('absolute inset-y-0 right-0 flex items-center pr-2', actionsClassName)}>
        {endAdornment}
        <button
          type="button"
          className={cn(
            'inline-flex h-7 w-7 items-center justify-center rounded text-muted-foreground transition-colors hover:text-foreground',
            toggleButtonClassName
          )}
          onClick={toggle}
          title={isRevealed ? 'Hide value' : 'Show value'}
          aria-label={isRevealed ? 'Hide value' : 'Show value'}
        >
          {isRevealed ? <EyeOff className="h-4 w-4" /> : <Eye className="h-4 w-4" />}
        </button>
      </div>
    </div>
  )
})
