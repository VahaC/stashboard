import { Monitor, Moon, Sun } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { accountApi } from '@/lib/account-api'
import { useAuthStore } from '@/lib/auth-store'
import { useTheme } from '@/lib/use-theme'
import type { Theme } from '@/lib/theme-store'
import { cn } from '@/lib/utils'

const options: Array<{ value: Theme; label: string; icon: typeof Sun }> = [
  { value: 'system', label: 'System', icon: Monitor },
  { value: 'light', label: 'Light', icon: Sun },
  { value: 'dark', label: 'Dark', icon: Moon },
]

interface Props {
  /** Compact icon-only segmented control (for sidebars), or full labels (for forms). */
  variant?: 'compact' | 'full'
}

export function ThemeSwitcher({ variant = 'full' }: Props) {
  const [current, setLocal] = useTheme()
  const accessToken = useAuthStore((s) => s.accessToken)

  const choose = async (next: Theme) => {
    setLocal(next)
    // Only sync to the server if we're authenticated. Pre-login picks are local-only.
    if (accessToken) {
      try { await accountApi.setTheme(next) } catch { /* server sync is best-effort */ }
    }
  }

  if (variant === 'compact') {
    return (
      <div className="inline-flex rounded-full border border-input p-0.5 bg-background">
        {options.map(({ value, label, icon: Icon }) => (
          <button
            key={value}
            type="button"
            onClick={() => choose(value)}
            title={label}
            aria-label={label}
            aria-pressed={current === value}
            className={cn(
              'h-6 w-6 flex items-center justify-center rounded-full transition-colors',
              current === value ? 'bg-primary text-primary-foreground' : 'text-muted-foreground hover:bg-accent hover:text-accent-foreground'
            )}
          >
            <Icon className="h-3 w-3" />
          </button>
        ))}
      </div>
    )
  }

  return (
    <div className="flex flex-wrap gap-2">
      {options.map(({ value, label, icon: Icon }) => (
        <Button
          key={value}
          type="button"
          variant={current === value ? 'default' : 'outline'}
          size="sm"
          className="min-w-0"
          onClick={() => choose(value)}
        >
          <Icon className="h-4 w-4" /> {label}
        </Button>
      ))}
    </div>
  )
}
