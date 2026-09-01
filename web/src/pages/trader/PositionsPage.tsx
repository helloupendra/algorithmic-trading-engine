/**
 * Positions for a chosen simulation run: portfolio summary tiles above the
 * position book, with an on-demand mark-to-market refresh.
 */

import { useState } from 'react'
import {
  useRefreshPortfolio,
  useRunPortfolio,
  useRunPositions,
} from '../../lib/queries'
import {
  formatDateTime,
  formatInr,
  formatInrWhole,
  formatPercent,
  formatPrice,
  pnlClass,
  shortSymbol,
} from '../../lib/format'
import { Badge, Panel, QueryBoundary, StatTile } from '../../components/ui'
import { RunPicker, useDefaultRunId } from './RunPicker'

export function PositionsPage() {
  const [runId, setRunId] = useState<number | null>(null)
  useDefaultRunId(runId, setRunId)

  const portfolio = useRunPortfolio(runId)
  const positions = useRunPositions(runId)
  const refresh = useRefreshPortfolio()

  return (
    <div className="page">
      <header className="page__header">
        <h1 className="page__title">Positions</h1>
        <p className="page__subtitle">Paper position book for a simulation run.</p>
      </header>

      <div className="toolbar">
        <RunPicker value={runId} onChange={setRunId} />
        <button
          type="button"
          className="btn btn--sm"
          disabled={runId == null || refresh.isPending}
          onClick={() => runId != null && refresh.mutate(runId)}
        >
          {refresh.isPending ? 'Refreshing…' : 'Refresh MTM'}
        </button>
      </div>

      {portfolio.data && (
        <div className="stat-grid">
          <StatTile
            label="Equity"
            value={formatInrWhole(portfolio.data.currentEquity)}
            sub={`initial ${formatInrWhole(portfolio.data.initialCapital)}`}
          />
          <StatTile
            label="Total P&L"
            value={formatInr(portfolio.data.totalPnl)}
            tone={portfolio.data.totalPnl >= 0 ? 'pos' : 'neg'}
            sub={formatPercent(portfolio.data.returnPercent)}
          />
          <StatTile
            label="Realized / Unrealized"
            value={`${formatInr(portfolio.data.realizedPnl)}`}
            sub={`unrealized ${formatInr(portfolio.data.unrealizedPnl)}`}
          />
          <StatTile
            label="Capital in use"
            value={formatInrWhole(portfolio.data.usedCapital)}
            sub={`free ${formatInrWhole(portfolio.data.availableCapital)}`}
          />
          <StatTile
            label="Positions"
            value={`${portfolio.data.openPositions} open`}
            sub={`${portfolio.data.closedPositions} closed`}
          />
        </div>
      )}

      <Panel title="Position book">
        <QueryBoundary query={positions} empty="This run has no positions.">
          {(data) => (
            <div className="tablewrap">
              <table className="table">
                <thead>
                  <tr>
                    <th>Symbol</th>
                    <th>Group</th>
                    <th>Dir</th>
                    <th className="r">Qty</th>
                    <th className="r">Avg price</th>
                    <th className="r">Mark</th>
                    <th className="r">Realized</th>
                    <th className="r">Unrealized</th>
                    <th>Status</th>
                    <th className="r">Opened</th>
                  </tr>
                </thead>
                <tbody>
                  {data.map((p) => (
                    <tr key={p.id}>
                      <td className="mono">{shortSymbol(p.symbol)}</td>
                      <td className="muted mono">{p.groupId || '—'}</td>
                      <td>
                        <Badge tone={p.direction === 'Long' ? 'pos' : 'neg'}>{p.direction}</Badge>
                      </td>
                      <td className="r mono">{p.quantity}</td>
                      <td className="r mono">{formatPrice(p.averagePrice)}</td>
                      <td className="r mono">{formatPrice(p.lastMarkPrice)}</td>
                      <td className={`r mono ${pnlClass(p.realizedPnl)}`}>{formatInr(p.realizedPnl)}</td>
                      <td className={`r mono ${pnlClass(p.unrealizedPnl)}`}>{formatInr(p.unrealizedPnl)}</td>
                      <td>
                        <Badge tone={p.status === 'Open' ? 'accent' : 'neutral'}>{p.status}</Badge>
                      </td>
                      <td className="r muted">{formatDateTime(p.openedUtc)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </QueryBoundary>
      </Panel>
    </div>
  )
}
