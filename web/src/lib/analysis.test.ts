import { describe, expect, it } from 'vitest'

import {
  PROOF,
  STATUS_META,
  analysisErrorText,
  asProb,
  asRange,
  bucketEdgesText,
  bucketLabel,
  ciAsSkill,
  ciReading,
  coverageTone,
  deskAnswer,
  displayStatus,
  forecastCells,
  describeValue,
  forecastsQuery,
  formatCiSkill,
  formatInputValue,
  formatIstClock,
  formatIstStamp,
  formatProb,
  formatProbDelta,
  formatRangeForecast,
  formatSentiment,
  formatSigned,
  formatSignedPct,
  formatSkill,
  formatSkillSigned,
  groupScoreboard,
  humanizeKey,
  inputLabel,
  inputsView,
  intervalBar,
  isProven,
  issueNote,
  issuedText,
  istMinutes,
  istToday,
  modelAbout,
  modelShort,
  modelTitle,
  newsLabel,
  pickSession,
  rangeScale,
  readForecastList,
  readLiveContext,
  readScoreboard,
  reliabilityLabel,
  reliabilityPoints,
  scoringNote,
  sentimentTone,
  sessionContexts,
  sharedExtent,
  shortDay,
  sortForecasts,
  sortNewsRows,
  statusMeta,
  todayCards,
  verdict,
} from './analysis'
import type { Forecast, ForecastModel, ModelStatus, RangePrediction, ScoreboardRow } from './analysis'
import { buildAnalysisFixture, fixtureResponse } from './analysis.fixture'

/**
 * The Analysis page's words, pinned. The rule under test throughout: the page
 * never says more than the evidence — nothing under 60 live forecasts is called
 * proven, an interval touching zero reads "could be no better than the
 * baseline", and a backtest is never what a verdict rests on.
 */

const RANGE: RangePrediction = {
  median: 1.02,
  low80: 0.68,
  high80: 1.55,
  prevClose: 24650.3,
  points: { median: 251.4, low80: 167.6, high80: 382.1 },
  buckets: { quiet: 0.31, normal: 0.45, wild: 0.24 },
  bucketEdges: [0.75, 1.25],
}

function row(over: Partial<ScoreboardRow> = {}): ScoreboardRow {
  return {
    modelKey: 'range.har-vix',
    modelVersion: '2026-09-27.1',
    target: 'range',
    underlying: 'ALL',
    description: '...',
    liveCount: 42,
    meanLoss: 0.228,
    meanBaselineLoss: 0.259,
    skill: 0.12,
    diffCiLow: -0.004,
    diffCiHigh: 0.061,
    status: 'testing',
    statusReason: '42 of 60 scored forecasts; the confidence interval still includes no improvement',
    coverage80: 0.81,
    calibration: [{ from: 0.3, to: 0.4, n: 18, meanP: 0.35, hitRate: 0.39 }],
    firstSession: '2026-09-28',
    lastSession: '2026-11-20',
    backtest: null,
    ...over,
  }
}

function forecast(over: Partial<Forecast> = {}): Forecast {
  return {
    id: 1,
    modelKey: 'range.har-vix',
    modelVersion: '2026-09-27.1',
    target: 'range',
    underlying: 'NIFTY',
    sessionDate: '2026-09-28',
    issuedUtc: '2026-09-28T03:20:04Z',
    prediction: RANGE,
    baseline: { ...RANGE, median: 0.97 },
    inputs: { vixPrevClose: 11.9, r1: 0.84, r5: 0.97, r22: 1.05, expiryDay: false },
    outcome: null,
    scores: null,
    scoredUtc: null,
    ...over,
  }
}

const OUTCOME = {
  open: 24660.1,
  high: 24790.4,
  low: 24540.0,
  close: 24771.2,
  range: 1.016,
  bucket: 'normal' as const,
  trendDay: false,
  efficiency: 0.44,
  up: true,
}

/** 2026-09-28 is a Monday; this is `hh:mm` IST on it. */
const ist = (hhmm: string, date = '2026-09-28') => Date.parse(`${date}T${hhmm}:00+05:30`)

describe('formatRangeForecast', () => {
  it('reads the contract example in one line', () => {
    expect(formatRangeForecast('NIFTY', RANGE)).toBe('NIFTY 1.02% · ≈251 pts, 80% between 0.68% and 1.55%')
  })

  it('groups large point counts the Indian way', () => {
    const wide = { ...RANGE, points: { median: 1120.4, low80: 800, high80: 1500 } }
    expect(formatRangeForecast('SENSEX', wide)).toContain('≈1,120 pts')
  })

  it('leaves out what the forecast does not carry instead of printing zero', () => {
    const bare = { median: 0.9 }
    expect(formatRangeForecast('BANKNIFTY', bare)).toBe('BANKNIFTY 0.90%')
    expect(formatRangeForecast('NIFTY', { p: 0.4 })).toBe('NIFTY —')
  })
})

describe('buckets', () => {
  it('names the three range buckets', () => {
    expect(['quiet', 'normal', 'wild'].map(bucketLabel)).toEqual(['Quiet', 'Normal', 'Wild'])
    expect(bucketLabel(null)).toBe('—')
    expect(bucketLabel('odd')).toBe('odd')
  })

  it('says where the edges are', () => {
    expect(bucketEdgesText([0.75, 1.25])).toBe('Quiet under 0.75% · Normal 0.75–1.25% · Wild from 1.25%')
    expect(bucketEdgesText(undefined)).toBe('')
  })
})

describe('status', () => {
  it('gives each status a tone that matches its weight', () => {
    expect(statusMeta('collecting').tone).toBe('neutral')
    expect(statusMeta('testing').tone).toBe('accent')
    expect(statusMeta('proven').tone).toBe('pos')
    expect(statusMeta('retired').tone).toBe('neg')
  })

  it('states the contract thresholds in words', () => {
    expect(STATUS_META.collecting.meaning).toContain('20')
    expect(STATUS_META.testing.meaning).toMatch(/60.*95%.*120/)
    expect(STATUS_META.proven.meaning).toMatch(/60.*95%/)
    expect(STATUS_META.retired.meaning).toMatch(/120.*95%/)
  })

  it('shows a status it does not know as itself, claiming nothing', () => {
    expect(statusMeta('paused')).toMatchObject({ label: 'paused', tone: 'neutral' })
  })

  it('does not repeat "proven" when the numbers do not back it', () => {
    expect(displayStatus(row({ status: 'proven', liveCount: 45, diffCiLow: 0.01 }))).toBe('testing')
    expect(displayStatus(row({ status: 'proven', liveCount: 80, diffCiLow: 0 }))).toBe('testing')
    expect(displayStatus(row({ status: 'proven', liveCount: 80, diffCiLow: 0.01 }))).toBe('proven')
    expect(displayStatus(row({ status: 'collecting', liveCount: 3 }))).toBe('collecting')
  })
})

describe('formatSkill', () => {
  it('says better or worse than the baseline, in whole percent', () => {
    expect(formatSkill(0.12)).toBe('12% better than the baseline')
    expect(formatSkill(-0.04)).toBe('4% worse than the baseline')
    expect(formatSkill(0.118, true)).toBe('12% better')
  })

  it('calls a skill that rounds to zero level, not better', () => {
    expect(formatSkill(0.004)).toBe('level with the baseline')
    expect(formatSkill(-0.004, true)).toBe('level')
  })

  it('does not invent a number when there is none', () => {
    expect(formatSkill(null)).toBe('no live score yet')
    expect(formatSkill(Number.NaN, true)).toBe('—')
  })

  it('signs it for tight columns', () => {
    expect(formatSkillSigned(0.073)).toBe('+7%')
    expect(formatSkillSigned(-0.011)).toBe('−1%')
    expect(formatSkillSigned(0.001)).toBe('0%')
    expect(formatSkillSigned(null)).toBe('—')
  })
})

describe('the 95% interval', () => {
  it('reads "could be no better than the baseline" whenever its low end is at or below zero', () => {
    expect(ciReading(row({ diffCiLow: -0.004, diffCiHigh: 0.061 }))).toMatch(/^Could be no better than the baseline/)
    expect(ciReading(row({ diffCiLow: 0, diffCiHigh: 0.061 }))).toMatch(/^Could be no better than the baseline/)
  })

  it('says better only when the whole interval is above zero, and worse only when it is below', () => {
    expect(ciReading(row({ diffCiLow: 0.002, diffCiHigh: 0.06 }))).toMatch(/^Better than the baseline/)
    expect(ciReading(row({ diffCiLow: -0.05, diffCiHigh: -0.001 }))).toMatch(/^Worse than the baseline/)
  })

  it('has nothing to say without both ends', () => {
    expect(ciReading(row({ diffCiLow: null, diffCiHigh: null }))).toBe('No interval yet.')
  })

  it('is rescaled to skill by the mean baseline loss', () => {
    // The contract's example row: skill 0.12 = 0.031 / 0.259.
    const ci = ciAsSkill(row())!
    expect(ci.low).toBeCloseTo(-0.004 / 0.259, 6)
    expect(ci.high).toBeCloseTo(0.061 / 0.259, 6)
    expect(formatCiSkill(row())).toBe('≈ −2% to +24%')
    expect(ciAsSkill(row({ meanBaselineLoss: 0 }))).toBeNull()
    // Just below zero keeps its sign and a decimal, so it cannot read as "no worse".
    expect(formatCiSkill(row({ diffCiLow: -0.0009, diffCiHigh: 0.03, meanBaselineLoss: 0.3 }))).toBe('≈ −0.3% to +10%')
    expect(formatCiSkill(row({ diffCiLow: 0.00001, diffCiHigh: 0.03, meanBaselineLoss: 0.3 }))).toBe('≈ 0% to +10%')
    expect(formatCiSkill(row({ diffCiLow: null }))).toBe('—')
  })

  it('draws zero in the middle, so a bar crossing zero crosses the line', () => {
    const bar = intervalBar(-0.02, 0.24, 0.12)
    expect(bar.zero).toBe(50)
    expect(bar.from).toBeLessThan(50)
    expect(bar.to).toBeGreaterThan(50)
    expect(bar.point!).toBeGreaterThan(50)
    expect(bar.to).toBeLessThanOrEqual(100)
  })

  it('puts every row of a table on one scale when given the shared extent', () => {
    const rows = [row({ diffCiLow: 0.01, diffCiHigh: 0.02, skill: 0.05 }), row({ diffCiLow: -0.1, diffCiHigh: 0.1, skill: 0 })]
    const extent = sharedExtent(rows)!
    const narrow = intervalBar(0.01 / 0.259, 0.02 / 0.259, 0.05, extent)
    const wide = intervalBar(-0.1 / 0.259, 0.1 / 0.259, 0, extent)
    expect(narrow.to - narrow.from).toBeLessThan(wide.to - wide.from)
    expect(sharedExtent([row({ diffCiLow: null })])).toBeUndefined()
  })
})

describe('verdict', () => {
  it('never calls a model proven under 60 live forecasts, whatever the API or the backtest says', () => {
    const statuses: ModelStatus[] = ['collecting', 'testing', 'proven', 'retired']
    for (const status of statuses) {
      for (const n of [0, 1, 19, 20, 45, 59]) {
        for (const lo of [-0.05, 0, 0.001, 0.05]) {
          const v = verdict(row({ status, liveCount: n, diffCiLow: lo, diffCiHigh: lo + 0.06, skill: 0.4 }))
          if (status === 'retired') continue
          expect(v.tone).not.toBe('pos')
          expect(v.text).not.toMatch(/^Proven/)
        }
      }
    }
  })

  it('says why a promising model is not proven yet', () => {
    const v = verdict(row({ liveCount: 59, diffCiLow: 0.004, diffCiHigh: 0.06, skill: 0.12 }))
    expect(v.text).toBe(
      'Promising, not proven: 12% better than the baseline on 59 live forecasts with the interval above zero so far. ' +
        'It needs 60 before it can be called proven.',
    )
  })

  it('calls it proven when all three agree: the API, 60 forecasts, and the interval', () => {
    const v = verdict(row({ status: 'proven', liveCount: 60, diffCiLow: 0.004, diffCiHigh: 0.06, skill: 0.12 }))
    expect(v).toEqual({
      tone: 'pos',
      text: 'Proven on 60 live forecasts: 12% better than the baseline, and even the bottom of the 95% interval is above zero.',
    })
    expect(isProven(row({ status: 'proven', liveCount: 60, diffCiLow: 0.004 }))).toBe(true)
  })

  it('does not overrule the API upward: 60 forecasts above zero but not marked proven stays unproven', () => {
    const v = verdict(row({ status: 'testing', liveCount: 75, diffCiLow: 0.004, diffCiHigh: 0.06 }))
    expect(v.tone).not.toBe('pos')
    expect(v.text).toContain('has not marked it proven')
  })

  it('counts toward the first reading while collecting', () => {
    expect(verdict(row({ status: 'collecting', liveCount: 12, diffCiLow: null, diffCiHigh: null })).text).toBe(
      'Collecting: 12 of 20 live forecasts before a first reading. Nothing is claimed yet.',
    )
    expect(verdict(row({ status: 'collecting', liveCount: 0, skill: null })).text).toMatch(/no live forecast scored yet/)
  })

  it('reads the contract example as testing that could be no better than the baseline', () => {
    expect(verdict(row()).text).toBe(
      'Testing: 42 of 60 live forecasts. So far 12% better than the baseline, but it could be no better than the baseline.',
    )
  })

  it('reads skill on the direction control as a reason to look for a leak, never as an edge', () => {
    const promising = verdict(row({ target: 'direction', liveCount: 45, skill: 0.06, diffCiLow: 0.002, diffCiHigh: 0.03 }))
    expect(promising.text).toMatch(/look for a leak\.$/)
    const proven = verdict(row({ target: 'direction', status: 'proven', liveCount: 90, skill: 0.06, diffCiLow: 0.002 }))
    expect(proven.text).toMatch(/look for a leak\.$/)
    expect(verdict(row({ liveCount: 45, diffCiLow: 0.002 })).text).not.toMatch(/leak/)
  })

  it('treats direction at the baseline as the honest answer', () => {
    const v = verdict(row({ target: 'direction', modelKey: 'direction.logit', skill: -0.01, diffCiLow: -0.01, diffCiHigh: 0.008 }))
    expect(v.text).toMatch(/^Control: .* the honest answer for direction\.$/)
  })

  it('warns when a model is behind, and names when it would retire', () => {
    const v = verdict(row({ liveCount: 80, skill: -0.08, diffCiLow: -0.05, diffCiHigh: -0.002 }))
    expect(v.tone).toBe('warn')
    expect(v.text).toContain('retired at 120')
  })

  it('reports retirement', () => {
    const v = verdict(row({ status: 'retired', liveCount: 130, skill: -0.06, diffCiLow: -0.04, diffCiHigh: -0.001 }))
    expect(v).toMatchObject({ tone: 'neg' })
    expect(v.text).toMatch(/^Retired after 130 live forecasts/)
  })
})

describe('groupScoreboard', () => {
  const model = (key: string, version: string, target: ForecastModel['target']): ForecastModel => ({
    key,
    version,
    target,
    description: `${key} model`,
    backtest: null,
  })

  it('puts the ALL row first and the indices in page order under it', () => {
    const rows = [
      row({ underlying: 'SENSEX' }),
      row({ underlying: 'NIFTY' }),
      row({ underlying: 'ALL' }),
      row({ underlying: 'BANKNIFTY' }),
    ]
    const [g] = groupScoreboard(rows, [])
    expect(g.all.underlying).toBe('ALL')
    expect(g.perUnderlying.map((r) => r.underlying)).toEqual(['NIFTY', 'BANKNIFTY', 'SENSEX'])
  })

  it('keeps a registered model with no scored forecast, as collecting with nothing scored', () => {
    const groups = groupScoreboard([], [model('trend.logit', '2026-09-27.1', 'trend')])
    expect(groups).toHaveLength(1)
    expect(groups[0].all).toMatchObject({ status: 'collecting', liveCount: 0, skill: null, diffCiLow: null })
    expect(groups[0].description).toBe('trend.logit model')
    expect(groups[0].registeredUtc).toBeNull()
    const registered = groupScoreboard([], [{ ...model('trend.logit', '2026-09-27.1', 'trend'), registeredUtc: '2026-09-27T13:10:00Z' }])
    expect(registered[0].registeredUtc).toBe('2026-09-27T13:10:00Z')
  })

  it('orders range, trend, direction; then by key; newest version first', () => {
    const groups = groupScoreboard(
      [
        row({ modelKey: 'direction.logit', target: 'direction' }),
        row({ modelKey: 'range.har-vix', modelVersion: '2026-09-27.1' }),
        row({ modelKey: 'range.har-vix', modelVersion: '2026-10-02.1' }),
        row({ modelKey: 'range.har' }),
      ],
      [model('trend.logit', '2026-09-27.1', 'trend')],
    )
    expect(groups.map((g) => `${g.modelKey}@${g.modelVersion}`)).toEqual([
      'range.har@2026-09-27.1',
      'range.har-vix@2026-10-02.1',
      'range.har-vix@2026-09-27.1',
      'trend.logit@2026-09-27.1',
      'direction.logit@2026-09-27.1',
    ])
  })
})

describe('deskAnswer', () => {
  it('has nothing to judge without a model', () => {
    expect(deskAnswer([]).text).toMatch(/No model is registered yet/)
  })

  it('says not yet while everything is collecting', () => {
    const groups = groupScoreboard([row({ liveCount: 9, status: 'collecting' })], [])
    expect(deskAnswer(groups).text).toBe('Not yet. Every model is still collecting its first 20 live forecasts.')
  })

  it('names the closest model without promoting it', () => {
    const groups = groupScoreboard(
      [row(), row({ modelKey: 'range.har', skill: 0.05 }), row({ modelKey: 'trend.logit', target: 'trend', skill: 0.3, liveCount: 10, status: 'collecting' })],
      [],
    )
    const answer = deskAnswer(groups)
    expect(answer.tone).toBe('neutral')
    expect(answer.text).toBe(
      'Not yet — no model is proven on live forecasts. Closest: range.har-vix, 12% better than the baseline on ' +
        '42 live forecasts, but it could be no better than the baseline.',
    )
  })

  it('says yes only for a model that is proven on live forecasts', () => {
    const groups = groupScoreboard([row({ status: 'proven', liveCount: 66, diffCiLow: 0.003 })], [])
    expect(deskAnswer(groups)).toMatchObject({ tone: 'pos' })
    expect(deskAnswer(groups).text).toBe(
      'Yes, on live forecasts, by the pass mark fixed before the data: ' +
        'range.har-vix (range, 12% better than the baseline on 66 live forecasts).',
    )
  })
})

describe('calibration', () => {
  it('plots each non-empty tenth at (said, happened), in order', () => {
    const points = reliabilityPoints([
      { from: 0.6, to: 0.7, n: 4, meanP: 0.64, hitRate: 0.5 },
      { from: 0.1, to: 0.2, n: 0, meanP: 0.15, hitRate: 0 },
      { from: 0.3, to: 0.4, n: 18, meanP: 0.35, hitRate: 0.39 },
      { from: 0.9, to: 1.0, n: 2, meanP: Number.NaN, hitRate: 1 },
    ])
    expect(points).toEqual([
      { x: 0.35, y: 0.39, n: 18, from: 0.3, to: 0.4 },
      { x: 0.64, y: 0.5, n: 4, from: 0.6, to: 0.7 },
    ])
    expect(reliabilityLabel(points[0])).toBe('30–40%: said 35%, happened 39% of 18')
    expect(reliabilityPoints(null)).toEqual([])
  })

  it('flags a band that holds far more or far less than 80%, once there is a first reading', () => {
    expect(coverageTone(0.81, 42)).toBeUndefined()
    expect(coverageTone(0.62, 42)).toBe('warn')
    expect(coverageTone(0.97, 42)).toBe('warn')
    expect(coverageTone(0.5, 10)).toBeUndefined()
  })
})

describe('rangeScale', () => {
  it('ends past everything drawn on it, on a round number', () => {
    const s = rangeScale([1.55, 1.02, 0.97, 1.8, 1.25])
    expect(s.max).toBeGreaterThan(1.8)
    expect((s.max * 4) % 1).toBe(0)
    expect(s.at(0)).toBe(0)
    expect(s.at(s.max)).toBe(100)
    expect(s.at(s.max * 3)).toBe(100)
    expect(s.ticks[0]).toBe(0)
  })

  it('ignores values that are not there', () => {
    expect(rangeScale([null, undefined, Number.NaN]).max).toBeGreaterThan(0)
  })
})

describe('today', () => {
  it('reads the IST date and minute, not the machine clock', () => {
    // 20:00 UTC on the 27th is 01:30 IST on the 28th.
    expect(istToday(Date.parse('2026-09-27T20:00:00Z'))).toBe('2026-09-28')
    expect(istMinutes(ist('08:50'))).toBe(530)
  })

  it('shows today when there is today, else the next issued session, else the last', () => {
    const list = [forecast({ sessionDate: '2026-09-25' }), forecast({ sessionDate: '2026-09-28' })]
    expect(pickSession(list, '2026-09-28')).toEqual({ sessionDate: '2026-09-28', relation: 'today' })
    expect(pickSession(list, '2026-09-27')).toEqual({ sessionDate: '2026-09-28', relation: 'next' })
    expect(pickSession(list, '2026-09-29')).toEqual({ sessionDate: '2026-09-28', relation: 'last' })
    expect(pickSession([], '2026-09-28')).toBeNull()
  })

  it('counts down to 08:50 on a trading morning', () => {
    const note = issueNote({ nowMs: ist('07:30'), hasToday: false, isTradingDay: true })
    expect(note).toEqual({
      tone: 'muted',
      text: "Today's forecasts are issued at 08:50 IST, before the 09:15 open — in 1h 20m.",
    })
  })

  it('warns when 08:50 has passed with nothing issued, and says none can come after the open', () => {
    expect(issueNote({ nowMs: ist('09:00'), hasToday: false, isTradingDay: true })).toMatchObject({ tone: 'warn' })
    const late = issueNote({ nowMs: ist('11:00'), hasToday: false, isTradingDay: true })!
    expect(late.tone).toBe('warn')
    expect(late.text).toContain('today has none')
  })

  it('names the next trading day on a holiday or weekend', () => {
    const note = issueNote({
      nowMs: ist('11:00', '2026-09-27'),
      hasToday: false,
      isTradingDay: false,
      nextOpenUtc: '2026-09-28T03:45:00Z',
    })!
    expect(note.text).toMatch(/^No session today\. The next forecasts are issued at 08:50 IST on Mon.*28/)
  })

  it('falls back to the schedule when the session is not known, and says nothing once today is here', () => {
    expect(issueNote({ nowMs: ist('11:00'), hasToday: false })!.text).toContain('on trading days')
    expect(issueNote({ nowMs: ist('11:00'), hasToday: true, isTradingDay: true })).toBeNull()
  })

  it('builds one card per index in page order, the stronger live record leading', () => {
    const groups = groupScoreboard(
      [row({ modelKey: 'range.har', skill: 0.02 }), row({ modelKey: 'range.har-vix', skill: 0.12 })],
      [],
    )
    const list = [
      forecast({ id: 1, underlying: 'SENSEX' }),
      forecast({ id: 2, underlying: 'NIFTY', modelKey: 'range.har' }),
      forecast({ id: 3, underlying: 'NIFTY', modelKey: 'range.har-vix' }),
      forecast({ id: 4, underlying: 'NIFTY', target: 'trend', modelKey: 'trend.logit', prediction: { p: 0.38 }, baseline: { p: 0.32 } }),
      forecast({ id: 5, underlying: 'NIFTY', sessionDate: '2026-09-25' }),
    ]
    const cards = todayCards(list, '2026-09-28', groups)
    expect(cards.map((c) => c.underlying)).toEqual(['NIFTY', 'SENSEX'])
    expect(cards[0].range.map((f) => f.modelKey)).toEqual(['range.har-vix', 'range.har'])
    expect(cards[0].trend.map((f) => f.id)).toEqual([4])
    expect(cards[0].direction).toEqual([])
  })

  it('says when an unscored forecast will be scored, and warns when a past one was missed', () => {
    expect(scoringNote(forecast(), '2026-09-28')).toEqual({ tone: 'muted', text: 'Scored after 15:50 IST.' })
    expect(scoringNote(forecast(), '2026-09-29')).toMatchObject({ tone: 'warn' })
    expect(scoringNote(forecast({ outcome: OUTCOME }), '2026-09-29')).toBeNull()
  })
})

describe('forecast cells', () => {
  it('reads a scored range forecast against its baseline and the band', () => {
    const f = forecast({
      outcome: OUTCOME,
      scores: { loss: 0.004, baselineLoss: 0.061, metrics: { covered80: true }, calibration: [] },
    })
    expect(forecastCells(f)).toEqual({
      forecast: '1.02% (0.68–1.55%)',
      baseline: '0.97%',
      outcome: '1.02% · Normal · inside',
      beat: true,
      lossTitle: 'loss 0.004 vs baseline 0.061 (lower is better)',
    })
  })

  it('reads trend and direction as probabilities and what happened', () => {
    const scores = { loss: 0.1444, baselineLoss: 0.1024 }
    const outcome = { ...OUTCOME, bucket: null }
    const trend = forecast({ target: 'trend', prediction: { p: 0.38 }, baseline: { p: 0.32 }, outcome, scores })
    expect(forecastCells(trend)).toMatchObject({ forecast: '38%', baseline: '32%', outcome: 'no trend day', beat: false })
    const dir = forecast({ target: 'direction', prediction: { p: 0.54 }, baseline: { p: 0.53 }, outcome: OUTCOME })
    expect(forecastCells(dir)).toMatchObject({ outcome: 'closed up', beat: null, lossTitle: 'not scored yet' })
  })

  it('formats probabilities and their distance from the baseline', () => {
    expect(formatProb(0.384)).toBe('38%')
    expect(formatProb(1.2)).toBe('100%')
    expect(formatProbDelta(0.38, 0.32)).toBe('+6 pts')
    expect(formatProbDelta(0.3, 0.32)).toBe('−2 pts')
    expect(formatProbDelta(0.321, 0.32)).toBe('±0 pts')
    expect(asProb({ p: 0.4 })).toBe(0.4)
    expect(asRange({ p: 0.4 })).toBeNull()
  })

  it('shows each input with a readable name, in its unit', () => {
    expect(inputLabel('vixPrevClose')).toBe('India VIX, previous close')
    expect(inputLabel('customThing')).toBe('Custom thing')
    expect(formatInputValue('vixPrevClose', 11.9)).toBe('11.90')
    expect(formatInputValue('r1', 0.6146)).toBe('0.61%')
    expect(formatInputValue('prevReturn', 0.336)).toBe('+0.34%')
    expect(formatInputValue('prevReturn', -0.2)).toBe('−0.20%')
    expect(formatInputValue('vixChange5', 0.75)).toBe('+0.75')
    expect(formatInputValue('rangeRatio', 0.8641)).toBe('0.86×')
    expect(formatInputValue('prevClose', 23140.5)).toBe('23,140.50')
    expect(formatInputValue('trainingSessions', 1277)).toBe('1,277')
    expect(formatInputValue('trainedThrough', '2026-09-22')).toBe('22 Sept')
    expect(formatInputValue('expiryDay', false)).toBe('no')
    // The models send the calendar facts as 0/1 too.
    expect(formatInputValue('expiryDay', 1)).toBe('yes')
    expect(formatInputValue('monday', 0)).toBe('no')
    expect(formatInputValue('events', '')).toBe('none')
    expect(formatInputValue('gap', null)).toBe('—')
    expect(formatInputValue('custom', 'x')).toBe('x')
  })

  it('sorts newest session first, then index, target and model', () => {
    const list = [
      forecast({ id: 1, sessionDate: '2026-09-25' }),
      forecast({ id: 2, underlying: 'SENSEX' }),
      forecast({ id: 3, target: 'direction', modelKey: 'direction.logit' }),
      forecast({ id: 4 }),
    ]
    expect(sortForecasts(list).map((f) => f.id)).toEqual([4, 3, 2, 1])
  })
})

/** inputs.liveOnly as the 08:50 job recorded it on 28 Sep 2026 (every forecast carries the same one). */
const LIVE_ONLY = {
  usedByModels: false,
  giftNiftyGapPct: 0,
  giftNiftyChangePct: -0.41,
  giftNiftyAsOf: '2026-09-28T03:13:18Z',
  giftNiftyFetchedUtc: '2026-09-28T03:15:03Z',
  news: {
    auto: { n: 50, scored: 50, sentiment: 0.705, maxImportance: 3 },
    banking: { n: 57, scored: 57, sentiment: 0.038, maxImportance: 2 },
    commodities: { n: 67, scored: 67, sentiment: -0.334, maxImportance: 3 },
    fmcg: { n: 55, scored: 54, sentiment: 0.325, maxImportance: 3 },
    global: { n: 377, scored: 375, sentiment: -0.14, maxImportance: 3 },
    india: { n: 490, scored: 488, sentiment: 0.167, maxImportance: 3 },
    it: { n: 75, scored: 75, sentiment: 0.095, maxImportance: 2 },
    'nifty50 announcements': { n: 9, scored: 9, sentiment: -0.046, maxImportance: 3 },
    uncategorised: { n: 2, scored: 0, sentiment: null, maxImportance: null },
  },
  earningsToday: 0,
  earningsSincePrev: 0,
}

describe('the models, in words', () => {
  it('gives each known model a short label and a description, the registered one first', () => {
    expect(modelShort('range.har-vix')).toBe('recent ranges + India VIX')
    expect(modelShort('direction.logit')).toBe('logistic, 7 inputs · control')
    expect(modelShort('direction.logit-cues')).toBe('logistic, 7 inputs + pre-open cues · control')
    expect(modelShort('range.new')).toBe('')
    expect(modelAbout('range.har')).toMatch(/1-, 5- and 22-session mean ranges/)
    expect(modelAbout('range.har', 'As registered.')).toBe('As registered.')
    expect(modelAbout('trend.logit-cues')).toMatch(/^trend\.logit plus the pre-open context/)
    expect(modelTitle('range.har', '2026-09-27.1')).toMatch(/^range\.har · version 2026-09-27\.1 — Log-range/)
    expect(modelTitle('mystery.model')).toBe('mystery.model')
  })
})

describe('times and signs', () => {
  it('reads UTC stamps on the IST clock', () => {
    expect(formatIstClock('2026-09-28T03:13:18Z')).toBe('08:43 IST')
    expect(formatIstClock('2026-09-27T20:00:00Z')).toBe('01:30 IST')
    expect(formatIstClock(null)).toBe('—')
    expect(formatIstClock('not a time')).toBe('—')
    expect(formatIstStamp('2026-09-27T20:00:00Z')).toBe('28 Sept, 01:30 IST')
    expect(shortDay('2026-09-25')).toBe('25 Sept')
  })

  it('says when a session was issued, as a span when its forecasts went out at different minutes', () => {
    expect(issuedText([{ issuedUtc: '2026-09-28T03:20:05.66Z' }, { issuedUtc: '2026-09-28T03:20:05.53Z' }])).toBe('issued 08:50 IST')
    expect(issuedText([{ issuedUtc: '2026-09-28T03:20:05Z' }, { issuedUtc: '2026-09-28T03:35:00Z' }])).toBe('issued 08:50–09:05 IST')
    expect(issuedText([])).toBe('')
  })

  it('signs numbers with a true minus and never signs a zero', () => {
    expect(formatSigned(0.336)).toBe('+0.34')
    expect(formatSigned(-2184.4, 0)).toBe('−2,184')
    expect(formatSigned(-0.001)).toBe('0.00')
    expect(formatSignedPct(0)).toBe('0.00%')
    expect(formatSignedPct(-0.41)).toBe('−0.41%')
    expect(formatSignedPct(null)).toBe('—')
  })
})

describe('sentiment', () => {
  it('is level within ±0.15, as on the Desk, and unknown when nothing was scored', () => {
    expect(sentimentTone(0.705)).toBe('pos')
    expect(sentimentTone(-0.334)).toBe('neg')
    expect(sentimentTone(0.15)).toBe('flat')
    expect(sentimentTone(-0.14)).toBe('flat')
    expect(sentimentTone(0)).toBe('flat')
    expect(sentimentTone(null)).toBeNull()
    expect(formatSentiment(0.705)).toBe('+0.71')
    expect(formatSentiment(-0.046)).toBe('−0.05')
    expect(formatSentiment(null)).toBe('—')
  })

  it('orders the news India, Global, the sectors A–Z, uncategorised, then the NIFTY 50 filings', () => {
    const c = readLiveContext(LIVE_ONLY)!
    expect(c.news!.map((r) => r.label)).toEqual([
      'India',
      'Global',
      'Auto',
      'Banking',
      'Commodities',
      'FMCG',
      'IT',
      'Uncategorised',
      'NIFTY 50 filings',
    ])
    const again = sortNewsRows([...c.news!].reverse())
    expect(again.map((r) => r.key)).toEqual(c.news!.map((r) => r.key))
    expect(newsLabel('real estate')).toBe('Real estate')
  })
})

describe('the pre-open context', () => {
  it('reads GIFT Nifty, the news and the earnings load, and says no model used them', () => {
    const c = readLiveContext(LIVE_ONLY)!
    expect(c.usedByModels).toBe(false)
    expect(c.gift).toEqual({
      gapPct: 0,
      changePct: -0.41,
      asOfUtc: '2026-09-28T03:13:18Z',
      fetchedUtc: '2026-09-28T03:15:03Z',
    })
    expect(c.news!.find((r) => r.key === 'india')).toEqual({
      key: 'india',
      label: 'India',
      group: 'market',
      n: 490,
      scored: 488,
      sentiment: 0.167,
      maxImportance: 3,
    })
    expect(c.earnings).toEqual({ today: 0, sincePrev: 0 })
    expect(c.other).toEqual([])
    expect(readLiveContext('x')).toBeNull()
    expect(readLiveContext(null)).toBeNull()
  })

  it('keeps a part the job could not read as its sentence, never as a zero', () => {
    const c = readLiveContext({
      usedByModels: false,
      giftNifty: 'no snapshot this morning',
      news: 'unavailable (UndefinedTable)',
      earnings: 'unavailable (OperationalError)',
    })!
    expect(c.gift).toBeNull()
    expect(c.giftNote).toBe('no snapshot this morning')
    expect(c.news).toBeNull()
    expect(c.newsNote).toBe('unavailable (UndefinedTable)')
    expect(c.earnings).toBeNull()
    expect(c.earningsNote).toBe('unavailable (OperationalError)')
    expect(readLiveContext({ news: {} })!.news).toEqual([])
  })

  it('passes anything it has no layout for to the generic view', () => {
    const c = readLiveContext({ ...LIVE_ONLY, breadth: { advancing: 31, asOf: '2026-09-28T03:10:00Z' } })!
    expect(c.other).toEqual([
      {
        key: 'breadth',
        label: 'Breadth',
        node: {
          kind: 'fields',
          fields: [
            { key: 'advancing', label: 'Advancing', node: { kind: 'text', text: '31' } },
            { key: 'asOf', label: 'As of', node: { kind: 'text', text: '28 Sept, 08:40 IST' } },
          ],
        },
      },
    ])
  })

  it('shows a session\'s context once, and each distinct one when a later run recorded another', () => {
    const f = (id: number, underlying: string, liveOnly: unknown, issuedUtc = '2026-09-28T03:20:05Z') =>
      forecast({ id, underlying, issuedUtc, inputs: { r1: 0.6, liveOnly } })
    // Same record, keys in another order: still one.
    const reordered = Object.fromEntries(Object.entries(LIVE_ONLY).reverse())
    const one = sessionContexts([f(1, 'SENSEX', LIVE_ONLY), f(2, 'NIFTY', reordered), f(3, 'BANKNIFTY', LIVE_ONLY)])
    expect(one).toHaveLength(1)
    expect(one[0].count).toBe(3)
    expect(one[0].underlyings).toEqual(['NIFTY', 'BANKNIFTY', 'SENSEX'])

    const later = { ...LIVE_ONLY, giftNiftyGapPct: 0.12 }
    const two = sessionContexts([f(1, 'NIFTY', LIVE_ONLY), f(2, 'NIFTY', LIVE_ONLY), f(3, 'SENSEX', later, '2026-09-28T03:36:00Z')])
    expect(two.map((c) => [c.count, c.underlyings])).toEqual([
      [2, ['NIFTY']],
      [1, ['SENSEX']],
    ])
    expect(sessionContexts([forecast()])).toEqual([])
  })
})

describe('the generic value view', () => {
  it('draws any value as nested labels and values, never as JSON', () => {
    expect(describeValue(null)).toEqual({ kind: 'text', text: '—' })
    expect(describeValue(true)).toEqual({ kind: 'text', text: 'yes' })
    expect(describeValue(12345.6789)).toEqual({ kind: 'text', text: '12,345.679' })
    expect(describeValue('2026-09-25')).toEqual({ kind: 'text', text: 'Fri, 25 Sept' })
    expect(describeValue([1, 'two', null])).toEqual({ kind: 'text', text: '1, two, —' })
    expect(describeValue([])).toEqual({ kind: 'text', text: 'none' })
    expect(describeValue({})).toEqual({ kind: 'text', text: 'none' })
    expect(describeValue({ maxImportance: 3, inner: { n: 2 } })).toEqual({
      kind: 'fields',
      fields: [
        { key: 'maxImportance', label: 'Max importance', node: { kind: 'text', text: '3' } },
        { key: 'inner', label: 'Inner', node: { kind: 'fields', fields: [{ key: 'n', label: 'N', node: { kind: 'text', text: '2' } }] } },
      ],
    })
    expect(describeValue([{ a: 1 }])).toEqual({
      kind: 'items',
      items: [{ kind: 'fields', fields: [{ key: 'a', label: 'A', node: { kind: 'text', text: '1' } }] }],
    })
  })

  it('stops at four levels and at forty entries, saying how many are left', () => {
    const deep = { a: { b: { c: { d: { e: 1, f: 2 } } } } }
    const walk = (n: ReturnType<typeof describeValue>): ReturnType<typeof describeValue> =>
      n.kind === 'fields' ? walk(n.fields[0].node) : n
    expect(walk(describeValue(deep))).toEqual({ kind: 'text', text: '2 fields' })
    const wide = describeValue(Object.fromEntries(Array.from({ length: 45 }, (_, i) => [`k${i}`, i])))
    expect(wide.kind === 'fields' && wide.fields.length).toBe(41)
    expect(wide.kind === 'fields' && wide.fields[40].node).toEqual({ kind: 'text', text: 'and 5 more' })
  })

  it('makes a readable label of any key', () => {
    expect(humanizeKey('giftNiftyGapPct')).toBe('Gift nifty gap pct')
    expect(humanizeKey('trained_through')).toBe('Trained through')
    expect(humanizeKey('r22')).toBe('R22')
  })
})

describe('what the models saw', () => {
  const card = [
    forecast({
      id: 1,
      modelKey: 'range.har',
      inputs: { prevSession: '2026-09-25', prevClose: 23140.5, r1: 0.6146, trainingSessions: 1491, liveOnly: LIVE_ONLY },
    }),
    forecast({
      id: 2,
      modelKey: 'range.har-vix',
      inputs: { prevSession: '2026-09-25', r1: 0.6146, vixPrevClose: 12.13, trainingSessions: 1238, liveOnly: LIVE_ONLY },
    }),
    forecast({
      id: 3,
      modelKey: 'trend.logit',
      target: 'trend',
      prediction: { p: 0.33 },
      baseline: { p: 0.38 },
      inputs: { prevSession: '2026-09-25', monday: true, vixPrevClose: 12.13, custom: { a: 1 }, liveOnly: LIVE_ONLY },
    }),
  ]

  const text = (n: unknown) => (n && (n as { kind: string }).kind === 'text' ? (n as { text: string }).text : n)

  it('shows each fact once, under the models that record it, in the documented order; the context left out', () => {
    const v = inputsView(card)!
    expect(v.prevSession).toBe('2026-09-25')
    expect(v.groups.map((g) => [g.models, g.all, g.facts.map((f) => [f.label, text(f.value)])])).toEqual([
      [['range.har'], false, [['Previous close', '23,140.50']]],
      [['range.har', 'range.har-vix'], false, [['Range, last session', '0.61%']]],
      [['range.har-vix', 'trend.logit'], false, [['India VIX, previous close', '12.13']]],
      [['trend.logit'], false, [['Monday', 'yes'], ['Custom', { kind: 'fields', fields: [{ key: 'a', label: 'A', node: { kind: 'text', text: '1' } }] }]]],
    ])
  })

  it('puts what differs by model in a table of its own, a row per model', () => {
    const v = inputsView(card)!
    expect(v.perModel!.keys).toEqual([{ key: 'trainingSessions', label: 'Training sessions' }])
    expect(v.perModel!.rows.map((r) => [r.model, r.cells.map(text)])).toEqual([
      ['range.har', ['1,491']],
      ['range.har-vix', ['1,238']],
      ['trend.logit', [null]],
    ])
  })

  it('names a group "all" only when every model on the card records it', () => {
    const both = [
      forecast({ id: 1, inputs: { vixPrevClose: 12.13, trainedThrough: '2026-09-22' } }),
      forecast({ id: 2, modelKey: 'trend.logit', target: 'trend', inputs: { vixPrevClose: 12.13, trainedThrough: '2026-09-22' } }),
    ]
    const v = inputsView(both)!
    expect(v.groups).toEqual([
      {
        models: ['range.har-vix', 'trend.logit'],
        all: true,
        facts: [
          { key: 'vixPrevClose', label: 'India VIX, previous close', value: { kind: 'text', text: '12.13' } },
          { key: 'trainedThrough', label: 'Trained through', value: { kind: 'text', text: '22 Sept' } },
        ],
      },
    ])
    expect(v.perModel).toBeNull()
  })

  it('lists the session when the models disagree on it, and has nothing to show without inputs', () => {
    const split = [
      forecast({ id: 1, inputs: { prevSession: '2026-09-25', r1: 0.6 } }),
      forecast({ id: 2, modelKey: 'range.har', inputs: { prevSession: '2026-09-24', r1: 0.6 } }),
    ]
    const v = inputsView(split)!
    expect(v.prevSession).toBeNull()
    expect(v.perModel!.keys.map((k) => k.key)).toEqual(['prevSession'])
    expect(v.perModel!.rows.map((r) => r.cells.map(text))).toEqual([['25 Sept'], ['24 Sept']])
    expect(v.groups[0].all).toBe(true)
    expect(inputsView([forecast({ inputs: null }), forecast({ inputs: { liveOnly: LIVE_ONLY } })])).toBeNull()
  })

  it('tells two versions of one model apart', () => {
    const v = inputsView([
      forecast({ id: 1, modelVersion: '2026-09-27.1', inputs: { trainingSessions: 1200 } }),
      forecast({ id: 2, modelVersion: '2026-10-05.1', inputs: { trainingSessions: 1206 } }),
    ])!
    expect(v.perModel!.rows.map((r) => r.model)).toEqual(['range.har-vix 2026-09-27.1', 'range.har-vix 2026-10-05.1'])
  })
})

describe('requests', () => {
  it('leaves empty filters out of the query', () => {
    expect(forecastsQuery({})).toBe('')
    expect(forecastsQuery({ from: '2026-09-28', to: '2026-09-28', target: 'range', underlying: 'NIFTY' })).toBe(
      'from=2026-09-28&to=2026-09-28&target=range&underlying=NIFTY',
    )
    expect(forecastsQuery({ target: '', underlying: '' })).toBe('')
  })

  it('reads a list strictly, so an odd answer is an error rather than an empty scoreboard', () => {
    expect(readForecastList([forecast()])).toHaveLength(1)
    expect(readScoreboard({ items: [row()] })).toHaveLength(1)
    expect(() => readScoreboard({ rows: [] })).toThrow(/cannot read/)
    expect(() => readForecastList(null)).toThrow()
  })

  it('puts the two expected failures in words', () => {
    expect(analysisErrorText({ status: 403 })).toMatch(/Analysis module/)
    expect(analysisErrorText({ status: 404 })).toMatch(/Forecasts API yet/)
    expect(analysisErrorText(new Error('boom'))).toBeNull()
    expect(analysisErrorText(null)).toBeNull()
  })
})

describe('the fixture', () => {
  const afterClose = buildAnalysisFixture({ nowMs: ist('16:30') })

  it('is shaped like the contract: every forecast parses, every model is registered', () => {
    expect(afterClose.models.map((m) => m.key).sort()).toEqual(['direction.logit', 'range.har', 'range.har-vix', 'trend.logit'])
    for (const f of afterClose.forecasts) {
      if (f.target === 'range') expect(asRange(f.prediction)).not.toBeNull()
      else expect(asProb(f.prediction)).not.toBeNull()
      expect(afterClose.models.some((m) => m.key === f.modelKey && m.version === f.modelVersion)).toBe(true)
    }
    expect(new Set(afterClose.forecasts.map((f) => f.underlying))).toEqual(new Set(['NIFTY', 'BANKNIFTY', 'SENSEX']))
    // Only a range forecast names a bucket; trend and direction state no edges.
    for (const f of afterClose.forecasts.filter((x) => x.outcome)) {
      if (f.target === 'range') expect(f.outcome!.bucket).not.toBeNull()
      else expect(f.outcome!.bucket).toBeNull()
    }
  })

  it('keeps its scoreboard consistent with its forecasts and the contract rules', () => {
    for (const r of afterClose.scoreboard) {
      const scored = afterClose.forecasts.filter(
        (f) => f.scores && f.modelKey === r.modelKey && f.modelVersion === r.modelVersion && (r.underlying === 'ALL' || f.underlying === r.underlying),
      )
      expect(r.liveCount).toBe(scored.length)
      if (r.liveCount < PROOF.firstReadingAt) expect(r.status).toBe('collecting')
      if (r.status === 'proven') expect(isProven(r)).toBe(true)
      expect(verdict(r).tone === 'pos').toBe(isProven(r))
    }
  })

  it('has nothing for today before 08:50 IST, and today unscored until 15:50 IST', () => {
    const early = buildAnalysisFixture({ nowMs: ist('07:00') })
    expect(early.forecasts.some((f) => f.sessionDate === '2026-09-28')).toBe(false)
    const midday = buildAnalysisFixture({ nowMs: ist('11:00') })
    const today = midday.forecasts.filter((f) => f.sessionDate === '2026-09-28')
    expect(today.length).toBeGreaterThan(0)
    expect(today.every((f) => f.outcome === null && f.scores === null)).toBe(true)
    expect(afterClose.forecasts.filter((f) => f.sessionDate === '2026-09-28').every((f) => f.scores)).toBe(true)
  })

  it('records one pre-open context per session on every forecast, as the morning job does', () => {
    const today = afterClose.forecasts.filter((f) => f.sessionDate === '2026-09-28')
    const contexts = sessionContexts(today)
    expect(contexts).toHaveLength(1)
    expect(contexts[0].count).toBe(today.length)
    expect(contexts[0].context.usedByModels).toBe(false)
    expect(contexts[0].context.news!.slice(0, 2).map((r) => r.key)).toEqual(['india', 'global'])
    expect(inputsView(today.filter((f) => f.underlying === 'NIFTY'))!.groups.length).toBeGreaterThan(0)
  })

  it('answers GET like the API: filtered, newest session first; an unknown path is a 404', () => {
    const list = fixtureResponse('/api/Forecasts?from=2026-09-24&target=range&underlying=NIFTY', afterClose) as Forecast[]
    expect(list.length).toBeGreaterThan(0)
    expect(list.every((f) => f.target === 'range' && f.underlying === 'NIFTY' && f.sessionDate >= '2026-09-24')).toBe(true)
    expect(list[0].sessionDate >= list[list.length - 1].sessionDate).toBe(true)
    expect(fixtureResponse('/api/Forecasts/scoreboard', afterClose)).toBe(afterClose.scoreboard)
    expect(() => fixtureResponse('/api/Forecasts/nope', afterClose)).toThrow(expect.objectContaining({ status: 404 }))
  })
})
