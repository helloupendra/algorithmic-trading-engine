import { describe, expect, it } from 'vitest'

import {
  actionText,
  aiTraderDecisionsQuery,
  aiTraderPositionsQuery,
  baselineText,
  beatText,
  confidenceText,
  contractText,
  dayCountsText,
  hm,
  jsonBlock,
  lakhText,
  limitsParts,
  listedText,
  looksSpanText,
  lotsText,
  modeLabel,
  netTone,
  placedText,
  planFacts,
  readAiTraderDecision,
  readAiTraderDecisionsPage,
  readAiTraderDetail,
  readAiTraderPlan,
  readAiTraderScoreboard,
  readAiTraderScoreRow,
  readAiTraderShadowBook,
  readAiTraderShadowPosition,
  readAiTraderStatus,
  replayRecordPollMs,
  ruleLabel,
  sampleNote,
  scoreKindText,
  scoreRowAnchor,
  shadowCountsText,
  shadowEnding,
  shadowLastPrice,
  shadowResultText,
  verdictOf,
  lessonEvidenceText,
  lessonGainText,
  lessonOrder,
  lessonOriginText,
  lessonStatus,
  readAiTraderLessons,
} from './aiTrader'
import type { AiTraderDecision } from './aiTrader'

/** One decision summary exactly as AiTraderController sends it (System.Text.Json, web defaults). */
const summary = (over: Record<string, unknown> = {}) => ({
  id: 41,
  clockUtc: '2026-10-01T05:50:00Z',
  clockIst: '11:20',
  day: '2026-10-01',
  mode: 'shadow',
  replaySessionId: null,
  action: 'buy',
  underlying: 'NIFTY',
  reason: 'NIFTY broke the morning high with PCR rising from 0.92 to 1.08.',
  confidence: 0.64,
  allowed: false,
  rule: 'stop',
  why: 'The stop ₹40 is more than 40% below the entry ₹112.5; the lowest is ₹67.5.',
  executed: false,
  error: '',
  model: 'nvidia/llama-3.3-nemotron-super-49b-v1.5',
  callId: 812,
  ...over,
})

/** GET /api/AiTrader/status with the default rules (TimeOnly serialises as "HH:mm:ss"). */
const status = {
  status: 'on',
  mode: 'shadow',
  everyMinutes: 10,
  rules: {
    underlyings: ['NIFTY', 'BANKNIFTY', 'SENSEX'],
    maxLotsPerTrade: 2,
    maxPremiumPerTrade: 50000.0,
    maxOpenPositions: 3,
    capital: 500000.0,
    maxStopFraction: 0.4,
    dailyLossLimit: 10000.0,
    openFrom: '09:20:00',
    openUntil: '14:45:00',
    maxTradesPerDay: 10,
    strategies: ['GhostTangentCrossings', 'ChainFlowBuy'],
    maxStrategyRuns: 3,
  },
  today: { date: '2026-10-01', decisions: 12, actions: 3, allowed: 2, refused: 1, noAnswer: 1 },
  latest: [summary(), summary({ id: 40, action: 'none', allowed: true, rule: 'ok', why: 'Nothing to do.' })],
  shadow: { positions: 2, open: 1, net: -1240.5 },
}

/** One shadow position exactly as AiTraderController.View sends it. */
const position = (over: Record<string, unknown> = {}) => ({
  id: 12,
  decisionId: 104,
  mode: 'shadow',
  replaySessionId: null,
  day: '2026-10-01',
  symbol: 'NSE:NIFTY25O0725300CE',
  underlying: 'NIFTY',
  optionType: 'CE',
  strike: 25300.0,
  expiry: '2026-10-07',
  lots: 1,
  lotSize: 75,
  entryUtc: '2026-10-01T04:30:04Z',
  entryIst: '10:00',
  entryPrice: 112.5,
  stopLoss: 78.0,
  target: 160.0,
  markPrice: 96.4,
  markUtc: '2026-10-01T06:12:00Z',
  open: false,
  exitUtc: '2026-10-01T06:12:00Z',
  exitIst: '11:42',
  exitPrice: 76.9,
  exitReason: 'stop',
  charges: 61.84,
  net: -2731.84,
  ...over,
})

const read = (over: Record<string, unknown> = {}) => readAiTraderDecision(summary(over))!

describe('reading the status', () => {
  it('reads the body as the controller sends it', () => {
    const s = readAiTraderStatus(status)
    expect(s.status).toBe('on')
    expect(s.mode).toBe('shadow')
    expect(s.everyMinutes).toBe(10)
    expect(s.rules).toEqual({
      underlyings: ['NIFTY', 'BANKNIFTY', 'SENSEX'],
      maxLotsPerTrade: 2,
      maxPremiumPerTrade: 50000,
      maxOpenPositions: 3,
      capital: 500000,
      maxStopFraction: 0.4,
      dailyLossLimit: 10000,
      openFrom: '09:20',
      openUntil: '14:45',
      maxTradesPerDay: 10,
      strategies: ['GhostTangentCrossings', 'ChainFlowBuy'],
      maxStrategyRuns: 3,
    })
    expect(s.today).toEqual({ date: '2026-10-01', decisions: 12, actions: 3, allowed: 2, refused: 1, noAnswer: 1 })
    expect(s.latest.map((d) => d.id)).toEqual([41, 40])
    expect(s.shadow).toEqual({ positions: 2, open: 1, net: -1240.5 })
  })

  it('refuses a body that is not the status, rather than drawing an AI Trader that is off', () => {
    for (const bad of ['<!doctype html>', null, [status], { hello: 1 }]) {
      expect(() => readAiTraderStatus(bad)).toThrow(/shape this page cannot read/)
    }
  })

  it('keeps what the body left out as not known, never as off or zero limits', () => {
    const s = readAiTraderStatus({ status: '', rules: 'none', latest: 'x' })
    expect(s).toEqual({ status: null, mode: null, everyMinutes: null, rules: null, today: null, latest: [], shadow: null })
    // A shadow figure it cannot read is not known, never ₹0.
    expect(readAiTraderStatus({ status: 'on', shadow: { positions: 'two', net: '-5' } }).shadow).toEqual({ positions: 0, open: 0, net: null })
    const r = readAiTraderStatus({ rules: { maxLotsPerTrade: '2', maxStopFraction: 4, dailyLossLimit: -10000, openFrom: '9:20', openUntil: 'noon' } }).rules!
    expect(r.maxLotsPerTrade).toBeNull()
    expect(r.maxStopFraction).toBeNull()
    expect(r.dailyLossLimit).toBe(10000)
    expect(r.openFrom).toBe('09:20')
    expect(r.openUntil).toBeNull()
    expect(r.underlyings).toEqual([])
  })

  it('reads a TimeOnly however it is written', () => {
    expect(hm('09:20:00')).toBe('09:20')
    expect(hm('14:45:00.0000000')).toBe('14:45')
    expect(hm('9:20')).toBe('09:20')
    expect(hm('24:00')).toBeNull()
    expect(hm('09:20 IST')).toBeNull()
    expect(hm(920)).toBeNull()
  })
})

describe('reading decisions', () => {
  it('reads a summary as the controller sends it', () => {
    expect(read()).toEqual({
      id: 41,
      clockUtc: '2026-10-01T05:50:00Z',
      clockIst: '11:20',
      day: '2026-10-01',
      mode: 'shadow',
      replaySessionId: null,
      action: 'buy',
      underlying: 'NIFTY',
      option: null,
      reason: 'NIFTY broke the morning high with PCR rising from 0.92 to 1.08.',
      confidence: 0.64,
      allowed: false,
      rule: 'stop',
      why: 'The stop ₹40 is more than 40% below the entry ₹112.5; the lowest is ₹67.5.',
      executed: false,
      error: '',
      model: 'nvidia/llama-3.3-nemotron-super-49b-v1.5',
      callId: 812,
    })
  })

  it('never trusts a field: allowed and executed only in as many words, a confidence only in 0..1', () => {
    const d = read({
      allowed: 'true',
      executed: 1,
      confidence: 64,
      action: ' BUY ',
      underlying: 'nifty',
      option: 'ce',
      rule: 'STOP',
      clockIst: null,
      replaySessionId: 4,
      callId: -1,
    })
    expect(d.allowed).toBe(false)
    expect(d.executed).toBe(false)
    expect(d.confidence).toBeNull()
    expect(d.action).toBe('buy')
    expect(d.underlying).toBe('NIFTY')
    expect(d.option).toBe('CE')
    expect(d.rule).toBe('stop')
    // No IST clock sent: worked out from the UTC one.
    expect(d.clockIst).toBe('11:20')
    expect(d.replaySessionId).toBe(4)
    expect(d.callId).toBeNull()
    expect(read({ option: 'FUT' }).option).toBeNull()
  })

  it('reads a page, dropping rows it could not open', () => {
    const page = readAiTraderDecisionsPage({ items: [summary(), { id: 'x' }, 'junk', summary({ id: 39 })], nextBeforeId: 39 })
    expect(page.items.map((d) => d.id)).toEqual([41, 39])
    expect(page.nextBeforeId).toBe(39)
    expect(readAiTraderDecisionsPage({ items: [], nextBeforeId: null })).toEqual({ items: [], nextBeforeId: null })
    expect(readAiTraderDecisionsPage([summary()]).items).toHaveLength(1)
    expect(() => readAiTraderDecisionsPage({ decisions: [] })).toThrow(/shape this page cannot read/)
    expect(() => readAiTraderDecisionsPage('<html>')).toThrow(/shape this page cannot read/)
  })

  it('reads a decision in full, with its plan', () => {
    const planJson = JSON.stringify({
      action: 'buy',
      underlying: 'NIFTY',
      option: 'CE',
      strike: 'ATM',
      lots: 1,
      stopLoss: 40,
      target: 160,
      strategy: null,
      positionId: null,
      runId: null,
      reason: 'x',
      confidence: 0.64,
    })
    const d = readAiTraderDetail({ decision: summary(), brief: 'NIFTY 25,312 (+0.4%)\n', planJson, resultJson: '{"contract":{"symbol":"NSE:NIFTY25O0725300CE"}}', briefHash: 'ABC123' })
    expect(d.decision.id).toBe(41)
    expect(d.brief).toBe('NIFTY 25,312 (+0.4%)\n')
    expect(d.briefHash).toBe('ABC123')
    expect(d.plan).toEqual({
      action: 'buy',
      underlying: 'NIFTY',
      option: 'CE',
      strike: 'ATM',
      lots: 1,
      stopLoss: 40,
      target: 160,
      strategy: null,
      positionId: null,
      runId: null,
    })
    expect(() => readAiTraderDetail({ brief: 'x' })).toThrow(/cannot read/)
    expect(() => readAiTraderDetail(null)).toThrow(/cannot read/)
  })

  it('reads an unreadable answer as no plan', () => {
    expect(readAiTraderPlan('{"answer":"I think NIFTY goes up"}')).toBeNull()
    expect(readAiTraderPlan('not json')).toBeNull()
    expect(readAiTraderPlan('[1,2]')).toBeNull()
    expect(readAiTraderPlan('')).toBeNull()
    expect(readAiTraderPlan('{"action":"exit","positionId":12}')).toMatchObject({ action: 'exit', positionId: 12 })
    expect(readAiTraderPlan('{"action":"buy","strike":25300}')!.strike).toBe('25300')
  })

  it('asks for a day or a replay, a page at a time', () => {
    expect(aiTraderDecisionsQuery({ day: '2026-10-01' }, null)).toBe('day=2026-10-01&take=50')
    expect(aiTraderDecisionsQuery({ replay: 4 }, 41, 20)).toBe('replay=4&take=20&beforeId=41')
    expect(aiTraderDecisionsQuery({ day: null, replay: null }, null)).toBe('take=50')
  })

  it("reads a replay's record while it plays and as it settles, then stops: an ended replay's record no longer changes", () => {
    const ended = Date.parse('2026-10-01T14:02:00Z')
    expect(replayRecordPollMs({ active: true, endedUtc: null }, ended)).toBe(10_000)
    // Its last look and the minute check that squares off its book land in the minutes after the end.
    expect(replayRecordPollMs({ active: false, endedUtc: '2026-10-01T14:02:00Z' }, ended + 60_000)).toBe(30_000)
    // Last night's replay, still on the page this morning: no more reads every 30 s.
    expect(replayRecordPollMs({ active: false, endedUtc: '2026-10-01T14:02:00Z' }, ended + 10 * 60_000)).toBe(false)
    expect(replayRecordPollMs({ active: false, endedUtc: '2026-10-01T14:02:00Z' }, ended + 12 * 3_600_000)).toBe(false)
    // Over, but when is not known: it may still be settling.
    expect(replayRecordPollMs({ active: false, endedUtc: null }, ended)).toBe(30_000)
  })
})

describe('the verdict', () => {
  it('says allowed, refused and by which rule, or no answer', () => {
    expect(verdictOf({ action: 'buy', allowed: true, rule: 'ok' })).toMatchObject({ key: 'allowed', label: 'Allowed', tone: 'pos' })
    // Doing nothing is allowed, and quiet.
    expect(verdictOf({ action: 'none', allowed: true, rule: 'ok' })).toMatchObject({ key: 'allowed', label: 'Allowed', tone: 'neutral' })
    expect(verdictOf({ action: 'buy', allowed: false, rule: 'stop' })).toMatchObject({ key: 'refused', label: 'Refused · stop-loss', tone: 'warn' })
    expect(verdictOf({ action: 'buy', allowed: false, rule: 'daily-loss' })).toMatchObject({ label: 'Refused · daily loss', tone: 'warn' })
    expect(verdictOf({ action: 'start_strategy', allowed: false, rule: 'strategy-list' }).label).toBe('Refused · strategy list')
    expect(verdictOf({ action: 'exit', allowed: false, rule: 'own-book' }).label).toBe('Refused · not its position')
    expect(verdictOf({ action: '', allowed: false, rule: 'no-answer' })).toMatchObject({ key: 'no-answer', label: 'No answer', tone: 'neutral' })
    expect(verdictOf({ action: '', allowed: false, rule: 'unreadable' })).toMatchObject({ key: 'no-answer', label: 'Unreadable' })
  })

  it('names every rule the guard can break, and an unknown one by its own name', () => {
    for (const rule of [
      'hours',
      'daily-loss',
      'size',
      'stop',
      'target',
      'instrument',
      'contract',
      'open-positions',
      'capital',
      'trades-a-day',
      'strategy-list',
      'strategy-runs',
      'own-book',
      'own-runs',
      'kill-switch',
      'action',
    ]) {
      const r = ruleLabel(rule)
      expect(r.short).not.toBe('')
      expect(r.means).not.toBe('')
    }
    expect(ruleLabel('margin-call')).toEqual({ short: 'margin call', means: '' })
    // Refused without a rule: refused all the same, never "allowed".
    expect(verdictOf({ action: 'buy', allowed: false, rule: '' })).toMatchObject({ key: 'refused', label: 'Refused' })
  })

  it('says whether an allowed action was placed', () => {
    const d = (over: Partial<AiTraderDecision>) => ({ action: 'buy', allowed: true, executed: false, mode: 'shadow', error: '', ...over })
    expect(placedText(d({}))).toEqual({ text: 'Not placed: shadow mode', tone: 'neutral' })
    expect(placedText(d({ mode: 'replay' }))?.text).toBe('Not placed: a replay places nothing')
    expect(placedText(d({ mode: 'live', executed: true }))).toEqual({ text: 'Placed in its own account', tone: 'pos' })
    expect(placedText(d({ mode: 'live', error: 'Execution is not switched on in this build: nothing was placed.' }))).toEqual({
      text: 'Not placed: Execution is not switched on in this build: nothing was placed.',
      tone: 'warn',
    })
    expect(placedText(d({ action: 'none' }))).toBeNull()
    expect(placedText(d({ allowed: false }))).toBeNull()
  })
})

describe('the words', () => {
  it('says the action in a few words, with the option when it is known', () => {
    expect(actionText(read())).toBe('Buy NIFTY')
    expect(actionText(read({ option: 'CE' }))).toBe('Buy NIFTY CE')
    const plan = readAiTraderPlan('{"action":"buy","underlying":"NIFTY","option":"PE","strike":"ATM"}')
    expect(actionText({ ...read(), plan })).toBe('Buy NIFTY PE')
    expect(actionText(read({ action: 'none', underlying: '' }))).toBe('Do nothing')
    expect(actionText(read({ action: 'exit', underlying: 'BANKNIFTY' }))).toBe('Exit BANKNIFTY')
    expect(actionText({ ...read({ action: 'exit' }), plan: readAiTraderPlan('{"action":"exit","positionId":12}') })).toBe('Exit position #12')
    expect(actionText(read({ action: 'start_strategy', underlying: 'BANKNIFTY' }))).toBe('Start a strategy on BANKNIFTY')
    expect(actionText({ ...read({ action: 'start_strategy' }), plan: readAiTraderPlan('{"action":"start_strategy","strategy":"ChainFlowBuy"}') })).toBe(
      'Start Chain Flow Buy on NIFTY',
    )
    expect(actionText({ ...read({ action: 'stop_strategy', underlying: '' }), plan: readAiTraderPlan('{"action":"stop_strategy","runId":77}') })).toBe(
      'Stop run #77',
    )
    expect(actionText(read({ action: '', underlying: '', rule: 'no-answer' }))).toBe('No decision')
    expect(actionText(read({ action: '', underlying: '', rule: 'unreadable' }))).toBe('No decision')
    expect(actionText(read({ action: 'sell' }))).toBe('“sell” NIFTY')
  })

  it("writes the model's confidence as a whole percent, and nothing when it gave none", () => {
    expect(confidenceText(0.64)).toBe('64%')
    expect(confidenceText(0.725)).toBe('73%')
    expect(confidenceText(1)).toBe('100%')
    expect(confidenceText(0)).toBe('0%')
    expect(confidenceText(null)).toBe('')
    expect(confidenceText(1.4)).toBe('')
  })

  it('names the mode and what it does', () => {
    expect(modeLabel('shadow')).toEqual({ label: 'Shadow', tone: 'neutral', means: 'decides, places nothing' })
    expect(modeLabel('live', 500000)).toEqual({ label: 'Live', tone: 'live', means: 'places paper orders in its own ₹5 lakh account' })
    expect(modeLabel('live').means).toBe('places paper orders in its own account')
    expect(modeLabel('replay').label).toBe('Replay')
    expect(modeLabel('paper')).toMatchObject({ label: 'Paper', tone: 'neutral' })
    expect(modeLabel(null).label).toBe('Not known')
  })

  it('writes rupees the Indian way', () => {
    expect(lakhText(500000)).toBe('₹5 lakh')
    expect(lakhText(250000)).toBe('₹2.5 lakh')
    expect(lakhText(10000000)).toBe('₹1 crore')
    expect(lakhText(50000)).toBe('₹50,000')
    expect(lakhText(null)).toBe('')
  })

  it('puts the limits in one line, each only when it was sent', () => {
    const rules = readAiTraderStatus(status).rules!
    expect(limitsParts(rules).join(' · ')).toBe(
      'NIFTY, BANKNIFTY, SENSEX options, buying only · 2 lots a trade · ₹50,000 of premium a trade · 3 positions at once · ' +
        'stop at most 40% below entry · daily loss limit ₹10,000 · new positions 09:20–14:45 IST · 10 trades a day · ' +
        'strategies: Ghost Tangent Crossings, Chain Flow Buy (3 runs at most)',
    )
    expect(limitsParts(readAiTraderStatus({ rules: { maxLotsPerTrade: 1, openFrom: '09:20:00' } }).rules!)).toEqual(['1 lot a trade'])
  })

  it("counts the day's looks in words", () => {
    expect(dayCountsText({ decisions: 12, actions: 3, allowed: 2, refused: 1, noAnswer: 0 })).toBe(
      '12 looks · 3 actions · 2 allowed · 1 refused · 0 no answer',
    )
    expect(dayCountsText({ decisions: 1, actions: 1, allowed: 1, refused: 0, noAnswer: 0 })).toBe('1 look · 1 action · 1 allowed · 0 refused · 0 no answer')
  })

  it("puts the plan's numbers in a line, and shows JSON pretty, or as written", () => {
    expect(planFacts(readAiTraderPlan('{"action":"buy","strike":"ATM+1","lots":2,"stopLoss":82.5,"target":140}'))).toBe(
      'strike ATM+1 · 2 lots · stop ₹82.50 · target ₹140.00',
    )
    expect(planFacts(readAiTraderPlan('{"action":"none"}'))).toBe('')
    expect(planFacts(null)).toBe('')
    expect(jsonBlock('{"a":1}')).toBe('{\n  "a": 1\n}')
    expect(jsonBlock('{}')).toBe('')
    expect(jsonBlock('  ')).toBe('')
    expect(jsonBlock('not json')).toBe('not json')
  })
})

describe('the shadow book', () => {
  it('reads a book as the controller sends it, oldest first', () => {
    const b = readAiTraderShadowBook({
      day: '2026-10-01',
      replay: null,
      positions: 2,
      open: 1,
      net: -1240.5,
      charges: 98.2,
      items: [position(), position({ id: 13, open: true, exitUtc: null, exitIst: null, exitPrice: null, exitReason: '', net: 1491.34 })],
    })
    expect(b).toMatchObject({ day: '2026-10-01', replay: null, positions: 2, open: 1, net: -1240.5, charges: 98.2 })
    expect(b.items.map((p) => p.id)).toEqual([12, 13])
    expect(b.items[0]).toEqual({
      id: 12,
      decisionId: 104,
      mode: 'shadow',
      replaySessionId: null,
      day: '2026-10-01',
      symbol: 'NSE:NIFTY25O0725300CE',
      underlying: 'NIFTY',
      optionType: 'CE',
      strike: 25300,
      expiry: '2026-10-07',
      lots: 1,
      lotSize: 75,
      entryUtc: '2026-10-01T04:30:04Z',
      entryIst: '10:00',
      entryPrice: 112.5,
      stopLoss: 78,
      target: 160,
      markPrice: 96.4,
      markUtc: '2026-10-01T06:12:00Z',
      open: false,
      exitUtc: '2026-10-01T06:12:00Z',
      exitIst: '11:42',
      exitPrice: 76.9,
      exitReason: 'stop',
      charges: 61.84,
      net: -2731.84,
    })
    expect(b.items[1]).toMatchObject({ open: true, exitIst: '', exitPrice: null, exitReason: '' })
  })

  it('refuses a body that is not a book, rather than drawing an empty one at ₹0', () => {
    for (const bad of ['<html>', null, [position()], { positions: 0, net: 0 }]) {
      expect(() => readAiTraderShadowBook(bad)).toThrow(/shape this page cannot read/)
    }
  })

  it('keeps what a row left out as not known, and drops a row it cannot name', () => {
    const b = readAiTraderShadowBook({ items: [position({ net: null, charges: 'x', markPrice: null, open: 'true', optionType: 'fut', entryIst: null }), { id: 0 }, 'junk'] })
    expect(b.items).toHaveLength(1)
    const p = b.items[0]
    expect(p.net).toBeNull()
    expect(p.charges).toBeNull()
    expect(p.markPrice).toBeNull()
    expect(p.open).toBe(false)
    expect(p.optionType).toBe('')
    // No IST entry sent: worked out from the UTC one.
    expect(p.entryIst).toBe('10:00')
    // The counts fall back to the rows; the money stays not known.
    expect(b).toMatchObject({ positions: 1, open: 0, net: null, charges: null, day: null, replay: null })
    expect(readAiTraderShadowPosition(position({ replaySessionId: 4, mode: 'REPLAY' }))).toMatchObject({ replaySessionId: 4, mode: 'replay' })
  })

  it('asks for a replay, else a day', () => {
    expect(aiTraderPositionsQuery({ day: '2026-10-01' })).toBe('day=2026-10-01')
    expect(aiTraderPositionsQuery({ day: '2026-10-01', replay: 4 })).toBe('replay=4')
    expect(aiTraderPositionsQuery({})).toBe('')
  })

  it('names the contract, the lots and how a position ended', () => {
    const p = readAiTraderShadowPosition(position())!
    expect(contractText(p)).toBe('NIFTY 25300 CE')
    expect(contractText({ ...p, strike: 25312.5 })).toBe('NIFTY 25312.5 CE')
    expect(contractText({ ...p, strike: null })).toBe('NSE:NIFTY25O0725300CE')
    expect(contractText({ ...p, strike: null, symbol: '' })).toBe('—')
    expect(lotsText(1, 75)).toBe('1 lot × 75')
    expect(lotsText(2, null)).toBe('2 lots')
    expect(lotsText(null, 75)).toBe('')
    expect(shadowEnding({ open: true, exitReason: '' })).toMatchObject({ label: 'Open', tone: 'live' })
    expect(shadowEnding({ open: false, exitReason: 'stop' })).toMatchObject({ label: 'Stop', tone: 'neg' })
    expect(shadowEnding({ open: false, exitReason: 'target' })).toMatchObject({ label: 'Target', tone: 'pos' })
    expect(shadowEnding({ open: false, exitReason: 'exit' }).label).toBe('Its exit')
    expect(shadowEnding({ open: false, exitReason: 'close' }).label).toBe('Close')
    expect(shadowEnding({ open: false, exitReason: 'replay-ended' }).label).toBe('Replay ended')
    expect(shadowEnding({ open: false, exitReason: 'margin-call' })).toMatchObject({ label: 'Margin call', tone: 'neutral' })
    expect(shadowEnding({ open: false, exitReason: '' }).label).toBe('Closed')
  })

  it('ends a row on its exit price, or on its mark while open', () => {
    expect(shadowLastPrice({ open: false, exitPrice: 76.9, markPrice: 96.4 })).toEqual({ price: 76.9, kind: 'exit' })
    expect(shadowLastPrice({ open: true, exitPrice: null, markPrice: 96.4 })).toEqual({ price: 96.4, kind: 'mark' })
    expect(shadowLastPrice({ open: true, exitPrice: null, markPrice: null })).toEqual({ price: null, kind: 'mark' })
    expect(shadowCountsText({ positions: 1, open: 0 })).toBe('1 position · 0 open')
    expect(shadowCountsText({ positions: 3, open: 1 })).toBe('3 positions · 1 open')
  })

  it("says the shadow position a decision's result names", () => {
    expect(shadowResultText('{"contract":{"symbol":"NSE:NIFTY25O0725300CE"},"shadowPositionId":12}')).toEqual({
      positionId: 12,
      text: 'Shadow position #12',
    })
    expect(shadowResultText('{"shadowPositionId":12,"exitPrice":96.4,"charges":61.84,"netPnl":1143.16}')?.text).toBe(
      'Shadow position #12, closed at ₹96.40, net +₹1,143 after charges',
    )
    expect(shadowResultText('{"shadowPositionId":12,"exitPrice":76.9,"netPnl":-2731.84}')?.text).toBe(
      'Shadow position #12, closed at ₹76.90, net −₹2,732 after charges',
    )
    expect(shadowResultText('{"shadowPositionId":12,"note":"It was no longer open."}')?.text).toBe('Shadow position #12: It was no longer open.')
    expect(shadowResultText('{"note":"Strategy runs are not simulated in shadow: recorded only."}')).toBeNull()
    expect(shadowResultText('{}')).toBeNull()
    expect(shadowResultText('not json')).toBeNull()
    expect(shadowResultText(null)).toBeNull()
  })

  it('colours a net as it is printed: a ₹0 book is not green', () => {
    expect(netTone(1491.34)).toBe('pos')
    expect(netTone(-0.4)).toBe('')
    expect(netTone(-2731.84)).toBe('neg')
    expect(netTone(null)).toBe('')
  })
})

describe('the scoreboard', () => {
  /** GET /api/AiTrader/scoreboard as AiTraderController.Scoreboard sends it. */
  const board = {
    rule: 'nifty-trend-1100',
    ruleText: "At 11:00 IST, NIFTY's 5-minute trend picks the side: …",
    totals: { days: 2, aiNet: -1840.5, baselineNet: 2210.0, aiBeatBaseline: 1, aiPositiveDays: 1, baselinePositiveDays: 1, trades: 5, charges: 312.4 },
    rowsTotal: 2,
    rows: [
      {
        kind: 'replay',
        replaySessionId: 23,
        day: '2026-09-30',
        firstIst: '09:20',
        lastIst: '15:00',
        full: true,
        looks: 34,
        actions: 4,
        noAnswer: 1,
        positions: 3,
        open: 0,
        net: 1356.2,
        charges: 186.1,
        baseline: {
          rule: 'nifty-trend-1100',
          optionType: 'CE',
          symbol: 'NSE:NIFTY25O0725300CE',
          entryIst: '11:00',
          entryPrice: 104.2,
          exitIst: '13:41',
          exitPrice: 156.4,
          exitReason: 'target',
          charges: 61.9,
          net: 3853.1,
          note: 'Up trend: bought NSE:NIFTY25O0725300CE at 104.2, target at 156.4.',
        },
        vsBaseline: -2496.9,
      },
      { kind: 'shadow', replaySessionId: null, day: '2026-10-01', firstIst: '10:40', lastIst: '13:10', full: false, looks: 15, actions: 2, noAnswer: 0, positions: 1, open: 1, net: 240, charges: 40, baseline: null, vsBaseline: null },
    ],
  }

  it('reads the scoreboard as the controller sends it', () => {
    const b = readAiTraderScoreboard(board)
    expect(b.rule).toBe('nifty-trend-1100')
    expect(b.totals).toEqual(board.totals)
    expect(b.rows).toHaveLength(2)
    expect(b.rowsTotal).toBe(2)
    expect(b.rows[0]).toEqual({ ...board.rows[0], baseline: { ...board.rows[0].baseline } })
    expect(b.rows[1]).toMatchObject({ kind: 'shadow', full: false, baseline: null, vsBaseline: null })
  })

  it('refuses a body that is not a scoreboard, and keeps unknown totals unknown', () => {
    for (const bad of ['<html>', null, [], { totals: board.totals }]) {
      expect(() => readAiTraderScoreboard(bad)).toThrow(/shape this page cannot read/)
    }
    expect(readAiTraderScoreboard({ rows: [] }).totals).toBeNull()
    // How many rows there are is not known when it is not sent: never the rows listed passed off as all of them.
    expect(readAiTraderScoreboard({ rows: [] }).rowsTotal).toBeNull()
    expect(readAiTraderScoreboard({ rows: [], rowsTotal: 'many' }).rowsTotal).toBeNull()
    expect(readAiTraderScoreboard({ rows: [], rowsTotal: -3 }).rowsTotal).toBeNull()
    expect(readAiTraderScoreboard({ rows: [], totals: { days: 'two', aiNet: '5' } }).totals).toMatchObject({ days: 0, aiNet: null })
  })

  it('reads a row defensively: full only in as many words, the rule not traded as no side', () => {
    expect(readAiTraderScoreRow({ day: 'today' })).toBeNull()
    const r = readAiTraderScoreRow({
      day: '2026-09-29',
      replaySessionId: 7,
      full: 'true',
      net: 500,
      baseline: { optionType: '', net: 0, note: 'Flat: EMA 20 and EMA 50 crossed.' },
    })!
    expect(r.kind).toBe('replay')
    expect(r.full).toBe(false)
    expect(r.baseline).toMatchObject({ optionType: '', net: 0, entryIst: '', exitReason: '' })
    // The difference is worked out when both nets are known.
    expect(r.vsBaseline).toBe(500)
    expect(readAiTraderScoreRow({ day: '2026-09-29', net: 500, baseline: null })!.vsBaseline).toBeNull()
  })

  it('says each row in a few words', () => {
    const [replay, live] = readAiTraderScoreboard(board).rows
    expect(scoreKindText(replay)).toBe('Replay #23')
    expect(scoreKindText(live)).toBe('Live shadow')
    expect(looksSpanText(replay)).toBe('09:20–15:00')
    expect(looksSpanText({ firstIst: '', lastIst: '' })).toBe('')
    expect(baselineText(replay.baseline)).toBe('Call · target')
    expect(baselineText({ ...replay.baseline!, optionType: 'PE', exitReason: 'stop' })).toBe('Put · stop')
    expect(baselineText({ ...replay.baseline!, optionType: '' })).toBe('No trade')
    expect(baselineText(null)).toBe('Scoring…')
    // The totals' "days" are rows: each replay and each live shadow day, a date replayed twice counted twice.
    expect(beatText({ aiBeatBaseline: 1, days: 2 })).toBe('1 of 2 full replays and shadow days')
    expect(beatText({ aiBeatBaseline: 0, days: 1 })).toBe('0 of 1 full replay or shadow day')
    expect(scoreRowAnchor(replay)).toBe('atr-score-r-23')
    expect(scoreRowAnchor(live)).toBe('atr-score-d-2026-10-01')
  })

  it('never lets a handful of replays read as an edge', () => {
    expect(sampleNote(8)).toBe('8 full replays and shadow days are a small sample; a difference here is not proof of an edge.')
    expect(sampleNote(1)).toBe('1 full replay or shadow day is a small sample; a difference here is not proof of an edge.')
    expect(sampleNote(0)).toMatch(/No full, scored replay or shadow day yet/)
    expect(sampleNote(120)).toBe('A difference over 120 full replays and shadow days is still not proof of an edge until it is tested against chance.')
  })

  it('says when the list holds only the newest rows, and is silent when it holds them all or the count is not known', () => {
    expect(listedText(60, 75)).toBe('60 of 75 rows listed, newest first; the totals count all of them.')
    expect(listedText(2, 2)).toBe('')
    expect(listedText(2, null)).toBe('')
  })
})

describe('its lessons', () => {
  /** GET /api/AiTrader/lessons exactly as AiTraderController sends it. */
  const body = () => ({
    enabled: true,
    maxActive: 8,
    testPoints: 16,
    minGain: 500,
    testing: { lessonId: 46, done: 5, of: 16 },
    counts: { active: 1, proposed: 1, rejected: 1, retired: 0 },
    lessons: [
      {
        id: 47, text: 'Buy calls on any green open.', status: 'rejected', sourceDay: '2026-10-01', subject: 'replay:13', replaySessionId: 13,
        decisionIds: [], createdUtc: '2026-10-02T13:00:00Z', decidedBy: 'check: −₹1,200 with it over 16 looks, under the +₹500 needed',
        decidedUtc: '2026-10-02T14:00:00Z', activatedUtc: null, retiredUtc: null, uses: 0, reflectionReportId: 900, checkReportId: 902,
        evidence: { points: 16, controlNet: 300, treatmentNet: -900, gain: -1200, helped: 1, hurt: 4, controlBad: 0, treatmentBad: 0, passed: false, used: false, verdict: 'check: …' },
      },
      {
        id: 46, text: 'Exit a position whose premium stalls for half an hour.', status: 'proposed', sourceDay: '2026-09-30', subject: 'replay:12',
        replaySessionId: 12, decisionIds: [4512], createdUtc: '2026-10-02T12:00:00Z', decidedBy: '', decidedUtc: null, activatedUtc: null,
        retiredUtc: null, uses: 0, reflectionReportId: 899, checkReportId: null, evidence: null,
      },
      {
        id: 45, text: 'Wait for the first half hour’s range to break before buying.', status: 'active', sourceDay: '2026-10-05',
        subject: 'day:2026-10-05', replaySessionId: null, decisionIds: [4512, 4518, 'x'], createdUtc: '2026-10-05T12:00:00Z',
        decidedBy: 'check: +₹3,268 over 16 looks; helped 3, hurt 1', decidedUtc: '2026-10-05T13:00:00Z', activatedUtc: '2026-10-05T13:00:00Z',
        retiredUtc: null, uses: 12, reflectionReportId: 898, checkReportId: 901,
        evidence: { points: 16, controlNet: 0, treatmentNet: 3268.4, gain: 3268.4, helped: 3, hurt: 1, controlBad: 0, treatmentBad: 1, passed: true, used: true, verdict: 'check: +₹3,268' },
      },
      { id: 0, text: 'no id' },
      { id: 48, text: '' },
    ],
  })

  it('reads the list as the controller sends it, dropping a row it cannot name', () => {
    const l = readAiTraderLessons(body())
    expect(l).toMatchObject({ enabled: true, maxActive: 8, testPoints: 16, minGain: 500, testing: { lessonId: 46, done: 5, of: 16 } })
    expect(l.lessons.map((x) => x.id)).toEqual([47, 46, 45])
    expect(l.lessons[2]).toMatchObject({ sourceDay: '2026-10-05', subject: 'day:2026-10-05', replaySessionId: null, decisionIds: [4512, 4518], uses: 12 })
    expect(l.lessons[1].evidence).toBeNull()
    expect(l.lessons[0].evidence).toMatchObject({ gain: -1200, helped: 1, hurt: 4, passed: false })
  })

  it('refuses a body that is not a lesson list, and keeps what was left out as not known', () => {
    expect(() => readAiTraderLessons({ counts: {} })).toThrow(/cannot read/)
    expect(() => readAiTraderLessons(null)).toThrow(/cannot read/)
    expect(readAiTraderLessons({ lessons: [] })).toEqual({
      enabled: null,
      maxActive: null,
      testPoints: null,
      minGain: null,
      testing: null,
      counts: { active: 0, proposed: 0, rejected: 0, retired: 0 },
      lessons: [],
    })
  })

  it('says where a lesson came from and what its test found', () => {
    const [rejected, waiting, used] = readAiTraderLessons(body()).lessons
    expect(lessonOriginText(waiting)).toBe('Replay #12 of 30 Sep')
    expect(lessonOriginText(used)).toBe('Live day 5 Oct')
    expect(lessonOriginText({ replaySessionId: null, sourceDay: null })).toBe('Its day was not recorded')
    expect(lessonEvidenceText(used.evidence!)).toBe('With it +₹3,268 against ₹0 without, over 16 looks: helped 3, hurt 1; unreadable or unanswered 0 without, 1 with')
    expect(lessonEvidenceText(rejected.evidence!)).toBe('With it −₹900 against +₹300 without, over 16 looks: helped 1, hurt 4')
    expect(lessonGainText(used.evidence)).toBe('+₹3,268')
    expect(lessonGainText(null)).toBe('')
  })

  it('names its state, with how far the one under test is, and lists the used ones first', () => {
    const l = readAiTraderLessons(body())
    const [rejected, waiting, used] = l.lessons
    expect(lessonStatus(used, l.testing).label).toBe('Used')
    expect(lessonStatus(waiting, l.testing).label).toBe('Testing 5/16')
    expect(lessonStatus(waiting, null).label).toBe('Waiting')
    expect(lessonStatus(rejected, l.testing)).toMatchObject({ label: 'Dropped', means: 'Its test did not pass.' })
    expect(lessonOrder(l.lessons).map((x) => x.id)).toEqual([45, 46, 47])
  })

  it('reads the memories a decision was given', () => {
    const d = readAiTraderDetail({ decision: summary(), brief: 'x', planJson: '{}', resultJson: '{}', briefHash: 'A', memoryIds: [45, 'x', 0, 46] })
    expect(d.memoryIds).toEqual([45, 46])
    expect(readAiTraderDetail({ decision: summary(), brief: 'x' }).memoryIds).toEqual([])
  })
})
