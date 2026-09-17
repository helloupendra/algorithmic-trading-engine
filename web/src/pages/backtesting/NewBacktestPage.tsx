/**
 * Backtesting module — New backtest, in two steps: pick a strategy, then set the
 * run up on the same page.
 *
 * The configurator is the launch dialog's own form, rendered inline rather than
 * in a modal: a run now carries windows, limits, extra exits, contract choice
 * and costs besides the dates and lots, and that is more than a dialog can hold
 * without becoming a scroll. What history exists is shown where it is chosen —
 * under the underlying and the resolution — instead of as a strip of every
 * stored index at the top of the page, which said nothing about the run being
 * set up.
 */

import { useMemo, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { useStrategies } from '../../lib/queries'
import { QueryBoundary } from '../../components/ui'
import { IconLayers, IconSearch } from '../../components/icons'
import type { StrategyListItem } from '../../lib/types'
import { StrategyCard } from '../strategies/shared'
import { BacktestConfigurator } from './BacktestDialog'
import './backtesting.css'

export function NewBacktestPage() {
  const strategies = useStrategies()
  const navigate = useNavigate()
  const [chosen, setChosen] = useState<StrategyListItem | null>(null)
  const [search, setSearch] = useState('')
  const [category, setCategory] = useState<string | null>(null)

  const categories = useMemo(() => {
    const seen = new Map<string, number>()
    for (const s of strategies.data ?? []) {
      const key = s.category || 'Other'
      seen.set(key, (seen.get(key) ?? 0) + 1)
    }
    return [...seen.entries()].sort((a, b) => a[0].localeCompare(b[0]))
  }, [strategies.data])

  const shown = useMemo(() => {
    const needle = search.trim().toLowerCase()
    return (strategies.data ?? []).filter((s) => {
      if (category && (s.category || 'Other') !== category) return false
      if (!needle) return true
      return (
        s.name.toLowerCase().includes(needle) ||
        (s.description ?? '').toLowerCase().includes(needle) ||
        s.supportedUnderlyings.some((u) => u.toLowerCase().includes(needle))
      )
    })
  }, [strategies.data, search, category])

  return (
    <div className="page">
      <header className="page__header">
        <div>
          <h1 className="page__title">New backtest</h1>
          <p className="page__subtitle">
            {chosen
              ? 'Set the market, the period and the rules this run should trade by. Everything here applies to the replay only.'
              : 'Pick the strategy to replay. The next step shows what history exists for it and how the run may trade.'}
          </p>
        </div>
      </header>

      {chosen ? (
        <BacktestConfigurator
          inline
          strategy={chosen}
          onClose={() => setChosen(null)}
          onStarted={(response) => navigate(`/admin/backtesting/runs/${response.runId}`)}
        />
      ) : (
        <section aria-labelledby="pick-a-strategy">
          <div className="toolbar">
            <h2 className="section-title" id="pick-a-strategy" style={{ margin: 0 }}>
              <IconLayers /> Pick a strategy
            </h2>
            <div className="bt-spacer" />
            <label className="bt-search">
              <IconSearch />
              <input
                className="field__input field__input--sm"
                type="search"
                placeholder="Search name, description or underlying"
                value={search}
                onChange={(e) => setSearch(e.target.value)}
                aria-label="Search strategies"
              />
            </label>
          </div>

          {categories.length > 1 && (
            <div className="seg seg--wrap" role="group" aria-label="Category">
              <button
                type="button"
                className={`seg__btn ${category === null ? 'is-active' : ''}`}
                onClick={() => setCategory(null)}
              >
                All {strategies.data ? `· ${strategies.data.length}` : ''}
              </button>
              {categories.map(([name, count]) => (
                <button
                  key={name}
                  type="button"
                  className={`seg__btn ${category === name ? 'is-active' : ''}`}
                  onClick={() => setCategory(category === name ? null : name)}
                >
                  {name} · {count}
                </button>
              ))}
            </div>
          )}

          <QueryBoundary query={strategies} empty="No strategies found in the Python engine.">
            {() =>
              shown.length === 0 ? (
                <p className="faint" role="status">
                  No strategy matches “{search}”.
                </p>
              ) : (
                <div className="strategy-grid">
                  {shown.map((s) => (
                    <StrategyCard
                      key={s.id}
                      strategy={s}
                      onStart={setChosen}
                      actionLabel="Set up backtest…"
                      allowWhileActive
                    />
                  ))}
                </div>
              )
            }
          </QueryBoundary>
        </section>
      )}
    </div>
  )
}
