import { Suspense, lazy } from 'react'
import { BrowserRouter, Navigate, Route, Routes } from 'react-router-dom'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { AppLayout } from './components/AppLayout'
import { ProtectedRoute } from './components/ProtectedRoute'
import { useServerThemeSync, useThemeSync } from './lib/use-theme'

const Login = lazy(() => import('./pages/Login').then((m) => ({ default: m.Login })))
const Register = lazy(() => import('./pages/Register').then((m) => ({ default: m.Register })))
const Dashboard = lazy(() => import('./pages/Dashboard').then((m) => ({ default: m.Dashboard })))
const Categories = lazy(() => import('./pages/Categories').then((m) => ({ default: m.Categories })))
const Tags = lazy(() => import('./pages/Tags').then((m) => ({ default: m.Tags })))
const Backup = lazy(() => import('./pages/settings/Backup').then((m) => ({ default: m.Backup })))
const DockerInstances = lazy(() => import('./pages/DockerInstances').then((m) => ({ default: m.DockerInstances })))
const Proxmox = lazy(() => import('./pages/Proxmox').then((m) => ({ default: m.Proxmox })))
const ForgotPassword = lazy(() => import('./pages/ForgotPassword').then((m) => ({ default: m.ForgotPassword })))
const ResetPassword = lazy(() => import('./pages/ResetPassword').then((m) => ({ default: m.ResetPassword })))
const ConfirmEmail = lazy(() => import('./pages/ConfirmEmail').then((m) => ({ default: m.ConfirmEmail })))
const ConfirmEmailChange = lazy(() => import('./pages/ConfirmEmailChange').then((m) => ({ default: m.ConfirmEmailChange })))
const Account = lazy(() => import('./pages/settings/Account').then((m) => ({ default: m.Account })))
const NotificationSettings = lazy(() => import('./pages/settings/NotificationSettings').then((m) => ({ default: m.NotificationSettings })))
const HostTerminalSettings = lazy(() => import('./pages/settings/HostTerminalSettings').then((m) => ({ default: m.HostTerminalSettings })))
const ContainerExecSettings = lazy(() => import('./pages/settings/ContainerExecSettings').then((m) => ({ default: m.ContainerExecSettings })))
const ProxmoxConsoleSettings = lazy(() => import('./pages/settings/ProxmoxConsoleSettings').then((m) => ({ default: m.ProxmoxConsoleSettings })))
const ProxmoxUpdatesSettings = lazy(() => import('./pages/settings/ProxmoxUpdatesSettings').then((m) => ({ default: m.ProxmoxUpdatesSettings })))
const ProxmoxDestroySettings = lazy(() => import('./pages/settings/ProxmoxDestroySettings').then((m) => ({ default: m.ProxmoxDestroySettings })))
const ProxmoxCreateSettings = lazy(() => import('./pages/settings/ProxmoxCreateSettings').then((m) => ({ default: m.ProxmoxCreateSettings })))
const ProxmoxCloneSettings = lazy(() => import('./pages/settings/ProxmoxCloneSettings').then((m) => ({ default: m.ProxmoxCloneSettings })))
const ProxmoxRestoreSettings = lazy(() => import('./pages/settings/ProxmoxRestoreSettings').then((m) => ({ default: m.ProxmoxRestoreSettings })))
const ImageCleanupSettings = lazy(() => import('./pages/settings/ImageCleanupSettings').then((m) => ({ default: m.ImageCleanupSettings })))
const HealthCheckSettings = lazy(() => import('./pages/settings/HealthCheckSettings').then((m) => ({ default: m.HealthCheckSettings })))
const MqttSettings = lazy(() => import('./pages/settings/MqttSettings').then((m) => ({ default: m.MqttSettings })))
const AuditLog = lazy(() => import('./pages/AuditLog').then((m) => ({ default: m.AuditLog })))
const Help = lazy(() => import('./pages/Help').then((m) => ({ default: m.Help })))
const HelpProxmoxApi = lazy(() => import('./pages/help/HelpProxmoxApi').then((m) => ({ default: m.HelpProxmoxApi })))
const HelpProxmoxSsh = lazy(() => import('./pages/help/HelpProxmoxSsh').then((m) => ({ default: m.HelpProxmoxSsh })))
const HelpDockerSsh = lazy(() => import('./pages/help/HelpDockerSsh').then((m) => ({ default: m.HelpDockerSsh })))
const HelpDockerTls = lazy(() => import('./pages/help/HelpDockerTls').then((m) => ({ default: m.HelpDockerTls })))
const Settings = lazy(() => import('./pages/Settings').then((m) => ({ default: m.Settings })))
const StatusPages = lazy(() => import('./pages/settings/StatusPages').then((m) => ({ default: m.StatusPages })))
const PublicStatusPage = lazy(() => import('./pages/PublicStatusPage').then((m) => ({ default: m.PublicStatusPage })))

const qc = new QueryClient({
  defaultOptions: { queries: { retry: 1, refetchOnWindowFocus: false } },
})

function ThemeBoot() {
  useThemeSync()
  useServerThemeSync()
  return null
}

export default function App() {
  return (
    <QueryClientProvider client={qc}>
      <ThemeBoot />
      <BrowserRouter>
        <Suspense fallback={<div className="app-boot-loading">Loading...</div>}>
          <Routes>
            <Route path="/login" element={<Login />} />
            <Route path="/register" element={<Register />} />
            <Route path="/forgot-password" element={<ForgotPassword />} />
            <Route path="/reset-password" element={<ResetPassword />} />
            <Route path="/confirm-email" element={<ConfirmEmail />} />
            {/* V10.2 — public, unauthenticated status page. Outside ProtectedRoute / AppLayout. */}
            <Route path="/status/:slug" element={<PublicStatusPage />} />
            <Route element={<ProtectedRoute />}>
              <Route path="/confirm-email-change" element={<ConfirmEmailChange />} />
              <Route element={<AppLayout />}>
                <Route path="/" element={<Dashboard />} />
                <Route path="/docker" element={<DockerInstances />} />
                <Route path="/proxmox" element={<Proxmox />} />
                <Route path="/categories" element={<Categories />} />
                <Route path="/tags" element={<Tags />} />
                <Route path="/audit" element={<AuditLog />} />
                <Route path="/help" element={<Help />}>
                  <Route index element={<Navigate to="/help/proxmox-api" replace />} />
                  <Route path="proxmox-api" element={<HelpProxmoxApi />} />
                  <Route path="proxmox-ssh" element={<HelpProxmoxSsh />} />
                  <Route path="docker-ssh" element={<HelpDockerSsh />} />
                  <Route path="docker-tls" element={<HelpDockerTls />} />
                </Route>
                <Route path="/settings" element={<Settings />}>
                  <Route index element={<Navigate to="/settings/account" replace />} />
                  <Route path="notifications" element={<NotificationSettings />} />
                  <Route path="host-terminal" element={<HostTerminalSettings />} />
                  <Route path="container-exec" element={<ContainerExecSettings />} />
                  <Route path="proxmox-console" element={<ProxmoxConsoleSettings />} />
                  <Route path="proxmox-updates" element={<ProxmoxUpdatesSettings />} />
                  <Route path="proxmox-destroy" element={<ProxmoxDestroySettings />} />
                  <Route path="proxmox-create" element={<ProxmoxCreateSettings />} />
                  <Route path="proxmox-clone" element={<ProxmoxCloneSettings />} />
                  <Route path="proxmox-restore" element={<ProxmoxRestoreSettings />} />
                  <Route path="image-cleanup" element={<ImageCleanupSettings />} />
                  <Route path="health-checks" element={<HealthCheckSettings />} />
                  <Route path="status-pages" element={<StatusPages />} />
                  <Route path="home-assistant" element={<MqttSettings />} />
                  <Route path="backup" element={<Backup />} />
                  <Route path="account" element={<Account />} />
                </Route>
              </Route>
            </Route>
            <Route path="*" element={<Navigate to="/" replace />} />
          </Routes>
        </Suspense>
      </BrowserRouter>
    </QueryClientProvider>
  )
}
