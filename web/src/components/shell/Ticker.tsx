/**
 * The top bar's ticker: the indices and the nearest commodity contracts from
 * the market pulse, the same answer the Markets page reads (one query, one
 * cache). An item whose price is not known yet is left out rather than shown
 * as a zero; a click opens its chart.
 */

import { Link } from 'react-router-dom'
import { useMarketPulse } from '../../lib/queries'

const INR = new Intl.NumberFormat('en-IN', { maximumFractionDigits: 2, minimumFractionDigits: 2 })

export function Ticker() {
  const pulse = useMarketPulse()
  const groups = pulse.data?.groups ?? []
  const items = groups
    .filter((g) => g.key === 'index' || g.key === 'commodity')
    .flatMap((g) => g.items)
    .filter((it) => it.lastTradedPrice != null)
    .slice(0, 7)
  if (items.length === 0) return <div className="shell__grow" />
  return (
    <ul className="tick" aria-label="Market prices">
      {items.map((it) => {
        const pct = it.changePercent
        const tone = pct == null ? '' : pct > 0 ? ' tick__chg--pos' : pct < 0 ? ' tick__chg--neg' : ''
        return (
          <li key={it.symbol}>
            <Link to={`/markets/chart?symbol=${encodeURIComponent(it.symbol)}`} className="tick__item" title={it.contract ? `${it.name} · ${it.contract}` : it.name}>
              <span className="tick__name">{it.name}</span>
              <span className="tick__row">
                <b className="num">{INR.format(it.lastTradedPrice as number)}</b>
                {pct != null && (
                  <span className={`tick__chg num${tone}`}>
                    {pct > 0 ? '+' : pct < 0 ? '−' : ''}
                    {Math.abs(pct).toFixed(2)}%
                  </span>
                )}
              </span>
            </Link>
          </li>
        )
      })}
    </ul>
  )
}
