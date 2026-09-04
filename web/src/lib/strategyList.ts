/**
 * Pure helpers over a `StrategyListItem` shared by the Strategies tiles,
 * library cards and the Live runner. Kept out of the component modules so
 * those files export only components (Fast Refresh stays whole-file safe).
 */

import type { StrategyListItem } from './types'

/** Underlyings a strategy is live on right now, in start order, e.g. ["BANKNIFTY", "NIFTY"]. */
export function activeUnderlyings(s: StrategyListItem): string[] {
  return s.activeRuns.map((r) => r.underlying)
}

/** "Titli · BANKNIFTY, NIFTY" — one line per running strategy for tiles and lists. */
export function runningSummary(s: StrategyListItem): string {
  const on = activeUnderlyings(s)
  return on.length > 0 ? `${s.name} · ${on.join(', ')}` : s.name
}
