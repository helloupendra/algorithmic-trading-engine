import { describe, expect, it } from 'vitest'
import { readAccount, summariseYears } from './backtestAccount'
import type { BacktestDailyPnl, BacktestEquityPoint } from './types'

/** A curve point as the API sends it: equity is the balance, capital plus P&L. */
function point(atUtc: string, pnl: number, capital = 200_000): BacktestEquityPoint {
  return { atUtc, equity: capital + pnl, realized: pnl, unrealized: 0 }
}

describe('the account reading', () => {
  it('follows the balance, its lowest point and the fall from its best', () => {
    const reading = readAccount(
      [point('2026-01-01T04:00:00Z', 0), point('2026-01-02T04:00:00Z', 40_000),
       point('2026-01-03T04:00:00Z', -30_000), point('2026-01-04T04:00:00Z', 10_000)],
      200_000,
    )!

    expect(reading.initialCapital).toBe(200_000)
    expect(reading.lowest).toBe(170_000)
    expect(reading.lowestAtUtc).toBe('2026-01-03T04:00:00Z')
    expect(reading.final).toBe(210_000)
    expect(reading.ranOutAtUtc).toBeNull()
    expect(reading.drawdown).toBe(70_000)                    // 240,000 down to 170,000
    expect(Math.round(reading.drawdownPercent)).toBe(29)
  })

  it('says when the account ran out, and on which day first', () => {
    const reading = readAccount(
      [point('2026-01-01T04:00:00Z', -50_000), point('2026-01-02T04:00:00Z', -200_000),
       point('2026-01-03T04:00:00Z', -260_000)],
      200_000,
    )!
    expect(reading.ranOutAtUtc).toBe('2026-01-02T04:00:00Z')
    expect(reading.lowest).toBe(-60_000)
  })

  it('reads the balance the API sends rather than adding the capital to it again', () => {
    // Run 164: ₹10,00,000 at the start, ₹8,94,608.50 at the end, ₹8,80,001.50 at its lowest.
    const reading = readAccount(
      [point('2026-01-01T03:45:00Z', 0, 1_000_000), point('2026-03-02T09:59:00Z', -119_998.5, 1_000_000),
       point('2026-09-17T09:59:00Z', -105_391.5, 1_000_000)],
      1_000_000,
    )!
    expect(reading.lowest).toBe(880_001.5)
    expect(reading.final).toBe(894_608.5)
  })

  it('has nothing to say without a curve or without capital', () => {
    expect(readAccount([], 200_000)).toBeNull()
    expect(readAccount([point('2026-01-01T04:00:00Z', 0)], 0)).toBeNull()
  })
})

describe('the yearly summary', () => {
  it('adds the days up by calendar year, in order', () => {
    const daily: BacktestDailyPnl[] = [
      { date: '2024-12-30', pnl: 1_000, trades: 2 },
      { date: '2025-01-02', pnl: -500, trades: 1 },
      { date: '2025-03-04', pnl: 2_500, trades: 3 },
    ]
    expect(summariseYears(daily)).toEqual([
      { year: 2024, pnl: 1_000, trades: 2, days: 1, winningDays: 1 },
      { year: 2025, pnl: 2_000, trades: 4, days: 2, winningDays: 1 },
    ])
    expect(summariseYears([])).toEqual([])
  })
})
