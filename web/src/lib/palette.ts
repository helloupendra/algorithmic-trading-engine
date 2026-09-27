/**
 * The ⌘K palette as data: what matches a query, in what order, and where each
 * result goes. The component only renders these and moves the highlight, so
 * every rule here is pinned by palette.test.ts rather than by a screenshot.
 *
 * Four kinds of result: pages (from the workspace registry, so a page the
 * user cannot see is never offered), symbols (the instrument master), the
 * strategy catalogue, and today's runs.
 */

import type { NavWorkspace, Side } from './modules'
import type { Instrument, LiveRunSummary, StrategyListItem } from './types'
import { UNDERLYINGS } from './optionChain'

export type PaletteKind = 'page' | 'symbol' | 'strategy' | 'run'

export interface PaletteItem {
  /** Unique across the whole list: it becomes the option's DOM id. */
  id: string
  kind: PaletteKind
  label: string
  /** Quieter text after the label: where the page lives, what the symbol is. */
  detail?: string
  /** Right-aligned: exchange, P&L, "2 live". */
  meta?: string
  tone?: 'pos' | 'neg' | 'live'
  to: string
}

export interface PaletteGroup {
  kind: PaletteKind
  label: string
  items: PaletteItem[]
}

/** Symbols are only looked up from this many characters: one letter matches half the market. */
export const SYMBOL_MIN_CHARS = 2

const norm = (s: string) => s.toLowerCase().normalize('NFKD').replace(/[̀-ͯ]/g, '')

/**
 * How well `query` matches: 0 is no match. Every word of the query must be
 * found somewhere; a hit at the start of the label counts most, then at the
 * start of a word in it, then anywhere in it, then in the extra words
 * (workspace, keywords, exchange). An empty query matches everything equally.
 */
export function matchScore(query: string, label: string, extra: readonly string[] = []): number {
  const words = norm(query).split(/\s+/).filter(Boolean)
  if (words.length === 0) return 1
  const l = norm(label)
  const lWords = l.split(/[\s·&/:,()-]+/)
  const x = extra.map(norm)
  let total = 0
  for (const w of words) {
    const score = l.startsWith(w)
      ? 100
      : lWords.some((lw) => lw.startsWith(w))
        ? 60
        : l.includes(w)
          ? 40
          : x.some((e) => e.startsWith(w) || e.split(/\s+/).some((ew) => ew.startsWith(w)))
            ? 30
            : x.some((e) => e.includes(w))
              ? 20
              : 0
    if (score === 0) return 0
    total += score
  }
  return total
}

/** The best matches first; equal scores keep their given order. */
function ranked<T>(rows: readonly T[], score: (row: T) => number, limit: number): T[] {
  return rows
    .map((row, i) => ({ row, i, s: score(row) }))
    .filter((r) => r.s > 0)
    .sort((a, b) => b.s - a.s || a.i - b.i)
    .slice(0, limit)
    .map((r) => r.row)
}

/** Pages the user can open, plus any outside the workspaces (the account page). */
export function pageItems(
  nav: readonly NavWorkspace[],
  query: string,
  extras: ReadonlyArray<{ label: string; to: string; keywords?: readonly string[] }> = [],
  limit = 50,
): PaletteItem[] {
  const pages = nav.flatMap((ws) =>
    ws.pages.map((p) => ({
      label: p.label,
      to: p.to,
      // Where it lives, by the name on the top bar; the Desk needs no such note.
      detail: p.label === ws.label ? undefined : ws.label,
      words: [ws.label, p.tab.label, ...p.keywords],
    })),
  )
  const all = [...pages, ...extras.map((e) => ({ label: e.label, to: e.to, detail: undefined, words: [...(e.keywords ?? [])] }))]
  const items: PaletteItem[] = ranked(all, (p) => matchScore(query, p.label, p.words), limit).map((p) => ({
    id: `page:${p.to}`,
    kind: 'page',
    label: p.label,
    detail: p.detail,
    to: p.to,
  }))

  // "nifty" also offers NIFTY's chain directly, when the chain is a page this user has.
  // The chain tab's first page is the chain itself (Open interest follows it).
  const chain = nav.flatMap((ws) => ws.pages).find((p) => p.tab.key === 'chain')
  const q = norm(query).trim()
  if (chain && q.length >= SYMBOL_MIN_CHARS) {
    const chains = UNDERLYINGS.filter((u) => norm(u).startsWith(q.replace(/\s+/g, '')))
      .slice(0, 3)
      .map<PaletteItem>((u) => ({ id: `chain:${u}`, kind: 'page', label: `${u} option chain`, detail: 'Markets', to: `${chain.to}?u=${u}` }))
    items.unshift(...chains)
  }
  return items
}

/**
 * Where a symbol opens: the Markets chart, the same for every role. (Symbols
 * are only searched for a user with the market data grant.)
 */
export function symbolUrl(symbol: string): string {
  return `/markets/chart?symbol=${encodeURIComponent(symbol)}`
}

export function symbolItems(instruments: readonly Instrument[] | undefined, limit = 8): PaletteItem[] {
  return (instruments ?? []).slice(0, limit).map((i) => ({
    id: `symbol:${i.id}:${i.symbol}`,
    kind: 'symbol',
    label: i.symbol,
    detail: i.description || undefined,
    meta: [i.exchange, i.instrumentType].filter(Boolean).join(' · '),
    to: symbolUrl(i.symbol),
  }))
}

export function strategyItems(strategies: readonly StrategyListItem[] | undefined, query: string, limit = 5): PaletteItem[] {
  if (!query.trim()) return []
  // Name and category only: nearly every strategy trades NIFTY, so matching the
  // underlyings would answer "nifty" with the whole catalogue.
  return ranked(strategies ?? [], (s) => matchScore(query, s.name, [s.category]), limit).map((s) => {
    const live = s.activeRuns?.length ?? 0
    return {
      id: `strategy:${s.id}`,
      kind: 'strategy',
      label: s.name,
      detail: s.category || undefined,
      meta: live > 0 ? `${live} live` : undefined,
      tone: live > 0 ? 'live' : undefined,
      to: `/trade/library/${s.id}`,
    }
  })
}

const MINUS = '−'

/** "+₹1,240", "−₹17,374", "₹0": the net the run page leads with. */
export function signedRupees(value: number): string {
  const whole = Math.round(value)
  const sign = whole > 0 ? '+' : whole < 0 ? MINUS : ''
  return `${sign}₹${Math.abs(whole).toLocaleString('en-IN')}`
}

/**
 * Today's runs that match: running ones first, then the rest newest first. A
 * run number matches too ("412"), since that is what a Telegram alert names.
 */
export function runItems(runs: readonly LiveRunSummary[] | undefined, side: Side, query: string, limit = 6): PaletteItem[] {
  if (!query.trim()) return []
  const ordered = [...(runs ?? [])].sort(
    (a, b) => Number(b.isActive) - Number(a.isActive) || (b.startedUtc ?? '').localeCompare(a.startedUtc ?? ''),
  )
  return ranked(
    ordered,
    (r) => matchScore(query, `${r.strategyName} ${r.underlying}`, [r.userName ?? '', String(r.runId), r.status]),
    limit,
  ).map((r) => ({
    id: `run:${r.runId}`,
    kind: 'run',
    label: `${r.strategyName} · ${r.underlying}`,
    detail: [side === 'admin' ? r.userName : null, r.isActive ? 'running' : r.status.toLowerCase(), `#${r.runId}`]
      .filter(Boolean)
      .join(' · '),
    meta: signedRupees(r.netPnl),
    tone: r.netPnl > 0 ? 'pos' : r.netPnl < 0 ? 'neg' : undefined,
    to: `/trade/runs/${r.runId}`,
  }))
}

const GROUP_LABEL: Record<PaletteKind, string> = { page: 'Go to', symbol: 'Symbols', strategy: 'Strategies', run: 'Runs today' }

/** The non-empty groups, in a fixed order so results do not jump between keystrokes. */
export function paletteGroups(parts: Partial<Record<PaletteKind, PaletteItem[]>>): PaletteGroup[] {
  return (['page', 'run', 'strategy', 'symbol'] as const)
    .map((kind) => ({ kind, label: GROUP_LABEL[kind], items: parts[kind] ?? [] }))
    .filter((g) => g.items.length > 0)
}

/** The highlight after an arrow key: wraps at both ends, and stays put in an empty list. */
export function moveActive(index: number, delta: number, count: number): number {
  if (count <= 0) return -1
  if (index < 0) return delta > 0 ? 0 : count - 1
  return (((index + delta) % count) + count) % count
}

/** ⌘K on a Mac, Ctrl-K elsewhere; either is accepted everywhere, and nothing else is. */
export function isPaletteShortcut(e: Pick<KeyboardEvent, 'key' | 'metaKey' | 'ctrlKey' | 'altKey' | 'shiftKey'>): boolean {
  return (e.metaKey || e.ctrlKey) && !e.altKey && !e.shiftKey && e.key.toLowerCase() === 'k'
}
