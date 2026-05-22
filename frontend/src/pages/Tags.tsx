import { useState } from 'react'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Badge } from '@/components/ui/badge'
import { useCreateTag, useDeleteTag, useTags } from '@/lib/queries'
import { Trash2 } from 'lucide-react'
import { parseApiErrors } from '@/lib/utils'
import '@/styles/management-pages.css'

export function Tags() {
  const { data = [] } = useTags()
  const create = useCreateTag()
  const del = useDeleteTag()
  const [name, setName] = useState('')
  const [fieldErrors, setFieldErrors] = useState<Record<string, string>>({})
  const [error, setError] = useState<string | null>(null)

  const add = async () => {
    if (!name.trim()) return
    setFieldErrors({})
    setError(null)
    try {
      await create.mutateAsync(name.trim())
      setName('')
    } catch (e: unknown) {
      const { fieldErrors: fe, globalError } = parseApiErrors(e)
      setFieldErrors(fe)
      setError(globalError)
    }
  }

  return (
    <>
      <h1 className="manage-page-title text-2xl font-semibold">Tags</h1>

      <div className="manage-create-row">
        <div className="manage-field-wrap">
          <Label>Name</Label>
          <Input
            value={name}
            onChange={(e) => setName(e.target.value)}
            className={fieldErrors['name'] ? 'border-destructive' : ''}
          />
          {fieldErrors['name'] && <p className="manage-field-error">{fieldErrors['name']}</p>}
        </div>
        <Button onClick={add} disabled={!name.trim() || create.isPending}>Add</Button>
      </div>
      {error && <p className="manage-error">{error}</p>}

      <div className="manage-list">
        {data.length === 0 && <p className="manage-empty">No tags yet.</p>}
        {data.map((t) => (
          <div key={t.id} className="manage-item tags-row">
            <Badge variant="secondary">{t.name}</Badge>
            <span className="manage-item-meta manage-grow">{t.serviceCount} service(s)</span>
            <Button variant="ghost" size="icon" onClick={() => del.mutate(t.id)}>
              <Trash2 className="h-3 w-3" />
            </Button>
          </div>
        ))}
      </div>
    </>
  )
}
