// V6.8 — pure health helpers shared by the PVE node card (Proxmox.tsx) and the
// node modal (NodeModal.tsx) so the summary badge on the card and the detailed
// metrics in the modal classify saturation identically. Thresholds are the sane
// global defaults the roadmap calls for; per-node overrides land with the
// alerting subsystem in V6.8.1.

import type { ProxmoxNodeStatus } from './types'

export type HealthLevel = 'ok' | 'warn' | 'crit'

/** Default warn/crit thresholds (percent) per metric. */
export const THRESHOLDS = {
  cpu: { warn: 80, crit: 95 },
  mem: { warn: 85, crit: 95 },
  root: { warn: 85, crit: 95 },
} as const

/** Classify a 0..100 percentage against a warn/crit pair. */
export function levelFor(pct: number | null, t: { warn: number; crit: number }): HealthLevel {
  if (pct == null) return 'ok'
  if (pct >= t.crit) return 'crit'
  if (pct >= t.warn) return 'warn'
  return 'ok'
}

/** used/total as a 0..100 percentage, or null when total is missing/zero. */
export function percent(used: number | null, total: number | null): number | null {
  if (used == null || total == null || total <= 0) return null
  return (used / total) * 100
}

export const cpuPercent = (s: ProxmoxNodeStatus): number | null =>
  s.cpuFraction == null ? null : s.cpuFraction * 100
export const memPercent = (s: ProxmoxNodeStatus): number | null => percent(s.memUsed, s.memTotal)
export const rootPercent = (s: ProxmoxNodeStatus): number | null => percent(s.rootUsed, s.rootTotal)

/** The worst level across CPU / RAM / root storage — drives the card badge. */
export function worstNodeLevel(s: ProxmoxNodeStatus): HealthLevel {
  const levels: HealthLevel[] = [
    levelFor(cpuPercent(s), THRESHOLDS.cpu),
    levelFor(memPercent(s), THRESHOLDS.mem),
    levelFor(rootPercent(s), THRESHOLDS.root),
  ]
  if (levels.includes('crit')) return 'crit'
  if (levels.includes('warn')) return 'warn'
  return 'ok'
}

/** Classify a disk's SMART health string + wearout into a level. */
export function diskLevel(health: string | null, wearoutPercent: number | null): HealthLevel {
  if (health && health.toUpperCase() !== 'PASSED' && health.toUpperCase() !== 'OK') return 'crit'
  if (wearoutPercent != null) {
    if (wearoutPercent <= 10) return 'crit'
    if (wearoutPercent <= 20) return 'warn'
  }
  return 'ok'
}

/** Classify a temperature against the chip's own high/crit thresholds, falling
 *  back to conservative defaults when the chip doesn't expose them. */
export function tempLevel(tempC: number | null, highC: number | null, critC: number | null): HealthLevel {
  if (tempC == null) return 'ok'
  const crit = critC ?? 90
  const warn = highC ?? 80
  if (tempC >= crit) return 'crit'
  if (tempC >= warn) return 'warn'
  return 'ok'
}

/** V7.2.1 — whether to show the connection-level scan-error banner. A
 *  connection-level error (e.g. "API unreachable: No route to host" from a scan
 *  that ran while the host was momentarily down) lingers on `lastError` until
 *  the next successful scan, but the node card polls independently and may
 *  already show the host online. When the live node status currently succeeds
 *  (`nodeReachable`), that banner is stale and contradicts the card, so it is
 *  suppressed — the host clearly responds right now. */
export const showConnectionError = (lastError: string | null | undefined, nodeReachable: boolean): boolean =>
  !!lastError && !nodeReachable

export type HealthMetric = 'cpu' | 'mem' | 'root' | 'storage' | 'disk' | 'temp'

/** The "root reason + suggested action" tooltip text for a degraded metric.
 *  Returns `undefined` for an `ok` level (no tooltip needed). Satisfies the
 *  UX requirement that degraded metrics expose why they're degraded and what to
 *  do about it. */
export function healthAdvice(metric: HealthMetric, level: HealthLevel): string | undefined {
  if (level === 'ok') return undefined
  const crit = level === 'crit'
  switch (metric) {
    case 'cpu':
      return crit
        ? 'CPU is saturated (≥95%). Suggested action: find the busiest guests in CPU/RAM → Live and stop/limit them, or add CPU capacity.'
        : 'CPU usage is high (≥80%). Suggested action: review the busiest guests in CPU/RAM → Live and rebalance load.'
    case 'mem':
      return crit
        ? 'Memory is nearly exhausted (≥95%). Suggested action: free or add RAM now — the node may start swapping or OOM-killing guests.'
        : 'Memory pressure is high (≥85%). Suggested action: reduce per-guest RAM allocations or add memory.'
    case 'root':
    case 'storage':
      return crit
        ? 'Storage is critically full (≥95%). Suggested action: free space immediately — a full pool can stop guests and backups.'
        : 'Storage is filling up (≥85%). Suggested action: prune old backups/images or expand the pool.'
    case 'disk':
      return crit
        ? 'SMART reports this drive as not healthy. Suggested action: back up its data and plan replacement; check the SMART attributes.'
        : 'SSD wearout is low. Suggested action: plan replacement before the drive reaches end of life.'
    case 'temp':
      return crit
        ? 'Temperature is at/above the critical threshold. Suggested action: reduce load and check cooling immediately.'
        : 'Temperature is near the high threshold. Suggested action: check airflow, fans, and dust.'
  }
}
