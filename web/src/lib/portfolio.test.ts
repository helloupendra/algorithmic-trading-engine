import { describe, expect, it } from 'vitest'

import {
  DISCLAIMER,
  UNKNOWN,
  adviceWords,
  betaMeaning,
  compactRupees,
  connectionView,
  filterHoldings,
  formatDay,
  formatSignedPct,
  formatWeight,
  hhiFraction,
  hhiMeaning,
  isSecretField,
  latestSyncMs,
  nothingSynced,
  num,
  portfolioKey,
  readConnectStart,
  readConnections,
  readHistory,
  readReport,
  readSyncResult,
  redirectResult,
  retryAfterSeconds,
  safeRedirectUrl,
  sentence,
  shownSummary,
  sortHoldings,
  syncWaitSeconds,
} from './portfolio'
import type { Holding, PortfolioConnection } from './portfolio'
import { buildReport } from './portfolio.fixture'

/** A report body as the contract writes it, with every section. */
function body(over: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    asOfUtc: '2026-10-16T10:01:00Z',
    sources: [
      { broker: 'fyers', takenUtc: '2026-10-16T10:01:00Z', ok: true, error: null },
      { broker: 'angel', takenUtc: '2026-10-16T10:01:00Z', ok: false, error: 'Token rejected' },
    ],
    totals: { invested: 100000, current: 123456.7, pnl: 23456.7, pnlPct: 23.46, dayChange: -812.5, dayChangePct: -0.65, holdingsCount: 2 },
    holdings: [
      {
        symbol: 'RELIANCE',
        isin: 'INE002A01018',
        name: 'Reliance Industries',
        brokers: ['fyers', 'dhan'],
        qty: 55,
        avgPrice: 1500,
        ltp: 1800,
        invested: 82500,
        value: 99000,
        pnl: 16500,
        pnlPct: 20,
        dayChange: -300,
        weight: 0.8019,
        sector: 'Energy',
        marketCapBand: 'Large cap',
      },
      { symbol: 'NEWGEN', name: 'Newgen', brokers: ['angel'], qty: 100, avgPrice: 175, ltp: 244.57, value: 24456.7, weight: 0.1981 },
    ],
    allocation: {
      bySector: [
        { name: 'Energy', value: 99000, weight: 0.8019 },
        { name: '', value: 24456.7, weight: 0.1981 },
      ],
      byBroker: [{ name: 'FYERS', value: 99000, weight: 0.8019 }],
      byMarketCap: [],
    },
    concentration: { top1Weight: 0.8019, top5Weight: 1, hhi: 0.682, largest: ['RELIANCE', 'NEWGEN'] },
    risk: { lookbackDays: 250, volatilityAnnualPct: 15.8, betaVsNifty: 0.94, maxDrawdownPct: -12.6, coveredWeight: 0.8, note: 'Daily closes.' },
    positions: [{ broker: 'fyers', symbol: 'NIFTY 28 OCT 25000 CE', product: 'NRML', qty: 75, avgPrice: 112.5, ltp: 96.4, pnl: -1207.5, isDerivative: true }],
    derivatives: { openPositions: 1, grossExposure: 7230, note: 'qty × ltp' },
    funds: [{ broker: 'fyers', available: 48210.4, used: 7230, total: 55440.4 }],
    summary: { text: 'Your portfolio is worth ₹1,23,457.', model: 'm', checked: true, generatedUtc: '2026-10-16T10:01:20Z' },
    dataNotes: ['Sectors from the NSE list.'],
    disclaimer: 'Information about your own holdings, not investment advice.',
    ...over,
  }
}

const connection = (over: Partial<PortfolioConnection> = {}): PortfolioConnection => ({
  broker: 'fyers',
  name: 'FYERS',
  status: 'connected',
  accountLabel: 'XU•••21',
  connectedUtc: '2026-10-16T03:30:00Z',
  expiresUtc: '2026-10-16T18:29:59Z',
  lastSyncUtc: '2026-10-16T10:01:00Z',
  lastError: null,
  connectMode: 'redirect',
  tokenFields: [],
  ...over,
})

describe('readConnections', () => {
  it('refuses an answer with no brokers list rather than reading it as none linked', () => {
    expect(() => readConnections(null)).toThrow(/no brokers list/)
    expect(() => readConnections({ brokers: 'fyers' })).toThrow(/no brokers list/)
    expect(() => readConnections([])).toThrow(/no brokers list/)
    expect(readConnections({ brokers: [] })).toEqual([])
  })

  it('reads a full row as it comes', () => {
    const [c] = readConnections({
      brokers: [
        {
          broker: 'dhan',
          name: 'Dhan',
          status: 'connected',
          accountLabel: '••••8812',
          connectedUtc: '2026-10-10T04:00:00Z',
          expiresUtc: '2026-11-09T04:00:00Z',
          lastSyncUtc: '2026-10-16T10:01:00Z',
          lastError: null,
          connectMode: 'token',
          tokenFields: [{ key: 'accessToken', label: 'Access token', help: 'From web.dhan.co' }],
        },
      ],
    })
    expect(c).toEqual({
      broker: 'dhan',
      name: 'Dhan',
      status: 'connected',
      accountLabel: '••••8812',
      connectedUtc: '2026-10-10T04:00:00Z',
      expiresUtc: '2026-11-09T04:00:00Z',
      lastSyncUtc: '2026-10-16T10:01:00Z',
      lastError: null,
      connectMode: 'token',
      tokenFields: [{ key: 'accessToken', label: 'Access token', help: 'From web.dhan.co' }],
    })
  })

  it('fills what is missing, and drops a row with no broker', () => {
    const rows = readConnections({ brokers: [{ broker: 'angel' }, { name: 'nobody' }, 'fyers', null] })
    expect(rows).toHaveLength(1)
    expect(rows[0]).toMatchObject({
      broker: 'angel',
      name: 'Angel One',
      status: 'error',
      accountLabel: null,
      expiresUtc: null,
      connectMode: 'redirect',
      tokenFields: [],
    })
  })

  it('shows a status it does not know as a link error that says why', () => {
    const [c] = readConnections({ brokers: [{ broker: 'fyers', status: 'pending' }] })
    expect(c.status).toBe('error')
    expect(c.lastError).toBe('The server reported "pending".')
  })

  it('reads wrong types as unknown, and guesses the mode from the fields it has', () => {
    const [c] = readConnections({
      brokers: [
        {
          broker: 'dhan',
          status: 'not_connected',
          accountLabel: 4521,
          expiresUtc: 17,
          connectMode: 'oauth',
          tokenFields: [{ key: 'accessToken' }, { label: 'no key' }, 'clientId'],
        },
      ],
    })
    expect(c.accountLabel).toBeNull()
    expect(c.expiresUtc).toBeNull()
    expect(c.connectMode).toBe('token')
    expect(c.tokenFields).toEqual([{ key: 'accessToken', label: 'accessToken', help: '' }])
  })
})

describe('readConnectStart and readSyncResult', () => {
  it('reads both ways of linking, and refuses one it cannot follow', () => {
    expect(readConnectStart({ mode: 'redirect', url: 'https://api-t1.fyers.in/x' })).toEqual({ mode: 'redirect', url: 'https://api-t1.fyers.in/x' })
    expect(readConnectStart({ mode: 'token', fields: [{ key: 'accessToken', label: 'Token', help: '' }] })).toEqual({
      mode: 'token',
      fields: [{ key: 'accessToken', label: 'Token', help: '' }],
    })
    expect(readConnectStart({ mode: 'token' })).toEqual({ mode: 'token', fields: [] })
    expect(() => readConnectStart({ mode: 'redirect' })).toThrow(/sign-in address/)
    expect(() => readConnectStart({ mode: 'magic' })).toThrow(/how to link/)
    expect(() => readConnectStart(null)).toThrow()
  })

  it('reads a sync, an ok only when it is true', () => {
    expect(
      readSyncResult({
        takenUtc: '2026-10-16T10:01:00Z',
        results: [{ broker: 'fyers', ok: true, error: null }, { broker: 'angel', ok: 'true', error: 'Token rejected' }, { ok: true }],
      }),
    ).toEqual({
      takenUtc: '2026-10-16T10:01:00Z',
      results: [
        { broker: 'fyers', ok: true, error: null },
        { broker: 'angel', ok: false, error: 'Token rejected' },
      ],
    })
    expect(readSyncResult(undefined)).toEqual({ takenUtc: null, results: [] })
  })
})

describe('readReport', () => {
  it('reads the contract as it is', () => {
    const r = readReport(body())
    expect(r.totals).toEqual({ invested: 100000, current: 123456.7, pnl: 23456.7, pnlPct: 23.46, dayChange: -812.5, dayChangePct: -0.65, holdingsCount: 2 })
    expect(r.holdings.map((h) => h.symbol)).toEqual(['RELIANCE', 'NEWGEN'])
    expect(r.holdings[0].brokers).toEqual(['fyers', 'dhan'])
    expect(r.sources[1]).toEqual({ broker: 'angel', takenUtc: '2026-10-16T10:01:00Z', ok: false, error: 'Token rejected' })
    expect(r.positions?.[0].isDerivative).toBe(true)
    expect(r.funds?.[0].total).toBe(55440.4)
    expect(r.summary?.checked).toBe(true)
    expect(r.dataNotes).toEqual(['Sectors from the NSE list.'])
  })

  it('refuses a body with no holdings list or no totals: that is not an empty portfolio', () => {
    expect(() => readReport(null)).toThrow(/could not be read/)
    expect(() => readReport('<!doctype html>')).toThrow(/could not be read/)
    expect(() => readReport(body({ holdings: undefined }))).toThrow(/no holdings list/)
    expect(() => readReport(body({ holdings: {} }))).toThrow(/no holdings list/)
    expect(() => readReport(body({ totals: null }))).toThrow(/no totals/)
    expect(() => readReport(body({ totals: [1, 2] }))).toThrow(/no totals/)
  })

  it('reads a missing section as missing, not as zero or none', () => {
    const r = readReport(body({ allocation: undefined, concentration: 'x', risk: null, positions: undefined, funds: 7, derivatives: [], summary: null }))
    expect(r.allocation).toBeNull()
    expect(r.concentration).toBeNull()
    expect(r.risk).toBeNull()
    expect(r.positions).toBeNull()
    expect(r.funds).toBeNull()
    expect(r.derivatives).toBeNull()
    expect(r.summary).toBeNull()
    // While an empty list is a real "none".
    expect(readReport(body({ positions: [], funds: [] }))).toMatchObject({ positions: [], funds: [] })
  })

  it('reads wrong types as unknown, and numbers written as text as numbers', () => {
    const r = readReport(
      body({
        totals: { invested: '100000.50', current: 'lots', pnl: Number.NaN, pnlPct: Infinity, dayChange: true, dayChangePct: null, holdingsCount: '' },
        holdings: [{ symbol: 'ITC', qty: '300', avgPrice: {}, weight: '0.25', brokers: ['fyers', 3, null, ''] }],
      }),
    )
    expect(r.totals).toEqual({ invested: 100000.5, current: null, pnl: null, pnlPct: null, dayChange: null, dayChangePct: null, holdingsCount: null })
    const h = r.holdings[0]
    expect(h).toMatchObject({ symbol: 'ITC', name: 'ITC', qty: 300, avgPrice: null, weight: 0.25, brokers: ['fyers'], isin: null, sector: null })
  })

  it('drops holdings and positions with no symbol and says so in the notes', () => {
    const r = readReport(
      body({
        holdings: [{ symbol: 'ITC' }, { name: 'no symbol' }, null, { symbol: '  ' }],
        positions: [{ product: 'MIS' }],
      }),
    )
    expect(r.holdings.map((h) => h.symbol)).toEqual(['ITC'])
    expect(r.positions).toEqual([])
    expect(r.dataNotes).toEqual([
      'Sectors from the NSE list.',
      '3 holdings in the answer had no symbol and are not shown.',
      '1 position in the answer had no symbol and is not shown.',
    ])
  })

  it('names a blank allocation group Unknown, and keeps it', () => {
    const r = readReport(body())
    expect(r.allocation?.bySector.map((s) => s.name)).toEqual(['Energy', UNKNOWN])
    expect(r.allocation?.byMarketCap).toEqual([])
  })

  it('reads an HHI on either scale as a fraction', () => {
    expect(readReport(body()).concentration?.hhi).toBe(0.682)
    expect(readReport(body({ concentration: { hhi: 6820 } })).concentration?.hhi).toBeCloseTo(0.682)
    expect(hhiFraction(-1)).toBeNull()
    expect(hhiFraction(1)).toBe(1)
  })

  it('falls back to the standing disclaimer, and drops a summary with no text', () => {
    const r = readReport(body({ disclaimer: '  ', summary: { text: '', checked: true } }))
    expect(r.disclaimer).toBe(DISCLAIMER)
    expect(r.summary).toBeNull()
  })

  it('reads what the dev fixture builds, so the mock stays on the contract', () => {
    const now = Date.parse('2026-10-16T10:05:00Z')
    const state = {
      scenario: 'full' as const,
      links: {
        fyers: { status: 'connected' as const, connectedUtc: null, expiresUtc: null, lastSyncUtc: '2026-10-16T10:01:00Z', lastError: null },
        dhan: { status: 'connected' as const, connectedUtc: null, expiresUtc: null, lastSyncUtc: '2026-10-16T10:01:00Z', lastError: null },
        angel: { status: 'not_connected' as const, connectedUtc: null, expiresUtc: null, lastSyncUtc: null, lastError: null },
      },
      failing: [],
      synced: true,
      lastSyncMs: now - 240_000,
      forgotten: [],
    }
    const r = readReport(buildReport(state, now))
    expect(r.holdings.length).toBeGreaterThan(5)
    expect(r.dataNotes.some((n) => /could not be read|not shown/.test(n))).toBe(false)
    const weights = r.holdings.reduce((s, h) => s + (h.weight ?? 0), 0)
    expect(weights).toBeCloseTo(1, 2)
    expect(r.holdings.find((h) => h.symbol === 'RELIANCE')?.brokers).toEqual(['fyers', 'dhan'])
    expect(r.allocation?.bySector.some((s) => s.name === UNKNOWN)).toBe(true)
  })
})

describe('readHistory', () => {
  it('refuses a body that is not a list', () => {
    expect(() => readHistory({ points: [] })).toThrow(/history/)
  })

  it('keeps the days that have a date, in order', () => {
    expect(
      readHistory([
        { date: '2026-10-16', invested: 100000, current: 123456, pnl: 23456 },
        { date: 'yesterday', current: 1 },
        { invested: 5 },
        { date: '2026-10-15T00:00:00Z', invested: '100000', current: 'x' },
      ]),
    ).toEqual([
      { date: '2026-10-15', invested: 100000, current: null, pnl: null },
      { date: '2026-10-16', invested: 100000, current: 123456, pnl: 23456 },
    ])
  })
})

describe('the answers that are not failures', () => {
  it("reads the report's 404 with a message as nothing synced, and any other 404 as a failure", () => {
    expect(nothingSynced({ status: 404, body: { message: 'Nothing has been synced yet.' } })).toBe('Nothing has been synced yet.')
    expect(nothingSynced({ status: 404, body: '<!doctype html><html>' })).toBeNull()
    expect(nothingSynced({ status: 404, body: null })).toBeNull()
    expect(nothingSynced({ status: 500, body: { message: 'x' } })).toBeNull()
    expect(nothingSynced(new Error('offline'))).toBeNull()
  })

  it('reads how long a refused sync says to wait', () => {
    expect(retryAfterSeconds({ status: 429, body: { retryAfterSeconds: 41.2 } })).toBe(42)
    expect(retryAfterSeconds({ status: 429, body: { message: 'slow down' } })).toBe(60)
    expect(retryAfterSeconds({ status: 429, body: { retryAfterSeconds: -3 } })).toBe(60)
    expect(retryAfterSeconds({ status: 500 })).toBeNull()
  })
})

describe('formats', () => {
  it('shows a weight as a percentage with one decimal', () => {
    expect(formatWeight(0.1234)).toBe('12.3%')
    expect(formatWeight(0.8019)).toBe('80.2%')
    expect(formatWeight(1)).toBe('100.0%')
    expect(formatWeight(0)).toBe('0.0%')
    expect(formatWeight(0.0004)).toBe('<0.1%')
    expect(formatWeight(null)).toBe('—')
    expect(formatWeight(Number.NaN)).toBe('—')
  })

  it('signs a percentage with a true minus, and never shows minus zero', () => {
    expect(formatSignedPct(23.456)).toBe('+23.46%')
    expect(formatSignedPct(-0.65)).toBe('−0.65%')
    expect(formatSignedPct(0)).toBe('0.00%')
    expect(formatSignedPct(-0.001)).toBe('0.00%')
    expect(formatSignedPct(null)).toBe('—')
  })

  it('shortens rupees for an axis in Indian units', () => {
    expect(compactRupees(512)).toBe('₹512')
    expect(compactRupees(85_000)).toBe('₹85k')
    expect(compactRupees(4_20_000)).toBe('₹4.2L')
    expect(compactRupees(1_35_00_000)).toBe('₹1.35Cr')
    expect(compactRupees(-12_000)).toBe('−₹12k')
  })

  it('reads a calendar day as that day, whatever the time zone', () => {
    expect(formatDay('2026-10-16')).toBe('16 Oct')
    expect(formatDay('2026-01-01')).toBe('1 Jan')
    expect(formatDay('nonsense')).toBe('nonsense')
  })

  it("ends a server's message with one full stop, never two", () => {
    expect(sentence('the session has ended.')).toBe('the session has ended.')
    expect(sentence('Token rejected ')).toBe('Token rejected.')
    expect(sentence('Why?')).toBe('Why?')
  })

  it('says what a beta means without a judgement', () => {
    expect(betaMeaning(0.94)).toBe('the value moved about 0.94× as much as NIFTY 50, day to day')
    expect(betaMeaning(-0.2)).toBe('on average the value moved against NIFTY 50, day to day')
    expect(betaMeaning(null)).toBeNull()
  })
})

describe('hhiMeaning', () => {
  it('puts an HHI in its band, with the number of equal holdings it is as concentrated as', () => {
    expect(hhiMeaning(0.05)).toEqual({ points: 500, equivalent: 20, band: 'low', text: 'spread out: about as concentrated as 20 equal-sized holdings' })
    expect(hhiMeaning(0.1499)?.band).toBe('low')
    expect(hhiMeaning(0.15)?.band).toBe('moderate')
    expect(hhiMeaning(0.25)).toMatchObject({ points: 2500, equivalent: 4, band: 'moderate' })
    expect(hhiMeaning(0.2501)?.band).toBe('high')
    expect(hhiMeaning(1)).toEqual({ points: 10000, equivalent: 1, band: 'high', text: 'highly concentrated: about as concentrated as a single holding' })
  })

  it('reads the 0–10,000 scale too', () => {
    expect(hhiMeaning(2400)).toMatchObject({ points: 2400, band: 'moderate', equivalent: 4 })
  })

  it('has nothing to say about a missing or zero HHI', () => {
    expect(hhiMeaning(null)).toBeNull()
    expect(hhiMeaning(undefined)).toBeNull()
    expect(hhiMeaning(0)).toBeNull()
  })
})

describe('connections on screen', () => {
  const now = Date.parse('2026-10-16T12:00:00Z')

  it('says linked, and offers Disconnect only', () => {
    expect(connectionView(connection(), now)).toEqual({ status: 'connected', label: 'Linked', tone: 'pos', action: null, canDisconnect: true })
  })

  it('calls a link past its expiry expired even when the list still says connected', () => {
    expect(connectionView(connection({ expiresUtc: '2026-10-16T11:59:59Z' }), now)).toMatchObject({ status: 'expired', action: 'Reconnect' })
    expect(connectionView(connection({ expiresUtc: 'garbage' }), now).status).toBe('connected')
  })

  it('offers Connect to an unlinked broker and Reconnect to a broken one', () => {
    expect(connectionView(connection({ status: 'not_connected' }), now)).toMatchObject({ action: 'Connect', canDisconnect: false, tone: 'neutral' })
    expect(connectionView(connection({ status: 'error' }), now)).toMatchObject({ action: 'Reconnect', canDisconnect: true, tone: 'neg' })
    expect(connectionView(connection({ status: 'expired' }), now)).toMatchObject({ action: 'Reconnect', tone: 'warn' })
  })

  it('counts down the once-a-minute sync from the latest one', () => {
    const last = latestSyncMs([connection({ lastSyncUtc: '2026-10-16T11:59:10Z' }), connection({ lastSyncUtc: null }), connection({ lastSyncUtc: 'x' })])
    expect(last).toBe(Date.parse('2026-10-16T11:59:10Z'))
    expect(syncWaitSeconds(now, last)).toBe(10)
    expect(syncWaitSeconds(now + 10_000, last)).toBe(0)
    expect(syncWaitSeconds(now, null)).toBe(0)
    expect(latestSyncMs([])).toBeNull()
  })

  it('follows a broker sign-in only to an https address, or http on this machine', () => {
    const origin = 'https://www.openfno.com'
    expect(safeRedirectUrl('https://smartapi.angelone.in/publisher-login?api_key=k', origin)).toBe('https://smartapi.angelone.in/publisher-login?api_key=k')
    expect(safeRedirectUrl('/portfolio?connected=fyers', origin)).toBe('https://www.openfno.com/portfolio?connected=fyers')
    expect(safeRedirectUrl('javascript:alert(1)', origin)).toBeNull()
    expect(safeRedirectUrl('data:text/html,hi', origin)).toBeNull()
    expect(safeRedirectUrl('http://evil.example/x', origin)).toBeNull()
    expect(safeRedirectUrl('/portfolio?connected=fyers', 'http://localhost:5173')).toBe('http://localhost:5173/portfolio?connected=fyers')
  })

  it("reads a redirect's result once, and keeps a long error short", () => {
    expect(redirectResult(new URLSearchParams('connected=FYERS'))).toEqual({ kind: 'connected', broker: 'fyers' })
    expect(redirectResult(new URLSearchParams('error=User%20cancelled'))).toEqual({ kind: 'error', message: 'User cancelled' })
    const long = redirectResult(new URLSearchParams({ error: 'x'.repeat(400) }))
    expect(long?.kind === 'error' && long.message.length).toBe(301)
    expect(redirectResult(new URLSearchParams('connected=<script>'))).toBeNull()
    expect(redirectResult(new URLSearchParams('mock=full'))).toBeNull()
  })

  it('hides every token field but an identifier the user reads off their profile', () => {
    expect(isSecretField('accessToken')).toBe(true)
    expect(isSecretField('apiSecret')).toBe(true)
    expect(isSecretField('pin')).toBe(true)
    expect(isSecretField('clientId')).toBe(false)
    expect(isSecretField('client_code')).toBe(false)
  })

  it("keys the cache by the user, so one account's report is never another's", () => {
    expect(portfolioKey(7, 'report')).toEqual(['portfolio', 7, 'report'])
    expect(portfolioKey(8, 'report')).not.toEqual(portfolioKey(7, 'report'))
    expect(portfolioKey(null)).toEqual(['portfolio', 'anon'])
  })
})

describe('the holdings table', () => {
  const h = (symbol: string, over: Partial<Holding> = {}): Holding => ({
    symbol,
    isin: null,
    name: symbol,
    brokers: [],
    qty: 1,
    avgPrice: 1,
    ltp: 1,
    invested: 1,
    value: 1,
    pnl: 0,
    pnlPct: 0,
    dayChange: 0,
    weight: 0,
    sector: null,
    marketCapBand: null,
    ...over,
  })
  const rows = [
    h('ITC', { value: 300, pnl: -20, sector: 'FMCG', brokers: ['angel'] }),
    h('SBIN', { value: null, pnl: 50, name: 'State Bank of India', brokers: ['dhan'] }),
    h('INFY', { value: 900, pnl: null, sector: 'Information technology', brokers: ['fyers', 'dhan'] }),
  ]

  it('sorts by a figure either way, with an unknown value last both ways', () => {
    expect(sortHoldings(rows, 'value', 'desc').map((r) => r.symbol)).toEqual(['INFY', 'ITC', 'SBIN'])
    expect(sortHoldings(rows, 'value', 'asc').map((r) => r.symbol)).toEqual(['ITC', 'INFY', 'SBIN'])
    expect(sortHoldings(rows, 'pnl', 'asc').map((r) => r.symbol)).toEqual(['ITC', 'SBIN', 'INFY'])
  })

  it('sorts text A to Z, and brokers by their names', () => {
    expect(sortHoldings(rows, 'symbol', 'asc').map((r) => r.symbol)).toEqual(['INFY', 'ITC', 'SBIN'])
    expect(sortHoldings(rows, 'brokers', 'asc').map((r) => r.symbol)).toEqual(['ITC', 'SBIN', 'INFY'])
    // The input is left as it was.
    expect(rows.map((r) => r.symbol)).toEqual(['ITC', 'SBIN', 'INFY'])
  })

  it('finds rows by symbol, name, sector or broker name, every word typed', () => {
    expect(filterHoldings(rows, 'state bank').map((r) => r.symbol)).toEqual(['SBIN'])
    expect(filterHoldings(rows, 'fmcg').map((r) => r.symbol)).toEqual(['ITC'])
    expect(filterHoldings(rows, 'angel one').map((r) => r.symbol)).toEqual(['ITC'])
    expect(filterHoldings(rows, 'dhan').map((r) => r.symbol)).toEqual(['SBIN', 'INFY'])
    expect(filterHoldings(rows, '  ')).toHaveLength(3)
    expect(filterHoldings(rows, 'nothing here')).toEqual([])
  })
})

describe('advice words', () => {
  it('finds the words that would tell someone what to do', () => {
    expect(adviceWords('You should buy more, then Sell at the target.')).toEqual(['should', 'buy', 'sell', 'target'])
    expect(adviceWords('Hold. Consider a stop-loss, or a stop loss, and rebalancing.')).toEqual(['hold', 'consider', 'stop-loss', 'stop loss', 'rebalancing'])
    expect(adviceWords('abhi kharid lo, kal bech do')).toEqual(['kharid', 'bech'])
  })

  it('leaves the nouns of fact alone', () => {
    expect(adviceWords('Your largest holding is 15% of your holdings; a considerable share, targeted by no one.')).toEqual([])
  })

  it('shows a summary only when the server checked it and it carries no advice', () => {
    const s = { text: 'Your portfolio is worth ₹1,23,457.', model: null, checked: true, generatedUtc: null }
    expect(shownSummary(s)).toBe(s)
    expect(shownSummary({ ...s, checked: false })).toBeNull()
    expect(shownSummary({ ...s, text: 'Your portfolio is worth ₹1,23,457; you should trim RELIANCE.' })).toBeNull()
    expect(shownSummary(null)).toBeNull()
  })
})

describe('num', () => {
  it('takes finite numbers and numeric text only', () => {
    expect(num(3)).toBe(3)
    expect(num('3.5')).toBe(3.5)
    expect(num(' ')).toBeNull()
    expect(num('3,500')).toBeNull()
    expect(num(Infinity)).toBeNull()
    expect(num(false)).toBeNull()
  })
})
