/**
 * Everything about one simulation run: equity curve, portfolio state,
 * performance metrics, positions, orders and signals. This is the screen that
 * proves the signal → risk gate → paper fill → P&L pipeline end to end.
 */

import { Link, useParams } from 'react-router-dom'
import {
  useRunEquityCurve,
  useRunOrders,
  useRunPerformance,
  useRunPortfolio,
  useRunPositions,
  useRunSignals,
  useSimulationRun,
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
import { EquityChart } from '../../components/charts'

export function RunDetailPage() {
  const params = useParams<{ id: string }>()
  const runId = params.id ? Number(params.id) : null

  const run = useSimulationRun(runId)
  const portfolio = useRunPortfolio(runId)
  const equity = useRunEquityCurve(runId)
  const performance = useRunPerformance(runId)
  const positions = useRunPositions(runId)
  const orders = useRunOrders(runId)
  const signals = useRunSignals(runId)

  return (
    <div className="page">
      <header className="page__header">
        <h1 className="page__title">
          Run #{runId} {run.data && <span className="muted">· {run.data.strategyName}</span>}
        </h1>
        <p className="page__subtitle">
          {run.data ? (
            <>
              {run.data.mode} · {shortSymbol(run.data.symbol)} · {run.data.resolution} ·{' '}
              created {formatDateTime(run.data.createdUtc)}
              {'  '}
              <Badge tone={run.data.status === 'Running' ? 'pos' : 'neutral'}>{run.data.status}</Badge>
            </>
          ) : (
            'Loading run…'
          )}
        </p>
      </header>

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
            label="Orders"
            value={`${portfolio.data.filledOrders}/${portfolio.data.totalOrders}`}
            sub="filled / total"
          />
          <StatTile
            label="Positions"
            value={`${portfolio.data.openPositions} open`}
            sub={`${portfolio.data.closedPositions} closed`}
          />
          {performance.data && (
            <>
              <StatTile
                label="Max drawdown"
                value={formatPercent(-Math.abs(performance.data.maxDrawdownPercent))}
                tone="warn"
              />
              <StatTile
                label="Win rate"
                value={formatPercent(performance.data.winRatePercent, 1)}
                sub={`${performance.data.winningPositions}W / ${performance.data.losingPositions}L`}
              />
            </>
          )}
        </div>
      )}

      <Panel title="Equity curve">
        <QueryBoundary
          query={equity}
          empty="No equity snapshots yet — the engine writes one per evaluation cycle."
        >
          {(data) => <EquityChart snapshots={data} />}
        </QueryBoundary>
      </Panel>

      {performance.data && (
        <Panel title="Performance">
          <div className="kv-grid">
            <div><span className="muted">Total return</span><b className={pnlClass(performance.data.totalReturnPercent)}>{formatPercent(performance.data.totalReturnPercent)}</b></div>
            <div><span className="muted">Profit factor</span><b>{performance.data.profitFactor.toFixed(2)}</b></div>
            <div><span className="muted">Expectancy</span><b>{formatInr(performance.data.expectancy)}</b></div>
            <div><span className="muted">Average win</span><b className="pos">{formatInr(performance.data.averageWin)}</b></div>
            <div><span className="muted">Average loss</span><b className="neg">{formatInr(performance.data.averageLoss)}</b></div>
            <div><span className="muted">Gross profit</span><b className="pos">{formatInr(performance.data.grossProfit)}</b></div>
            <div><span className="muted">Gross loss</span><b className="neg">{formatInr(performance.data.grossLoss)}</b></div>
            <div><span className="muted">Closed positions</span><b>{performance.data.totalClosedPositions}</b></div>
          </div>
        </Panel>
      )}

      <Panel title="Positions">
        <QueryBoundary query={positions} empty="No positions in this run.">
          {(data) => (
            <div className="tablewrap">
              <table className="table">
                <thead>
                  <tr>
                    <th>Symbol</th>
                    <th>Dir</th>
                    <th className="r">Qty</th>
                    <th className="r">Avg</th>
                    <th className="r">Mark</th>
                    <th className="r">Realized</th>
                    <th className="r">Unrealized</th>
                    <th>Status</th>
                  </tr>
                </thead>
                <tbody>
                  {data.map((p) => (
                    <tr key={p.id}>
                      <td className="mono">{shortSymbol(p.symbol)}</td>
                      <td><Badge tone={p.direction === 'Long' ? 'pos' : 'neg'}>{p.direction}</Badge></td>
                      <td className="r mono">{p.quantity}</td>
                      <td className="r mono">{formatPrice(p.averagePrice)}</td>
                      <td className="r mono">{formatPrice(p.lastMarkPrice)}</td>
                      <td className={`r mono ${pnlClass(p.realizedPnl)}`}>{formatInr(p.realizedPnl)}</td>
                      <td className={`r mono ${pnlClass(p.unrealizedPnl)}`}>{formatInr(p.unrealizedPnl)}</td>
                      <td><Badge tone={p.status === 'Open' ? 'accent' : 'neutral'}>{p.status}</Badge></td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </QueryBoundary>
      </Panel>

      <div className="two-col">
        <Panel title="Orders">
          <QueryBoundary query={orders} empty="No orders.">
            {(data) => (
              <div className="tablewrap">
                <table className="table">
                  <thead>
                    <tr>
                      <th>Symbol</th>
                      <th>Side</th>
                      <th className="r">Qty</th>
                      <th className="r">Fill</th>
                      <th>Status</th>
                    </tr>
                  </thead>
                  <tbody>
                    {data.map((o) => (
                      <tr key={o.id}>
                        <td className="mono">{shortSymbol(o.symbol)}</td>
                        <td><Badge tone={o.side === 'Buy' ? 'pos' : 'neg'}>{o.side}</Badge></td>
                        <td className="r mono">{o.quantity}</td>
                        <td className="r mono">{formatPrice(o.fillPrice)}</td>
                        <td><Badge tone={o.status === 'Filled' ? 'pos' : 'neutral'}>{o.status}</Badge></td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
          </QueryBoundary>
        </Panel>

        <Panel title="Signals">
          <QueryBoundary query={signals} empty="No signals.">
            {(data) => (
              <div className="tablewrap">
                <table className="table">
                  <thead>
                    <tr>
                      <th>Type</th>
                      <th>Symbol</th>
                      <th className="r">Price</th>
                      <th className="r">At</th>
                    </tr>
                  </thead>
                  <tbody>
                    {data.map((s) => (
                      <tr key={s.id}>
                        <td><Badge tone="accent">{s.signalType}</Badge></td>
                        <td className="mono">{s.symbol ? shortSymbol(s.symbol) : '—'}</td>
                        <td className="r mono">{formatPrice(s.price)}</td>
                        <td className="r muted">{formatDateTime(s.timestampUtc)}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
          </QueryBoundary>
        </Panel>
      </div>

      <p>
        <Link to="/trader/strategies">← All runs</Link>
      </p>
    </div>
  )
}
