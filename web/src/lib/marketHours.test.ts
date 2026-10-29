import { describe, expect, it } from 'vitest'

import {
  CASH_CLOSE_IST,
  derivativesCloseIst,
  isDerivativeSymbol,
  istDayOf,
  nseCloseIstOf,
  utcHm,
} from './marketHours'

/**
 * NSE and BSE close at 15:30 until 6 Dec 2026; from 7 Dec the options and futures close at 15:45 while the
 * cash market, and so the indices, keep 15:30 (SEBI circular of 15 Oct 2026). The same rule as the API's
 * EquityMarketHours and the engine's core/market_hours.py.
 */
describe('derivativesCloseIst', () => {
  it('is 15:30 up to 6 Dec 2026 and 15:45 from 7 Dec', () => {
    expect(derivativesCloseIst('2026-10-30')).toBe('15:30')
    expect(derivativesCloseIst('2026-12-06')).toBe('15:30')
    expect(derivativesCloseIst('2026-12-07')).toBe('15:45')
    expect(derivativesCloseIst('2027-01-04')).toBe('15:45')
    expect(CASH_CLOSE_IST).toBe('15:30')
  })
})

describe('nseCloseIstOf', () => {
  it('keeps a share and an index at 15:30 and moves an option or a future', () => {
    expect(nseCloseIstOf('NSE:NIFTY50-INDEX', '2026-12-07')).toBe('15:30')
    expect(nseCloseIstOf('NSE:BAJAJ-AUTO-EQ', '2026-12-07')).toBe('15:30')
    expect(nseCloseIstOf('NSE:BAJAJ-AUTO26DECFUT', '2026-12-07')).toBe('15:45')
    expect(nseCloseIstOf('BSE:SENSEX26D1080000PE', '2026-12-07')).toBe('15:45')
    expect(nseCloseIstOf('NSE:NIFTY26D0824500CE', '2026-12-04')).toBe('15:30')
  })

  it('reads a symbol the way the desk stores it', () => {
    expect(isDerivativeSymbol('nse:nifty26decfut')).toBe(true)
    expect(isDerivativeSymbol('NSE:NIFTY2692924500CE')).toBe(true)
    expect(isDerivativeSymbol('NSE:NIFTY50-INDEX')).toBe(false)
    expect(isDerivativeSymbol(null)).toBe(false)
  })
})

describe('the IST helpers', () => {
  it('turn an instant into its IST day and an IST time into the UTC one', () => {
    expect(istDayOf(Date.parse('2026-12-06T18:29:59Z'))).toBe('2026-12-06')
    expect(istDayOf(Date.parse('2026-12-06T18:30:00Z'))).toBe('2026-12-07')
    expect(utcHm('15:30')).toBe('10:00')
    expect(utcHm('15:45')).toBe('10:15')
  })
})
