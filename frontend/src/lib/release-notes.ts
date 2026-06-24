// Release notes are authored as individual markdown files in
// `src/release-notes/*.md`, each with a small frontmatter block:
//
//   ---
//   version: 8.0.0
//   date: 2026-06-19
//   title: Clone & snapshot LXC
//   ---
//   <markdown body…>
//
// They are bundled at build time and surfaced in the "What's new" modal.

export interface ReleaseNote {
  version: string
  date: string
  title: string
  body: string
}

const files = import.meta.glob('../release-notes/*.md', {
  query: '?raw',
  import: 'default',
  eager: true,
}) as Record<string, string>

/** Parse the leading `--- … ---` frontmatter; returns the fields and the body. */
function parse(raw: string): ReleaseNote | null {
  const text = raw.charCodeAt(0) === 0xfeff ? raw.slice(1) : raw // drop a leading BOM
  const match = /^---\r?\n([\s\S]*?)\r?\n---\r?\n?([\s\S]*)$/.exec(text)
  if (!match) return null
  const [, front, body] = match
  const fields: Record<string, string> = {}
  for (const line of front.split(/\r?\n/)) {
    const kv = /^([A-Za-z]+):\s*(.*)$/.exec(line.trim())
    if (kv) fields[kv[1].toLowerCase()] = kv[2].trim().replace(/^["']|["']$/g, '')
  }
  if (!fields.version) return null
  return {
    version: fields.version,
    date: fields.date ?? '',
    title: fields.title ?? '',
    body: body.trim(),
  }
}

/** Compare two semver-ish strings descending (newest first). */
export function compareVersionsDesc(a: string, b: string): number {
  const pa = a.split('.').map(Number)
  const pb = b.split('.').map(Number)
  for (let i = 0; i < Math.max(pa.length, pb.length); i++) {
    const d = (pb[i] ?? 0) - (pa[i] ?? 0)
    if (d !== 0) return d
  }
  return 0
}

/** All release notes, newest first. */
export function getReleaseNotes(): ReleaseNote[] {
  return Object.values(files)
    .map(parse)
    .filter((n): n is ReleaseNote => n !== null)
    .sort((a, b) => compareVersionsDesc(a.version, b.version))
}
