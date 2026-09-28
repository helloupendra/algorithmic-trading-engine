import { describe, expect, it } from 'vitest'

import {
  EMPTY_RULE_FORM,
  NO_FILTERS,
  candleSpan,
  crossTone,
  filterAlerts,
  filterChoices,
  formToRequest,
  indicatorNumbers,
  indicatorTelegramNote,
  parseList,
  ruleStateTone,
  ruleSummary,
  ruleToForm,
  scannerHealth,
  sortWatches,
  telegramChannelNote,
  toggle,
} from './patterns'
import type { IndicatorWatch, PatternAlert, PatternCatalog, PatternRule, PatternScannerStatus } from './patterns'

const status: PatternScannerStatus = {
  enabled: true,
  intervalSeconds: 20,
  startedUtc: '2026-09-15T03:40:00Z',
  lastScanUtc: '2026-09-15T05:00:00Z',
  lastScanMilliseconds: 12,
  lastErrorUtc: null,
  lastError: null,
  ruleCount: 3,
  watchCount: 20,
  closedCandlesRead: 200,
  symbols: [],
  unresolved: [],
  telegramConfigured: true,
  telegramMaxMessages: 4,
  telegramWindowMinutes: 5,
  lastTelegramUtc: null,
  telegramMessagesSent: 0,
  telegramMessagesSuppressed: 0,
  telegramMessagesFailed: 0,
  lastTelegramProblem: null,
  alertsToday: 0,
  deliveredToday: 0,
  todayByPattern: [],
  todayByTimeframe: [],
}

const at = (iso: string) => new Date(iso).getTime()

describe('scannerHealth', () => {
  it('is running only while scans keep arriving', () => {
    expect(scannerHealth(status, at('2026-09-15T05:00:30Z')).label).toBe('Running')
    // Three intervals is sixty seconds; a minute and a second is a stopped loop.
    expect(scannerHealth(status, at('2026-09-15T05:01:01Z'))).toMatchObject({ label: 'Stalled', tone: 'neg' })
  })

  it('reports a failure newer than the last good scan', () => {
    const failing = { ...status, lastErrorUtc: '2026-09-15T05:00:20Z', lastError: 'database down' }
    expect(scannerHealth(failing, at('2026-09-15T05:00:25Z'))).toMatchObject({ label: 'Failing', detail: 'database down' })
    // An old error followed by good scans is history, not a state.
    const recovered = { ...status, lastErrorUtc: '2026-09-15T04:00:00Z', lastError: 'blip' }
    expect(scannerHealth(recovered, at('2026-09-15T05:00:10Z')).label).toBe('Running')
  })

  it('never says running before the first scan, and says off when disabled', () => {
    expect(scannerHealth({ ...status, lastScanUtc: null }, at('2026-09-15T05:00:00Z')).label).toBe('Starting')
    expect(scannerHealth({ ...status, enabled: false }, at('2026-09-15T05:00:00Z')).label).toBe('Off')
  })
})

const alert = (over: Partial<PatternAlert>): PatternAlert => ({
  id: 1,
  occurredUtc: '2026-09-15T05:15:00Z',
  symbol: 'NSE:NIFTYBANK-INDEX',
  displayName: 'BANKNIFTY',
  timeframe: 15,
  pattern: 'doji',
  patternName: 'doji',
  direction: 'neutral',
  barStartUtc: '2026-09-15T05:00:00Z',
  barEndUtc: '2026-09-15T05:15:00Z',
  open: 57210,
  high: 57260,
  low: 57164,
  close: 57214,
  minutesInBar: 15,
  minutesExpected: 15,
  title: '',
  message: '',
  deliveredToTelegram: true,
  notify: true,
  notifySkippedReason: null,
  rules: [],
  ...over,
})

describe('alert filters', () => {
  const alerts = [
    alert({ id: 1 }),
    alert({ id: 2, symbol: 'NSE:SBIN-EQ', displayName: 'SBIN', pattern: 'hammer', patternName: 'hammer', direction: 'bullish' }),
    alert({ id: 3, timeframe: 5, pattern: 'bearish_engulfing', patternName: 'bearish engulfing', direction: 'bearish' }),
  ]

  it('matches every filter at once and nothing when empty', () => {
    expect(filterAlerts(alerts, NO_FILTERS).map((a) => a.id)).toEqual([1, 2, 3])
    expect(filterAlerts(alerts, { ...NO_FILTERS, symbol: 'NSE:NIFTYBANK-INDEX', timeframe: '5' }).map((a) => a.id)).toEqual([3])
    expect(filterAlerts(alerts, { ...NO_FILTERS, direction: 'bullish' }).map((a) => a.id)).toEqual([2])
  })

  it('offers only values that occur, labelled for people', () => {
    const choices = filterChoices(alerts)
    expect(choices.symbols).toEqual([
      { value: 'NSE:NIFTYBANK-INDEX', label: 'BANKNIFTY' },
      { value: 'NSE:SBIN-EQ', label: 'SBIN' },
    ])
    expect(choices.timeframes).toEqual([5, 15])
    expect(choices.patterns.map((p) => p.value)).toEqual(['bearish_engulfing', 'doji', 'hammer'])
  })
})

describe('rule form', () => {
  const rule: PatternRule = {
    id: 7,
    name: 'Crude',
    symbols: ['NSE:SBIN-EQ'],
    groups: ['indices', 'future:CRUDEOIL'],
    timeframes: [15, 5],
    patterns: ['doji'],
    isEnabled: true,
    notify: false,
    updatedBy: 'admin',
    updatedUtc: '2026-09-14T16:00:00Z',
    resolvedSymbols: [],
  }

  it('round-trips a rule, with futures split out of the groups', () => {
    const form = ruleToForm(rule)
    expect(form.groups).toEqual(['indices'])
    expect(form.futures).toBe('CRUDEOIL')
    expect(formToRequest(form)).toEqual({
      name: 'Crude',
      symbols: ['NSE:SBIN-EQ'],
      groups: ['indices', 'future:CRUDEOIL'],
      timeframes: [5, 15],
      patterns: ['doji'],
      isEnabled: true,
      notify: false,
    })
  })

  it('reads typed lists however they were separated', () => {
    expect(parseList(' nse:sbin-eq,NSE:INFY-EQ\nnse:sbin-eq  MCX:CRUDEOIL26OCTFUT ')).toEqual([
      'NSE:SBIN-EQ',
      'NSE:INFY-EQ',
      'MCX:CRUDEOIL26OCTFUT',
    ])
    expect(formToRequest({ ...EMPTY_RULE_FORM, name: ' x ', futures: 'crudeoil, gold' }).groups).toEqual([
      'future:CRUDEOIL',
      'future:GOLD',
    ])
  })

  it('summarises what a rule watches in one line', () => {
    const catalog: PatternCatalog = {
      patterns: [{ key: 'doji', name: 'doji', direction: 'neutral', bars: 1, suggests: '', definition: '', portedFrom: null }],
      groups: [{ key: 'indices', label: 'Indices', description: '' }],
      timeframes: [3, 5, 15, 30, 60],
    }
    expect(ruleSummary(rule, catalog)).toBe('Indices, CRUDEOIL nearest future, 1 symbol · 15m, 5m · every pattern')
    expect(ruleSummary({ ...rule, patterns: [] }, catalog)).toContain('· 0 patterns')
  })

  it('toggles in catalog order', () => {
    expect(toggle([15], 5, [3, 5, 15, 30, 60])).toEqual([5, 15])
    expect(toggle([5, 15], 5, [3, 5, 15, 30, 60])).toEqual([15])
  })
})

describe('candleSpan', () => {
  it('reads in IST', () => {
    expect(candleSpan('2026-09-15T05:00:00Z', '2026-09-15T05:15:00Z')).toBe('10:30–10:45')
  })
})

describe('indicator alerts', () => {
  it('uses the same health rules for the indicator scanner, with its own switch named when off', () => {
    const indicators = { enabled: false, intervalSeconds: 20, lastScanUtc: null, lastErrorUtc: null, lastError: null }
    expect(scannerHealth(indicators, at('2026-09-15T05:00:00Z'), 'IndicatorAlerts:Enabled is false.')).toEqual({
      label: 'Off',
      tone: 'warn',
      detail: 'IndicatorAlerts:Enabled is false.',
    })
  })

  it('shows the numbers behind each kind of alert, briefly', () => {
    expect(indicatorNumbers({ ruleName: 'rsi-above', values: { rsiBefore: 68.44, rsi: 71.2 } })).toBe('RSI 68.4 → 71.2')
    expect(indicatorNumbers({ ruleName: 'ema-cross', values: { emaFast: 25061.3, emaSlow: 25058.9 } })).toBe(
      'EMA 25,061.30 / 25,058.90',
    )
    expect(indicatorNumbers({ ruleName: 'supertrend-flip', values: { through: 25080, line: 25161.33 } })).toBe(
      'band 25,080.00 · line 25,161.33',
    )
    expect(indicatorNumbers({ ruleName: 'vwap-cross', values: { vwap: 100.01 } })).toBe('VWAP 100.01')
    // A value the API sent without it: a dash, never "undefined".
    expect(indicatorNumbers({ ruleName: 'rsi-below', values: { rsi: 28.7 } })).toBe('RSI — → 28.7')
  })

  it('colours crosses by direction and rule states by readiness', () => {
    expect(crossTone('up')).toBe('pos')
    expect(crossTone('down')).toBe('neg')
    expect(ruleStateTone('ready')).toBe('neutral')
    expect(ruleStateTone('warming up')).toBe('warn')
    expect(ruleStateTone('skipped')).toBe('neutral')
  })

  it('lists watches with a problem first, then those still warming up', () => {
    const watch = (over: Partial<IndicatorWatch>): IndicatorWatch => ({
      symbol: 'NSE:NIFTY50-INDEX',
      displayName: 'NIFTY',
      timeframe: 15,
      exchange: 'NSE',
      inSession: true,
      historyCandles: 250,
      todayCandles: 4,
      lastBarUtc: null,
      rules: [{ rule: 'rsi-above(14,70)', label: 'RSI(14) crosses above 70', state: 'ready', detail: null }],
      problem: null,
      ...over,
    })
    const sorted = sortWatches([
      watch({ displayName: 'NIFTY', timeframe: 5 }),
      watch({ displayName: 'SENSEX', rules: [{ rule: 'x', label: 'x', state: 'warming up', detail: '40 of 84 candles' }] }),
      watch({ displayName: 'BANKNIFTY', problem: 'no live bars — not streamed by any feed' }),
      watch({ displayName: 'NIFTY', timeframe: 15 }),
    ])
    expect(sorted.map((w) => `${w.displayName} ${w.timeframe}`)).toEqual(['BANKNIFTY 15', 'SENSEX 15', 'NIFTY 5', 'NIFTY 15'])
  })
})

describe('telegramChannelNote', () => {
  it('names the live trades channel, where the owner moved these alerts on 28 Sep', () => {
    // The default, with or without a system chat on the server.
    for (const telegramSystemChatConfigured of [true, false]) {
      expect(telegramChannelNote({ telegramChannel: 'trades', telegramSystemChatConfigured })).toEqual({
        text: 'Live trades channel',
        title: 'The channel the live trade alerts go to (Telegram:ChatId).',
        problem: null,
      })
    }
  })

  it('names the Desk System channel only when it is set and the server has a system chat', () => {
    expect(telegramChannelNote({ telegramChannel: 'system', telegramSystemChatConfigured: true })?.text).toBe('Desk System channel')
    // Set to system, but no system chat: the server sends these among the trades, and the page says so.
    const fallback = telegramChannelNote({ telegramChannel: 'system', telegramSystemChatConfigured: false })
    expect(fallback?.text).toBe('Live trades channel: no Desk System chat is set on this server')
    expect(fallback?.title).toMatch(/Telegram:SystemChatId is not set/)
  })

  it('passes on why a setting was not read, rather than hiding it behind the fallback', () => {
    const problem = 'PatternAlerts:TelegramChannel is "desk", which is neither trades nor system; sent to the trades channel.'
    const note = telegramChannelNote({ telegramChannel: 'trades', telegramChannelProblem: problem, telegramSystemChatConfigured: true })
    expect(note?.text).toBe('Live trades channel')
    expect(note?.problem).toBe(problem)
  })

  it('reads an API from before the move as the system chat it then used, and names nothing when nothing is reported', () => {
    expect(telegramChannelNote({ telegramSystemChatConfigured: true })?.text).toBe('Desk System channel')
    expect(telegramChannelNote({ telegramSystemChatConfigured: false })?.text).toBe('Live trades channel: no Desk System chat is set on this server')
    expect(telegramChannelNote({})).toBeNull()
  })
})

describe('indicatorTelegramNote', () => {
  it('says whether indicator alerts go to Telegram at all', () => {
    expect(indicatorTelegramNote({ telegram: false, telegramConfigured: true, telegramChannel: 'trades', telegramSystemChatConfigured: true })).toEqual({
      text: 'off in the file',
      title: null,
      problem: null,
    })
    expect(indicatorTelegramNote({ telegram: true, telegramConfigured: false, telegramChannel: 'trades', telegramSystemChatConfigured: true }).text).toBe(
      'on, but no bot is configured on this server',
    )
  })

  it('names the channel the server reports', () => {
    expect(indicatorTelegramNote({ telegram: true, telegramConfigured: true, telegramChannel: 'trades', telegramSystemChatConfigured: true }).text).toBe(
      'on (Live trades channel)',
    )
    expect(indicatorTelegramNote({ telegram: true, telegramConfigured: true, telegramChannel: 'system', telegramSystemChatConfigured: true }).text).toBe(
      'on (Desk System channel)',
    )
    const fallback = indicatorTelegramNote({ telegram: true, telegramConfigured: true, telegramChannel: 'system', telegramSystemChatConfigured: false })
    expect(fallback.text).toBe('on (Live trades channel: no Desk System chat is set on this server)')
    expect(fallback.title).toMatch(/Telegram:SystemChatId is not set/)
    const bad = indicatorTelegramNote({
      telegram: true,
      telegramConfigured: true,
      telegramChannel: 'trades',
      telegramChannelProblem: 'IndicatorAlerts:TelegramChannel is "desk", which is neither trades nor system; sent to the trades channel.',
    })
    expect(bad.text).toBe('on (Live trades channel)')
    expect(bad.problem).toMatch(/^IndicatorAlerts:TelegramChannel is "desk"/)
  })

  it('names no chat when an older API does not report which', () => {
    const on = indicatorTelegramNote({ telegram: true, telegramConfigured: true })
    expect(on.text).toBe('on')
    expect(on.title).toMatch(/trades chat when no system chat is set/)
  })
})
