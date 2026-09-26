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

/**
 * Underlyings the signed-in account cannot start this strategy on, because
 * it already runs it there: the API refuses a second run in the same account
 * only. Another account's run of the same thing is not a clash — two books,
 * one signal — but until 28 Sep the launch dialog greyed the row out and told
 * the admin to stop that run first, which would have squared off the other
 * account's book. A run whose owner is unknown (an API older than 27 Sep)
 * still blocks, as before.
 */
export function blockedUnderlyings(s: StrategyListItem, userId: number | null | undefined): Set<string> {
  return new Set(
    s.activeRuns
      .filter((r) => r.ownerUserId == null || userId == null || r.ownerUserId === userId)
      .map((r) => r.underlying.toUpperCase()),
  )
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

/**
 * A run's P&L after the charges of its fills so far — the figure the run
 * history reports for the same run. An API that sends no net (older than
 * 28 Sep) leaves the gross, which is what these pages showed before.
 */
export function liveNet(pnl: { total: number; net?: number | null }): number {
  return pnl.net ?? pnl.total
}

/** Realized after charges, for "realized today": what the run history will say once the run ends. */
export function realizedNet(pnl: { realized: number; charges?: number | null }): number {
  return pnl.realized - (pnl.charges ?? 0)
}
