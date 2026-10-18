/**
 * A contract-shaped stand-in for the Portfolio API (/api/Portfolio), for
 * development only. The numbers and the account labels are made up; the
 * shapes are the contract's.
 *
 * Production never reaches it: lib/portfolio.ts imports it only behind
 * `import.meta.env.DEV && VITE_PORTFOLIO_MOCK === '1'`, which a production
 * build folds to `false`, dropping the branch and this file with it.
 *
 *   VITE_PORTFOLIO_MOCK=1 npm run dev        then open /portfolio
 *
 * `?mock=<scenario>` on the page's URL starts that scenario afresh:
 *   full       FYERS and Dhan linked and synced, Angel One not linked (the default);
 *   partial    all three linked, Angel One fails in every sync;
 *   expired    the FYERS link expired two hours ago, Dhan linked;
 *   unsynced   Dhan linked, nothing synced yet (the report's 404);
 *   first-run  nothing linked;
 *   down       every read fails with a 503;
 *   loading    every read waits for ever.
 * The state (what is linked, whether it has synced) is kept in
 * sessionStorage, so a "redirect" to a broker and back keeps it. A token
 * typed into the form is checked for being non-empty and dropped: nothing
 * here keeps it.
 *
 * Totals, weights, the allocation, the concentration figures and the HHI are
 * computed from the holdings, so the page's sections agree with one another
 * the way the real report's must.
 *
 * Imports types only, so Node can run it as-is.
 */

import type { ConnectionStatus, TokenField } from './portfolio'

export type MockScenario = 'full' | 'partial' | 'expired' | 'unsynced' | 'first-run' | 'down' | 'loading'
const SCENARIOS: readonly MockScenario[] = ['full', 'partial', 'expired', 'unsynced', 'first-run', 'down', 'loading']

type Broker = 'angel' | 'dhan' | 'fyers'
const BROKERS: readonly Broker[] = ['angel', 'dhan', 'fyers']
const NAMES: Record<Broker, string> = { angel: 'Angel One', dhan: 'Dhan', fyers: 'FYERS' }
const LABELS: Record<Broker, string> = { angel: 'A•••7731', dhan: '••••8812', fyers: 'XU•••21' }

const DHAN_FIELDS: TokenField[] = [
  { key: 'clientId', label: 'Dhan client ID', help: 'The ten-digit ID on your Dhan profile page.' },
  {
    key: 'accessToken',
    label: 'Access token',
    help: 'Made on web.dhan.co under My profile → Access DhanHQ APIs. It is valid for 30 days.',
  },
]

interface LinkState {
  status: ConnectionStatus
  connectedUtc: string | null
  expiresUtc: string | null
  lastSyncUtc: string | null
  lastError: string | null
}

interface MockState {
  scenario: MockScenario
  links: Record<Broker, LinkState>
  /** Brokers whose sync fails. */
  failing: Broker[]
  synced: boolean
  lastSyncMs: number | null
  /** Brokers whose snapshots were deleted on unlinking. */
  forgotten: Broker[]
}

// ---------- the made-up book ----------

interface Lot {
  broker: Broker
  symbol: string
  name: string
  isin: string
  qty: number
  avg: number
  ltp: number
  prevClose: number
  sector: string | null
  cap: string | null
}

const LOTS: Lot[] = [
  { broker: 'fyers', symbol: 'RELIANCE', name: 'Reliance Industries', isin: 'INFIX0000001', qty: 40, avg: 2410, ltp: 2862.4, prevClose: 2841.1, sector: 'Energy', cap: 'Large cap' },
  { broker: 'fyers', symbol: 'INFY', name: 'Infosys', isin: 'INFIX0000002', qty: 60, avg: 1402, ltp: 1489.2, prevClose: 1497.6, sector: 'Information technology', cap: 'Large cap' },
  { broker: 'fyers', symbol: 'TATAMOTORS', name: 'Tata Motors', isin: 'INFIX0000003', qty: 120, avg: 912, ltp: 708.5, prevClose: 716.3, sector: 'Automobiles', cap: 'Large cap' },
  { broker: 'fyers', symbol: 'ITC', name: 'ITC', isin: 'INFIX0000004', qty: 300, avg: 412, ltp: 438.9, prevClose: 436.2, sector: 'FMCG', cap: 'Large cap' },
  { broker: 'fyers', symbol: 'NIFTYBEES', name: 'Nippon India ETF Nifty 50 BeES', isin: 'INFIX0000005', qty: 250, avg: 241, ltp: 268.3, prevClose: 266.9, sector: null, cap: null },
  { broker: 'dhan', symbol: 'HDFCBANK', name: 'HDFC Bank', isin: 'INFIX0000006', qty: 55, avg: 1532, ltp: 1688, prevClose: 1679.4, sector: 'Financial services', cap: 'Large cap' },
  { broker: 'dhan', symbol: 'RELIANCE', name: 'Reliance Industries', isin: 'INFIX0000001', qty: 15, avg: 2705, ltp: 2862.4, prevClose: 2841.1, sector: 'Energy', cap: 'Large cap' },
  { broker: 'dhan', symbol: 'LT', name: 'Larsen & Toubro', isin: 'INFIX0000007', qty: 18, avg: 3350, ltp: 3598, prevClose: 3611.5, sector: 'Capital goods', cap: 'Large cap' },
  { broker: 'dhan', symbol: 'POLYCAB', name: 'Polycab India', isin: 'INFIX0000008', qty: 12, avg: 6820, ltp: 6312, prevClose: 6255, sector: 'Capital goods', cap: 'Mid cap' },
  { broker: 'dhan', symbol: 'IRCTC', name: 'Indian Railway Catering and Tourism', isin: 'INFIX0000009', qty: 80, avg: 790, ltp: 742, prevClose: 739.8, sector: 'Consumer services', cap: 'Mid cap' },
  { broker: 'angel', symbol: 'SBIN', name: 'State Bank of India', isin: 'INFIX0000010', qty: 150, avg: 602, ltp: 811, prevClose: 805.2, sector: 'Financial services', cap: 'Large cap' },
  { broker: 'angel', symbol: 'TITAN', name: 'Titan Company', isin: 'INFIX0000011', qty: 20, avg: 3320, ltp: 3402, prevClose: 3420.5, sector: 'Consumer durables', cap: 'Large cap' },
  { broker: 'angel', symbol: 'DMART', name: 'Avenue Supermarts', isin: 'INFIX0000012', qty: 10, avg: 4110, ltp: 3935, prevClose: 3951, sector: 'Consumer services', cap: 'Large cap' },
  { broker: 'angel', symbol: 'NEWGEN-SM', name: 'Newgen Fabrics (SME)', isin: 'INFIX0000013', qty: 1200, avg: 48.5, ltp: 61.2, prevClose: 59.9, sector: null, cap: null },
]

const POSITIONS = [
  { broker: 'fyers' as Broker, symbol: 'NIFTY 28 OCT 25000 CE', product: 'NRML', qty: 75, avgPrice: 112.5, ltp: 96.4, isDerivative: true },
  { broker: 'dhan' as Broker, symbol: 'TATAPOWER', product: 'MIS', qty: 100, avgPrice: 412, ltp: 415.2, isDerivative: false },
]

const FUNDS: Record<Broker, { available: number; used: number }> = {
  fyers: { available: 48_210.4, used: 7_230 },
  dhan: { available: 1_12_480.75, used: 41_520 },
  angel: { available: 9_315.2, used: 0 },
}

const round2 = (n: number) => Math.round(n * 100) / 100
const round4 = (n: number) => Math.round(n * 10_000) / 10_000

// ---------- state ----------

const STORE_KEY = 'openfno.portfolio.mock'

function iso(ms: number): string {
  return new Date(ms).toISOString()
}

/** The end of today's IST day, when a FYERS link stops working. */
function endOfIstDay(nowMs: number): number {
  const ist = nowMs + 19_800_000
  const end = Math.floor(ist / 86_400_000) * 86_400_000 + 86_400_000 - 1_000
  return end - 19_800_000
}

function link(status: ConnectionStatus, broker: Broker, nowMs: number, synced: boolean): LinkState {
  if (status === 'not_connected') return { status, connectedUtc: null, expiresUtc: null, lastSyncUtc: null, lastError: null }
  const connected = nowMs - (broker === 'dhan' ? 6 * 86_400_000 : 5 * 3_600_000)
  const expires = broker === 'fyers' ? endOfIstDay(nowMs) : broker === 'dhan' ? connected + 30 * 86_400_000 : nowMs + 14 * 3_600_000
  return {
    status,
    connectedUtc: iso(connected),
    expiresUtc: status === 'expired' ? iso(nowMs - 2 * 3_600_000) : iso(expires),
    lastSyncUtc: synced ? iso(nowMs - 4 * 60_000) : null,
    lastError: status === 'error' ? 'The broker refused the stored token.' : null,
  }
}

function initial(scenario: MockScenario, nowMs: number): MockState {
  const s = (fyers: ConnectionStatus, dhan: ConnectionStatus, angel: ConnectionStatus, synced: boolean, failing: Broker[] = []): MockState => ({
    scenario,
    links: { fyers: link(fyers, 'fyers', nowMs, synced), dhan: link(dhan, 'dhan', nowMs, synced), angel: link(angel, 'angel', nowMs, synced) },
    failing,
    synced,
    lastSyncMs: synced ? nowMs - 4 * 60_000 : null,
    forgotten: [],
  })
  switch (scenario) {
    case 'partial': {
      const state = s('connected', 'connected', 'connected', true, ['angel'])
      state.links.angel.lastError = 'Angel One answered 401: the session has ended.'
      state.links.angel.lastSyncUtc = iso(nowMs - 26 * 3_600_000)
      return state
    }
    case 'expired': {
      const state = s('expired', 'connected', 'not_connected', true)
      state.links.fyers.lastSyncUtc = iso(nowMs - 20 * 3_600_000)
      return state
    }
    case 'unsynced':
      return s('not_connected', 'connected', 'not_connected', false)
    case 'first-run':
    case 'down':
    case 'loading':
      return s('not_connected', 'not_connected', 'not_connected', false)
    default:
      return s('connected', 'connected', 'not_connected', true)
  }
}

function storage(): Storage | null {
  try {
    return typeof window === 'undefined' ? null : window.sessionStorage
  } catch {
    return null
  }
}

function requestedScenario(): MockScenario | null {
  if (typeof window === 'undefined') return null
  const asked = new URLSearchParams(window.location.search).get('mock')
  return SCENARIOS.includes(asked as MockScenario) ? (asked as MockScenario) : null
}

function load(nowMs: number): MockState {
  const asked = requestedScenario()
  if (!asked) {
    try {
      const saved = storage()?.getItem(STORE_KEY)
      if (saved) return JSON.parse(saved) as MockState
    } catch {
      /* a broken saved state: start the default afresh */
    }
  }
  return initial(asked ?? 'full', nowMs)
}

function save(state: MockState) {
  try {
    storage()?.setItem(STORE_KEY, JSON.stringify(state))
  } catch {
    /* storage refused: the state lives as long as the page */
  }
}

// ---------- answers ----------

function failure(status: number, body: unknown, message: string): Error {
  return Object.assign(new Error(message), { status, body })
}

function connectionRow(state: MockState, broker: Broker) {
  const l = state.links[broker]
  return {
    broker,
    name: NAMES[broker],
    status: l.status,
    accountLabel: l.status === 'not_connected' ? null : LABELS[broker],
    connectedUtc: l.connectedUtc,
    expiresUtc: l.expiresUtc,
    lastSyncUtc: l.lastSyncUtc,
    lastError: l.lastError,
    connectMode: broker === 'dhan' ? 'token' : 'redirect',
    tokenFields: broker === 'dhan' ? DHAN_FIELDS : [],
  }
}

/** The brokers whose snapshots the report is built from: read at least once, not forgotten. */
function reported(state: MockState): Broker[] {
  return BROKERS.filter((b) => state.links[b].lastSyncUtc && !state.forgotten.includes(b) && !state.failing.includes(b))
}

/** The brokers the report names as sources: linked, or unlinked with their snapshots kept. */
function sourced(state: MockState): Broker[] {
  return BROKERS.filter((b) => (state.links[b].status !== 'not_connected' || state.links[b].lastSyncUtc) && !state.forgotten.includes(b))
}

function slices(rows: { key: string; value: number }[], total: number) {
  const by = new Map<string, number>()
  for (const r of rows) by.set(r.key, (by.get(r.key) ?? 0) + r.value)
  return [...by.entries()]
    .map(([name, value]) => ({ name, value: round2(value), weight: round4(value / total) }))
    .sort((a, b) => b.value - a.value)
}

/** The report from the brokers that have snapshots, with every aggregate computed from the rows. */
export function buildReport(state: MockState, nowMs: number) {
  const from = reported(state)
  const lots = LOTS.filter((l) => from.includes(l.broker))
  const bySymbol = new Map<string, Lot[]>()
  for (const l of lots) bySymbol.set(l.symbol, [...(bySymbol.get(l.symbol) ?? []), l])

  const merged = [...bySymbol.values()].map((ls) => {
    const qty = ls.reduce((s, l) => s + l.qty, 0)
    const invested = ls.reduce((s, l) => s + l.qty * l.avg, 0)
    const first = ls[0]
    const value = qty * first.ltp
    return {
      symbol: first.symbol,
      isin: first.isin,
      name: first.name,
      brokers: ls.map((l) => l.broker),
      qty,
      avgPrice: round2(invested / qty),
      ltp: first.ltp,
      invested: round2(invested),
      value: round2(value),
      pnl: round2(value - invested),
      pnlPct: round2(((value - invested) / invested) * 100),
      dayChange: round2(qty * (first.ltp - first.prevClose)),
      sector: first.sector,
      marketCapBand: first.cap,
      perBroker: ls.map((l) => ({ broker: l.broker, value: l.qty * l.ltp })),
    }
  })

  const current = merged.reduce((s, h) => s + h.value, 0)
  const invested = merged.reduce((s, h) => s + h.invested, 0)
  const dayChange = merged.reduce((s, h) => s + h.dayChange, 0)
  const holdings = merged
    .map(({ perBroker: _perBroker, ...h }) => ({ ...h, weight: current > 0 ? round4(h.value / current) : 0 }))
    .sort((a, b) => b.value - a.value)
  const weights = holdings.map((h) => h.value / current)
  const uncovered = holdings.filter((h) => h.symbol === 'NEWGEN-SM').reduce((s, h) => s + h.weight, 0)

  const positions = POSITIONS.filter((p) => from.includes(p.broker)).map((p) => ({
    ...p,
    pnl: round2((p.ltp - p.avgPrice) * p.qty),
  }))
  const derivs = positions.filter((p) => p.isDerivative)

  const sources = sourced(state).map((b) => ({
    broker: b,
    takenUtc: state.links[b].lastSyncUtc,
    ok: !state.failing.includes(b),
    error: state.failing.includes(b) ? state.links[b].lastError : null,
  }))

  const top = holdings[0]
  const top5 = weights.slice(0, 5).reduce((s, w) => s + w, 0)
  const sectorSlices = slices(holdings.map((h) => ({ key: h.sector ?? 'Unknown', value: h.value })), current)
  const pnl = current - invested
  const fmt = (n: number) => `₹${Math.round(n).toLocaleString('en-IN')}`
  const unknownCount = holdings.filter((h) => h.sector == null).length

  return {
    asOfUtc: iso(state.lastSyncMs ?? nowMs),
    sources,
    totals: {
      invested: round2(invested),
      current: round2(current),
      pnl: round2(pnl),
      pnlPct: round2((pnl / invested) * 100),
      dayChange: round2(dayChange),
      dayChangePct: round2((dayChange / (current - dayChange)) * 100),
      holdingsCount: holdings.length,
    },
    holdings,
    allocation: {
      bySector: sectorSlices,
      byBroker: slices(merged.flatMap((h) => h.perBroker.map((p) => ({ key: NAMES[p.broker], value: p.value }))), current),
      byMarketCap: slices(holdings.map((h) => ({ key: h.marketCapBand ?? 'Unknown', value: h.value })), current),
    },
    concentration: {
      top1Weight: round4(weights[0] ?? 0),
      top5Weight: round4(top5),
      hhi: round4(weights.reduce((s, w) => s + w * w, 0)),
      largest: holdings.slice(0, 5).map((h) => h.symbol),
    },
    risk: {
      lookbackDays: 250,
      volatilityAnnualPct: 15.8,
      betaVsNifty: 0.94,
      maxDrawdownPct: -12.6,
      coveredWeight: round4(1 - uncovered),
      note:
        uncovered > 0
          ? 'From daily closes over the last 250 trading days. NEWGEN-SM has under 250 days of prices and is left out.'
          : 'From daily closes over the last 250 trading days.',
    },
    positions,
    derivatives: {
      openPositions: derivs.length,
      grossExposure: round2(derivs.reduce((s, p) => s + Math.abs(p.qty * p.ltp), 0)),
      note: 'Gross exposure is the sum of quantity × last price over the open F&O positions.',
    },
    funds: from.map((b) => ({
      broker: b,
      available: FUNDS[b].available,
      used: FUNDS[b].used,
      total: round2(FUNDS[b].available + FUNDS[b].used),
    })),
    summary: top
      ? {
          text:
            `Your portfolio is worth ${fmt(current)} across ${holdings.length} holdings at ${from.length} ${from.length === 1 ? 'broker' : 'brokers'}. ` +
            `It is ${pnl >= 0 ? 'up' : 'down'} ${fmt(Math.abs(pnl))} (${Math.abs((pnl / invested) * 100).toFixed(2)}%) on the ${fmt(invested)} invested, ` +
            `and ${dayChange >= 0 ? 'up' : 'down'} ${fmt(Math.abs(dayChange))} today. ` +
            `The largest holding, ${top.symbol}, is ${(top.weight * 100).toFixed(1)}% of the value; the five largest are ${(top5 * 100).toFixed(1)}%. ` +
            `${sectorSlices[0].name} is the largest sector at ${(sectorSlices[0].weight * 100).toFixed(1)}%.`,
          model: 'mock-writer',
          checked: true,
          generatedUtc: iso((state.lastSyncMs ?? nowMs) + 20_000),
        }
      : null,
    dataNotes: [
      `Sector and market-cap band come from the NSE and BSE index lists of 30 Sep 2026; ${unknownCount} ${unknownCount === 1 ? 'holding is' : 'holdings are'} not in them and show${unknownCount === 1 ? 's' : ''} as Unknown.`,
      'Average prices are as each broker reports them. Where a broker leaves charges out of its average, the P&L here is higher than the cash result.',
      'Made-up numbers from the development mock (lib/portfolio.fixture.ts).',
    ],
    disclaimer: 'Information about your own holdings, not investment advice.',
  }
}

/** mulberry32: a seeded generator, so the history is the same on every load. */
function rng(seed: number): () => number {
  let a = seed >>> 0
  return () => {
    a = (a + 0x6d2b79f5) >>> 0
    let t = a
    t = Math.imul(t ^ (t >>> 15), t | 1)
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61)
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296
  }
}

/** A weekday a point, walking back from today's totals; money was added twice along the way. */
export function buildHistory(state: MockState, nowMs: number, days: number) {
  if (!state.synced) return []
  const report = buildReport(state, nowMs)
  const r = rng(20261018)
  const out: { date: string; invested: number; current: number; pnl: number }[] = []
  let current = report.totals.current
  let invested = report.totals.invested
  for (let i = 0; i < days; i++) {
    const d = new Date(nowMs + 19_800_000 - i * 86_400_000)
    const dow = d.getUTCDay()
    if (dow === 0 || dow === 6) continue
    out.push({ date: d.toISOString().slice(0, 10), invested: round2(invested), current: round2(current), pnl: round2(current - invested) })
    // Walking backwards: undo a day's move, and two top-ups.
    current = current / (1 + (r() - 0.48) * 0.018)
    if (i === 23 || i === 61) {
      invested -= 40_000
      current -= 40_000
    }
  }
  return out.reverse()
}

let shared: ReturnType<typeof createPortfolioMock> | null = null

/** The page's mock: one per page load, its scenario from `?mock=` or the state saved before a redirect. */
export function portfolioMock() {
  if (!shared) {
    const state = load(Date.now())
    save(state)
    shared = createPortfolioMock(state)
  }
  return shared
}

/**
 * A mock answering the Portfolio routes from one state: a scenario's
 * (`createPortfolioMock('partial')`, what a screenshot harness running this
 * file in Node uses) or one already loaded.
 */
export function createPortfolioMock(start: MockScenario | MockState = 'full') {
  const s: MockState = typeof start === 'string' ? initial(start, Date.now()) : start

  async function handle(method: string, path: string, body?: unknown): Promise<unknown> {
    if (s.scenario === 'loading') return new Promise(() => {})
    await new Promise((r) => setTimeout(r, 250))
    if (s.scenario === 'down') throw failure(503, { message: 'The portfolio service is not answering.' }, 'The portfolio service is not answering.')

    const url = new URL(path, 'http://mock.local')
    const route = url.pathname.replace(/^\/api\/Portfolio/i, '')
    const now = Date.now()
    const m = /^\/connections\/(\w+)(\/start|\/token)?$/.exec(route)
    const broker = (m && BROKERS.includes(m[1] as Broker) ? m[1] : null) as Broker | null

    if (method === 'GET' && route === '/connections') return { brokers: BROKERS.map((b) => connectionRow(s, b)) }

    if (method === 'POST' && broker && m?.[2] === '/start') {
      if (broker === 'dhan') return { mode: 'token', fields: DHAN_FIELDS }
      // The broker's sign-in, skipped: the link is made, and the browser comes back as it would from the broker.
      s.links[broker] = link('connected', broker, now, false)
      s.forgotten = s.forgotten.filter((b) => b !== broker)
      save(s)
      return { mode: 'redirect', url: `/portfolio?connected=${broker}` }
    }

    if (method === 'POST' && broker && m?.[2] === '/token') {
      const fields = (body as { fields?: Record<string, unknown> } | undefined)?.fields ?? {}
      const missing = DHAN_FIELDS.filter((f) => typeof fields[f.key] !== 'string' || !(fields[f.key] as string).trim())
      if (missing.length) throw failure(400, { message: `${missing[0].label} is empty.` }, `${missing[0].label} is empty.`)
      s.links[broker] = link('connected', broker, now, false)
      s.forgotten = s.forgotten.filter((b) => b !== broker)
      save(s)
      return connectionRow(s, broker)
    }

    if (method === 'DELETE' && broker && !m?.[2]) {
      // Unlinked, its last snapshot kept in the report unless it is forgotten too.
      const forget = url.searchParams.get('forget') === 'true'
      const kept = s.failing.includes(broker) ? null : s.links[broker].lastSyncUtc
      s.links[broker] = { ...link('not_connected', broker, now, false), lastSyncUtc: forget ? null : kept }
      if (forget) s.forgotten = [...new Set([...s.forgotten, broker])]
      s.failing = s.failing.filter((b) => b !== broker)
      if (!BROKERS.some((b) => s.links[b].lastSyncUtc)) s.synced = false
      save(s)
      return null
    }

    if (method === 'POST' && route === '/sync') {
      const wait = s.lastSyncMs == null ? 0 : Math.ceil((s.lastSyncMs + 60_000 - now) / 1000)
      if (wait > 0) {
        const message = `Sync is limited to once a minute. Try again in ${wait} s.`
        throw failure(429, { message, retryAfterSeconds: wait }, message)
      }
      const linked = BROKERS.filter((b) => s.links[b].status === 'connected')
      if (linked.length === 0) throw failure(400, { message: 'No linked account to read.' }, 'No linked account to read.')
      for (const b of linked) {
        if (!s.failing.includes(b)) s.links[b].lastSyncUtc = iso(now)
      }
      s.synced = true
      s.lastSyncMs = now
      save(s)
      return {
        takenUtc: iso(now),
        results: linked.map((b) => ({ broker: b, ok: !s.failing.includes(b), error: s.failing.includes(b) ? s.links[b].lastError : null })),
      }
    }

    if (method === 'GET' && route === '/report') {
      if (!s.synced || reported(s).length === 0) {
        const message = 'Nothing has been synced yet. Link an account, then press Sync now.'
        throw failure(404, { message }, message)
      }
      return buildReport(s, now)
    }

    if (method === 'GET' && route === '/history') {
      const days = Math.min(365, Math.max(1, Number(url.searchParams.get('days')) || 90))
      return buildHistory(s, now, days)
    }

    throw failure(404, null, `Not found: ${url.pathname}`)
  }

  return { handle }
}
