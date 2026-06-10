namespace Stashboard.Core.Enums;

/// <summary>
/// V6.8.1 — severity of a node-health metric, ordered so a numeric comparison
/// gives the worst level (<c>Crit &gt; Warn &gt; Ok</c>). The backend analogue of
/// the frontend's <c>HealthLevel</c> in <c>proxmox-node-health.ts</c> — the two
/// classify saturation identically so the card colour and the fired alert never
/// disagree.
/// </summary>
public enum HealthLevel
{
    Ok = 0,
    Warn = 1,
    Crit = 2,
}
