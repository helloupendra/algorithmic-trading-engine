import { describe, expect, it } from 'vitest'

import {
  EMPTY_RULE_FORM,
  NO_FILTERS,
  candleSpan,
  filterAlerts,
  filterChoices,
  formToRequest,
  parseList,
  ruleSummary,
  ruleToForm,
  scannerHealth,
  toggle,
} from './patterns'
import type { PatternAlert, PatternCatalog, PatternRule, PatternScannerStatus } from './patterns'

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
