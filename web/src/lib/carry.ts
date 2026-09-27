/**
 * Intraday vs carry forward: the rules and words around the per-position
 * "carry forward" tick.
 *
 * The owner, 27 Sep: "if I want to carry forward, there should be a tick there
 * and ticking it is enough. In strategies, even a single leg — I should be
 * able to do it." Built like a broker's MIS (intraday) and NRML (carry
 * forward), and the tick means a different thing in each place it appears:
 *
 *  - in the manual book an unticked position is intraday and is squared off at
 *    its exchange's close (NSE/BSE 15:30 IST, MCX at the MCX close); ticked, it
 *    is held overnight;
 *  - in a strategy run a ticked leg moves to the owner's manual book when the
 *    MARKET CLOSE stops the run. Any other stop — the Stop button, a risk rule,
 *    a runner that dies — squares off every leg: a protective stop that left
 *    legs behind would not be one.
 *
 * The API enforces all of it; this only says it, so the operator is never
 * surprised by what happens at 15:30.
 */

import type { LivePosition } from './types'

/** An MCX contract closes with the MCX evening session, not at 15:30. */
export function isMcx(symbol: string | null | undefined): boolean {
  return (symbol ?? '').trim().toUpperCase().startsWith('MCX:')
}

/** "the close (15:30 IST)" for NSE/BSE, "the MCX close" for commodities. */
export function closeName(symbol: string | null | undefined): string {
  return isMcx(symbol) ? 'the MCX close' : 'the close (15:30 IST)'
}

/** The one line under the positions table that says what the ticks do here. */
export function carryHint(isManualBook: boolean): string {
  return isManualBook
    ? 'Carry: unticked positions are intraday and are squared off at their exchange’s close (NSE/BSE 15:30 IST, MCX at its close); ticked ones are held overnight.'
    : 'Carry: a ticked leg moves to your manual book at the market close instead of being squared off. Stop, a risk-rule trip or a runner that dies still squares off every leg.'
}

/** What the Carry cell of one row shows and allows. */
export interface CarryControl {
  /** A checkbox is shown (open rows only). */
  show: boolean
  checked: boolean
  disabled: boolean
  /** The tooltip: why it is disabled, or what the tick does to this row. */
  title: string
}

export interface CarryContext {
  /** The viewer may change it (owner or admin) — the API's canControl. */
  canControl: boolean
  /** The run is live and not a recap — the API's canCarryForward. */
  canCarryForward: boolean
  isManualBook: boolean
}

export function carryControl(p: Pick<LivePosition, 'status' | 'carryForward' | 'symbol'>, ctx: CarryContext): CarryControl {
  const show = p.status === 'Open'
  const checked = !!p.carryForward
  if (!ctx.canControl) {
    return { show, checked, disabled: true, title: 'Only the owner of this run or an admin can change carry forward' }
  }
  if (!ctx.canCarryForward) {
    return { show, checked, disabled: true, title: 'This run is not live — nothing will carry its positions now' }
  }
  let title: string
  if (ctx.isManualBook) {
    title = checked
      ? `Carry forward: held overnight. Untick to square it off at ${closeName(p.symbol)}.`
      : `Intraday: squared off at ${closeName(p.symbol)}. Tick to hold it overnight.`
  } else {
    title = checked
      ? 'Moves to your manual book at the market close. Stop, a risk-rule trip or a runner that dies still squares it off.'
      : 'Squared off with the run at the close. Tick to move it to your manual book instead.'
  }
  return { show, checked, disabled: false, title }
}

/** The note under a row that changed books: where it went, or where it came from. */
export function carriedNote(
  p: Pick<LivePosition, 'status' | 'carriedFromRunId' | 'carriedFromStrategy'>,
): string | null {
  if (p.status === 'Carried') return 'carried to the manual book'
  if (p.carriedFromRunId != null) {
    return `from run #${p.carriedFromRunId}${p.carriedFromStrategy ? ` · ${p.carriedFromStrategy}` : ''}`
  }
  return null
}

/** "2 open · 3 closed · 1 carried" — carried legs are neither. */
export function positionCountsNote(positions: Pick<LivePosition, 'status'>[]): string {
  const open = positions.filter((p) => p.status === 'Open').length
  const carried = positions.filter((p) => p.status === 'Carried').length
  const closed = positions.length - open - carried
  return `${open} open · ${closed} closed${carried > 0 ? ` · ${carried} carried` : ''}`
}

/**
 * Added to the Stop confirmation when a ticked leg is open: the tick is for
 * the close, and pressing Stop now is a different decision.
 */
export function stopCarryWarning(positions: Pick<LivePosition, 'status' | 'carryForward'>[]): string | null {
  const ticked = positions.filter((p) => p.status === 'Open' && p.carryForward).length
  if (ticked === 0) return null
  return (
    `${ticked === 1 ? 'One leg is' : `${ticked} legs are`} ticked to carry forward. ` +
    'The tick applies only at the market close — Stop squares off every leg, ticked or not.'
  )
}

/** The hint under the ticket's tick. */
export function ticketCarryHint(carryForward: boolean, symbol: string | null | undefined): string {
  return carryForward
    ? 'Held overnight until you close it or it expires. Each order is its own position; change its tick in the book any time.'
    : `Intraday: squared off at ${closeName(symbol)}. Each order is its own position; change its tick in the book any time.`
}
