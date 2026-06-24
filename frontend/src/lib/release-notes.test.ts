import { describe, it, expect } from 'vitest'
import { compareVersionsDesc, getReleaseNotes } from './release-notes'

describe('compareVersionsDesc', () => {
  it('orders newest first', () => {
    const versions = ['7.9.0', '8.0.0', '7.10.0', '8.0.1']
    expect([...versions].sort(compareVersionsDesc)).toEqual([
      '8.0.1',
      '8.0.0',
      '7.10.0',
      '7.9.0',
    ])
  })

  it('treats missing segments as zero', () => {
    expect(compareVersionsDesc('8.0', '8.0.0')).toBe(0)
    expect(compareVersionsDesc('8.1', '8.0.5')).toBeLessThan(0)
  })
})

describe('getReleaseNotes', () => {
  const notes = getReleaseNotes()

  it('loads the release-note markdown files', () => {
    expect(notes.length).toBeGreaterThanOrEqual(1)
  })

  it('parses frontmatter into structured fields', () => {
    const latest = notes[0]
    expect(latest.version).toMatch(/^\d+\.\d+\.\d+$/)
    expect(latest.title.length).toBeGreaterThan(0)
    // release notes are version-driven, never dated
    expect(latest.date).toBe('')
    // frontmatter must be stripped — the body should not start with it
    expect(latest.body.length).toBeGreaterThan(0)
    expect(latest.body.startsWith('---')).toBe(false)
    expect(latest.body).not.toMatch(/^version:/m)
  })

  it('returns releases newest first', () => {
    const sorted = [...notes].sort((a, b) => compareVersionsDesc(a.version, b.version))
    expect(notes.map((n) => n.version)).toEqual(sorted.map((n) => n.version))
  })
})
