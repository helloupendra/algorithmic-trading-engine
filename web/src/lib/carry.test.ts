import { describe, expect, it } from 'vitest'

import {
  carriedNote,
  carryControl,
  carryHint,
  closeName,
  positionCountsNote,
  stopCarryWarning,
  ticketCarryHint,
} from './carry'
import type { PositionStatus } from './types'

/**
 * The words around the carry-forward tick (27 Sep: "if I want to carry
 * forward, there should be a tick there and ticking it is enough"). The tick
 * means one thing in the manual book (unticked = squared off at the close)
 * and another in a strategy run (ticked = moved to the book at the close, and
 * only at the close) — a tooltip that described the wrong one would tell the
 * owner his leg is safe overnight when Stop is about to square it off.
 */

const NIFTY_CALL = 'NSE:NIFTY2692924500CE'
const CRUDE = 'MCX:CRUDEOIL26OCTFUT'

const row = (status: PositionStatus, carryForward = false, symbol = NIFTY_CALL) => ({ status, carryForward, symbol })

describe('closeName', () => {
  it('names the exchange’s own close', () => {
    expect(closeName(NIFTY_CALL)).toBe('the close (15:30 IST)')
    expect(closeName('BSE:SENSEX2610185000CE')).toBe('the close (15:30 IST)')
    expect(closeName(CRUDE)).toBe('the MCX close')
    expect(closeName('mcx:goldm26octfut')).toBe('the MCX close')
  })
})

describe('carryControl', () => {
  const owner = { canControl: true, canCarryForward: true }

  it('offers the tick on open rows only', () => {
    expect(carryControl(row('Open'), { ...owner, isManualBook: true }).show).toBe(true)
    expect(carryControl(row('Closed'), { ...owner, isManualBook: true }).show).toBe(false)
    expect(carryControl(row('Carried', true), { ...owner, isManualBook: false }).show).toBe(false)
  })

  it('is disabled, and says why, for someone who may not change it', () => {
    const c = carryControl(row('Open', true), { canControl: false, canCarryForward: true, isManualBook: false })
    expect(c.disabled).toBe(true)
    expect(c.checked).toBe(true)
    expect(c.title).toMatch(/owner of this run or an admin/)
  })

  it('is disabled on a run that is no longer live', () => {
    const c = carryControl(row('Open'), { canControl: true, canCarryForward: false, isManualBook: false })
    expect(c.disabled).toBe(true)
    expect(c.title).toMatch(/not live/)
  })

  it('in the manual book, says when an unticked position will be squared off', () => {
    expect(carryControl(row('Open'), { ...owner, isManualBook: true }).title).toBe(
      'Intraday: squared off at the close (15:30 IST). Tick to hold it overnight.',
    )
    expect(carryControl(row('Open', false, CRUDE), { ...owner, isManualBook: true }).title).toMatch(/the MCX close/)
    expect(carryControl(row('Open', true), { ...owner, isManualBook: true }).title).toMatch(/^Carry forward: held overnight/)
  })

  it('in a strategy run, says the tick is for the close and not for Stop', () => {
    const ticked = carryControl(row('Open', true), { ...owner, isManualBook: false })
    expect(ticked.disabled).toBe(false)
    expect(ticked.title).toMatch(/manual book at the market close/)
    expect(ticked.title).toMatch(/Stop, a risk-rule trip or a runner that dies still squares it off/)
    expect(carryControl(row('Open'), { ...owner, isManualBook: false }).title).toMatch(/^Squared off with the run/)
  })

  it('reads an API from before the tick as unticked', () => {
    expect(carryControl({ status: 'Open', symbol: NIFTY_CALL }, { ...owner, isManualBook: true }).checked).toBe(false)
  })
})

describe('carryHint', () => {
  it('explains each place in its own terms', () => {
    expect(carryHint(true)).toMatch(/unticked positions are intraday/)
    expect(carryHint(false)).toMatch(/moves to your manual book at the market close/)
    expect(carryHint(false)).toMatch(/Stop, a risk-rule trip or a runner that dies still squares off every leg/)
  })
})

describe('carriedNote', () => {
  it('says where a carried leg went and where a book row came from', () => {
    expect(carriedNote({ status: 'Carried' })).toBe('carried to the manual book')
    expect(carriedNote({ status: 'Open', carriedFromRunId: 412, carriedFromStrategy: 'Ghost' })).toBe('from run #412 · Ghost')
    expect(carriedNote({ status: 'Closed', carriedFromRunId: 412, carriedFromStrategy: null })).toBe('from run #412')
    expect(carriedNote({ status: 'Open' })).toBeNull()
  })
})

describe('positionCountsNote', () => {
  it('counts carried legs apart from open and closed ones', () => {
    expect(positionCountsNote([row('Open'), row('Closed'), row('Closed'), row('Carried')])).toBe(
      '1 open · 2 closed · 1 carried',
    )
    expect(positionCountsNote([row('Open'), row('Closed')])).toBe('1 open · 1 closed')
  })
})

describe('stopCarryWarning', () => {
  it('warns only when an open leg is ticked', () => {
    expect(stopCarryWarning([row('Open'), row('Closed', true)])).toBeNull()
    expect(stopCarryWarning([row('Open', true)])).toMatch(/^One leg is ticked.*Stop squares off every leg/)
    expect(stopCarryWarning([row('Open', true), row('Open', true)])).toMatch(/^2 legs are ticked/)
  })
})

describe('ticketCarryHint', () => {
  it('names the close an intraday order will be squared off at', () => {
    expect(ticketCarryHint(false, NIFTY_CALL)).toMatch(/^Intraday: squared off at the close \(15:30 IST\)/)
    expect(ticketCarryHint(false, CRUDE)).toMatch(/^Intraday: squared off at the MCX close/)
    expect(ticketCarryHint(true, CRUDE)).toMatch(/^Held overnight/)
  })

  it('says each order is its own position, so ticks never merge', () => {
    expect(ticketCarryHint(false, NIFTY_CALL)).toMatch(/Each order is its own position/)
    expect(ticketCarryHint(true, NIFTY_CALL)).toMatch(/Each order is its own position/)
  })
})
