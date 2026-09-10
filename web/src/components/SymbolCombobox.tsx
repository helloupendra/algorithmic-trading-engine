/**
 * A symbol field that suggests real instruments while you type.
 *
 * The backfill form used to take free text, so "nifty50" was accepted, sent,
 * and refused by the broker as an unknown symbol — the vendor spelling
 * ("NSE:NIFTY50-INDEX") is not something anyone should have to remember. The
 * suggestions come from the local instrument master, so anything picked here
 * is known to exist before the request is made.
 *
 * Typing is still allowed: an instrument imported after the last master sync
 * would otherwise be unreachable.
 */

import { useEffect, useRef, useState } from 'react'

import { useInstrumentSearch } from '../lib/queries'

export function SymbolCombobox({
  value,
  onChange,
  id,
  placeholder = 'NSE:SBIN-EQ',
  disabled = false,
  includeExpired = false,
}: {
  value: string
  onChange: (symbol: string) => void
  id: string
  placeholder?: string
  disabled?: boolean
  /** Offer contracts whose expiry has passed. Only historical work wants them. */
  includeExpired?: boolean
}) {
  const [open, setOpen] = useState(false)
  const [highlight, setHighlight] = useState(0)
  const boxRef = useRef<HTMLDivElement>(null)

  const search = useInstrumentSearch(value, undefined, includeExpired)
  const results = (search.data ?? []).slice(0, 12)

  // A click anywhere else is a dismissal; without this the list stays open
  // over whatever the page shows next.
  useEffect(() => {
    if (!open) return
    function onDocClick(e: MouseEvent) {
      if (!boxRef.current?.contains(e.target as Node)) setOpen(false)
    }
    document.addEventListener('mousedown', onDocClick)
    return () => document.removeEventListener('mousedown', onDocClick)
  }, [open])

  useEffect(() => setHighlight(0), [value])

  function choose(symbol: string) {
    onChange(symbol)
    setOpen(false)
  }

  function onKeyDown(e: React.KeyboardEvent<HTMLInputElement>) {
    if (!open || results.length === 0) return
    if (e.key === 'ArrowDown') {
      e.preventDefault()
      setHighlight((h) => (h + 1) % results.length)
    } else if (e.key === 'ArrowUp') {
      e.preventDefault()
      setHighlight((h) => (h - 1 + results.length) % results.length)
    } else if (e.key === 'Enter') {
      // Only when a suggestion is genuinely highlighted, so Enter still submits
      // the form for someone who typed a full symbol.
      e.preventDefault()
      choose(results[highlight].symbol)
    } else if (e.key === 'Escape') {
      setOpen(false)
    }
  }

  const showList = open && value.trim().length >= 2

  return (
    <div className="combo" ref={boxRef}>
      <input
        id={id}
        className="field__input"
        value={value}
        disabled={disabled}
        placeholder={placeholder}
        autoComplete="off"
        role="combobox"
        aria-expanded={showList}
        aria-controls={`${id}-list`}
        onChange={(e) => {
          onChange(e.target.value)
          setOpen(true)
        }}
        onFocus={() => setOpen(true)}
        onKeyDown={onKeyDown}
      />

      {showList && (
        <ul className="combo__list" id={`${id}-list`} role="listbox">
          {results.map((row, i) => (
            <li key={row.id} role="option" aria-selected={i === highlight}>
              <button
                type="button"
                className={`combo__item ${i === highlight ? 'is-active' : ''}`}
                // mousedown, not click: the input's blur would close the list
                // before a click ever landed.
                onMouseDown={(e) => {
                  e.preventDefault()
                  choose(row.symbol)
                }}
                onMouseEnter={() => setHighlight(i)}
              >
                <b>{row.symbol}</b>
                <span>{row.description || row.exchange}</span>
              </button>
            </li>
          ))}

          {results.length === 0 && (
            <li className="combo__empty">
              {search.isFetching ? 'Searching…' : 'No instrument matches — check the spelling, or type the full symbol.'}
            </li>
          )}
        </ul>
      )}
    </div>
  )
}
