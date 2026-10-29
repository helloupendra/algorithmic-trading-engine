import { describe, expect, it } from 'vitest'

import { backtestPositionsCsv, backtestPositionsCsvName, positionRowClass, positionValues } from './positions'
import type { BacktestPosition } from './types'

/**
 * A strategy leg carried forward at the close leaves its run without being
 * sold. Its row there has no exit and no move of its own — the P&L from then
 * on is the manual book's — so the table must not print "+0.0 pts · 0.0%" as
 * if it were a trade that went nowhere.
 */
describe('positionValues', () => {
  const base = { side: 'SELL' as const, quantity: 150, entryPrice: 100, mark: null, pnl: 0 }

  it('derives a closed row’s move from its realized P&L', () => {
    const v = positionValues({ ...base, status: 'Closed', pnl: 1500 })
    expect(v.pnlPoints).toBe(10)
    expect(v.pnlPercent).toBe(10)
  })

  it('gives a carried row no move at all', () => {
    const v = positionValues({ ...base, status: 'Carried' })
    expect(v.pnlPoints).toBeNull()
    expect(v.pnlPercent).toBeNull()
    expect(v.currentValue).toBeNull()
    // What left the run is still sized, so the row says what was carried.
    expect(v.entryValue).toBe(15000)
  })
})

describe('positionRowClass', () => {
  it('dims a row that is no longer open while another still is', () => {
    expect(positionRowClass(false, true, true)).toBe('pos-row--closed')
    // A run stopped without squaring off still holds its open legs.
    expect(positionRowClass(false, true, false)).toBe('pos-row--closed')
  })

  it('leaves an open row at full strength', () => {
    expect(positionRowClass(true, true, true)).toBe('')
    expect(positionRowClass(true, true, false)).toBe('')
  })

  it('keeps the closed rows dimmed while the run is flat between two trades', () => {
    expect(positionRowClass(false, false, true)).toBe('pos-row--closed')
  })

  it('dims nothing once the run is over with no row open: a finished backtest, a run stopped and squared off', () => {
    expect(positionRowClass(false, false, false)).toBe('')
  })
})

describe('backtestPositionsCsv', () => {
  const leg = (over: Partial<BacktestPosition>): BacktestPosition => ({
    id: 1, groupId: 'g1', symbol: 'NSE:NIFTY26N1024500CE', contract: null, side: 'SELL', lots: 2, lotSize: 75, quantity: 150,
    status: 'Closed', entryPrice: 100, exitPrice: 90, pnl: 1_500, openedUtc: '2026-10-28T04:00:00Z', closedUtc: '2026-10-28T09:45:00Z',
    exitReason: 'Target hit, 10 points', ...over,
  })
  const csv = backtestPositionsCsv([
    leg({}),
    leg({ id: 2, side: 'BUY', status: 'Open', exitPrice: 104.5, pnl: 675, closedUtc: null, exitReason: null, contract: { underlying: 'NIFTY', strike: 24500, optionType: 'PE', expiryDate: '2026-11-10', label: 'NIFTY 24500 PE · 10 Nov' } }),
  ])
  const [head, closed, open] = csv.split('\r\n')

  it('heads every column the run table shows, and the times in IST', () => {
    expect(head).toBe(
      'Contract,Symbol,Group,Side,Lots,Lot size,Quantity,Entry,Exit,Entry value (₹),Value at mark (₹),P&L (₹),P&L points,P&L %,Exit reason,Status,Opened (IST),Closed (IST)',
    )
  })

  it("writes a closed leg with the table's own derived values, and its reason quoted for its comma", () => {
    expect(closed).toBe(
      'NIFTY 24500 CE · 10 Nov,NSE:NIFTY26N1024500CE,g1,SELL,2,75,150,100,90,15000,,1500,10,10,"Target hit, 10 points",Closed,2026-10-28T09:30:00+05:30,2026-10-28T15:15:00+05:30',
    )
  })

  it("writes an open leg at its last mark, by the API's contract label, with no close", () => {
    expect(open).toBe(
      'NIFTY 24500 PE · 10 Nov,NSE:NIFTY26N1024500CE,g1,BUY,2,75,150,100,104.5,15000,15675,675,4.5,4.5,,Open,2026-10-28T09:30:00+05:30,',
    )
  })

  it('names the file by the run, its strategy, its underlying and its days', () => {
    expect(
      backtestPositionsCsvName({ runId: 412, strategyName: 'GhostTangentCrossings', underlying: 'NIFTY', fromDate: '2026-01-01', toDate: '2026-10-28T00:00:00' }),
    ).toBe('backtest-412-GhostTangentCrossings-NIFTY-2026-01-01-2026-10-28.csv')
  })
})
