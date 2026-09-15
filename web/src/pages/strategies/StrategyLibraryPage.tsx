/**
 * Strategies module — Library. Scannable first, details on demand:
 *
 *   - one summary line, a search box and a few filters derived from the
 *     catalogue (side, category, instrument, option chain, running now);
 *   - the strategies in sections a trader reaches for in order (buying,
 *     selling, spreads & hedged, commodities, alerts & examples), a family of
 *     variants collapsed into one card;
 *   - a four-line card per strategy with "How it works" (the strategy's own
 *     page: summary, then the full specification) and "Start" (the launch
 *     dialog the Live runner uses).
 *
 * Filters live in the URL, so coming back from a strategy's page keeps them.
 * Labels and grouping are lib/strategyLibrary.ts, tested without a DOM.
 */

import { useCallback, useEffect, useMemo, useState } from 'react'
import { useNavigate, useSearchParams } from 'react-router-dom'
import { useLiveRunHistory, useStrategies } from '../../lib/queries'
import { useStrategySpecFacts } from '../../lib/specs'
import type { SpecFacts } from '../../lib/specs'
import {
  INSTRUMENT_LABEL,
  NO_FILTERS,
  RUN_WINDOW_DAYS,
  SIDE_LABEL,
  buildLibrary,
  familyRoots,
  filtersActive,
  instrumentClass,
  librarySummary,
  matchesFilters,
  recentPaperByStrategy,
  usesOptionChain,
} from '../../lib/strategyLibrary'
import type { InstrumentClass, LibraryFilters, TradeSide } from '../../lib/strategyLibrary'
import { QueryBoundary } from '../../components/ui'
import { IconSearch, IconX } from '../../components/icons'
import type { StrategyListItem } from '../../lib/types'
import { addDays, todayIst } from '../backtesting/shared'
import { LaunchDialog } from './shared'
import { FamilyCard, LibraryCard } from './LibraryCards'
import type { CardContext } from './LibraryCards'
import './library.css'

const EMPTY_FACTS: ReadonlyMap<number, SpecFacts> = new Map()
const SIDES: TradeSide[] = ['buy', 'sell', 'mixed']
const INSTRUMENTS: InstrumentClass[] = ['index', 'mcx', 'stocks', 'any']

function readFilters(params: URLSearchParams): LibraryFilters {
  const side = params.get('side')
  const instrument = params.get('instrument')
  return {
    q: params.get('q') ?? '',
    side: SIDES.includes(side as TradeSide) || side === 'none' ? (side as TradeSide) : 'all',
    category: params.get('category') || 'all',
    instrument: INSTRUMENTS.includes(instrument as InstrumentClass) ? (instrument as InstrumentClass) : 'all',
    chain: params.get('chain') === '1',
    running: params.get('running') === '1',
  }
}

function writeFilters(f: LibraryFilters): URLSearchParams {
  const p = new URLSearchParams()
  if (f.q) p.set('q', f.q)
  if (f.side !== 'all') p.set('side', f.side)
  if (f.category !== 'all') p.set('category', f.category)
  if (f.instrument !== 'all') p.set('instrument', f.instrument)
  if (f.chain) p.set('chain', '1')
  if (f.running) p.set('running', '1')
  return p
}

function Toggle({
  pressed,
  onClick,
  count,
  children,
}: {
  pressed: boolean
  onClick: () => void
  count: number
  children: string
}) {
  return (
    <button type="button" className={`lib-toggle${pressed ? ' is-on' : ''}`} aria-pressed={pressed} onClick={onClick}>
      {children} <span className="lib-toggle__count">{count}</span>
    </button>
  )
}

function Library({ items }: { items: StrategyListItem[] }) {
  const navigate = useNavigate()
  const [params, setParams] = useSearchParams()
  const filters = readFilters(params)
  const [launchId, setLaunchId] = useState<number | null>(null)

  // Spec facts feed the timeframe / data / exit chips and the chain filter;
  // the run history of the last week feeds the P&L line. Either may be
  // missing — the cards then leave those parts out.
  const factsQuery = useStrategySpecFacts()
  const facts = factsQuery.data ?? EMPTY_FACTS
  const fromDate = useMemo(() => addDays(todayIst(), -(RUN_WINDOW_DAYS - 1)), [])
  const history = useLiveRunHistory({ fromDate, take: 500 })
  const recent = useMemo(() => {
    const rows = history.data
    // A full page may be cut short; a partial sum would read as the truth.
    if (!rows || rows.length >= 500) return null
    return recentPaperByStrategy(rows)
  }, [history.data])

  const setFilters = useCallback(
    (next: Partial<LibraryFilters>) => {
      const merged = { ...readFilters(params), ...next }
      const out = writeFilters(merged)
      setParams(out, { replace: true })
    },
    [params, setParams],
  )

  // Read the strategy from the polled list so the dialog's "already running"
  // rows track runs started elsewhere while it is open.
  const launch = launchId != null ? (items.find((s) => s.id === launchId) ?? null) : null

  const roots = useMemo(() => familyRoots(items.map((s) => s.name)), [items])
  const summary = librarySummary(items)
  const active = filtersActive(filters)
  const visible = items.filter((s) => matchesFilters(s, facts.get(s.id), filters))
  const sections = buildLibrary(visible, roots, recent ?? undefined)

  // Counts beside each filter: how many strategies it would leave, given the
  // other filters — so a choice that empties the page is visible before it is made.
  const countWith = (next: Partial<LibraryFilters>) =>
    items.filter((s) => matchesFilters(s, facts.get(s.id), { ...filters, ...next })).length
  const categories = [...new Set(items.map((s) => s.category).filter(Boolean))].sort()
  const instruments = INSTRUMENTS.filter((k) => items.some((s) => instrumentClass(s) === k))
  const chainTotal = items.filter((s) => usesOptionChain(facts.get(s.id), s.dataRequirements)).length

  const ctx: CardContext = {
    facts,
    recent,
    recentDays: RUN_WINDOW_DAYS,
    onStart: (s) => setLaunchId(s.id),
  }

  return (
    <>
      <div className="lib-toolbar">
        <label className="lib-search">
          <IconSearch aria-hidden="true" />
          <input
            type="search"
            className="lib-search__input"
            placeholder="Search name, legs, underlying"
            value={filters.q}
            onChange={(e) => setFilters({ q: e.target.value })}
            aria-label="Search strategies"
          />
        </label>

        <div className="seg" role="group" aria-label="Side">
          <button type="button" className={`seg__btn${filters.side === 'all' ? ' is-active' : ''}`} aria-pressed={filters.side === 'all'} onClick={() => setFilters({ side: 'all' })}>
            All
          </button>
          {SIDES.map((side) => (
            <button
              key={side}
              type="button"
              className={`seg__btn${filters.side === side ? ' is-active' : ''}`}
              aria-pressed={filters.side === side}
              onClick={() => setFilters({ side: filters.side === side ? 'all' : side })}
            >
              {SIDE_LABEL[side]} <span className="lib-seg-count">{countWith({ side })}</span>
            </button>
          ))}
        </div>

        {instruments.length > 1 && (
          <div className="seg" role="group" aria-label="Instrument">
            <button type="button" className={`seg__btn${filters.instrument === 'all' ? ' is-active' : ''}`} aria-pressed={filters.instrument === 'all'} onClick={() => setFilters({ instrument: 'all' })}>
              Any market
            </button>
            {instruments.map((k) => (
              <button
                key={k}
                type="button"
                className={`seg__btn${filters.instrument === k ? ' is-active' : ''}`}
                aria-pressed={filters.instrument === k}
                onClick={() => setFilters({ instrument: filters.instrument === k ? 'all' : k })}
              >
                {INSTRUMENT_LABEL[k]} <span className="lib-seg-count">{countWith({ instrument: k })}</span>
              </button>
            ))}
          </div>
        )}

        <select
          className="field__input field__input--sm lib-select"
          value={filters.category}
          onChange={(e) => setFilters({ category: e.target.value })}
          aria-label="Category"
        >
          <option value="all">All categories</option>
          {categories.map((c) => (
            <option key={c} value={c}>
              {c} ({countWith({ category: c })})
            </option>
          ))}
        </select>

        {chainTotal > 0 && (
          <Toggle pressed={filters.chain} onClick={() => setFilters({ chain: !filters.chain })} count={countWith({ chain: true })}>
            Uses option chain
          </Toggle>
        )}
        {/* Hidden while nothing runs: the summary line already says so. */}
        {(summary.running > 0 || filters.running) && (
          <Toggle pressed={filters.running} onClick={() => setFilters({ running: !filters.running })} count={countWith({ running: true })}>
            Running now
          </Toggle>
        )}

        {active && (
          <button type="button" className="btn btn--sm btn--ghost" onClick={() => setParams(writeFilters(NO_FILTERS), { replace: true })}>
            <IconX style={{ width: 12, height: 12 }} /> Clear
          </button>
        )}
      </div>

      {active && (
        <p className="lib-count" role="status">
          Showing {visible.length} of {items.length}
        </p>
      )}

      {sections.length === 0 ? (
        <p className="empty">No strategy matches these filters.</p>
      ) : (
        sections.map((section) => (
          <section key={section.key} className="lib-section" aria-labelledby={`lib-${section.key}`}>
            <h2 className="lib-section__title" id={`lib-${section.key}`}>
              {section.title}
              <span className="lib-section__count">{section.count}</span>
              <span className="lib-section__hint">{section.hint}</span>
            </h2>
            <div className="lib-grid">
              {section.items.map((item) =>
                item.kind === 'single' ? (
                  <LibraryCard key={item.key} strategy={item.strategy} ctx={ctx} />
                ) : (
                  <FamilyCard key={item.key} root={item.root} members={item.members} ctx={ctx} forceOpen={active} />
                ),
              )}
            </div>
          </section>
        ))
      )}

      {factsQuery.isError && (
        <p className="small-note">Spec facts could not be loaded — timeframe, data and exit chips are left out.</p>
      )}

      {launch && (
        <LaunchDialog strategy={launch} onClose={() => setLaunchId(null)} onStarted={() => navigate('/admin/strategies/live')} />
      )}
    </>
  )
}

/** "24 strategies · 10 buy options · 4 sell options · 10 buy + sell · none running now". */
function SummaryLine({ items }: { items: StrategyListItem[] }) {
  const summary = librarySummary(items)
  const parts = [
    `${summary.total} strategies`,
    `${summary.buy} buy options`,
    `${summary.sell} sell options`,
    `${summary.mixed} buy + sell`,
    ...(summary.none > 0 ? [`${summary.none} ${summary.none === 1 ? 'alerter' : 'alerters'}`] : []),
  ]
  return (
    <p className="page__subtitle lib-summary">
      {parts.join(' · ')} ·{' '}
      <span className={summary.running > 0 ? 'lib-summary__live' : undefined}>
        {summary.running > 0 ? `${summary.running} running now` : 'none running now'}
      </span>
    </p>
  )
}

export function StrategyLibraryPage() {
  const strategies = useStrategies()
  const navigate = useNavigate()

  // ?strategy=<catalog id> is how older whiteboard cards link here; the spec
  // now has a page of its own, so the link is forwarded there.
  const [params] = useSearchParams()
  useEffect(() => {
    const wanted = Number(params.get('strategy'))
    if (Number.isInteger(wanted) && wanted > 0) navigate(`/admin/strategies/library/${wanted}`, { replace: true })
  }, [params, navigate])

  return (
    <div className="page lib-page">
      <header className="page__header lib-header">
        <div>
          <h1 className="page__title">Strategy library</h1>
          {strategies.data && strategies.data.length > 0 && <SummaryLine items={strategies.data} />}
        </div>
      </header>

      <QueryBoundary query={strategies} empty="No strategies found in the Python engine.">
        {(items) => <Library items={items} />}
      </QueryBoundary>
    </div>
  )
}
