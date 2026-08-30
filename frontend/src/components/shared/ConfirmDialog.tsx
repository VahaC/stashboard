/* eslint-disable react-refresh/only-export-components */
import { createContext, useCallback, useContext, useRef, useState } from 'react'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'

export interface ConfirmOptions {
  /** Heading. Defaults to "Are you sure?". */
  title?: string
  /** Body text / node explaining what will happen. */
  message: React.ReactNode
  /** Confirm button label. Defaults to "Confirm". */
  confirmLabel?: string
  /** Cancel button label. Defaults to "Cancel". */
  cancelLabel?: string
  /** Render the confirm button in the destructive (red) style. */
  destructive?: boolean
}

type ConfirmFn = (options: ConfirmOptions) => Promise<boolean>

const ConfirmContext = createContext<ConfirmFn | null>(null)

/**
 * Returns an imperative `confirm(options)` that resolves to `true`/`false` — a drop-in,
 * promise-based replacement for the native `window.confirm`, rendered as the app's own modal.
 * The project never uses native confirm/alert dialogs; always go through this.
 */
export function useConfirm(): ConfirmFn {
  const ctx = useContext(ConfirmContext)
  if (!ctx) throw new Error('useConfirm must be used within a <ConfirmProvider>.')
  return ctx
}

/** App-wide provider that hosts the single confirmation modal. Mount once near the root. */
export function ConfirmProvider({ children }: { children: React.ReactNode }) {
  const [options, setOptions] = useState<ConfirmOptions | null>(null)
  const resolver = useRef<((value: boolean) => void) | null>(null)

  const confirm = useCallback<ConfirmFn>((opts) => {
    setOptions(opts)
    return new Promise<boolean>((resolve) => { resolver.current = resolve })
  }, [])

  const settle = (result: boolean) => {
    resolver.current?.(result)
    resolver.current = null
    setOptions(null)
  }

  return (
    <ConfirmContext.Provider value={confirm}>
      {children}
      <Dialog open={options !== null} onOpenChange={(open) => { if (!open) settle(false) }}>
        {options && (
          <DialogContent className="confirm-dialog">
            <DialogHeader>
              <DialogTitle>{options.title ?? 'Are you sure?'}</DialogTitle>
              <DialogDescription>{options.message}</DialogDescription>
            </DialogHeader>
            <DialogFooter>
              <Button variant="outline" onClick={() => settle(false)}>{options.cancelLabel ?? 'Cancel'}</Button>
              <Button variant={options.destructive ? 'destructive' : 'default'} onClick={() => settle(true)}>
                {options.confirmLabel ?? 'Confirm'}
              </Button>
            </DialogFooter>
          </DialogContent>
        )}
      </Dialog>
    </ConfirmContext.Provider>
  )
}
