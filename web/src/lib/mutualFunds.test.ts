import { describe, expect, it } from 'vitest'
import {
  backfillSummary,
  categoryPosition,
  compareAsOfLine,
  compareHref,
  compareSections,
  currentDrawdownText,
  drawdownMissing,
  formatDay,
  formatNav,
  formatPct,
  formatPoints,
  formatReturn,
  freshness,
  groupMissing,
  historyLine,
  jobRequested,
  jobRun,
  lumpSumSentence,
  missingLine,
  navRange,
  ordinal,
  parseCompareCodes,
  periodLabel,
  planGapWords,
  rankText,
  recoveryText,
  reportAsOfLine,
  reportWords,
  rollingAboveText,
  searchScope,
  sipSentence,
  splitCategory,
  summaryView,
  type MutualFundAnalyticsCoverage,
  type MutualFundBackfill,
  type MutualFundCategory,
  type MutualFundNavCoverage,
  type MutualFundPeriod,
  type MutualFundReport,
  type MutualFundRolling,
} from './mutualFunds'

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

// ---------- Phase 2: the fund report ----------

const period = (years: number, over: Partial<MutualFundPeriod> = {}): MutualFundPeriod => ({
  years,
  start: '2025-10-30',
  end: '2026-10-30',
  cagrPct: 7.38,
  absolutePct: 7.38,
  missing: null,
  benchmarkCagrPct: 6.1,
  excessPct: 1.28,
  benchmarkMissing: null,
  ...over,
})

const category = (over: Partial<MutualFundCategory> = {}): MutualFundCategory => ({
  peerGroup: 'Equity Scheme - Large Cap Fund (open-ended)',
  plan: 'direct',
  asOf: '2026-10-30',
  computedUtc: '2026-10-30T18:40:00Z',
  periods: [
    { years: 1, rank: 4, peers: 31, returnPct: 7.38, medianPct: 5.2, highestPct: 12.1, lowestPct: -1.4 },
    { years: 3, rank: null, peers: 28, returnPct: null, medianPct: 14.02, highestPct: 19.3, lowestPct: 9.8 },
  ],
  note: 'Peers: direct plans, growth option, of the same AMFI category.',
  missing: null,
  ...over,
})

function report(code: number, over: Partial<MutualFundReport> = {}): MutualFundReport {
  return {
    scheme: {
      schemeCode: code,
      schemeName: `Fund ${code} - Direct Plan - Growth`,
      amc: 'Some AMC',
      category: 'Open Ended Schemes(Equity Scheme - Large Cap Fund)',
      plan: 'direct',
      option: 'growth',
      optionLabel: null,
      isinGrowth: 'INF000000001',
      isinReinvest: null,
      firstNavDate: '2018-01-03',
      lastNavDate: '2026-10-30',
      lastNav: 45.1234,
      isActive: true,
    },
    categoryName: 'Equity Scheme - Large Cap Fund',
    schemeType: 'Open Ended',
    asOf: '2026-10-30',
    nav: 45.1234,
    history: { firstDate: '2018-01-03', lastDate: '2026-10-30', navs: 2163, complete: true, note: '' },
    sources: [],
    returns: [
      period(1),
      period(3, { cagrPct: null, absolutePct: null, excessPct: null, benchmarkCagrPct: null, missing: 'History starts 3 Jan 2024.' }),
    ],
    calendarYears: [],
    rolling: null,
    rollingMissing: 'Fewer than 3 years stored.',
    examples: [
      {
        years: 1,
        sip: {
          instalment: 10000,
          instalments: 12,
          firstDate: '2025-11-03',
          lastDate: '2026-10-01',
          valuedOn: '2026-10-30',
          invested: 120000,
          units: 2700,
          value: 124500,
          gain: 4500,
          xirrPct: 7.1,
        },
        lumpSum: {
          amount: 200000,
          start: '2025-10-30',
          startNav: 42,
          valuedOn: '2026-10-30',
          endNav: 45.1234,
          value: 214876,
          gain: 14876,
          cagrPct: 7.44,
        },
        missing: null,
      },
      { years: 3, sip: null, lumpSum: null, missing: 'History starts 3 Jan 2024.' },
    ],
    risk: {
      volatility: { years: 3, start: '2023-10-30', end: '2026-10-30', returns: 740, returnsPerYear: 247, annualisedPct: 15.8, sharpe: null, sortino: null },
      volatilityMissing: null,
      riskFree: null,
      sharpeMissing: 'No risk-free rate stored.',
      drawdownsMissing: ['10 years: not enough history (the NAVs start on 3 Jan 2018, after 30 Oct 2016)'],
      drawdowns: [
        { years: 5, start: '2021-10-29', end: '2026-10-30', maxDrawdownPct: -15.91, peak: '2024-09-26', peakNav: 50, trough: '2025-03-03', troughNav: 42.05, recovery: null },
        { years: null, start: '2018-01-03', end: '2026-10-30', maxDrawdownPct: -38.2, peak: '2020-01-17', peakNav: 30, trough: '2020-03-23', troughNav: 18.54, recovery: '2020-11-24' },
      ],
      currentDrawdownPct: -9.75,
      highestNavDate: '2024-09-26',
      highestNav: 50,
    },
    benchmark: {
      index: 'NIFTY 50',
      name: 'NIFTY 50 TRI',
      basis: 'index-name',
      note: 'The scheme name says NIFTY 50.',
      collected: true,
      firstDate: '2006-04-03',
      lastDate: '2026-10-30',
      lastCommonDate: '2026-10-30',
      indexFund: false,
      beta: null,
      trackingError: null,
      capture: null,
      missing: [],
    },
    category: category(),
    cost: {
      ter: { date: '2026-10-31', terSchemeName: 'Fund', regularPct: 1.62, directPct: 0.58, thisPlanPct: 0.58 },
      terMissing: null,
      directCode: code,
      regularCode: code + 1,
      planGap: [],
      planGapMissing: null,
    },
    notes: [],
    disclaimer: 'Past returns are figures about the past.',
    generatedUtc: '2026-10-31T00:10:00Z',
    ...over,
  }
}

describe('number formats', () => {
  it('signs a return with a true minus and says nothing for a null', () => {
    expect(formatReturn(7.384)).toBe('+7.38%')
    expect(formatReturn(-10.16)).toBe('−10.16%')
    expect(formatReturn(0)).toBe('0.00%')
    expect(formatReturn(-0.001)).toBe('0.00%')
    expect(formatReturn(null)).toBe('—')
    expect(formatReturn(undefined)).toBe('—')
  })

  it('leaves a level unsigned, and points as pp', () => {
    expect(formatPct(15.8)).toBe('15.80%')
    expect(formatPct(-15.91)).toBe('−15.91%')
    expect(formatPct(62.47, 1)).toBe('62.5%')
    expect(formatPct(null)).toBe('—')
    expect(formatPoints(1.284)).toBe('+1.28 pp')
    expect(formatPoints(-0.45)).toBe('−0.45 pp')
    expect(formatPoints(null)).toBe('—')
  })

  it('writes a NAV with up to four decimals and Indian grouping', () => {
    expect(formatNav(45.1234)).toBe('₹45.1234')
    expect(formatNav(1234.5)).toBe('₹1,234.50')
    expect(formatNav(null)).toBe('—')
  })
})

describe('ranks and periods', () => {
  it('gives every ordinal its own suffix', () => {
    const cases: [number, string][] = [
      [1, '1st'], [2, '2nd'], [3, '3rd'], [4, '4th'], [11, '11th'], [12, '12th'], [13, '13th'],
      [21, '21st'], [22, '22nd'], [23, '23rd'], [101, '101st'], [111, '111th'], [112, '112th'], [1001, '1001st'],
    ]
    for (const [n, text] of cases) expect(ordinal(n)).toBe(text)
  })

  it('says a place as "Nth of M", and nothing when there is no place', () => {
    expect(rankText(4, 31)).toBe('4th of 31')
    expect(rankText(1, 1250)).toBe('1st of 1,250')
    expect(rankText(null, 31)).toBe('—')
    expect(rankText(3, 0)).toBe('—')
  })

  it('names periods, and the whole history', () => {
    expect(periodLabel(1)).toBe('1 year')
    expect(periodLabel(3)).toBe('3 years')
    expect(periodLabel(null)).toBe('Whole history')
  })
})

describe('as-of and history lines', () => {
  it("prefers the API's own history sentence", () => {
    const h = { firstDate: '2018-01-03', lastDate: '2026-10-30', navs: 2163, complete: false, note: 'NAVs from 3 Jan 2018 to 30 Oct 2026 (2,163 days).' }
    expect(historyLine(h)).toBe('NAVs from 3 Jan 2018 to 30 Oct 2026 (2,163 days).')
  })

  it('says why longer periods may be empty while the history is not complete', () => {
    const h = { firstDate: '2018-01-03', lastDate: '2026-10-30', navs: 2163, complete: false, note: '' }
    expect(historyLine(h)).toBe(
      'NAVs from 3 Jan 2018 to 30 Oct 2026 (2,163 days). Older NAVs may still arrive, so the longer periods may be empty for that reason.',
    )
    expect(historyLine({ ...h, complete: true })).toBe('NAVs from 3 Jan 2018 to 30 Oct 2026 (2,163 days).')
    expect(historyLine({ ...h, firstDate: null, lastDate: null, navs: 0 })).toBe('No NAVs stored for this scheme.')
  })

  it('puts the as-of day on a report and on a comparison', () => {
    expect(reportAsOfLine('2026-10-30', 45.1234)).toBe('Every figure ends on 30 Oct 2026, when the NAV was ₹45.1234.')
    expect(compareAsOfLine('2026-10-30', 2)).toBe('Every figure ends on 30 Oct 2026, the latest NAV day both schemes have.')
    expect(compareAsOfLine('2026-10-30', 3)).toBe('Every figure ends on 30 Oct 2026, the latest NAV day all 3 schemes have.')
  })

  it('shows what is stored before a search', () => {
    expect(navRange({ firstNavDate: '2006-04-01', lastNavDate: '2026-10-30' })).toBe('1 Apr 2006 – 30 Oct 2026')
    expect(navRange({ firstNavDate: null, lastNavDate: null })).toBe('none stored')
    expect(searchScope({ schemes: 16000, activeSchemes: 14200, nav: nav({ firstDate: '2006-04-01', lastDate: '2026-10-30' }) })).toBe(
      '16,000 schemes (14,200 active) with NAVs from 1 Apr 2006 to 30 Oct 2026',
    )
  })
})

describe('worked examples', () => {
  it('turns a SIP into one sentence', () => {
    expect(
      sipSentence({
        instalment: 10000,
        instalments: 60,
        firstDate: '2021-11-01',
        lastDate: '2026-10-01',
        valuedOn: '2026-10-30',
        invested: 600000,
        units: 12345.678,
        value: 679412.4,
        gain: 79412.4,
        xirrPct: 4.92,
      }),
    ).toBe(
      '₹10,000 a month from 1 Nov 2021 to 1 Oct 2026, 60 instalments, ₹6,00,000 in all, was worth ₹6,79,412 on 30 Oct 2026: an XIRR of 4.92%.',
    )
  })

  it('never invents an XIRR it was not given', () => {
    const s = sipSentence({
      instalment: 10000,
      instalments: 1,
      firstDate: '2026-10-01',
      lastDate: '2026-10-01',
      valuedOn: '2026-10-30',
      invested: 10000,
      units: 221,
      value: 9800,
      gain: -200,
      xirrPct: null,
    })
    expect(s).toBe('₹10,000 a month from 1 Oct 2026 to 1 Oct 2026, 1 instalment, ₹10,000 in all, was worth ₹9,800 on 30 Oct 2026; its XIRR is not known.')
  })

  it('turns a lump sum into one sentence, with a loss said as one', () => {
    const gain = lumpSumSentence({ amount: 200000, start: '2021-11-01', startNav: 45.1234, valuedOn: '2026-10-30', endNav: 58.95, value: 261240, gain: 61240, cagrPct: 5.48 })
    expect(gain).toBe(
      '₹2,00,000 invested on 1 Nov 2021 at a NAV of ₹45.1234 was worth ₹2,61,240 on 30 Oct 2026, at a NAV of ₹58.95: a gain of ₹61,240, a CAGR of 5.48%.',
    )
    const loss = lumpSumSentence({ amount: 200000, start: '2025-10-30', startNav: 50, valuedOn: '2026-10-30', endNav: 47, value: 188000, gain: -12000, cagrPct: -6 })
    expect(loss).toContain('a loss of ₹12,000, a CAGR of −6.00%.')
  })

  it('says which plan a rupee gap favours, in plain words', () => {
    expect(planGapWords(13580.4)).toBe('₹13,580 more in the direct plan')
    expect(planGapWords(-2100)).toBe('₹2,100 more in the regular plan')
    expect(planGapWords(0.2)).toBe('the same in both plans')
    expect(planGapWords(null)).toBe('—')
  })
})

describe('missing figures', () => {
  it('says each reason once, with the rows it applies to', () => {
    const notes = groupMissing([
      { label: '1 year', reason: null },
      { label: '5 years', reason: 'History starts 3 Jan 2018.' },
      { label: '10 years', reason: 'History starts 3 Jan 2018.' },
      { label: '3 years', reason: 'No benchmark values.' },
      { label: '3 years', reason: '  ' },
    ])
    expect(notes.map(missingLine)).toEqual(['5 years and 10 years: History starts 3 Jan 2018.', '3 years: No benchmark values.'])
  })

  it("gives a category place, or the scheme's own reason, or the category's", () => {
    expect(categoryPosition(category(), period(1), 1)).toEqual({ text: '4th of 31', median: '+5.20%', missing: null })
    const p3 = period(3, { cagrPct: null, missing: 'History starts 3 Jan 2024.' })
    expect(categoryPosition(category(), p3, 3)).toEqual({ text: '—', median: '+14.02%', missing: 'History starts 3 Jan 2024.' })
    expect(categoryPosition(category({ periods: [], missing: 'A pre-2017 category: AMFI names no peer group.' }), period(5), 5)).toEqual({
      text: '—',
      median: '—',
      missing: 'A pre-2017 category: AMFI names no peer group.',
    })
    // The note describes the peers; it is never the reason, even with no ranking and no reason given.
    expect(categoryPosition(category({ periods: [] }), period(5), 5).missing).toBe('No category ranking in this report.')
    // A ranking exists but has no 10-year entry.
    const p10 = period(10, { cagrPct: null, missing: 'NAVs start 3 Jan 2018: fewer than 10 years stored.' })
    expect(categoryPosition(category(), p10, 10).missing).toBe('NAVs start 3 Jan 2018: fewer than 10 years stored.')
    expect(categoryPosition(category(), period(5), 5).missing).toBe('No category ranking for 5 years.')
    expect(categoryPosition(category(), period(3), 3).missing).toBe('Not placed in the category ranking for 3 years.')
  })

  it("gives a left-out drawdown window the API's own line for it", () => {
    const r = report(1)
    expect(drawdownMissing(r.risk, 10)).toBe('10 years: not enough history (the NAVs start on 3 Jan 2018, after 30 Oct 2016)')
    expect(drawdownMissing(r.risk, 3)).toBe('3 years: not in this report.')
    expect(drawdownMissing({ ...r.risk, drawdownsMissing: [] }, null)).toBe('No NAVs to measure a fall on.')
  })

  it('says a drawdown that has not recovered is not recovered, by the as-of day', () => {
    const r = report(1)
    expect(recoveryText(r.risk.drawdowns[0], r.asOf)).toBe('Not recovered by 30 Oct 2026')
    expect(recoveryText(r.risk.drawdowns[1], r.asOf)).toBe('Recovered 24 Nov 2020')
    expect(currentDrawdownText(r.risk)).toBe('−9.75% from its highest NAV stored (₹50.00 on 26 Sep 2024).')
    expect(currentDrawdownText({ ...r.risk, currentDrawdownPct: 0 })).toBe('At its highest NAV stored (₹50.00 on 26 Sep 2024).')
    expect(currentDrawdownText({ ...r.risk, currentDrawdownPct: null })).toBe('—')
  })

  it('counts rolling windows against the benchmark only when there were some', () => {
    const roll: MutualFundRolling = {
      years: 3, windows: 1500, firstEnd: '2021-01-04', lastEnd: '2026-10-30', lowestPct: 2.1, lowestEnd: '2023-03-23',
      highestPct: 25.2, highestEnd: '2024-09-26', medianPct: 12.3, belowZeroPct: 0, benchmarkCompared: 1234, aboveBenchmarkPct: 62.47,
    }
    expect(rollingAboveText(roll)).toBe('62.5% of the 1,234 windows with a benchmark return over the same dates')
    expect(rollingAboveText({ ...roll, benchmarkCompared: 0, aboveBenchmarkPct: null })).toBeNull()
  })
})

describe('compare', () => {
  it('reads codes from the address: each once, at most four', () => {
    expect(parseCompareCodes('120716,100822')).toEqual({ codes: [120716, 100822], dropped: 0 })
    expect(parseCompareCodes(' 1, 2,2,x,-3,0,4,5,6')).toEqual({ codes: [1, 2, 4, 5], dropped: 1 })
    expect(parseCompareCodes(null)).toEqual({ codes: [], dropped: 0 })
    expect(compareHref([120716, 100822])).toBe('/data/mutual-funds/compare?codes=120716,100822')
  })

  it('builds one cell per fund, in the order the funds came, with every reason in text', () => {
    const a = report(120716)
    const b = report(100822, {
      benchmark: { ...a.benchmark, indexFund: true },
      cost: { ...a.cost, ter: null, terMissing: 'Not in the latest TER file.' },
      category: category({ periods: [], missing: 'A pre-2017 category: AMFI names no peer group.' }),
      risk: { ...a.risk, volatility: null, volatilityMissing: 'Volatility needs a year of NAVs.', drawdowns: [a.risk.drawdowns[1]], drawdownsMissing: ['5 years: not enough history (the NAVs start on 3 Jan 2024, after 30 Oct 2021)'] },
    })
    const sections = compareSections([a, b])
    expect(sections.map((s) => s.title)).toEqual([
      'CAGR',
      'Category position by return',
      'Against the benchmark (CAGR difference)',
      '₹10,000 a month SIP: value',
      '₹2,00,000 lump sum: value',
      'Risk',
      'Cost and history',
    ])
    const row = (key: string) => sections.flatMap((s) => s.rows).find((r) => r.key === key)!

    expect(row('cagr-1').cells.map((c) => c.text)).toEqual(['+7.38%', '+7.38%'])
    expect(row('cagr-3').cells[0]).toEqual({ text: '—', missing: 'History starts 3 Jan 2024.' })
    expect(row('rank-1').cells).toEqual([
      { text: '4th of 31', sub: 'median +5.20%' },
      { text: '—', missing: 'A pre-2017 category: AMFI names no peer group.' },
    ])
    expect(row('excess-1').cells[0].sub).toBe('benchmark +6.10%')
    expect(row('excess-1').cells[1].sub).toBe('tracking difference · benchmark +6.10%')
    expect(row('sip-1').cells[0]).toEqual({ text: '₹1,24,500', sub: 'XIRR 7.10% · ₹1,20,000 put in' })
    expect(row('sip-3').cells[0]).toEqual({ text: '—', missing: 'History starts 3 Jan 2024.' })
    expect(row('lump-1').cells[0].text).toBe('₹2,14,876')
    expect(row('dd-5').cells[0]).toMatchObject({ text: '−15.91%', sub: '26 Sep 2024 → 3 Mar 2025' })
    expect(row('dd-5').cells[1]).toEqual({ text: '—', missing: '5 years: not enough history (the NAVs start on 3 Jan 2024, after 30 Oct 2021)' })
    expect(row('volatility').cells[1]).toEqual({ text: '—', missing: 'Volatility needs a year of NAVs.' })
    expect(row('dd-all').cells[0].text).toBe('−38.20%')
    expect(row('ter').cells).toEqual([
      { text: '0.58%', sub: 'on 31 Oct 2026' },
      { text: '—', missing: 'Not in the latest TER file.' },
    ])
    expect(row('history').cells[0]).toEqual({ text: '3 Jan 2018', sub: '2,163 NAVs, complete' })
    // Never a zero for a figure that is not known.
    for (const s of sections) for (const r of s.rows) for (const c of r.cells) if (c.text === '—') expect(c.missing).toBeTruthy()
  })
})

describe('jobs', () => {
  const analytics: MutualFundAnalyticsCoverage = {
    benchmarks: [],
    benchmarkSync: { trigger: 'nightly', startedUtc: '2026-10-30T18:00:00Z', finishedUtc: '2026-10-30T18:02:00Z', ok: true, message: 'ok', reached: '2026-10-30' },
    terLatestDate: null,
    terSchemes: 0,
    terSync: null,
    statsAsOf: null,
    statsSchemes: 0,
    statsComputedUtc: null,
    statsRun: null,
    requested: ['TER'],
  }

  it("finds each job's last run and whether it waits for its turn", () => {
    expect(jobRun(analytics, 'benchmarks')?.reached).toBe('2026-10-30')
    expect(jobRun(analytics, 'ter')).toBeNull()
    expect(jobRequested(analytics, 'ter')).toBe(true)
    expect(jobRequested(analytics, 'stats')).toBe(false)
  })
})

describe('the written summary', () => {
  const ok = { schemeCode: 1, asOf: '2026-10-30', summary: { text: 'Over 3 years the NAV grew 14.0% a year.', model: 'claude', writtenUtc: '2026-10-31T00:00:00Z' }, note: null }

  it('shows a summary for the same as-of day', () => {
    expect(summaryView(ok, '2026-10-30')).toEqual({ kind: 'summary', summary: ok.summary })
  })

  it("shows the API's reason when there is none", () => {
    expect(summaryView({ ...ok, summary: null, note: 'No model is switched on.' }, '2026-10-30')).toEqual({ kind: 'note', text: 'No model is switched on.' })
    expect(summaryView({ ...ok, summary: null }, '2026-10-30').kind).toBe('note')
  })

  it('does not put a summary of other dates next to these figures', () => {
    const v = summaryView(ok, '2026-06-30')
    expect(v.kind).toBe('note')
  })

  it('keeps back a summary that ranks or advises', () => {
    const v = summaryView({ ...ok, summary: { ...ok.summary, text: 'One of the best large-cap funds; investors should hold it.' } }, '2026-10-30')
    expect(v).toEqual({ kind: 'note', text: 'A written summary came back but is not shown: it uses “best”, “should” and “hold”, which this report does not.' })
  })

  it('leaves the words of fact alone', () => {
    expect(reportWords('The highest 3-year return was above the benchmark; the lowest below it. Holdings, threshold, stopped, starts.')).toEqual([])
    expect(reportWords('A top pick with a 5-star rating that will outperform; consider it, avoid the rest.')).toEqual([
      'top',
      'star',
      'rating',
      'outperform',
      'consider',
      'avoid',
    ])
  })
})

describe("the mutual fund pages' own words", () => {
  const sources = import.meta.glob(['../pages/data/MutualFund*.tsx', './mutualFunds.ts'], {
    query: '?raw',
    import: 'default',
    eager: true,
  }) as Record<string, string>

  /**
   * The text a reader can see: string and template literals and JSX text,
   * after comments and the word list are taken out. A template's ${…} is
   * code, not text (`b.top` is a field).
   */
  function uiText(source: string): string[] {
    const code = source
      .replace(/\/\/ report-words:start[\s\S]*?\/\/ report-words:end/g, '')
      .replace(/\/\*[\s\S]*?\*\//g, ' ')
      .replace(/(^|[^:'"`\\])\/\/.*$/gm, '$1')
    const literals = [...code.matchAll(/(?<![A-Za-z])'((?:[^'\\\n]|\\.)*)'|"((?:[^"\\\n]|\\.)*)"|`((?:[^`\\]|\\.)*)`/g)].map(
      (m) => m[1] ?? m[2] ?? (m[3] ?? '').replace(/\$\{[^}]*\}/g, ' '),
    )
    const jsx = [...code.matchAll(/>([^<>{}]*[A-Za-z][^<>{}]*)</g)].map((m) => m[1])
    return [...literals, ...jsx]
  }

  it('reads the files it means to', () => {
    const files = Object.keys(sources)
    expect(files.filter((f) => f.includes('pages/data/MutualFund')).length).toBe(3)
    const all = Object.values(sources).flatMap(uiText).join('\n')
    // A scanner that stopped finding text would pass vacuously.
    expect(all).toContain('Trailing returns')
    expect(all).toContain('Compare funds')
    expect(all).toContain('Report data')
    expect(all).toContain('Not recovered by')
  })

  it('catches a planted word, so the scan is not vacuous', () => {
    expect(reportWords(uiText('const x = <p className="a">The best fund to buy</p>').join(' '))).toEqual(['best', 'buy'])
    expect(reportWords(uiText('const y = `${b.top} is the worst`').join(' '))).toEqual(['worst'])
  })

  it('never ranks, rates or advises', () => {
    const found = Object.entries(sources).flatMap(([file, text]) =>
      uiText(text).flatMap((t) => reportWords(t).map((w) => `${file.replace(/^.*\//, '')}: "${w}" in ${JSON.stringify(t.trim().slice(0, 80))}`)),
    )
    expect(found).toEqual([])
  })
})
