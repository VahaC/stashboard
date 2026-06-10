import { useState } from 'react'
import { NavLink, Outlet } from 'react-router-dom'
import { Server, KeyRound, Container, ShieldCheck, Menu, X } from 'lucide-react'
import { cn } from '@/lib/utils'
import '@/styles/settings-page.css'

const helpNavItems = [
  { to: '/help/proxmox-api', label: 'Proxmox · API token', icon: KeyRound },
  { to: '/help/proxmox-ssh', label: 'Proxmox · SSH key', icon: Server },
  { to: '/help/docker-ssh', label: 'Docker · SSH tunnel', icon: Container },
  { to: '/help/docker-tls', label: 'Docker · TCP + TLS', icon: ShieldCheck },
]

export function Help() {
  const [mobileNavOpen, setMobileNavOpen] = useState(false)

  return (
    <div className="settings-layout">
      {/* Mobile bar */}
      <div className="settings-mobile-bar">
        <button
          className="settings-mobile-toggle"
          onClick={() => setMobileNavOpen(v => !v)}
        >
          {mobileNavOpen ? <X className="h-4 w-4" /> : <Menu className="h-4 w-4" />}
          <span>Contents</span>
        </button>
      </div>

      {/* Mobile nav overlay */}
      {mobileNavOpen && (
        <div
          className="settings-overlay"
          onClick={() => setMobileNavOpen(false)}
        >
          <nav
            className="settings-mobile-nav"
            onClick={e => e.stopPropagation()}
          >
            {helpNavItems.map(({ to, label, icon: Icon }) => (
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
            ))}
          </nav>
        </div>
      )}

      {/* Desktop sidebar */}
      <nav className="settings-sidebar">
        {helpNavItems.map(({ to, label, icon: Icon }) => (
          <NavLink
            key={to}
            to={to}
            className={({ isActive }) =>
              cn('settings-nav-link', isActive && 'settings-nav-link-active')
            }
          >
            <Icon className="h-3.5 w-3.5" />
            {label}
          </NavLink>
        ))}
      </nav>

      {/* Content */}
      <div className="settings-content">
        <Outlet />
      </div>
    </div>
  )
}
