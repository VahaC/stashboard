import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { useCategories, useDeleteCategory, useUpsertCategory } from '@/lib/queries'
import { Pencil, Trash2, X } from 'lucide-react'
import { parseApiErrors } from '@/lib/utils'
import { useState } from 'react'
import '@/styles/management-pages.css'

export function Categories() {
  const { data = [] } = useCategories()
  const upsert = useUpsertCategory()
  const del = useDeleteCategory()
  const [name, setName] = useState('')
  const [color, setColor] = useState('#6c757d')
  const [editingId, setEditingId] = useState<string | null>(null)
  const [editingName, setEditingName] = useState('')
  const [editingColor, setEditingColor] = useState('#6c757d')
  const [fieldErrors, setFieldErrors] = useState<Record<string, string>>({})
  const [error, setError] = useState<string | null>(null)

  const add = async () => {
    if (!name.trim()) return
    setFieldErrors({})
    setError(null)
    try {
      await upsert.mutateAsync({ data: { name: name.trim(), color } })
      setName(''); setColor('#6c757d')
    } catch (e: unknown) {
      const { fieldErrors: fe, globalError } = parseApiErrors(e)
      setFieldErrors(fe)
      setError(globalError)
    }
  }

  const startEdit = (id: string, currentName: string, currentColor: string) => {
    setEditingId(id)
    setEditingName(currentName)
    setEditingColor(currentColor)
    setFieldErrors({})
    setError(null)
  }

  const cancelEdit = () => {
    setEditingId(null)
    setEditingName('')
    setEditingColor('#6c757d')
  }

  const saveEdit = async () => {
    if (!editingId || !editingName.trim()) return
    setFieldErrors({})
    setError(null)
    try {
      await upsert.mutateAsync({ id: editingId, data: { name: editingName.trim(), color: editingColor } })
      cancelEdit()
    } catch (e: unknown) {
      const { fieldErrors: fe, globalError } = parseApiErrors(e)
      setFieldErrors(fe)
      setError(globalError)
    }
  }

  return (
    <>
      <h1 className="manage-page-title text-2xl font-semibold">Categories</h1>

      <div className="manage-create-row">
        <div className="manage-field-wrap">
          <Label>Name</Label>
          <Input
            value={name}
            onChange={(e) => setName(e.target.value)}
            className={fieldErrors['name'] && !editingId ? 'border-destructive' : ''}
          />
          {fieldErrors['name'] && !editingId && <p className="manage-field-error">{fieldErrors['name']}</p>}
        </div>
        <div className="manage-color-wrap">
          <Label>Color</Label>
          <input type="color" value={color} onChange={(e) => setColor(e.target.value)} className="manage-color-input" />
        </div>
        <Button onClick={add} disabled={!name.trim() || upsert.isPending}>Add</Button>
      </div>
      {error && <p className="manage-error">{error}</p>}

      <div className="manage-list">
        {data.length === 0 && <p className="manage-empty">No categories yet.</p>}
        {data.map((c) => (
          <div key={c.id} className="manage-item">
            {editingId === c.id ? (
              <div className="manage-item-edit">
                <div className="manage-field-wrap">
                  <Input
                    value={editingName}
                    onChange={(e) => setEditingName(e.target.value)}
                    className={fieldErrors['name'] ? 'border-destructive' : ''}
                  />
                  {fieldErrors['name'] && <p className="manage-field-error">{fieldErrors['name']}</p>}
                </div>
                <input type="color" value={editingColor} onChange={(e) => setEditingColor(e.target.value)} className="manage-color-input" />
                <div className="manage-item-edit-actions">
                  <Button size="sm" onClick={saveEdit} disabled={!editingName.trim() || upsert.isPending}>Save</Button>
                  <Button variant="outline" size="sm" onClick={cancelEdit}><X className="h-4 w-4" /> Cancel</Button>
                </div>
              </div>
            ) : (
              <div className="manage-item-view">
                <span className="manage-color-chip" style={{ backgroundColor: c.color }} />
                <span className="manage-item-name">{c.name}</span>
                <span className="manage-item-meta">{c.serviceCount} service(s)</span>
                <Button variant="ghost" size="icon" onClick={() => startEdit(c.id, c.name, c.color)}>
                  <Pencil className="h-3 w-3" />
                </Button>
                <Button variant="ghost" size="icon" onClick={() => del.mutate(c.id)}>
                  <Trash2 className="h-3 w-3" />
                </Button>
              </div>
            )}
          </div>
        ))}
      </div>
    </>
  )
}
