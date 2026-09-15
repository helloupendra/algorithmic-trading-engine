/**
 * Strategy library — how the catalogue is read at a glance. Every label on
 * the Library page's cards, filters and sections is derived here from what
 * the API already says about a strategy (its catalog entry, the facts block
 * of its spec, its run history), so the rules can be tested without a DOM.
 *
 * The house rule applies throughout: a fact that is not known is left out,
 * never shown as its default. A strategy whose spec facts have not loaded
 * gets no "built-in exit" chip at all rather than a "no".
 */

import type { LiveRunSummary, StrategyDataRequirement, StrategyListItem } from './types'
import type { SpecFacts } from './specs'
import { runNetPnl } from './runHistory'
import { formatResolution } from './symbols'

/* ------------------------------------------------------------------ side */

/** Which way the strategy's orders go: all buys, all sells, both, or none (an alerter). */
export type TradeSide = 'buy' | 'sell' | 'mixed' | 'none'

export const SIDE_LABEL: Record<TradeSide, string> = {
  buy: 'Buy',
  sell: 'Sell',
  mixed: 'Buy + sell',
  none: 'No orders',
}

/**
 * From the legs summary's verbs: "Buy ATM CE on an up-break, or Buy ATM PE…"
 * is buy, "Sell ATM CE + Sell ATM PE" is sell, "Buy ATM CE + Sell OTM CE" is
 * mixed. A summary with neither verb places no orders.
 */
export function tradeSide(legsSummary: string | null | undefined): TradeSide {
  const text = legsSummary ?? ''
  const buy = /\bbuys?\b/i.test(text)
  const sell = /\bsells?\b/i.test(text)
  if (buy && sell) return 'mixed'
  if (buy) return 'buy'
  if (sell) return 'sell'
  return 'none'
}

/* ------------------------------------------------------------------ legs */

// Where a leg's description stops being the leg and starts being the rule.
const LEG_QUALIFIER = /,|\s(?:on|around|held|rolled|chosen|until|when|if)\s/i

function trimLeg(leg: string): string {
  const cut = leg.search(LEG_QUALIFIER)
  return (cut >= 0 ? leg.slice(0, cut) : leg)
    .replace(/\b(?:far\s+)?OTM\s+CE\s+and\s+PE\s+wings\b/i, 'wings')
    .replace(/\bCE\s+and\s+PE\b/i, 'CE+PE')
    .replace(/\bCE\s+or\s+PE\b/i, 'CE/PE')
    .replace(/\s+/g, ' ')
    .trim()
}

/** "Sell ATM CE", "Sell ATM PE" joined by `joiner` → "Sell ATM CE+PE"; anything else is left as two legs. */
function mergePairs(legs: string[], joiner: '+' | '/'): string[] {
  const out: string[] = []
  for (const leg of legs) {
    const prev = out[out.length - 1]
    const a = prev?.match(/^(.*\S)\s+CE$/)
    const b = leg.match(/^(.*\S)\s+PE$/)
    if (a && b && a[1] === b[1]) out[out.length - 1] = `${a[1]} CE${joiner}PE`
    else if (leg !== prev) out.push(leg)
  }
  return out
}

/**
 * The legs summary as a chip: the legs only, without the rule text around
 * them. "Sell ATM CE + Sell ATM PE, rolled on every ATM change" → "Sell ATM
 * CE+PE"; "Buy ATM CE on an up-break, or Buy ATM PE on a down-break" → "Buy
 * ATM CE/PE"; "Sell 1-2 straddles on adjacent strikes + Buy far OTM CE and PE
 * wings" → "Sell 1-2 straddles + Buy wings". Null when there are no legs.
 */
export function shortLegs(legsSummary: string | null | undefined): string | null {
  const text = (legsSummary ?? '').replace(/\s*\([^)]*\)/g, '').trim()
  if (!text || tradeSide(text) === 'none') return null
  const alternatives = text
    .split(/,?\s+or\s+(?=(?:buy|sell)s?\b)/i)
    .map((alt) => mergePairs(alt.split(/\s*\+\s*/).map(trimLeg).filter(Boolean), '+'))
  // "Buy ATM CE" or "Buy ATM PE" → "Buy ATM CE/PE".
  if (alternatives.length > 1 && alternatives.every((a) => a.length === 1)) {
    return mergePairs(alternatives.map((a) => a[0]), '/').join(' or ')
  }
  return alternatives.map((a) => a.join(' + ')).join(' or ')
}

/* ------------------------------------------------------------ instrument */

/** What the strategy trades, by its supported underlyings. */
export type InstrumentClass = 'index' | 'mcx' | 'stocks' | 'any'

export const INSTRUMENT_LABEL: Record<InstrumentClass, string> = {
  index: 'Index options',
  mcx: 'MCX',
  stocks: 'Stocks',
  any: 'Any underlying',
}

const INDEX_UNDERLYINGS = new Set(['NIFTY', 'BANKNIFTY', 'FINNIFTY', 'MIDCPNIFTY', 'NIFTYNXT50', 'SENSEX', 'BANKEX', 'SENSEX50'])
const MCX_UNDERLYINGS = new Set([
  'CRUDEOIL', 'CRUDEOILM', 'NATURALGAS', 'NATGASMINI', 'GOLD', 'GOLDM', 'GOLDPETAL', 'GOLDGUINEA',
  'SILVER', 'SILVERM', 'SILVERMIC', 'COPPER', 'ZINC', 'ZINCMINI', 'ALUMINIUM', 'ALUMINI', 'LEAD', 'LEADMINI', 'NICKEL',
])

/**
 * Index options when every supported underlying is an index, MCX when any is
 * a commodity and none is a stock, Stocks when any underlying is neither (or
 * the catalog says the strategy trades equity), Any when it names none.
 */
export function instrumentClass(s: Pick<StrategyListItem, 'supportedUnderlyings' | 'instrumentKind'>): InstrumentClass {
  if (/equity|stock|cash/i.test(s.instrumentKind ?? '')) return 'stocks'
  const names = s.supportedUnderlyings.map((u) => u.trim().toUpperCase()).filter(Boolean)
  if (names.length === 0) return 'any'
  if (names.some((u) => !INDEX_UNDERLYINGS.has(u) && !MCX_UNDERLYINGS.has(u))) return 'stocks'
  if (names.some((u) => MCX_UNDERLYINGS.has(u))) return 'mcx'
  return 'index'
}

/* ------------------------------------------------------------- spec facts */

/** The spec says the strategy reads option-chain OI (ChainFlowBuy, LogicEngine). */
export function usesOptionChain(facts: SpecFacts | null | undefined, data: StrategyDataRequirement[] = []): boolean {
  if (/option\s+chain|\bOI\b/i.test(facts?.data ?? '')) return true
  return data.some((d) => /chain/i.test(d.symbolType))
}

/** True / false from the spec's `built_in_exit`; null when the spec does not say. */
export function builtInExit(facts: SpecFacts | null | undefined): boolean | null {
  const v = (facts?.built_in_exit ?? '').trim().toLowerCase()
  if (v === 'true' || v === 'yes') return true
  if (v === 'false' || v === 'no') return false
  return null
}

/**
 * The chart the strategy reads: "5m chart" for a bar resolution, "Every tick"
 * for a strategy with no bar of its own. Falls back to the catalog's declared
 * index resolution when the spec's facts are not known; null when neither says.
 */
export function timeframeLabel(facts: SpecFacts | null | undefined, data: StrategyDataRequirement[] = []): string | null {
  const resolution = (facts?.resolution ?? '').replace(/\(.*?\)/g, '').trim().toLowerCase()
  const evaluatesOn = (facts?.evaluates_on ?? '').trim().toLowerCase()
  const bar = /^(\d+)\s*(m|min|h|d)$/.exec(resolution)
  if (bar) return `${bar[1]}${bar[2] === 'min' ? 'm' : bar[2]} chart`
  if (resolution === 'tick' || (evaluatesOn === 'tick' && (resolution === 'any' || resolution === ''))) return 'Every tick'
  if (facts) return null
  const declared = data.find((d) => d.symbolType === 'index') ?? data[0]
  return declared ? `${formatResolution(declared.resolution)} chart` : null
}

/** "When it acts" in words, for the spec page's summary; null when the facts do not say. */
export function cadenceText(facts: SpecFacts | null | undefined): string | null {
  const evaluatesOn = (facts?.evaluates_on ?? '').trim().toLowerCase()
  const chart = timeframeLabel(facts)
  if (evaluatesOn === 'bar') return chart && chart !== 'Every tick' ? `Once per closed ${chart.replace(' chart', '')} bar` : 'Once per closed bar'
  if (evaluatesOn === 'tick') return chart && chart !== 'Every tick' ? `On every tick, reading the ${chart}` : 'On every tick'
  return null
}

/**
 * The one data need worth a chip: the option chain when the spec reads it,
 * else candles, else ticks. The full list is the chip's title.
 */
export function dataLabel(facts: SpecFacts | null | undefined, data: StrategyDataRequirement[] = []): string | null {
  const text = (facts?.data ?? '').toLowerCase()
  if (text) {
    if (usesOptionChain(facts)) return 'Chain OI'
    if (/candles/.test(text)) return 'Candles'
    if (/ticks|quotes/.test(text)) return 'Ticks'
    return null
  }
  if (usesOptionChain(null, data)) return 'Chain OI'
  return data.length > 0 ? 'Candles' : null
}

/* ------------------------------------------------------------ description */

/**
 * The first sentence of a description, trimmed to `max` characters. A
 * sentence that is still too long is cut at its first colon, semicolon or
 * dash when that leaves at least a short phrase ("Runs a rolling short
 * straddle: sells…" → "Runs a rolling short straddle"), else at a word.
 */
export function oneLiner(description: string | null | undefined, max = 120): string {
  const text = (description ?? '').replace(/\s+/g, ' ').trim()
  if (!text) return ''
  const end = /[.!?](?=\s+["'“(]?[A-Z0-9])/.exec(text)
  let sentence = end ? text.slice(0, end.index + 1) : text
  if (sentence.length <= max) return sentence
  const clause = /\s*(?::|;|\s—\s|\s-\s)/.exec(sentence)
  if (clause && clause.index >= 20 && clause.index <= max) return sentence.slice(0, clause.index)
  sentence = sentence.slice(0, max - 1)
  const space = sentence.lastIndexOf(' ')
  return `${(space > 40 ? sentence.slice(0, space) : sentence).replace(/[\s,;:.-]+$/, '')}…`
}

/* ------------------------------------------------------------- run history */

/** Days of run history the Library's P&L line covers, today included. */
export const RUN_WINDOW_DAYS = 7

export interface RecentPaper {
  /** Trading runs started in the window. */
  runs: number
  /** Alerter runs started in the window; they place no orders, so carry no P&L. */
  alertRuns: number
  /** Σ net P&L of the trading runs (open P&L included while a run is live). */
  netPnl: number
}

/** Per catalog id: the paper runs in `runs` (one history request) folded into a count and a P&L. */
export function recentPaperByStrategy(runs: LiveRunSummary[]): Map<number, RecentPaper> {
  const out = new Map<number, RecentPaper>()
  for (const run of runs) {
    const row = out.get(run.strategyId) ?? { runs: 0, alertRuns: 0, netPnl: 0 }
    if (run.role === 'alerts') row.alertRuns += 1
    else {
      row.runs += 1
      row.netPnl += runNetPnl(run)
    }
    out.set(run.strategyId, row)
  }
  return out
}

/** The rows of several strategies added up (a variant family's card). */
export function sumRecentPaper(rows: Array<RecentPaper | undefined>): RecentPaper | undefined {
  const present = rows.filter((r): r is RecentPaper => r != null)
  if (present.length === 0) return undefined
  return present.reduce((a, b) => ({ runs: a.runs + b.runs, alertRuns: a.alertRuns + b.alertRuns, netPnl: a.netPnl + b.netPnl }))
}

/* --------------------------------------------------------------- families */

/**
 * Names that head a family of variants: a strategy whose name starts at
 * least two other names, followed there by a capital or a digit ("Fulcrum"
 * starts "FulcrumBuy" and "Fulcrum2Straddle20"). "Short" is not a strategy,
 * so ShortStraddle and ShortStrangle stay apart.
 */
export function familyRoots(names: string[]): Set<string> {
  const roots = new Set<string>()
  for (const root of names) {
    const variants = names.filter((n) => n !== root && n.startsWith(root) && /[A-Z0-9]/.test(n.charAt(root.length)))
    if (variants.length >= 2) roots.add(root)
  }
  return roots
}

/** The family a strategy belongs to — the longest root its name starts with, itself included; null for none. */
export function familyOf(name: string, roots: ReadonlySet<string>): string | null {
  let best: string | null = null
  for (const root of roots) {
    const matches = name === root || (name.startsWith(root) && /[A-Z0-9]/.test(name.charAt(root.length)))
    if (matches && (best == null || root.length > best.length)) best = root
  }
  return best
}

/* --------------------------------------------------------------- sections */

export type SectionKey = 'buying' | 'selling' | 'hedged' | 'commodities' | 'other'

/** In the order a trader reaches for them. */
export const SECTIONS: ReadonlyArray<{ key: SectionKey; title: string; hint: string }> = [
  { key: 'buying', title: 'Option buying', hint: 'Pays premium; needs the market to move' },
  { key: 'selling', title: 'Option selling', hint: 'Collects premium; wants the market to stay put' },
  { key: 'hedged', title: 'Spreads & hedged', hint: 'Buy and sell legs in one position' },
  { key: 'commodities', title: 'Commodities', hint: 'MCX' },
  { key: 'other', title: 'Alerts & examples', hint: 'Alerters and templates' },
]

export function sectionOf(s: Pick<StrategyListItem, 'category' | 'legsSummary' | 'supportedUnderlyings' | 'instrumentKind'>): SectionKey {
  const category = (s.category ?? '').trim().toLowerCase()
  const side = tradeSide(s.legsSummary)
  if (category === 'alerts' || category === 'alert' || category === 'example' || side === 'none') return 'other'
  if (instrumentClass(s) === 'mcx') return 'commodities'
  if (side === 'buy') return 'buying'
  if (side === 'sell') return 'selling'
  return 'hedged'
}

export type LibraryItem =
  | { kind: 'single'; key: string; strategy: StrategyListItem }
  | { kind: 'family'; key: string; root: string; members: StrategyListItem[] }

export interface LibrarySection {
  key: SectionKey
  title: string
  hint: string
  /** Strategies in the section, families counted by their members. */
  count: number
  items: LibraryItem[]
}

const byName = (a: StrategyListItem, b: StrategyListItem) => a.name.localeCompare(b.name, 'en', { numeric: true })

/**
 * The strategies laid out as sections of cards. A family with two or more
 * members in the same section is one card. Within a section, whatever is
 * running comes first, then what ran most in the recent window, then by name.
 * `roots` is computed over the whole catalogue so a filter that leaves two
 * variants still shows them as their family.
 */
export function buildLibrary(
  strategies: StrategyListItem[],
  roots: ReadonlySet<string>,
  recent: ReadonlyMap<number, RecentPaper> = new Map(),
): LibrarySection[] {
  const usage = (s: StrategyListItem) => {
    const r = recent.get(s.id)
    return r ? r.runs + r.alertRuns : 0
  }
  const rank = (members: StrategyListItem[]) => ({
    running: members.some((m) => m.activeRuns.length > 0) ? 1 : 0,
    usage: members.reduce((n, m) => n + usage(m), 0),
  })

  return SECTIONS.map(({ key, title, hint }) => {
    const inSection = strategies.filter((s) => sectionOf(s) === key)
    const families = new Map<string, StrategyListItem[]>()
    const items: LibraryItem[] = []
    for (const s of inSection) {
      const root = familyOf(s.name, roots)
      if (root) families.set(root, [...(families.get(root) ?? []), s])
      else items.push({ kind: 'single', key: `s-${s.id}`, strategy: s })
    }
    for (const [root, members] of families) {
      const sorted = [...members].sort(byName)
      items.push(
        sorted.length >= 2
          ? { kind: 'family', key: `f-${key}-${root}`, root, members: sorted }
          : { kind: 'single', key: `s-${sorted[0].id}`, strategy: sorted[0] },
      )
    }
    const membersOf = (item: LibraryItem) => (item.kind === 'single' ? [item.strategy] : item.members)
    const nameOf = (item: LibraryItem) => (item.kind === 'single' ? item.strategy.name : item.root)
    items.sort((a, b) => {
      const ra = rank(membersOf(a))
      const rb = rank(membersOf(b))
      return rb.running - ra.running || rb.usage - ra.usage || nameOf(a).localeCompare(nameOf(b), 'en', { numeric: true })
    })
    return { key, title, hint, count: inSection.length, items }
  }).filter((section) => section.items.length > 0)
}

/* ---------------------------------------------------------------- filters */

export interface LibraryFilters {
  q: string
  side: TradeSide | 'all'
  category: string
  instrument: InstrumentClass | 'all'
  chain: boolean
  running: boolean
}

export const NO_FILTERS: LibraryFilters = { q: '', side: 'all', category: 'all', instrument: 'all', chain: false, running: false }

export function filtersActive(f: LibraryFilters): boolean {
  return f.q.trim() !== '' || f.side !== 'all' || f.category !== 'all' || f.instrument !== 'all' || f.chain || f.running
}

/** Every search word must appear in the name, description, category, legs or an underlying. */
export function matchesSearch(s: StrategyListItem, q: string): boolean {
  const words = q.trim().toLowerCase().split(/\s+/).filter(Boolean)
  if (words.length === 0) return true
  const hay = [s.name, s.description, s.category, s.legsSummary, ...s.supportedUnderlyings].join(' ').toLowerCase()
  return words.every((w) => hay.includes(w))
}

export function matchesFilters(s: StrategyListItem, facts: SpecFacts | null | undefined, f: LibraryFilters): boolean {
  if (!matchesSearch(s, f.q)) return false
  if (f.side !== 'all' && tradeSide(s.legsSummary) !== f.side) return false
  if (f.category !== 'all' && (s.category ?? '').toLowerCase() !== f.category.toLowerCase()) return false
  if (f.instrument !== 'all' && instrumentClass(s) !== f.instrument) return false
  if (f.chain && !usesOptionChain(facts, s.dataRequirements)) return false
  if (f.running && s.activeRuns.length === 0) return false
  return true
}

/** The page's one-line summary counts. */
export function librarySummary(strategies: StrategyListItem[]) {
  const counts = { total: strategies.length, buy: 0, sell: 0, mixed: 0, none: 0, running: 0 }
  for (const s of strategies) {
    counts[tradeSide(s.legsSummary)] += 1
    if (s.activeRuns.length > 0) counts.running += 1
  }
  return counts
}
