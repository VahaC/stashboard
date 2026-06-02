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
const Backup = lazy(() => import('./pages/Backup').then((m) => ({ default: m.Backup })))
const DockerInstances = lazy(() => import('./pages/DockerInstances').then((m) => ({ default: m.DockerInstances })))
const ForgotPassword = lazy(() => import('./pages/ForgotPassword').then((m) => ({ default: m.ForgotPassword })))
const ResetPassword = lazy(() => import('./pages/ResetPassword').then((m) => ({ default: m.ResetPassword })))
const ConfirmEmail = lazy(() => import('./pages/ConfirmEmail').then((m) => ({ default: m.ConfirmEmail })))
const ConfirmEmailChange = lazy(() => import('./pages/ConfirmEmailChange').then((m) => ({ default: m.ConfirmEmailChange })))
const Account = lazy(() => import('./pages/Account').then((m) => ({ default: m.Account })))
const NotificationSettings = lazy(() => import('./pages/NotificationSettings').then((m) => ({ default: m.NotificationSettings })))
const HostTerminalSettings = lazy(() => import('./pages/HostTerminalSettings').then((m) => ({ default: m.HostTerminalSettings })))
const ContainerExecSettings = lazy(() => import('./pages/ContainerExecSettings').then((m) => ({ default: m.ContainerExecSettings })))
const ImageCleanupSettings = lazy(() => import('./pages/ImageCleanupSettings').then((m) => ({ default: m.ImageCleanupSettings })))
const HealthCheckSettings = lazy(() => import('./pages/HealthCheckSettings').then((m) => ({ default: m.HealthCheckSettings })))
const AuditLog = lazy(() => import('./pages/AuditLog').then((m) => ({ default: m.AuditLog })))

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
            <Route element={<ProtectedRoute />}>
              <Route path="/confirm-email-change" element={<ConfirmEmailChange />} />
              <Route element={<AppLayout />}>
                <Route path="/" element={<Dashboard />} />
                <Route path="/docker" element={<DockerInstances />} />
                <Route path="/categories" element={<Categories />} />
                <Route path="/tags" element={<Tags />} />
                <Route path="/backup" element={<Backup />} />
                <Route path="/notifications" element={<NotificationSettings />} />
                <Route path="/host-terminal" element={<HostTerminalSettings />} />
                <Route path="/container-exec" element={<ContainerExecSettings />} />
                <Route path="/image-cleanup" element={<ImageCleanupSettings />} />
                <Route path="/health-checks" element={<HealthCheckSettings />} />
                <Route path="/audit" element={<AuditLog />} />
                <Route path="/account" element={<Account />} />
              </Route>
            </Route>
            <Route path="*" element={<Navigate to="/" replace />} />
          </Routes>
        </Suspense>
      </BrowserRouter>
    </QueryClientProvider>
  )
}
