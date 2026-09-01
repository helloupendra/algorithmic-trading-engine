/**
 * Top movers by category: day gainers and losers within an equity group
 * (Nifty 50, Bank Nifty, Sensex constituents), computed from public quote
 * data on the server and cached.
 *
 * This is a mechanical sort by day change — market data, not a recommendation.
 */

import { useEffect, useState } from 'react'
import { useEquityGroups, useTopMovers } from '../../lib/queries'
import { formatAge, formatPrice, pnlClass, shortSymbol } from '../../lib/format'
import { Panel, QueryBoundary } from '../../components/ui'
import type { Mover } from '../../lib/types'

function MoversTable({ items, empty }: { items: Mover[]; empty: string }) {
  if (items.length === 0) return <p className="empty">{empty}</p>
  return (
    <div className="tablewrap">
      <table className="table">
        <thead>
          <tr>
            <th>#</th>
            <th>Symbol</th>
            <th className="r">Last</th>
            <th className="r">Prev close</th>
            <th className="r">Change</th>
          </tr>
        </thead>
        <tbody>
          {items.map((m, i) => (
            <tr key={m.symbol}>
              <td className="mono muted">{i + 1}</td>
              <td className="mono">{shortSymbol(m.symbol)}</td>
              <td className="r mono">{formatPrice(m.lastPrice)}</td>
              <td className="r mono">{formatPrice(m.previousClose)}</td>
              <td className={`r mono ${pnlClass(m.changePercent)}`}>
                {m.changePercent != null
                  ? `${m.changePercent >= 0 ? '+' : ''}${m.changePercent.toFixed(2)}%`
                  : '—'}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}

export function TopMoversPage() {
  const groups = useEquityGroups()
  const [group, setGroup] = useState<string | null>(null)

  const firstGroup = groups.data?.[0]?.name
  useEffect(() => {
    if (group == null && firstGroup) setGroup(firstGroup)
  }, [group, firstGroup])

  const movers = useTopMovers(group)

  return (
    <div className="page">
      <header className="page__header">
        <h1 className="page__title">Top movers</h1>
        <p className="page__subtitle">
          Day gainers and losers within a category — a mechanical sort of public quote
          data, not investment advice.
        </p>
      </header>

      <div className="toolbar">
        {(groups.data ?? []).map((g) => (
          <button
            key={g.name}
            type="button"
            className={`btn btn--sm ${group === g.name ? 'btn--primary' : 'btn--ghost'}`}
            onClick={() => setGroup(g.name)}
          >
            {g.displayName || g.name} ({g.memberCount})
          </button>
        ))}
        {movers.data && (
          <span className="muted small-note" style={{ marginTop: 0 }}>
            {movers.data.symbolsResolved} quoted · fetched {formatAge(movers.data.fetchedUtc)}
          </span>
        )}
      </div>

      <QueryBoundary query={movers}>
        {(data) => (
          <div className="two-col">
            <Panel title={`Top gainers — ${data.displayName}`}>
              <MoversTable items={data.gainers} empty="No quotes resolved for this group." />
            </Panel>
            <Panel title={`Top losers — ${data.displayName}`}>
              <MoversTable items={data.losers} empty="No quotes resolved for this group." />
            </Panel>
          </div>
        )}
      </QueryBoundary>
    </div>
  )
}
