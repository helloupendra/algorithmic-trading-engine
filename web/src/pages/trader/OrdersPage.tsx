/**
 * Order and signal history for a chosen simulation run — the audit trail of
 * what the strategy asked for and what the paper engine did with it.
 */

import { useState } from 'react'
import { useRunOrders, useRunSignals } from '../../lib/queries'
import { formatDateTime, formatPrice, shortSymbol } from '../../lib/format'
import { Badge, Panel, QueryBoundary } from '../../components/ui'
import { RunPicker, useDefaultRunId } from './RunPicker'

export function OrdersPage() {
  const [runId, setRunId] = useState<number | null>(null)
  useDefaultRunId(runId, setRunId)

  const orders = useRunOrders(runId)
  const signals = useRunSignals(runId)

  return (
    <div className="page">
      <header className="page__header">
        <h1 className="page__title">Orders</h1>
        <p className="page__subtitle">Paper orders and the strategy signals that produced them.</p>
      </header>

      <div className="toolbar">
        <RunPicker value={runId} onChange={setRunId} />
      </div>

      <Panel title="Paper orders">
        <QueryBoundary query={orders} empty="This run placed no orders.">
          {(data) => (
            <div className="tablewrap">
              <table className="table">
                <thead>
                  <tr>
                    <th>#</th>
                    <th>Symbol</th>
                    <th>Side</th>
                    <th className="r">Qty</th>
                    <th>Type</th>
                    <th className="r">Requested</th>
                    <th className="r">Fill</th>
                    <th>Status</th>
                    <th className="r">Created</th>
                    <th className="r">Filled</th>
                  </tr>
                </thead>
                <tbody>
                  {data.map((o) => (
                    <tr key={o.id}>
                      <td className="mono muted">{o.id}</td>
                      <td className="mono">{shortSymbol(o.symbol)}</td>
                      <td>
                        <Badge tone={o.side === 'Buy' ? 'pos' : 'neg'}>{o.side}</Badge>
                      </td>
                      <td className="r mono">{o.quantity}</td>
                      <td className="muted">{o.orderType}</td>
                      <td className="r mono">{formatPrice(o.requestedPrice)}</td>
                      <td className="r mono">{formatPrice(o.fillPrice)}</td>
                      <td>
                        <Badge
                          tone={
                            o.status === 'Filled' ? 'pos' : o.status === 'Cancelled' ? 'warn' : 'neutral'
                          }
                        >
                          {o.status}
                        </Badge>
                      </td>
                      <td className="r muted">{formatDateTime(o.createdUtc)}</td>
                      <td className="r muted">{formatDateTime(o.filledUtc)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </QueryBoundary>
      </Panel>

      <Panel title="Signals">
        <QueryBoundary query={signals} empty="No signals recorded for this run.">
          {(data) => (
            <div className="tablewrap">
              <table className="table">
                <thead>
                  <tr>
                    <th>#</th>
                    <th>Strategy</th>
                    <th>Type</th>
                    <th>Symbol</th>
                    <th className="r">Price</th>
                    <th>Group</th>
                    <th className="r">Timestamp</th>
                  </tr>
                </thead>
                <tbody>
                  {data.map((s) => (
                    <tr key={s.id}>
                      <td className="mono muted">{s.id}</td>
                      <td>{s.strategyName}</td>
                      <td>
                        <Badge tone="accent">{s.signalType}</Badge>
                      </td>
                      <td className="mono">{s.symbol ? shortSymbol(s.symbol) : '—'}</td>
                      <td className="r mono">{formatPrice(s.price)}</td>
                      <td className="mono muted">{s.groupId || '—'}</td>
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
  )
}
