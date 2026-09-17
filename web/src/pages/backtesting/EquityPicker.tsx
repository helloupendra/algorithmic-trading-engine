/**
 * The underlying picker for an equity strategy: the stocks that actually have
 * stored candles, because a stock with no history is not a choice — it is a
 * failed run waiting to happen.
 */

import { useMemo, useState } from 'react'
import { useBacktestEquities } from '../../lib/queries'
import { formatNumber } from '../../lib/format'
import { InlineError, Loading } from '../../components/ui'
import { IconSearch } from '../../components/icons'
import { resolutionLabel } from '../../lib/symbols'
import { formatDayRange } from './shared'

export function EquityPicker({
  value,
  onChange,
}: {
  value: string | null
  onChange: (underlying: string) => void
}) {
  const equities = useBacktestEquities(true)
  const [search, setSearch] = useState('')

  const shown = useMemo(() => {
    const needle = search.trim().toUpperCase()
    const rows = equities.data ?? []
    return needle ? rows.filter((r) => r.underlying.includes(needle)) : rows
  }, [equities.data, search])

  if (equities.isPending) return <Loading label="Looking for stocks with stored candles…" />
  if (equities.isError && !equities.data) return <InlineError error={equities.error} />

  if ((equities.data ?? []).length === 0) {
    return (
      <div className="alert alert--warn" role="status">
        <span>
          No stock candles are stored yet. Import them with{' '}
          <code>tools/equities_candles_import.py</code> before replaying a stock.
        </span>
      </div>
    )
  }

  return (
    <div className="equity-picker">
      <label className="bt-search">
        <IconSearch />
        <input
          className="field__input field__input--sm"
          type="search"
          placeholder="Search stocks"
          value={search}
          onChange={(e) => setSearch(e.target.value)}
          aria-label="Search stocks"
        />
      </label>
      <div className="equity-picker__list" role="radiogroup" aria-label="Stock">
        {shown.map((row) => {
          const active = row.underlying === value
          return (
            <button
              key={row.underlying}
              type="button"
              role="radio"
              aria-checked={active}
              className={`equity-picker__row ${active ? 'is-active' : ''}`}
              onClick={() => onChange(row.underlying)}
            >
              <b>{row.underlying}</b>
              <span className="faint">{formatDayRange(row.firstUtc, row.lastUtc)}</span>
              <span className="equity-picker__bars">
                {row.resolutions
                  .map((r) => `${resolutionLabel(r.resolution)} ${formatNumber(r.barCount)}`)
                  .join(' · ')}
              </span>
            </button>
          )
        })}
        {shown.length === 0 && <p className="faint">No stock matches “{search}”.</p>}
      </div>
    </div>
  )
}
