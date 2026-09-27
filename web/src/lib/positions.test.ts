import { describe, expect, it } from 'vitest'

import { positionValues } from './positions'

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
