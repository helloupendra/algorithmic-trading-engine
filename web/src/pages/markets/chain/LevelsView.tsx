/**
 * Markets → Option chain → Levels: what desks read off the chain before they
 * read the chain itself. The OI walls sellers are defending, max pain, the
 * put-call ratio, IV and VIX, and the move the ATM straddle prices to expiry.
 *
 * Read from the same chain view the Chain tab polls, so the two never
 * disagree and switching between them costs no request. It used to be a
 * section of Market factors with an underlying picker of its own.
 */

import { useSearchParams } from 'react-router-dom'
import { useOptionChainView } from '../../../lib/queries'
import {
  distanceTo,
  expectedMove,
  formatDistance,
  formatSignedContracts,
  signTone,
  topWalls,
} from '../../../lib/factors'
import type { WallRow } from '../../../lib/factors'
import { formatOi, movePercent } from '../../../lib/movers'
import { formatAge } from '../../../lib/format'
import { EmptyState, InlineError, Loading, Panel } from '../../../components/ui'
import { Metric, ResearchNote, num } from '../factorParts'

function WallsTable({ title, rows, tone }: { title: string; rows: WallRow[]; tone: 'pos' | 'neg' }) {
  return (
    <Panel title={title} className="mf-card">
      {rows.length === 0 ? (
        <EmptyState>No open interest in this chain.</EmptyState>
      ) : (
        <table className="table">
          <thead>
            <tr>
              <th>Strike</th>
              <th className="num">Open interest</th>
              <th className="num">Change today</th>
              <th className="num">From spot</th>
            </tr>
          </thead>
          <tbody>
            {rows.map((w, i) => (
              <tr key={w.strike}>
                <td className={`mono ${i === 0 ? tone : ''}`}>{w.strike.toLocaleString('en-IN')}</td>
                <td className="num mono">{formatOi(w.openInterest)}</td>
                <td className={`num mono ${signTone(w.openInterestChange)}`}>{formatSignedContracts(w.openInterestChange)}</td>
                <td className="num mono">{formatDistance(w.distance)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </Panel>
  )
}

export function LevelsView({ underlying }: { underlying: string }) {
  const [params] = useSearchParams()
  const view = useOptionChainView(underlying, params.get('expiry') ?? undefined)
  // keepPreviousData must never show one underlying's levels under another's name.
  const chain = view.data && view.data.underlying === underlying ? view.data : undefined
  const header = chain?.header ?? null
  const spot = header?.spot?.lastPrice ?? chain?.spotPrice ?? null
  const move = expectedMove(chain)

  if (view.isError && !chain) return <InlineError error={view.error} />
  if (!chain) return <Loading label={`Loading the ${underlying} chain…`} />
  if (!header) return <EmptyState>No chain has been captured for {underlying} yet.</EmptyState>

  return (
    <div className="mf-body lv">
      <p className="ocp-meta small muted">
        {header.mode === 'live' ? 'Live' : 'Last capture'} · {formatAge(header.liveOverlayUtc ?? header.snapshotCapturedUtc)}
        {chain.expiryDate ? ` · expiry ${chain.expiryDate}` : ''}
      </p>
      <div className="mf-metrics lv-metrics">
        <Metric
          label={underlying}
          value={num(spot, 2)}
          sub={header.spot?.changePercent != null ? movePercent(header.spot.changePercent) : undefined}
          tone={signTone(header.spot?.changePercent)}
        />
        <Metric
          label="Resistance · biggest call OI"
          value={num(header.resistanceStrike)}
          sub={`${formatOi(header.resistanceOpenInterest)} · ${formatDistance(distanceTo(spot, header.resistanceStrike))}`}
          tone="neg"
        />
        <Metric
          label="Support · biggest put OI"
          value={num(header.supportStrike)}
          sub={`${formatOi(header.supportOpenInterest)} · ${formatDistance(distanceTo(spot, header.supportStrike))}`}
          tone="pos"
        />
        <Metric label="Max pain" value={num(header.maxPainStrike)} sub={formatDistance(distanceTo(spot, header.maxPainStrike))} />
        <Metric
          label="Put-call ratio"
          value={header.putCallRatio != null ? header.putCallRatio.toFixed(2) : '—'}
          sub={header.putCallRatioOfChange != null ? `of today's change ${header.putCallRatioOfChange.toFixed(2)}` : 'above 1: more puts written'}
        />
        <Metric
          label="Move the straddle prices"
          value={move ? `±${num(move.points)}` : '—'}
          sub={move ? `${move.percent.toFixed(2)}% to expiry · ${num(move.strike)} straddle` : 'no ATM prices'}
        />
        <Metric label="ATM IV" value={header.atTheMoneyIv != null ? `${header.atTheMoneyIv.toFixed(1)}%` : '—'} />
        <Metric
          label="India VIX"
          value={num(header.vix?.lastPrice, 2)}
          sub={header.vix?.changePercent != null ? movePercent(header.vix.changePercent) : undefined}
          // VIX up is fear: coloured the way a falling market is.
          tone={signTone(header.vix?.changePercent) === 'pos' ? 'neg' : signTone(header.vix?.changePercent) === 'neg' ? 'pos' : ''}
        />
        <Metric label="Days to expiry" value={header.daysToExpiry ?? '—'} />
      </div>

      <div className="mf-grid">
        <WallsTable title="Call walls · where sellers expect a ceiling" rows={topWalls(chain, 'call')} tone="neg" />
        <WallsTable title="Put walls · where sellers expect a floor" rows={topWalls(chain, 'put')} tone="pos" />
      </div>
      <p className="muted small">
        A wall is where option sellers have the most contracts open. Desks read the biggest call OI as resistance and the
        biggest put OI as support; walls move during the day as positions are added and closed.
      </p>
      <ResearchNote id="levels" />
    </div>
  )
}
