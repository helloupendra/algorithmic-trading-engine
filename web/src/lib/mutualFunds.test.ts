import { describe, expect, it } from 'vitest'
import { backfillSummary, formatDay, freshness, splitCategory, type MutualFundBackfill, type MutualFundNavCoverage } from './mutualFunds'

const nav = (over: Partial<MutualFundNavCoverage> = {}): MutualFundNavCoverage => ({
  rows: 14200,
  rowsApproximate: false,
  firstDate: '2018-07-02',
  lastDate: '2026-11-01',
  latestFullDate: '2026-10-30',
  expectedDate: '2026-10-30',
  businessDaysBehind: 0,
  ...over,
})

const backfill = (over: Partial<MutualFundBackfill> = {}): MutualFundBackfill => ({
  enabled: true,
  requested: false,
  running: false,
  state: 'idle',
  now: null,
  from: '2006-04-01',
  top: null,
  cursor: null,
  windowsDone: 0,
  windowsLeft: 0,
  rowsWritten: 0,
  maxWindows: null,
  lastWindow: null,
  lastError: null,
  startedUtc: null,
  updatedUtc: null,
  ...over,
})

describe('formatDay', () => {
  it('writes an IST day without moving it through a time zone', () => {
    expect(formatDay('2026-10-30')).toBe('30 Oct 2026')
    expect(formatDay('2006-04-01')).toBe('1 Apr 2006')
    expect(formatDay(null)).toBe('—')
  })
})

describe('freshness', () => {
  it('is up to date when the newest full NAV day is the one due', () => {
    expect(freshness(nav())).toMatchObject({ tone: 'pos', label: 'Up to date' })
  })

  it('is a warning one business day behind and red from two, as the checkup', () => {
    expect(freshness(nav({ expectedDate: '2026-11-02', businessDaysBehind: 1 }))).toMatchObject({
      tone: 'warn',
      label: '1 business day behind',
    })
    const late = freshness(nav({ expectedDate: '2026-11-03', businessDaysBehind: 2 }))
    expect(late).toMatchObject({ tone: 'neg', label: '2 business days behind' })
    expect(late.detail).toBe("NAVs to 30 Oct 2026; 3 Nov 2026's were due.")
  })

  it('never calls an empty table current', () => {
    expect(freshness(nav({ latestFullDate: null, businessDaysBehind: null }))).toMatchObject({ tone: 'warn', label: 'No NAVs' })
  })
})

describe('splitCategory', () => {
  it("splits AMFI's section line into its type and category", () => {
    expect(splitCategory('Open Ended Schemes(Equity Scheme - Large Cap Fund)')).toEqual({
      type: 'Open Ended',
      name: 'Equity Scheme - Large Cap Fund',
    })
    expect(splitCategory('Open Ended Schemes(Exchange Traded Funds (ETFs) - Gold ETF)').name).toBe(
      'Exchange Traded Funds (ETFs) - Gold ETF',
    )
    expect(splitCategory('Interval Fund Schemes(Income)')).toEqual({ type: 'Interval Fund', name: 'Income' })
    expect(splitCategory('')).toEqual({ type: '', name: '—' })
  })
})

describe('backfillSummary', () => {
  it('says the history is not there until someone starts it', () => {
    expect(backfillSummary(backfill())).toMatchObject({ label: 'Not started' })
  })

  it('counts windows while it runs and names the request', () => {
    const s = backfillSummary(backfill({ state: 'running', windowsDone: 12, windowsLeft: 72, now: 'tp=1 2023-04-02..2023-06-30' }))
    expect(s.label).toBe('Running')
    expect(s.detail).toBe('12 of 84 windows; reading tp=1 2023-04-02..2023-06-30.')
  })

  it('gives the reach once done, and the error when it failed', () => {
    expect(backfillSummary(backfill({ state: 'done', cursor: '2006-04-01', top: '2026-10-31' })).detail).toBe(
      'History from 1 Apr 2006 to 31 Oct 2026.',
    )
    const failed = backfillSummary(backfill({ state: 'failed', windowsDone: 3, windowsLeft: 81, lastError: 'tp=1: timeout' }))
    expect(failed).toMatchObject({ tone: 'neg', label: 'Failed' })
    expect(failed.detail).toContain('tp=1: timeout')
  })
})
