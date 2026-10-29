import { describe, expect, it } from 'vitest'
import { maxDrawdownEpisode, underwater } from './drawdown'
import type { BacktestEquityPoint } from './types'

/** One balance per IST session close (10:00Z = 15:30 IST), day n of January 2026. */
function curve(...balances: number[]): BacktestEquityPoint[] {
  return balances.map((equity, i) => ({
    atUtc: `2026-01-${String(i + 1).padStart(2, '0')}T10:00:00Z`,
    equity,
    realized: equity - 200_000,
    unrealized: 0,
  }))
}

const day = (n: number) => `2026-01-${String(n).padStart(2, '0')}T10:00:00Z`

describe('the underwater curve', () => {
  it('is the balance less the best balance so far, zero at every new high', () => {
    const series = underwater(curve(200_000, 210_000, 189_000, 215_000))
    expect(series.map((p) => p.drawdown)).toEqual([0, 0, -21_000, 0])
    expect(series.map((p) => p.peak)).toEqual([200_000, 210_000, 210_000, 215_000])
    expect(series[2].drawdownPercent).toBeCloseTo(-10, 10)
  })

  it('reads the points in time order whatever order they came in', () => {
    const [a, b, c] = curve(200_000, 190_000, 205_000)
    expect(underwater([c, a, b]).map((p) => p.atUtc)).toEqual([a.atUtc, b.atUtc, c.atUtc])
  })

  it('is empty for an empty run', () => {
    expect(underwater([])).toEqual([])
    expect(underwater(undefined)).toEqual([])
  })
})

describe('the deepest fall', () => {
  it('names its peak, its trough and the day it was made good', () => {
    const episode = maxDrawdownEpisode(curve(200_000, 220_000, 198_000, 187_000, 205_000, 221_000, 215_000))!
    expect(episode.amount).toBe(33_000)                        // 220,000 down to 187,000
    expect(episode.percent).toBeCloseTo(15, 10)
    expect(episode.peakUtc).toBe(day(2))
    expect(episode.troughUtc).toBe(day(4))
    expect(episode.recoveredUtc).toBe(day(6))                  // the first balance at or above 220,000
    expect(episode.troughEquity).toBe(187_000)
  })

  it('says it never recovered when the run ended under the peak', () => {
    const episode = maxDrawdownEpisode(curve(200_000, 230_000, 180_000, 210_000, 229_999))!
    expect(episode.amount).toBe(50_000)
    expect(episode.recoveredUtc).toBeNull()
  })

  it('counts a return to exactly the peak as recovered', () => {
    expect(maxDrawdownEpisode(curve(200_000, 190_000, 200_000))!.recoveredUtc).toBe(day(3))
  })

  it('starts the fall at the last of two equal peaks', () => {
    // 210,000 is reached on day 2 and again on day 4; the fall to 180,000 began on day 4.
    const episode = maxDrawdownEpisode(curve(200_000, 210_000, 205_000, 210_000, 180_000, 211_000))!
    expect(episode.peakUtc).toBe(day(4))
    expect(episode.amount).toBe(30_000)
    expect(episode.recoveredUtc).toBe(day(6))
  })

  it('keeps the first of two falls of the same depth, as the API does', () => {
    const episode = maxDrawdownEpisode(curve(200_000, 180_000, 200_000, 180_000))!
    expect(episode.peakUtc).toBe(day(1))
    expect(episode.troughUtc).toBe(day(2))
    expect(episode.recoveredUtc).toBe(day(3))
  })

  it('measures from the first point, as the API does, not from the capital', () => {
    // A first balance under the capital (charges on the first bar) is the start, not a fall.
    const episode = maxDrawdownEpisode(curve(199_000, 198_000))!
    expect(episode.amount).toBe(1_000)
    expect(episode.peakUtc).toBe(day(1))
  })

  it('is nothing for a curve that never fell, or an empty run', () => {
    expect(maxDrawdownEpisode(curve(200_000, 200_000, 201_000))).toBeNull()
    expect(maxDrawdownEpisode([])).toBeNull()
    expect(maxDrawdownEpisode(null)).toBeNull()
  })
})
