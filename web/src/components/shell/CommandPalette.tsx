/**
 * ⌘K: one field that reaches every page, symbol, strategy and today's run, so
 * nobody has to remember which workspace something lives in.
 *
 * Keyboard first. The field is an ARIA combobox that owns the list: arrows
 * move the highlight (which wraps), Enter opens it, Escape closes, and focus
 * never leaves the field, so there is nothing to tab through. Mounted only
 * while open, so its searches cost nothing the rest of the time.
 */

import { useEffect, useMemo, useRef, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { allows } from '../../lib/modules'
import type { Access, NavWorkspace } from '../../lib/modules'
import { useInstrumentSearch, useLiveRunHistory, useStrategies } from '../../lib/queries'
import {
  SYMBOL_MIN_CHARS,
  moveActive,
  pageItems,
  paletteGroups,
  runItems,
  strategyItems,
  symbolItems,
} from '../../lib/palette'
import type { PaletteItem, Side } from '../../lib/palette'
import { istDate } from '../../lib/factors'
import { IconSearch } from '../icons'

/** The query as it was a moment ago: one symbol search per pause, not per keystroke. */
function useSettled(value: string, ms: number): string {
  const [settled, setSettled] = useState(value)
  useEffect(() => {
    const id = window.setTimeout(() => setSettled(value), ms)
    return () => window.clearTimeout(id)
  }, [value, ms])
  return settled
}

export function CommandPalette({
  nav,
  access,
  extras,
  onClose,
}: {
  nav: readonly NavWorkspace[]
  access: Access
  /** Pages outside the workspaces that this user has (the trader's account). */
  extras: ReadonlyArray<{ label: string; to: string; keywords?: readonly string[] }>
  onClose: () => void
}) {
  const navigate = useNavigate()
  const [query, setQuery] = useState('')
  const [active, setActive] = useState(0)
  const inputRef = useRef<HTMLInputElement>(null)
  const side: Side = access.isAdmin ? 'admin' : 'trader'

  // Each source only for a user its API would answer: a search that can only
  // come back 403 is not made at all.
  const markets = allows(access, 'market-data')
  const trading = allows(access, 'strategies')
  const settled = useSettled(query.trim(), 150)
  const symbols = useInstrumentSearch(markets ? settled : '')
  const strategies = useStrategies({ enabled: trading })
  // The same filters as the Strategies overview, so the two share one cached list.
  const today = istDate()
  const runs = useLiveRunHistory({ fromDate: today, toDate: today, take: 500 }, trading)

  const groups = useMemo(
    () =>
      paletteGroups({
        page: pageItems(nav, query, extras),
        run: runItems(runs.data, side, query),
        strategy: strategyItems(strategies.data, side, query),
        // Only the answer for what is in the field now, never the last query's.
        symbol: query.trim().length >= SYMBOL_MIN_CHARS && settled === query.trim() ? symbolItems(symbols.data, side) : [],
      }),
    [nav, query, extras, runs.data, side, strategies.data, settled, symbols.data],
  )
  const flat = useMemo(() => groups.flatMap((g) => g.items), [groups])
  // Where each group starts in the flat list, which the option ids count through.
  const starts = groups.map((_, gi) => groups.slice(0, gi).reduce((n, g) => n + g.items.length, 0))

  // A new query starts from its best match.
  useEffect(() => setActive(0), [query])

  // The highlight stays in view as the arrows walk past the edge of the list.
  useEffect(() => {
    document.getElementById(`palette-opt-${active}`)?.scrollIntoView({ block: 'nearest' })
  }, [active])

  // Back to whatever had focus before, and the page behind stays put meanwhile.
  useEffect(() => {
    const previous = document.activeElement as HTMLElement | null
    const overflow = document.body.style.overflow
    document.body.style.overflow = 'hidden'
    inputRef.current?.focus()
    return () => {
      document.body.style.overflow = overflow
      previous?.focus?.()
    }
  }, [])

  function open(item: PaletteItem | undefined) {
    if (!item) return
    onClose()
    navigate(item.to)
  }

  function onKeyDown(e: React.KeyboardEvent<HTMLInputElement>) {
    if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
      e.preventDefault()
      setActive((i) => moveActive(i, e.key === 'ArrowDown' ? 1 : -1, flat.length))
    } else if (e.key === 'Enter') {
      e.preventDefault()
      open(flat[active])
    } else if (e.key === 'Escape') {
      e.preventDefault()
      onClose()
    } else if (e.key === 'Tab') {
      // Nothing else in the dialog takes focus; Tab must not walk out of it.
      e.preventDefault()
    }
  }

  const searching = markets && query.trim().length >= SYMBOL_MIN_CHARS && (settled !== query.trim() || symbols.isFetching)

  return (
    <div className="palette" onMouseDown={(e) => e.target === e.currentTarget && onClose()}>
      <div className="palette__card" role="dialog" aria-modal="true" aria-label="Search the console">
        <div className="palette__field">
          <IconSearch aria-hidden="true" />
          <input
            ref={inputRef}
            className="palette__input"
            role="combobox"
            aria-expanded={flat.length > 0}
            aria-controls="palette-list"
            aria-autocomplete="list"
            aria-activedescendant={flat[active] ? `palette-opt-${active}` : undefined}
            placeholder={markets ? 'Search symbols, runs, strategies and pages' : 'Search runs, strategies and pages'}
            value={query}
            onChange={(e) => setQuery(e.target.value)}
            onKeyDown={onKeyDown}
            spellCheck={false}
            autoComplete="off"
          />
          <kbd>esc</kbd>
        </div>
        <div className="palette__list" id="palette-list" role="listbox" aria-label="Results">
          {groups.map((g, gi) => (
            <div key={g.kind} role="group" aria-labelledby={`palette-group-${g.kind}`}>
              <div className="palette__group-label" id={`palette-group-${g.kind}`} role="presentation">
                {g.label}
              </div>
              {g.items.map((item, n) => {
                const i = starts[gi] + n
                return (
                  <div
                    key={item.id}
                    id={`palette-opt-${i}`}
                    className="palette__option"
                    role="option"
                    aria-selected={i === active}
                    // Move, not enter: a list scrolling under a still pointer must not steal the highlight.
                    onMouseMove={() => i !== active && setActive(i)}
                    onClick={() => open(item)}
                  >
                    <span className="palette__label">
                      {item.label}
                      {item.detail && <span className="palette__detail">{item.detail}</span>}
                    </span>
                    {item.meta && <span className={`palette__meta ${item.tone ? `palette__meta--${item.tone}` : ''}`}>{item.meta}</span>}
                  </div>
                )
              })}
            </div>
          ))}
          {flat.length === 0 && (
            <div className="palette__empty" role="presentation">
              {searching ? 'Searching…' : `Nothing matches “${query.trim()}”.`}
            </div>
          )}
        </div>
        <div className="palette__foot" aria-hidden="true">
          <span>
            <kbd>↑</kbd>
            <kbd>↓</kbd> move
          </span>
          <span>
            <kbd>↵</kbd> open
          </span>
          <span>{searching ? 'Searching symbols…' : ''}</span>
        </div>
      </div>
    </div>
  )
}
