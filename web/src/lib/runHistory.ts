/**
 * Pure helpers over a `LiveRunSummary` shared by the Run history table, the
 * Strategies overview and the run detail header. Kept out of the component
 * modules so those files export only components (Fast Refresh stays
 * whole-file safe).
 */

import type { LiveRunSummary, LiveRunStatus } from './types'

/**
 * The short form of a RUN_STOPPED reason for a badge or a status cell; the
 * full text goes in the title. The API's reasons are "<what>: <numbers>",
 * "<what> (<detail>)" or "<what>; <detail>":
 *   "Stop loss hit: P&L −₹5,120 ≤ −₹5,000"   → "Stop loss hit"
 *   "Market closed (15:30 IST)"               → "Market closed"
 *   "Stopped by admin"                        → "Stopped by admin"
 *   "Runner exited (code 1)"                  → "Runner exited"
 *   "API restarted; runner not found"         → "API restarted"
 */
export function shortStopReason(reason: string | null | undefined): string | null {
  if (!reason) return null
  const t = reason.trim()
  if (t === '') return null
  let cut = t.length
  for (const sep of [':', ' (', ';', ' — ']) {
    const i = t.indexOf(sep)
    if (i > 0 && i < cut) cut = i
  }
  return t.slice(0, cut).trim()
}

/** Badge tone of a run's status; a Stopped run reads as a warning so its reason is looked at. */
export function runStatusTone(status: LiveRunStatus | string): 'live' | 'warn' | 'neg' | 'pos' | 'neutral' {
  switch (status) {
    case 'Running':
      return 'live'
    case 'Stopping':
    case 'Stopped':
      return 'warn'
    case 'Failed':
      return 'neg'
    case 'Completed':
      return 'pos'
    default:
      return 'neutral'
  }
}

/** The user a run belongs to, by name — or "user <id>" when the user row is gone (a deleted user's runs stay). */
export function runUserLabel(userName: string | null | undefined, userId: number): string {
  return userName && userName.trim() !== '' ? userName : `user ${userId}`
}

/** Seconds a run has been (or was) alive: the API's figure, else derived from the timestamps. */
export function runDurationSeconds(
  run: Pick<LiveRunSummary, 'durationSeconds' | 'startedUtc' | 'stoppedUtc' | 'isActive'>,
  now: number = Date.now(),
): number | null {
  if (run.durationSeconds != null && !run.isActive) return run.durationSeconds
  if (!run.startedUtc) return run.durationSeconds ?? null
  const start = new Date(run.startedUtc).getTime()
  if (Number.isNaN(start)) return run.durationSeconds ?? null
  const end = run.isActive || !run.stoppedUtc ? now : new Date(run.stoppedUtc).getTime()
  if (Number.isNaN(end)) return run.durationSeconds ?? null
  return Math.max(0, Math.floor((end - start) / 1000))
}

/** The P&L a history row shows: realized, plus the open book while the run is live. */
export function runNetPnl(run: Pick<LiveRunSummary, 'netPnl' | 'unrealizedPnl' | 'isActive'>): number {
  return run.isActive ? run.netPnl + (run.unrealizedPnl ?? 0) : run.netPnl
}

/** Human line for the detail header: "Stopped by admin · 12 Sep 14:02" etc. is composed by the page; this is the "by" part. */
export function stoppedByLabel(by: string | null | undefined): string | null {
  if (!by) return null
  switch (by.toLowerCase()) {
    case 'runner':
      return 'the runner'
    case 'api':
      return 'the API'
    case 'system':
      return 'the system'
    case 'risk-guard':
    case 'riskguard':
    case 'guard':
      return 'the risk guard'
    default:
      return by
  }
}
