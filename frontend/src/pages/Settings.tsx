import { useState, type JSX } from 'react'
import { NavLink, Outlet } from 'react-router-dom'
import { Bell, TerminalSquare, SquareChevronRight, Server, RefreshCw, HardDrive, Activity, Database, UserCog, Menu, X, Trash2, Plus, Copy, ArchiveRestore } from 'lucide-react'
import { cn } from '@/lib/utils'
import '@/styles/settings-page.css'

const settingsNavItems = [
  { to: '/settings/account', label: 'Account', group: 'General', icon: UserCog },
  { to: '/settings/backup', label: 'Backup / Restore', group: 'General', icon: Database },
  { to: '/settings/notifications', label: 'Notifications', group: 'General', icon: Bell },
  { to: '/settings/container-exec', label: 'Container exec', group: 'Docker', icon: SquareChevronRight },
  { to: '/settings/host-terminal', label: 'Host terminal', group: 'Docker', icon: TerminalSquare },
  { to: '/settings/image-cleanup', label: 'Image cleanup', group: 'Docker', icon: HardDrive },
  { to: '/settings/proxmox-console', label: 'LXC console', group: 'Proxmox', icon: Server },
  { to: '/settings/proxmox-updates', label: 'Proxmox updates', group: 'Proxmox', icon: RefreshCw },
  { to: '/settings/proxmox-create', label: 'Create LXC', group: 'Proxmox', icon: Plus },
  { to: '/settings/proxmox-clone', label: 'Clone/snapshot', group: 'Proxmox', icon: Copy },
  { to: '/settings/proxmox-restore', label: 'Restore guest', group: 'Proxmox', icon: ArchiveRestore },
  { to: '/settings/proxmox-destroy', label: 'Destroy guest', group: 'Proxmox', icon: Trash2 },
  { to: '/settings/health-checks', label: 'Health checks', group: 'Services', icon: Activity },
]

export function Settings() {
  const [mobileNavOpen, setMobileNavOpen] = useState(false)

  return (
    <div className="settings-layout">
      {/* Mobile header bar */}
      <div className="settings-mobile-bar">
        <button
          className="settings-mobile-toggle"
          onClick={() => setMobileNavOpen(v => !v)}
        >
          {mobileNavOpen ? <X className="h-4 w-4" /> : <Menu className="h-4 w-4" />}
          <span>Settings menu</span>
        </button>
      </div>

      {/* Mobile nav overlay */}
      {mobileNavOpen && (
        <div className="settings-overlay" onClick={() => setMobileNavOpen(false)}>
          <nav className="settings-mobile-nav" onClick={e => e.stopPropagation()}>
            {settingsNavItems.reduce((acc, { to, label, group, icon: Icon }, index) => {
              const prevGroup = index > 0 ? settingsNavItems[index - 1].group : null
              if (prevGroup !== group) {
                acc.push(<div key={`group-${group}`} className='settings-nav-link-group'>{group}</div>)
              }
              acc.push(
                <NavLink
                  key={to}
                  to={to}
                  onClick={() => setMobileNavOpen(false)}
                  className={({ isActive }) =>
                    cn('settings-nav-link', isActive && 'settings-nav-link-active')
                  }
                >
                  <Icon className="h-3.5 w-3.5" />
                  {label}
                </NavLink>
              )
              return acc
            }, [] as JSX.Element[])}
          </nav>
        </div>
      )}

      {/* Desktop sidebar */}
      <nav className="settings-sidebar">
        {settingsNavItems.reduce((acc, { to, label, group, icon: Icon }, index) => {
          const prevGroup = index > 0 ? settingsNavItems[index - 1].group : null
          if (prevGroup !== group) {
            acc.push(<div key={`group-${group}`} className='settings-nav-link-group'>{group}</div>)
          }
          acc.push(
            <NavLink
              key={to}
              to={to}
              className={({ isActive }) => cn('settings-nav-link', isActive && 'settings-nav-link-active')}
            >
              <Icon className="h-3.5 w-3.5" />
              {label}
            </NavLink>
          )
          return acc
        }, [] as JSX.Element[])}
      </nav>

      {/* Content */}
      <div className="settings-content">
        <Outlet />
      </div>
    </div>
  )
}
