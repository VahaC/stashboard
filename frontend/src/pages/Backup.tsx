import { useState } from 'react'
import { Button } from '@/components/ui/button'
import { api } from '@/lib/api'
import { useQueryClient } from '@tanstack/react-query'
import { qk } from '@/lib/queries'
import { getApiErrorMessage } from '@/lib/utils'
import '@/styles/management-pages.css'

export function Backup() {
  const qc = useQueryClient()
  const [busy, setBusy] = useState(false)
  const [status, setStatus] = useState<string | null>(null)

  const exportNow = async () => {
    setBusy(true); setStatus(null)
    try {
      const resp = await api.get('/api/backup/export', { responseType: 'blob' })
      const url = URL.createObjectURL(resp.data as Blob)
      const a = document.createElement('a')
      a.href = url
      a.download = `stashboard-backup-${new Date().toISOString().slice(0, 19).replace(/[-T:]/g, '')}.json`
      document.body.appendChild(a); a.click(); document.body.removeChild(a)
      URL.revokeObjectURL(url)
    } finally { setBusy(false) }
  }

  const importFile = async (e: React.ChangeEvent<HTMLInputElement>) => {
    const file = e.target.files?.[0]
    if (!file) return
    setBusy(true); setStatus(null)
    try {
      const fd = new FormData(); fd.append('file', file)
      const resp = await api.post('/api/backup/import', fd)
      setStatus(`Imported ${resp.data.imported} services.`)
      qc.invalidateQueries({ queryKey: qk.services })
      qc.invalidateQueries({ queryKey: qk.categories })
      qc.invalidateQueries({ queryKey: qk.tags })
    } catch (e: unknown) {
      setStatus(`Import failed: ${getApiErrorMessage(e, 'An unexpected error occurred.')}`)
    } finally { setBusy(false); e.target.value = '' }
  }

  return (
    <>
      <h1 className="manage-page-title text-2xl font-semibold">Backup</h1>

      <div className="backup-warning">
        Exports include decrypted credential values in plain text. Treat the JSON file as a password file.
      </div>

      <div className="backup-sections">
        <div className="backup-card">
          <h2 className="backup-card-title">Export</h2>
          <Button onClick={exportNow} disabled={busy}>Download backup JSON</Button>
        </div>

        <div className="backup-card">
          <h2 className="backup-card-title">Import</h2>
          <label className="backup-upload">
            Choose file
            <input type="file" accept=".json,application/json" onChange={importFile} disabled={busy} className="sr-only" />
          </label>
          {status && <p className="backup-status">{status}</p>}
        </div>
      </div>
    </>
  )
}
