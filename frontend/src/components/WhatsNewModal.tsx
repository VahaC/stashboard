import { useMemo, useState } from 'react'
import ReactMarkdown from 'react-markdown'
import remarkGfm from 'remark-gfm'
import { Sparkles } from 'lucide-react'
import {
  Dialog,
  DialogContent,
  DialogHeader,
  DialogTitle,
  DialogDescription,
} from '@/components/ui/dialog'
import { getReleaseNotes } from '@/lib/release-notes'
import { cn } from '@/lib/utils'
import '@/styles/whats-new.css'

interface WhatsNewModalProps {
  open: boolean
  onOpenChange: (open: boolean) => void
}

export function WhatsNewModal({ open, onOpenChange }: WhatsNewModalProps) {
  const notes = useMemo(() => getReleaseNotes(), [])

  // Group releases by major version, preserving the newest-first order.
  const groups = useMemo(() => {
    const byMajor = new Map<string, typeof notes>()
    for (const n of notes) {
      const major = n.version.split('.')[0]
      const list = byMajor.get(major) ?? []
      list.push(n)
      byMajor.set(major, list)
    }
    return Array.from(byMajor, ([major, items]) => ({ major, items }))
  }, [notes])

  const [selected, setSelected] = useState(notes[0]?.version ?? '')
  // Only the group holding the currently-shown release starts expanded; the user
  // can open others freely.
  const [expanded, setExpanded] = useState<Set<string>>(
    () => new Set(selected ? [selected.split('.')[0]] : []),
  )
  const current = notes.find((n) => n.version === selected)

  const toggle = (major: string) =>
    setExpanded((prev) => {
      const next = new Set(prev)
      if (next.has(major)) next.delete(major)
      else next.add(major)
      return next
    })

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="whats-new-dialog">
        <DialogHeader className="whats-new-header">
          <DialogTitle className="flex items-center justify-center gap-2">
            <Sparkles className="h-5 w-5" /> What's new
          </DialogTitle>
          <DialogDescription>
            Detailed notes for every Stashboard release.
          </DialogDescription>
        </DialogHeader>

        {notes.length === 0 ? (
          <p className="whats-new-empty">No release notes yet.</p>
        ) : (
          <div className="whats-new-body">
            <nav className="whats-new-tree" aria-label="Releases">
              {groups.map((g) => {
                const isOpen = expanded.has(g.major)
                return (
                  <div key={g.major} className="whats-new-group">
                    <button
                      type="button"
                      className="whats-new-group-head"
                      onClick={() => toggle(g.major)}
                      aria-expanded={isOpen}
                    >
                      <span className="whats-new-toggle">{isOpen ? '▾' : '▸'}</span>
                      v{g.major}.X.X
                    </button>
                    {isOpen && (
                      <div className="whats-new-group-items">
                        {g.items.map((n) => (
                          <button
                            key={n.version}
                            type="button"
                            onClick={() => setSelected(n.version)}
                            className={cn(
                              'whats-new-version',
                              n.version === selected && 'whats-new-version-active',
                            )}
                          >
                            v{n.version}
                          </button>
                        ))}
                      </div>
                    )}
                  </div>
                )
              })}
            </nav>

            <article className="whats-new-content">
              {current && (
                <>
                  <div className="whats-new-content-head">
                    <h3>
                      v{current.version}
                      {current.title && <> — {current.title}</>}
                    </h3>
                  </div>
                  <div className="whats-new-markdown">
                    <ReactMarkdown remarkPlugins={[remarkGfm]}>
                      {current.body}
                    </ReactMarkdown>
                  </div>
                </>
              )}
            </article>
          </div>
        )}
      </DialogContent>
    </Dialog>
  )
}
