import { describe, expect, it } from 'vitest'
import { compareFlags, compareHref, curveValues, parseCompareIds, runStats, togglePick } from './backtestCompare'
import type { BacktestMetrics, BacktestPosition, BacktestRunView } from './types'

const METRICS: BacktestMetrics = {
  closedPositions: 4,
  winning: 3,
  losing: 1,
  winRatePercent: 75,
  grossProfit: 9_000,
  grossLoss: 3_000,
  profitFactor: 3,
  averageWin: 3_000,
  averageLoss: 3_000,
  expectancy: 1_500,
  maxDrawdownPercent: 1.5,
  maxDrawdownAmount: 3_000,
  largestWin: 4_000,
  largestLoss: -3_000,
  tradingDays: 20,
  profitableDays: 3,
}

function position(id: number, closedUtc: string, pnl: number): BacktestPosition {
  return {
    id, groupId: `g${id}`, symbol: 'NSE:NIFTY26JAN25000CE', contract: null, side: 'BUY', lots: 1, lotSize: 75,
    quantity: 75, status: 'Closed', entryPrice: 100, exitPrice: 110, pnl, openedUtc: closedUtc, closedUtc, exitReason: null,
  }
}

function view(over: Partial<BacktestRunView> = {}): BacktestRunView {
  return {
    runId: 12, strategyId: 1, strategyName: 'Ghost', underlying: 'NIFTY', spotSymbol: 'NSE:NIFTY50-INDEX',
    resolution: '5m', fromDate: '2026-01-01', toDate: '2026-03-31', lots: 1, lotSize: 75, lotSizeSource: 'master',
    stopLoss: null, target: null, eodSquareOffIst: '15:15', chargesPerLot: 0, initialCapital: 200_000,
    parametersJson: '{}', status: 'Completed', lastError: null, startedUtc: null, completedUtc: null, stopReason: null,
    progress: null,
    pnl: { realized: 6_000, unrealized: 0, total: 6_000, charges: 0, returnPercent: 3 },
    metrics: METRICS,
    daily: [],
    positions: [
      position(1, '2026-01-05T09:55:00Z', 4_000),
      position(2, '2026-01-06T09:55:00Z', 3_000),
      position(3, '2026-02-02T09:55:00Z', -3_000),
      position(4, '2026-03-02T09:55:00Z', 2_000),
    ],
    activity: [],
    dataNotes: [],
    equityCurve: [],
    ...over,
  }
}

describe('a compare link', () => {
  it('reads each run id once, in order, and at most four', () => {
    expect(parseCompareIds('12,15,15, 17,x,-3,0,21,30')).toEqual({ ids: [12, 15, 17, 21], dropped: 1 })
    expect(parseCompareIds('')).toEqual({ ids: [], dropped: 0 })
    expect(parseCompareIds(null)).toEqual({ ids: [], dropped: 0 })
    expect(parseCompareIds('1.5,2e3')).toEqual({ ids: [], dropped: 0 })
  })

  it('is a page address that can be shared', () => {
    expect(compareHref([12, 15])).toBe('/research/backtests/compare?ids=12,15')
  })

  it('is picked by ticks that join at the end, leave on a second tick, and stop at four', () => {
    expect(togglePick([], 12)).toEqual([12])
    expect(togglePick([12, 15], 9)).toEqual([12, 15, 9])
    expect(togglePick([12, 15, 9], 15)).toEqual([12, 9])
    expect(togglePick([1, 2, 3, 4], 5)).toEqual([1, 2, 3, 4])
  })
})

describe('a run in the comparison', () => {
  it('carries the API figures and the best and worst month', () => {
    const stats = runStats(view())
    expect(stats).toMatchObject({
      netPnl: 6_000,
      trades: 4,
      winRatePercent: 75,
      averageWin: 3_000,
      averageLoss: 3_000,
      profitFactor: 3,
      maxDrawdown: 3_000,
      costsLabel: 'none',
      sessions: 20,
    })
    expect(stats.bestMonth).toEqual({ year: 2026, month: 1, pnl: 7_000, trades: 2 })
    expect(stats.worstMonth).toEqual({ year: 2026, month: 2, pnl: -3_000, trades: 1 })
  })

  it('reads a run with no loss as having no profit factor to divide, not a factor of 0', () => {
    const stats = runStats(view({ metrics: { ...METRICS, losing: 0, grossLoss: 0, profitFactor: 0, averageLoss: 0 } }))
    expect(stats.profitFactor).toBe('no-losses')
    expect(stats.averageLoss).toBeNull()
  })

  it('has no rate, average or month for a run that closed nothing', () => {
    const stats = runStats(view({
      positions: [],
      metrics: { ...METRICS, closedPositions: 0, winning: 0, losing: 0, winRatePercent: 0, grossProfit: 0, grossLoss: 0, profitFactor: 0, averageWin: 0, averageLoss: 0 },
    }))
    expect(stats).toMatchObject({ winRatePercent: null, averageWin: null, averageLoss: null, profitFactor: null, bestMonth: null, worstMonth: null })
  })

  it('nets its months of a flat rate, and says when the real costs could not be', () => {
    expect(runStats(view({ chargesPerLot: 40 })).bestMonth).toMatchObject({ month: 1, pnl: 7_000 - 160 })
    const modelled = runStats(view({ parametersJson: '{"costs":{"slippage_percent":0.5}}' }))
    expect(modelled.monthsBeforeStatutory).toBe(true)
    expect(modelled.costsLabel).toBe('real costs · 0.5% slippage a side')
  })
})

describe('what makes runs not like for like', () => {
  it('says nothing about runs that differ only in strategy', () => {
    expect(compareFlags([runStats(view()), runStats(view({ runId: 15, strategyName: 'Fulcrum' }))])).toEqual([])
  })

  it('names each difference with every run’s own value', () => {
    const flags = compareFlags([
      runStats(view()),
      runStats(view({ runId: 15, underlying: 'BANKNIFTY', fromDate: '2025-01-01', chargesPerLot: 40, initialCapital: 500_000, lotSize: 30 })),
    ])
    expect(flags.map((f) => f.kind)).toEqual(['period', 'underlying', 'costs', 'capital', 'lots'])
    expect(flags[0].text).toContain('#12 2026-01-01 → 2026-03-31; #15 2025-01-01 → 2026-03-31')
    expect(flags[1].text).toContain('#12 NIFTY; #15 BANKNIFTY')
    expect(flags[2].text).toContain('#15 ₹40 per lot per fill')
  })

  it('warns about a run that is still replaying, failed, ended early or never traded', () => {
    const flags = compareFlags([
      runStats(view({ status: 'Running' })),
      runStats(view({ runId: 15, status: 'Failed' })),
      runStats(view({ runId: 17, stopReason: 'Stop loss hit' })),
      runStats(view({ runId: 21, positions: [], metrics: { ...METRICS, closedPositions: 0 } })),
    ])
    expect(flags.map((f) => f.kind)).toEqual(['running', 'failed', 'ended-early', 'no-trades'])
    expect(flags[2].text).toContain('#17 Stop loss hit')
  })

  it('needs two runs to compare', () => {
    expect(compareFlags([runStats(view({ status: 'Running' }))])).toEqual([])
  })
})

describe('the overlay values', () => {
  it('is the balance, or the P&L from the run’s own capital', () => {
    const points = [
      { atUtc: '2026-01-01T10:00:00Z', equity: 200_000, realized: 0, unrealized: 0 },
      { atUtc: '2026-01-02T10:00:00Z', equity: 195_500, realized: -4_500, unrealized: 0 },
    ]
    expect(curveValues(points, 200_000, 'balance').map((p) => p.value)).toEqual([200_000, 195_500])
    expect(curveValues(points, 200_000, 'zero').map((p) => p.value)).toEqual([0, -4_500])
  })
})
