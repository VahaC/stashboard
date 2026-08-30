import type { ProxmoxNodeAlert, ProxmoxNodeAlertThresholdValues } from '@/lib/types'

/** Client-side mirror of the controller's threshold validation: percentages
 *  1..100, °C 1..150, and warn < crit for any pair where both are supplied.
 *  Exported for unit testing. */
export function validateThresholds(t: ProxmoxNodeAlertThresholdValues): string | null {
  const pct = (v: number | null, name: string) =>
    v != null && (v < 1 || v > 100) ? `${name} must be between 1 and 100.` : null
  const temp = (v: number | null, name: string) =>
    v != null && (v < 1 || v > 150) ? `${name} must be between 1 and 150.` : null
  const pair = (warn: number | null, crit: number | null, name: string) =>
    warn != null && crit != null && warn >= crit ? `${name} warn must be below crit.` : null
  return pct(t.cpuWarn, 'CPU warn') ?? pct(t.cpuCrit, 'CPU crit')
    ?? pct(t.memWarn, 'RAM warn') ?? pct(t.memCrit, 'RAM crit')
    ?? pct(t.storageWarn, 'Storage warn') ?? pct(t.storageCrit, 'Storage crit')
    ?? temp(t.tempWarn, 'Temp warn') ?? temp(t.tempCrit, 'Temp crit')
    ?? pair(t.cpuWarn, t.cpuCrit, 'CPU')
    ?? pair(t.memWarn, t.memCrit, 'RAM')
    ?? pair(t.storageWarn, t.storageCrit, 'Storage')
    ?? pair(t.tempWarn, t.tempCrit, 'Temp')
}

export function alertCategoryLabel(category: string): string {
  switch (category) {
    case 'cpu': return 'CPU'
    case 'memory': return 'Memory'
    case 'storage': return 'Storage'
    case 'thermal': return 'Thermal'
    case 'smart': return 'SMART'
    case 'network': return 'Network'
    default: return category
  }
}

export function alertDetail(a: ProxmoxNodeAlert): string {
  const v = a.value == null ? '?' : Math.round(a.value).toString()
  const th = a.threshold == null ? '?' : Math.round(a.threshold).toString()
  switch (a.category) {
    case 'cpu':
    case 'memory':
    case 'storage':
      return `${v}% (threshold ${th}%)`
    case 'thermal':
      return `${v}°C (threshold ${th}°C)`
    case 'network':
      return `+${v} errors since last check (threshold ${th})`
    case 'smart':
      return a.value == null ? 'SMART health is not PASSED' : `SSD wearout ${v}% (threshold ${th}%)`
    default:
      return `${v} (threshold ${th})`
  }
}
