/**
 * Every open leg across books (GET /api/Positions/open), as Trade → Positions
 * reads them: grouped by account, then by run (the manual book first, since
 * it holds what was carried overnight), with each group's open P&L and theta,
 * and each mark's age.
 *
 * Two rules the page keeps:
 *
 * - A leg with no mark has no P&L. It is counted as unmarked and left out of
 *   the sums, never added in as ₹0.
 * - A mark older than 30 s is stale and says so, whatever the time of day: a
 *   closed market's last quote is still yesterday's price.
 */

import { runUserLabel } from './runHistory'
import type { OpenPosition } from './types'

/** A mark older than this is not "now". */
export const STALE_AFTER_S = 30

export type MarkState = 'fresh' | 'stale' | 'none'

/** Fresh, stale (older than 30 s, or of unknown age), or no mark at all. */
export function markState(p: Pick<OpenPosition, 'markPrice' | 'markAgeSeconds'>): MarkState {
  if (p.markPrice == null) return 'none'
  return p.markAgeSeconds != null && p.markAgeSeconds <= STALE_AFTER_S ? 'fresh' : 'stale'
}

/** "12 s", "4 min", "3 h", "2 d": how old a mark is. */
export function ageText(seconds: number): string {
  const s = Math.max(0, Math.round(seconds))
  if (s < 90) return `${s} s`
  const m = Math.round(s / 60)
  if (m < 90) return `${m} min`
  const h = Math.round(m / 60)
  if (h < 36) return `${h} h`
  return `${Math.round(h / 24)} d`
}

/** Sums over a set of legs. */
export interface LegSums {
  legs: number
  /** Unrealized P&L of the marked legs, before exit charges. */
  openPnl: number
  /** Legs with no mark, left out of `openPnl`. */
  unmarked: number
  /** Legs whose mark is older than 30 s (or of unknown age). */
  stale: number
  /** Theta in rupees a day over the legs that have greeks; null when none has. */
  thetaPerDay: number | null
}

function sums(legs: readonly OpenPosition[]): LegSums {
  let openPnl = 0
  let unmarked = 0
  let stale = 0
  let theta: number | null = null
  for (const p of legs) {
    const state = markState(p)
    if (state === 'none' || p.unrealizedPnl == null) unmarked++
    else openPnl += p.unrealizedPnl
    if (state === 'stale') stale++
    if (p.greeks) theta = (theta ?? 0) + p.greeks.thetaRupeesPerDay
  }
  return { legs: legs.length, openPnl, unmarked, stale, thetaPerDay: theta }
}

export interface PositionRun extends LegSums {
  runId: number
  strategyName: string
  isManualBook: boolean
  /** The underlyings its legs are on, in the order first opened. */
  underlyings: string[]
  /** Oldest first. */
  positions: OpenPosition[]
}

export interface PositionAccount extends LegSums {
  userId: number
  name: string
  runs: PositionRun[]
}

const opened = (p: OpenPosition) => Date.parse(p.openedUtc) || 0

/**
 * The legs by account (by user id, the admin first, as the Desk orders
 * accounts), then by run: the manual book first, then strategy runs in the
 * order they started (by run id); legs oldest first. `userId` narrows to one
 * account.
 */
export function groupPositions(positions: readonly OpenPosition[], userId: number | null = null): PositionAccount[] {
  const byAccount = new Map<number, OpenPosition[]>()
  for (const p of positions) {
    if (userId != null && p.userId !== userId) continue
    byAccount.set(p.userId, [...(byAccount.get(p.userId) ?? []), p])
  }
  return [...byAccount.entries()]
    .sort(([a], [b]) => a - b)
    .map(([id, legs]) => {
      const byRun = new Map<number, OpenPosition[]>()
      for (const p of legs) byRun.set(p.runId, [...(byRun.get(p.runId) ?? []), p])
      const runs: PositionRun[] = [...byRun.entries()]
        .map(([runId, own]) => {
          const sorted = [...own].sort((a, b) => opened(a) - opened(b) || a.positionId - b.positionId)
          return {
            runId,
            strategyName: sorted[0].strategyName,
            isManualBook: sorted[0].isManualBook,
            underlyings: [...new Set(sorted.map((p) => p.underlying.toUpperCase()))],
            positions: sorted,
            ...sums(sorted),
          }
        })
        .sort((a, b) => Number(b.isManualBook) - Number(a.isManualBook) || a.runId - b.runId)
      return { userId: id, name: runUserLabel(legs[0].userName, id), runs, ...sums(legs) }
    })
}

/** The sums over every account shown. */
export function totalSums(accounts: readonly PositionAccount[]): LegSums {
  return sums(accounts.flatMap((a) => a.runs.flatMap((r) => r.positions)))
}

/** The accounts holding a leg, for the account filter: by user id, with their names. */
export function positionAccounts(positions: readonly OpenPosition[]): Array<{ id: number; name: string }> {
  const seen = new Map<number, string>()
  for (const p of positions) if (!seen.has(p.userId)) seen.set(p.userId, runUserLabel(p.userName, p.userId))
  return [...seen.entries()].sort(([a], [b]) => a - b).map(([id, name]) => ({ id, name }))
}
