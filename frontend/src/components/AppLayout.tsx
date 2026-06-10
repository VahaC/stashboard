import { useState } from 'react'
import { Link, NavLink, Outlet, useNavigate } from 'react-router-dom'
import { Button } from '@/components/ui/button'
import { ThemeSwitcher } from '@/components/ThemeSwitcher'
import { useAuthStore } from '@/lib/auth-store'
import { api } from '@/lib/api'
import { cn } from '@/lib/utils'
import logo from '@/assets/logo.svg'
import { LayoutGrid, FolderTree, LogOut, Menu, X, Container, Settings, ScrollText, Server, BookOpen } from 'lucide-react'
import '@/styles/app-layout.css'

const mainNavItems = [
  { to: '/', label: 'Services', icon: LayoutGrid, end: true },
  { to: '/docker', label: 'Docker', icon: Container, end: false },
  { to: '/proxmox', label: 'Proxmox', icon: Server, end: false },
  { to: '/categories', label: 'Categories', icon: FolderTree, end: false },
  { to: '/audit', label: 'Audit', icon: ScrollText, end: false },
]

export function AppLayout() {
  const nav = useNavigate()
  const { user, refreshToken, clear } = useAuthStore()
  const [sidebarOpen, setSidebarOpen] = useState(false)

  const logout = async () => {
    try {
      if (refreshToken) await api.post('/api/auth/logout', { refreshToken })
    } catch { /* ignore */ }
    clear()
    nav('/login')
  }

  const closeSidebar = () => setSidebarOpen(false)

  return (
    <div className="app-shell">
      {/* Mobile overlay */}
      {sidebarOpen && (
        <div
          className="app-shell-overlay"
          onClick={closeSidebar}
        />
      )}

      {/* Sidebar */}
      <aside
        className={cn(
          'app-sidebar',
          sidebarOpen ? 'app-sidebar-open' : 'app-sidebar-hidden'
        )}
      >
        <div className="app-sidebar-header">
          <Link to="/" className="app-brand" onClick={closeSidebar}>
            <img src={logo} alt="" className="h-5 w-5" />
            <span>Stashboard</span>
            <span className="app-brand-version">v{__APP_VERSION__}</span>
          </Link>
          <Button variant="ghost" size="icon" className="md:hidden" onClick={closeSidebar}>
            <X className="h-4 w-4" />
          </Button>
        </div>
        <nav className="app-sidebar-nav">
          {mainNavItems.map(({ to, label, icon: Icon, end }) => (
            <NavLink
              key={to}
              to={to}
              end={end}
              onClick={closeSidebar}
              className={({ isActive }) =>
                cn(
                  'app-nav-link',
                  isActive && 'app-nav-link-active'
                )
              }
            >
              <Icon className="h-3.5 w-3.5" /> {label}
            </NavLink>
          ))}
        </nav>
        <div className="app-sidebar-settings">
          <NavLink
            to="/settings"
            onClick={closeSidebar}
            className={({ isActive }) =>
              cn('app-nav-link', isActive && 'app-nav-link-active')
            }
          >
            <Settings className="h-3.5 w-3.5" /> Settings
          </NavLink>
          <NavLink
            to="/help"
            onClick={closeSidebar}
            className={({ isActive }) =>
              cn('app-nav-link', isActive && 'app-nav-link-active')
            }
          >
            <BookOpen className="h-3.5 w-3.5" /> Setup guides
          </NavLink>
        </div>
        <div className="app-sidebar-footer">
          <div className="app-theme-switcher-wrap">
            <ThemeSwitcher variant="compact" />
          </div>
          <p className="app-user-email" title={user?.email}>{user?.email}</p>
          <Button variant="outline" size="sm" className="w-full" onClick={logout}>
            <LogOut className="h-3.5 w-3.5" /> Sign out
          </Button>
        </div>
      </aside>

      {/* Main content */}
      <div className="app-main">
        {/* Mobile top bar */}
        <header className="app-mobile-header">
          <Button variant="ghost" size="icon" onClick={() => setSidebarOpen(true)}>
            <Menu className="h-4 w-4" />
          </Button>
          <img src={logo} alt="" className="h-5 w-5" />
          <span className="app-mobile-title">Stashboard</span>
        </header>
        <main className="app-content">
          <Outlet />
        </main>
      </div>
    </div>
  )
}
