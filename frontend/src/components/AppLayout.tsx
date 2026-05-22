import { useState } from 'react'
import { Link, NavLink, Outlet, useNavigate } from 'react-router-dom'
import { Button } from '@/components/ui/button'
import { ThemeSwitcher } from '@/components/ThemeSwitcher'
import { useAuthStore } from '@/lib/auth-store'
import { api } from '@/lib/api'
import { cn } from '@/lib/utils'
import logo from '@/assets/logo.svg'
import { LayoutGrid, Tags as TagsIcon, FolderTree, Database, LogOut, UserCog, Menu, X, Container, Bell } from 'lucide-react'
import '@/styles/app-layout.css'

const navItems = [
  { to: '/', label: 'Services', icon: LayoutGrid, end: true },
  { to: '/docker', label: 'Docker', icon: Container, end: false },
  { to: '/categories', label: 'Categories', icon: FolderTree, end: false },
  { to: '/tags', label: 'Tags', icon: TagsIcon, end: false },
  { to: '/backup', label: 'Backup', icon: Database, end: false },
  { to: '/notifications', label: 'Notifications', icon: Bell, end: false },
  { to: '/account', label: 'Account', icon: UserCog, end: false },
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
          </Link>
          <Button variant="ghost" size="icon" className="md:hidden" onClick={closeSidebar}>
            <X className="h-4 w-4" />
          </Button>
        </div>
        <nav className="app-sidebar-nav">
          {navItems.map(({ to, label, icon: Icon, end }) => (
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
