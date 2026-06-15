import { useEffect, useMemo, useState } from 'react'
import { AlertTriangle, ChevronDown, ChevronRight, Info, Plus, Trash2 } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { streamContainerStats } from '@/lib/docker-stats'
import { useComposeAllocation } from '@/lib/queries'
import { MIB, bytesToComposeSize, parseComposeSize } from '@/lib/compose-size'
import type { ComposeResourceConstraints, ComposeUlimit } from '@/lib/types'
import { formatBytes } from '@/lib/utils'

/**
 * V7.2 — the resource-constraints editor for one Compose service, rendered
 * below the V7.1 basic-fields form inside the service tab. Numeric inputs +
 * sliders for CPU / memory limits and reservations are bounded by the target
 * host's actual capacity (from the V3.5 `docker stats` stream — `onlineCpus`
 * and `memoryLimitBytes`), with a companion panel showing host capacity, what
 * other containers already reserve (`useComposeAllocation`), and this draft —
 * plus an inline over-commit warning. cpu_shares / pids_limit / oom_* /
 * shm_size / ulimits live behind an "Advanced" disclosure.
 *
 * Controlled: the parent ({@link ComposeServiceEditForm}) owns the state and
 * folds it into the single atomic save, so untouched values round-trip
 * byte-for-byte (the inputs keep the file's original strings until edited).
 */

// ── helpers ──────────────────────────────────────────────────────────────────

const parseCpu = (s: string | null): number => {
  const n = parseFloat(s ?? '')
  return Number.isFinite(n) ? n : 0
}

// ── field help text (hover tooltips) ─────────────────────────────────────────

const HELP = {
  cpuLimit: 'Hard ceiling on CPU cores this service may use (e.g. 1.5 = one and a half cores). The container is throttled once it hits the limit — it never gets more, even if the host is idle.',
  cpuReservation: 'Soft guarantee — CPU cores kept available for this service when the host is busy. It is not a cap: the container can still burst above this when cores are free.',
  memLimit: 'Hard ceiling on RAM. If the container exceeds it the kernel kills the process (OOM). Maps to deploy.resources.limits.memory or the legacy mem_limit.',
  memReservation: 'Soft RAM target the scheduler tries to keep free for this service under memory pressure. Not a hard cap — usage can exceed it.',
  cpuShares: 'Relative CPU weight versus other containers when the CPU is saturated (default 1024; double it for twice the share). Only matters under contention — it is a priority, not an absolute limit.',
  pidsLimit: 'Maximum number of processes/threads the container may create. Caps fork bombs and runaway thread leaks. Leave empty for unlimited.',
  oomScoreAdj: 'Bias for the kernel out-of-memory killer (-1000 … 1000). Lower values make this container less likely to be killed first when the host runs out of memory; higher makes it a preferred victim.',
  oomKillDisable: 'Stops the kernel from killing this container when the host runs out of memory. Use with care: without a memory limit a leaking container can freeze the whole host.',
  shmSize: 'Size of the container\'s /dev/shm shared-memory mount (e.g. 256m, 1g). Raise it for apps that need a large shared-memory segment — Chromium/Puppeteer, PostgreSQL, some ML tools. Default is 64m.',
  ulimits: 'Per-process kernel limits (soft = warning threshold, hard = absolute max). Common ones: nofile = max open file descriptors, nproc = max processes per user.',
} as const

// ── host capacity (one-shot stats sample) ────────────────────────────────────

interface HostCapacity {
  cpus: number
  memoryBytes: number
}

function useHostCapacity(connectionId: string, containerName: string | null) {
  const [capacity, setCapacity] = useState<HostCapacity | null>(null)
  useEffect(() => {
    if (!containerName) return
    const handle = streamContainerStats(
      connectionId, containerName, { oneShot: true },
      (s) => setCapacity({ cpus: s.onlineCpus, memoryBytes: s.memoryLimitBytes }),
      () => {},
    )
    return () => handle.abort()
  }, [connectionId, containerName])
  return containerName ? capacity : null
}

// ── a label with a hover-tooltip help icon ───────────────────────────────────

function HelpLabel({ text, hint }: { text: string; hint: string }) {
  return (
    <Label className="service-modal-label compose-res-label">
      <span>{text}</span>
      <span className="compose-res-help" title={hint} aria-label={hint} role="img">
        <Info className="h-3 w-3" />
      </span>
    </Label>
  )
}

// ── the form ─────────────────────────────────────────────────────────────────

export interface ComposeResourcesFormProps {
  connectionId: string
  project: string
  value: ComposeResourceConstraints
  onChange: (next: ComposeResourceConstraints) => void
  /** Name of a running container on this connection, for the host-capacity
   *  stats sample; `null` when the project has none deployed. */
  capacityContainerName: string | null
}

export function ComposeResourcesForm({
  connectionId, project, value, onChange, capacityContainerName,
}: ComposeResourcesFormProps) {
  const [advancedOpen, setAdvancedOpen] = useState(false)
  const [composeResOpen, setComposeResOpen] = useState(false)
  const capacity = useHostCapacity(connectionId, capacityContainerName)
  const allocation = useComposeAllocation(connectionId, project)

  const set = (partial: Partial<ComposeResourceConstraints>) => onChange({ ...value, ...partial })
  const legacy = value.convention === 'legacy'

  const cpuMax = capacity ? Math.max(1, capacity.cpus) : null
  const memMax = capacity?.memoryBytes ?? null

  return (
    <div>
      <button
        type="button"
        className="compose-res-toggle compose-tab-details-title"
        onClick={() => setComposeResOpen((o) => !o)}
        aria-expanded={composeResOpen}
        >
        {composeResOpen ? <ChevronDown className="h-3.5 w-3.5" /> : <ChevronRight className="h-3.5 w-3.5" />}
        <span className="compose-res-head">Resource constraints</span>
           <span className="compose-res-convention" title={legacy
              ? 'This file uses the legacy top-level cpus / mem_limit form'
              : 'This file uses the deploy.resources form (Compose v3.8+)'}>
              {legacy ? 'legacy form' : 'deploy form'}
            </span>
      </button>
      {composeResOpen && (      
        <div className={`compose-res ${composeResOpen ? 'open' : 'closed'}`}>
          <CapacityPanel value={value} capacity={capacity} allocation={allocation.data ?? null} />

          {/* ── CPU ──────────────────────────────────────────────────── */}
          <div className="compose-res-grid">
            <CpuField
              label="CPU limit" hint={HELP.cpuLimit} value={value.cpuLimit} max={cpuMax}
              onChange={(v) => set({ cpuLimit: v })}
            />
            <CpuField
              label="CPU reservation" hint={HELP.cpuReservation} value={value.cpuReservation} max={cpuMax}
              onChange={(v) => set({ cpuReservation: v })}
              disabled={legacy}
              disabledHint={legacy ? 'The legacy form has no CPU reservation key.' : undefined}
            />
            <MemField
              label="Memory limit" hint={HELP.memLimit} value={value.memLimit} maxBytes={memMax}
              onChange={(v) => set({ memLimit: v })}
            />
            <MemField
              label="Memory reservation" hint={HELP.memReservation} value={value.memReservation} maxBytes={memMax}
              onChange={(v) => set({ memReservation: v })}
            />
          </div>

          {/* ── Advanced ─────────────────────────────────────────────── */}
          <button
            type="button"
            className="compose-res-advanced-toggle"
            onClick={() => setAdvancedOpen((o) => !o)}
            aria-expanded={advancedOpen}
          >
            {advancedOpen ? <ChevronDown className="h-3.5 w-3.5" /> : <ChevronRight className="h-3.5 w-3.5" />}
            Advanced (cpu_shares, pids, ulimits, OOM, shm_size)
          </button>

          {advancedOpen && (
            <div className="compose-res-advanced">
              <div className="compose-edit-grid">
                <div className="service-modal-field">
                  <HelpLabel text="CPU shares" hint={HELP.cpuShares} />
                  <Input
                    type="number" inputMode="numeric" min={0}
                    value={value.cpuShares ?? ''}
                    onChange={(e) => set({ cpuShares: e.target.value === '' ? null : Number(e.target.value) })}
                    placeholder="1024"
                    className="font-mono text-[12px]"
                  />
                </div>
                <div className="service-modal-field">
                  <HelpLabel text="PID limit" hint={HELP.pidsLimit} />
                  <Input
                    type="number" inputMode="numeric" min={0}
                    value={value.pidsLimit ?? ''}
                    onChange={(e) => set({ pidsLimit: e.target.value === '' ? null : e.target.value })}
                    placeholder="200"
                    className="font-mono text-[12px]"
                  />
                </div>
                <div className="service-modal-field">
                  <HelpLabel text="OOM score adj" hint={HELP.oomScoreAdj} />
                  <Input
                    type="number" inputMode="numeric" min={-1000} max={1000}
                    value={value.oomScoreAdj ?? ''}
                    onChange={(e) => set({ oomScoreAdj: e.target.value === '' ? null : Number(e.target.value) })}
                    placeholder="-500"
                    className="font-mono text-[12px]"
                  />
                </div>
                <div className="service-modal-field">
                  <HelpLabel text="shm_size" hint={HELP.shmSize} />
                  <Input
                    value={value.shmSize ?? ''}
                    onChange={(e) => set({ shmSize: e.target.value === '' ? null : e.target.value })}
                    placeholder="64m"
                    className="font-mono text-[12px]"
                  />
                </div>
                <div className="service-modal-field compose-res-checkbox-field">
                  <label className="compose-res-checkbox">
                    <input
                      type="checkbox"
                      checked={value.oomKillDisable === true}
                      onChange={(e) => set({ oomKillDisable: e.target.checked ? true : null })}
                    />
                    Disable OOM killer
                    <span className="compose-res-help" title={HELP.oomKillDisable} aria-label={HELP.oomKillDisable} role="img">
                      <Info className="h-3 w-3" />
                    </span>
                  </label>
                </div>
              </div>

              <UlimitsEditor
                ulimits={value.ulimits}
                onChange={(ulimits) => set({ ulimits })}
              />
            </div>
          )}
        </div>      
      )}
    </div>
  )
}

// ── CPU field (number + slider) ───────────────────────────────────────────────

function CpuField({
  label, hint, value, max, onChange, disabled, disabledHint,
}: {
  label: string
  hint: string
  value: string | null
  max: number | null
  onChange: (v: string | null) => void
  disabled?: boolean
  disabledHint?: string
}) {
  return (
    <div className="service-modal-field">
      <HelpLabel text={label} hint={hint} />
      <div className="compose-res-row">
        <Input
          type="number" inputMode="decimal" min={0} step={0.1}
          value={value ?? ''}
          disabled={disabled}
          onChange={(e) => onChange(e.target.value === '' ? null : e.target.value)}
          placeholder="0.5"
          className="font-mono text-[12px] compose-res-num"
        />
        {max !== null && (
          <input
            type="range" min={0} max={max} step={0.1}
            value={Math.min(parseCpu(value), max)}
            disabled={disabled}
            onChange={(e) => onChange(e.target.value === '0' ? null : e.target.value)}
            className="compose-res-slider"
            aria-label={`${label} slider`}
          />
        )}
      </div>
      {disabled && disabledHint && <p className="compose-edit-hint">{disabledHint}</p>}
    </div>
  )
}

// ── Memory field (compose-size text + slider in bytes) ────────────────────────

function MemField({
  label, hint, value, maxBytes, onChange,
}: {
  label: string
  hint: string
  value: string | null
  maxBytes: number | null
  onChange: (v: string | null) => void
}) {
  const bytes = parseComposeSize(value)
  return (
    <div className="service-modal-field">
      <HelpLabel text={label} hint={hint} />
      <div className="compose-res-row">
        <Input
          value={value ?? ''}
          onChange={(e) => onChange(e.target.value === '' ? null : e.target.value)}
          placeholder="512m"
          className="font-mono text-[12px] compose-res-num"
        />
        {maxBytes !== null && maxBytes > 0 && (
          <input
            type="range" min={0} max={maxBytes} step={64 * MIB}
            value={Math.min(bytes ?? 0, maxBytes)}
            onChange={(e) => {
              const n = Number(e.target.value)
              onChange(n === 0 ? null : bytesToComposeSize(n))
            }}
            className="compose-res-slider"
            aria-label={`${label} slider`}
          />
        )}
      </div>
      {bytes !== null && <p className="compose-edit-hint">{formatBytes(bytes)}</p>}
    </div>
  )
}

// ── ulimits add/remove table ──────────────────────────────────────────────────

function UlimitsEditor({
  ulimits, onChange,
}: {
  ulimits: ComposeUlimit[]
  onChange: (next: ComposeUlimit[]) => void
}) {
  const update = (i: number, partial: Partial<ComposeUlimit>) =>
    onChange(ulimits.map((u, j) => (j === i ? { ...u, ...partial } : u)))
  const num = (v: string): number | null => (v === '' ? null : Number(v))

  return (
    <div className="service-modal-field">
      <HelpLabel text="ulimits" hint={HELP.ulimits} />
      {ulimits.map((u, i) => (
        <div key={i} className="compose-edit-row">
          <Input
            value={u.name}
            onChange={(e) => update(i, { name: e.target.value })}
            placeholder="nofile"
            className="font-mono text-[12px] compose-edit-pair-name"
            aria-label={`ulimit name ${i + 1}`}
          />
          <Input
            type="number" inputMode="numeric" min={0}
            value={u.soft ?? ''}
            onChange={(e) => update(i, { soft: num(e.target.value) })}
            placeholder="soft"
            className="font-mono text-[12px] compose-res-num"
            aria-label={`ulimit soft ${i + 1}`}
          />
          <Input
            type="number" inputMode="numeric" min={0}
            value={u.hard ?? ''}
            onChange={(e) => update(i, { hard: num(e.target.value) })}
            placeholder="hard"
            className="font-mono text-[12px] compose-res-num"
            aria-label={`ulimit hard ${i + 1}`}
          />
          <Button
            type="button" variant="ghost" size="sm"
            onClick={() => onChange(ulimits.filter((_, j) => j !== i))}
            title="Remove this ulimit"
          >
            <Trash2 className="h-3.5 w-3.5" />
          </Button>
        </div>
      ))}
      <Button
        type="button" variant="outline" size="sm"
        onClick={() => onChange([...ulimits, { name: '', soft: null, hard: null }])}
      >
        <Plus className="h-3.5 w-3.5" />
        <span className="label-text">Add ulimit</span>
      </Button>
    </div>
  )
}

// ── capacity panel ────────────────────────────────────────────────────────────

function CapacityPanel({
  value, capacity, allocation,
}: {
  value: ComposeResourceConstraints
  capacity: HostCapacity | null
  allocation: { containerCount: number; reservedCpus: number; reservedMemoryBytes: number } | null
}) {
  const draftCpu = parseCpu(value.cpuLimit)
  const draftMem = parseComposeSize(value.memLimit) ?? 0

  const overCommit = useMemo(() => {
    if (!capacity) return null
    const cpuUsed = (allocation?.reservedCpus ?? 0) + draftCpu
    const memUsed = (allocation?.reservedMemoryBytes ?? 0) + draftMem
    const parts: string[] = []
    if (draftCpu > 0 && cpuUsed > capacity.cpus + 1e-9)
      parts.push(`CPU: ${fmtCpu(cpuUsed)} of ${capacity.cpus} cores`)
    if (draftMem > 0 && memUsed > capacity.memoryBytes)
      parts.push(`memory: ${formatBytes(memUsed)} of ${formatBytes(capacity.memoryBytes)}`)
    return parts.length > 0 ? parts.join(' · ') : null
  }, [capacity, allocation, draftCpu, draftMem])

  if (!capacity) {
    return (
      <p className="compose-edit-hint compose-capacity-unknown">
        Host capacity unavailable — no running container on this host to sample.
        Sliders are hidden; enter values manually.
      </p>
    )
  }

  const pct = (used: number, total: number) => (total > 0 ? Math.round((used / total) * 100) : 0)

  return (
    <div className="compose-capacity">
      <div className="compose-capacity-line">
        <span><strong>Host capacity:</strong> {capacity.cpus} CPUs, {formatBytes(capacity.memoryBytes)}</span>
        {allocation && (
          <span>
            · allocated by other containers: {fmtCpu(allocation.reservedCpus)} CPUs
            ({pct(allocation.reservedCpus, capacity.cpus)} %),{' '}
            {formatBytes(allocation.reservedMemoryBytes)} ({pct(allocation.reservedMemoryBytes, capacity.memoryBytes)} %)
          </span>
        )}
        <span>· this service draft: {fmtCpu(draftCpu)} CPUs, {formatBytes(draftMem)}</span>
      </div>
      {overCommit && (
        <p className="compose-edit-warning" role="alert">
          <AlertTriangle className="h-3 w-3 inline" /> Over-commit — {overCommit}.
        </p>
      )}
    </div>
  )
}

const fmtCpu = (n: number): string => (Number.isInteger(n) ? String(n) : n.toFixed(2).replace(/\.?0+$/, ''))
