/**
 * Pure helpers over a `StrategyListItem` shared by the Strategies tiles,
 * library cards and the Live runner. Kept out of the component modules so
 * those files export only components (Fast Refresh stays whole-file safe).
 */

import type { StrategyListItem } from './types'

/**
 * Underlyings a strategy is live on right now, each once, in start order,
 * e.g. ["BANKNIFTY", "NIFTY"]. Two accounts running it on NIFTY is one NIFTY
 * here — listed twice, it read as the same strategy deployed twice.
 */
export function activeUnderlyings(s: StrategyListItem): string[] {
  return [...new Set(s.activeRuns.map((r) => r.underlying))]
}

/** Whose account a run trades in, for grouping and labels; who started it on an older API. */
export function runOwner(run: { ownerName?: string | null; startedBy?: string | null }): string {
  return run.ownerName || run.startedBy || 'unknown account'
}

/** How many accounts are running this strategy right now. */
export function activeAccounts(s: StrategyListItem): number {
  return new Set(s.activeRuns.map(runOwner)).size
}

/** "Fulcrum · BANKNIFTY, NIFTY (2 accounts)" — one line per running strategy for tiles and lists. */
export function runningSummary(s: StrategyListItem): string {
  const on = activeUnderlyings(s)
  if (on.length === 0) return s.name
  const accounts = activeAccounts(s)
  return `${s.name} · ${on.join(', ')}${accounts > 1 ? ` (${accounts} accounts)` : ''}`
}
