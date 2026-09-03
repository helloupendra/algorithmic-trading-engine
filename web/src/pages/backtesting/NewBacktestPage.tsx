/**
 * Backtesting module — New backtest. A strip of what index history exists,
 * then the strategy catalogue as cards; "Backtest…" opens the dialog, and a
 * successful start lands on the run page.
 */

import { useMemo, useState } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import { useDataCoverage, useStrategies } from '../../lib/queries'
import { formatNumber, shortSymbol } from '../../lib/format'
import { classifySymbol, resolutionLabel, resolutionRank } from '../../lib/symbols'
import { Badge, InlineError, QueryBoundary } from '../../components/ui'
import { IconDatabase, IconLayers } from '../../components/icons'
import type { StrategyListItem } from '../../lib/types'
import { StrategyCard } from '../strategies/shared'
import { BacktestDialog } from './BacktestDialog'
import { estimateSessions, formatDayRange } from './shared'

export function NewBacktestPage() {
  const strategies = useStrategies()
  const coverage = useDataCoverage()
  const navigate = useNavigate()
  const [launch, setLaunch] = useState<StrategyListItem | null>(null)

  const indexRows = useMemo(
    () =>
      (coverage.data ?? [])
        .filter((r) => classifySymbol(r.symbol) === 'Index')
        .sort(
          (a, b) =>
            a.symbol.localeCompare(b.symbol) || resolutionRank(a.resolution) - resolutionRank(b.resolution),
        ),
    [coverage.data],
  )

  return (
    <div className="page">
      <header className="page__header">
        <div>
          <h1 className="page__title">New backtest</h1>
          <p className="page__subtitle">
            Pick a strategy, then the underlying, resolution and dates from the history that is
            actually stored. Lots, stop-loss and target work exactly as on the live runner.
          </p>
        </div>
      </header>

      <section aria-labelledby="data-on-hand">
        <h2 className="section-title" id="data-on-hand">
          <IconDatabase /> Index history on hand
        </h2>
        {coverage.isError && !coverage.data ? (
          <InlineError error={coverage.error} />
        ) : coverage.data && indexRows.length === 0 ? (
          <div className="alert alert--warn" role="status">
            <span>
              No index candles are stored yet — nothing can be replayed until one is backfilled on the{' '}
              <Link to="/admin/backtesting">Backtesting overview</Link>.
            </span>
          </div>
        ) : (
          <div className="coverage-strip">
            {indexRows.map((r) => (
              <span key={`${r.symbol}|${r.resolution}|${r.source}`} className="coverage-chip" title={r.symbol}>
                <b>{shortSymbol(r.symbol).replace(/-INDEX$/i, '')}</b>
                <Badge tone="neutral">{resolutionLabel(r.resolution)}</Badge>
                ≈ {formatNumber(estimateSessions(r.resolution, r.barCount))} sessions
                <span className="faint">{formatDayRange(r.fromUtc, r.toUtc)}</span>
                {r.source === 'live' && <Badge tone="live">live</Badge>}
              </span>
            ))}
            {!coverage.data && <span className="faint">Loading stored ranges…</span>}
          </div>
        )}
      </section>

      <section aria-labelledby="pick-a-strategy">
        <h2 className="section-title" id="pick-a-strategy">
          <IconLayers /> Pick a strategy
        </h2>
        <QueryBoundary query={strategies} empty="No strategies found in the Python engine.">
          {(items) => (
            <div className="strategy-grid">
              {items.map((s) => (
                <StrategyCard
                  key={s.id}
                  strategy={s}
                  onStart={setLaunch}
                  actionLabel="Backtest…"
                  allowWhileActive
                />
              ))}
            </div>
          )}
        </QueryBoundary>
      </section>

      {launch && (
        <BacktestDialog
          strategy={launch}
          onClose={() => setLaunch(null)}
          onStarted={(response) => navigate(`/admin/backtesting/runs/${response.runId}`)}
        />
      )}
    </div>
  )
}
