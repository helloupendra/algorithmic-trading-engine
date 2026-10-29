import { describe, expect, it } from 'vitest'
import {
  describeRunCosts,
  flatPositionCharges,
  istMonthOf,
  monthLabel,
  readRunCosts,
  summariseMonths,
  type RunCosts,
} from './backtestMonths'
import type { BacktestPosition } from './types'

let nextId = 1
/** A closed leg as the API sends it: lots are the lots it opened, pnl is price P&L. */
function closed(closedUtc: string, pnl: number, lots = 1): BacktestPosition {
  const id = nextId++
  return {
    id,
    groupId: `g${id}`,
    symbol: 'NSE:NIFTY26JAN25000CE',
    contract: null,
    side: 'BUY',
    lots,
    lotSize: 75,
    quantity: lots * 75,
    status: 'Closed',
    entryPrice: 100,
    exitPrice: 100 + pnl / (lots * 75),
    pnl,
    openedUtc: closedUtc,
    closedUtc,
    exitReason: null,
  }
}

function open(openedUtc: string): BacktestPosition {
  return { ...closed(openedUtc, 0), status: 'Open', exitPrice: null, closedUtc: null }
}

const NONE: RunCosts = { kind: 'none' }

describe('the IST month of an instant', () => {
  it('turns at IST midnight, not UTC midnight', () => {
    expect(istMonthOf('2026-01-31T18:29:59Z')).toBe('2026-01')   // 23:59:59 IST on 31 Jan
    expect(istMonthOf('2026-01-31T18:30:00Z')).toBe('2026-02')   // 00:00 IST on 1 Feb
    expect(istMonthOf('2025-12-31T18:30:00Z')).toBe('2026-01')   // the year turns with it
    expect(istMonthOf(null)).toBeNull()
    expect(istMonthOf('not a time')).toBeNull()
  })
})

describe('the monthly reading', () => {
  it('books each position in the IST month it closed', () => {
    const reading = summariseMonths(
      [closed('2026-01-31T09:55:00Z', 1_000), closed('2026-01-31T18:45:00Z', 500), closed('2026-02-02T09:55:00Z', -200)],
      NONE,
    )
    expect(reading.years).toHaveLength(1)
    const [jan, feb] = reading.years[0].months
    expect(jan).toEqual({ month: 1, pnl: 1_000, gross: 1_000, charges: 0, trades: 1 })
    expect(feb).toEqual({ month: 2, pnl: 300, gross: 300, charges: 0, trades: 2 })
    expect(reading.years[0]).toMatchObject({ year: 2026, pnl: 1_300, trades: 3 })
  })

  it('leaves a month with no close blank, never ₹0, and keeps a flat month that traded', () => {
    const reading = summariseMonths([closed('2026-01-10T09:55:00Z', 400), closed('2026-01-12T09:55:00Z', -400), closed('2026-03-02T09:55:00Z', 10)], NONE)
    const months = reading.years[0].months
    expect(months[0]).toMatchObject({ pnl: 0, trades: 2 })       // traded, broke even
    expect(months[1]).toBeNull()                                   // no trade in February
    expect(months.slice(3).every((m) => m == null)).toBe(true)
  })

  it('reads a month of only losses as the worst, and its total as negative', () => {
    const reading = summariseMonths(
      [closed('2026-04-01T09:55:00Z', -1_200), closed('2026-04-15T09:55:00Z', -300), closed('2026-05-04T09:55:00Z', 900)],
      NONE,
    )
    expect(reading.years[0].months[3]).toMatchObject({ pnl: -1_500, trades: 2 })
    expect(reading.worst).toEqual({ year: 2026, month: 4, pnl: -1_500, trades: 2 })
    expect(reading.best).toEqual({ year: 2026, month: 5, pnl: 900, trades: 1 })
  })

  it('gives a tie for best or worst to the earlier month', () => {
    const reading = summariseMonths([closed('2025-03-03T09:55:00Z', 500), closed('2025-06-03T09:55:00Z', 500)], NONE)
    expect(reading.best).toMatchObject({ month: 3 })
    expect(reading.worst).toMatchObject({ month: 3 })
  })

  it('nets a flat per-lot rate off both fills of each closed position', () => {
    const flat: RunCosts = { kind: 'flat', perLot: 40 }
    expect(flatPositionCharges({ lots: 2 }, 40)).toBe(160)
    const reading = summariseMonths([closed('2026-01-05T09:55:00Z', 1_000, 2), closed('2026-01-06T09:55:00Z', -500, 1)], flat)
    expect(reading.years[0].months[0]).toEqual({ month: 1, pnl: 1_000 - 160 + (-500 - 80), gross: 500, charges: 240, trades: 2 })
    expect(reading.charges).toBe(240)
    expect(reading.netted).toBe('all')
  })

  it('counts a position whose size could not be read back instead of guessing its charges', () => {
    const reading = summariseMonths([closed('2026-01-05T09:55:00Z', 1_000, 0)], { kind: 'flat', perLot: 40 })
    expect(reading.unsized).toBe(1)
    expect(reading.charges).toBe(0)
    expect(reading.years[0].months[0]).toMatchObject({ pnl: 1_000, charges: 0 })
  })

  it('says the months are before the real costs, which are kept as a run total only', () => {
    const reading = summariseMonths([closed('2026-01-05T09:55:00Z', 1_000, 2)], { kind: 'model', slippagePercent: 0.5, brokeragePerOrder: null })
    expect(reading.netted).toBe('statutory-missing')
    expect(reading.charges).toBe(0)
    expect(reading.years[0].months[0]).toMatchObject({ pnl: 1_000, gross: 1_000 })
  })

  it('keeps open positions out of every month and counts them', () => {
    const reading = summariseMonths([open('2026-01-05T04:00:00Z'), closed('2026-01-05T09:55:00Z', 250)], NONE)
    expect(reading.open).toBe(1)
    expect(reading.years[0].trades).toBe(1)
  })

  it('shows the years of the range a run covered without a single close', () => {
    const reading = summariseMonths([closed('2025-02-03T09:55:00Z', 100)], NONE, { fromDate: '2023-01-02', toDate: '2025-06-30' })
    expect(reading.years.map((y) => y.year)).toEqual([2023, 2024, 2025])
    expect(reading.years[0].months.every((m) => m == null)).toBe(true)
    expect(reading.years[0]).toMatchObject({ pnl: 0, trades: 0 })
  })

  it('has nothing to say about an empty run', () => {
    const reading = summariseMonths([], NONE)
    expect(reading).toEqual({ years: [], netted: 'all', charges: 0, open: 0, unsized: 0, best: null, worst: null })
    expect(summariseMonths(undefined, NONE).years).toEqual([])
  })

  it('labels a month the way the table heads it', () => {
    expect(monthLabel(2025, 3)).toBe('Mar 2025')
  })
})

describe('how a run was charged', () => {
  it('reads the flat rate when the run has no costs block', () => {
    expect(readRunCosts({ chargesPerLot: 40, parametersJson: '{"lots":1,"charges_per_lot":40}' })).toEqual({ kind: 'flat', perLot: 40 })
    expect(readRunCosts({ chargesPerLot: 0, parametersJson: '{}' })).toEqual({ kind: 'none' })
    expect(readRunCosts({ chargesPerLot: 20, parametersJson: 'not json' })).toEqual({ kind: 'flat', perLot: 20 })
  })

  it('reads a costs block the way the runner does, over any flat rate', () => {
    expect(readRunCosts({ chargesPerLot: 40, parametersJson: '{"costs":{"slippage_percent":0.5,"brokerage_per_order":"20"}}' })).toEqual({
      kind: 'model',
      slippagePercent: 0.5,
      brokeragePerOrder: 20,
    })
    expect(readRunCosts({ chargesPerLot: 0, parametersJson: '{"costs":true}' })).toEqual({ kind: 'model', slippagePercent: null, brokeragePerOrder: null })
    expect(readRunCosts({ chargesPerLot: 0, parametersJson: '{"costs":"yes"}' }).kind).toBe('model')
    // An empty block or a false-y value leaves the costs off, as it does in the runner.
    expect(readRunCosts({ chargesPerLot: 0, parametersJson: '{"costs":{}}' })).toEqual({ kind: 'none' })
    expect(readRunCosts({ chargesPerLot: 0, parametersJson: '{"costs":"false"}' })).toEqual({ kind: 'none' })
  })

  it('names the setting in words', () => {
    expect(describeRunCosts({ kind: 'none' })).toBe('none')
    expect(describeRunCosts({ kind: 'flat', perLot: 40 })).toBe('₹40 per lot per fill')
    expect(describeRunCosts({ kind: 'model', slippagePercent: 0.5, brokeragePerOrder: 20 })).toBe('real costs · 0.5% slippage a side · ₹20 brokerage an order')
    expect(describeRunCosts({ kind: 'model', slippagePercent: null, brokeragePerOrder: null })).toBe('real costs · engine defaults')
  })
})
