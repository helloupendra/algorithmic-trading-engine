/**
 * The report's sections, top to bottom after the holdings: the totals, the
 * written summary, the allocation, concentration and risk, the open
 * positions, the funds, and the notes with the disclaimer.
 *
 * Every figure is the server's, formatted; nothing here is computed into a
 * new claim. A section the report did not carry says so rather than showing
 * zeroes.
 */

import type { ReactNode } from 'react'
import { formatInrSigned, formatInrWhole, formatNumber, formatPrice } from '../../lib/format'
import {
  UNKNOWN,
  betaMeaning,
  brokerName,
  formatPct,
  formatSignedPct,
  formatStamp,
  formatWeight,
  hhiMeaning,
  signTone,
} from '../../lib/portfolio'
import type {
  Allocation,
  AllocationSlice,
  BrokerFunds,
  Concentration,
  DerivativesExposure,
  PortfolioPosition,
  PortfolioSummary,
  ReportTotals,
  RiskFigures,
} from '../../lib/portfolio'
import { Badge, Panel, StatTile } from '../../components/ui'

// ---------- totals ----------

export function Totals({ totals }: { totals: ReportTotals }) {
  const count = totals.holdingsCount
  return (
    <div className="stat-grid stat-grid--pair pf-totals" role="group" aria-label="Totals">
      <StatTile
        label="Current value"
        value={formatInrWhole(totals.current)}
        sub={count == null ? undefined : `${formatNumber(count)} ${count === 1 ? 'holding' : 'holdings'}`}
      />
      <StatTile label="Invested" value={formatInrWhole(totals.invested)} sub="at average cost" />
      <StatTile
        label="P&L"
        value={formatInrSigned(totals.pnl)}
        tone={signTone(totals.pnl) || undefined}
        sub={<span className={signTone(totals.pnl)}>{formatSignedPct(totals.pnlPct)} on the amount invested</span>}
      />
      <StatTile
        label="Today"
        value={formatInrSigned(totals.dayChange)}
        tone={signTone(totals.dayChange) || undefined}
        sub={<span className={signTone(totals.dayChange)}>{formatSignedPct(totals.dayChangePct)} since the last close</span>}
      />
    </div>
  )
}

// ---------- summary ----------

export function Summary({ summary }: { summary: PortfolioSummary }) {
  return (
    <section className="pf-summary" aria-labelledby="pf-summary-title">
      <h2 className="pf-kicker" id="pf-summary-title">
        Summary <span className="pf-kicker__note">written automatically from the numbers below, and checked against them</span>
      </h2>
      <p className="pf-summary__text">{summary.text}</p>
      {(summary.model || summary.generatedUtc) && (
        <p className="pf-summary__meta">
          {[summary.model, summary.generatedUtc ? `written ${formatStamp(summary.generatedUtc)} IST` : null].filter(Boolean).join(' · ')}
        </p>
      )}
    </section>
  )
}

// ---------- allocation ----------

function BarList({ title, slices }: { title: string; slices: AllocationSlice[] }) {
  return (
    <section className="pf-bars" aria-label={`Allocation ${title.toLowerCase()}`}>
      <h3 className="pf-bars__title">{title}</h3>
      {slices.length === 0 ? (
        <p className="faint pf-flush">Not in this report.</p>
      ) : (
        <ul className="pf-bars__list">
          {slices.map((s, i) => {
            const unknown = s.name === UNKNOWN
            const w = s.weight == null ? 0 : Math.max(0, Math.min(1, s.weight))
            return (
              <li key={`${s.name}-${i}`} className={`pf-bar${unknown ? ' pf-bar--unknown' : ''}`}>
                <span className="pf-bar__name" title={unknown ? 'The server could not tell for these holdings' : s.name}>
                  {s.name}
                </span>
                <span className="pf-bar__track" aria-hidden="true">
                  <span className="pf-bar__fill" style={{ width: `${(w * 100).toFixed(2)}%` }} />
                </span>
                <span className="pf-bar__pct">{formatWeight(s.weight)}</span>
                <span className="pf-bar__inr">{formatInrWhole(s.value)}</span>
              </li>
            )
          })}
        </ul>
      )}
    </section>
  )
}

export function AllocationPanel({ allocation }: { allocation: Allocation | null }) {
  return (
    <Panel title="Allocation" className="pf-alloc">
      {allocation ? (
        <div className="pf-alloc__grid">
          <BarList title="By sector" slices={allocation.bySector} />
          <BarList title="By broker" slices={allocation.byBroker} />
          <BarList title="By market cap" slices={allocation.byMarketCap} />
        </div>
      ) : (
        <p className="empty">The report carried no allocation.</p>
      )}
    </Panel>
  )
}

// ---------- concentration and risk ----------

function Fact({ k, v, children }: { k: string; v: ReactNode; children?: ReactNode }) {
  return (
    <div className="pf-fact">
      <dt>{k}</dt>
      <dd>
        <span className="pf-fact__v">{v}</span>
        {children && <span className="pf-fact__m">{children}</span>}
      </dd>
    </div>
  )
}

function ConcentrationFacts({ concentration }: { concentration: Concentration | null }) {
  const hhi = hhiMeaning(concentration?.hhi)
  return (
    <section aria-labelledby="pf-conc-title">
      <h3 className="pf-bars__title" id="pf-conc-title">
        Concentration
      </h3>
      {concentration ? (
        <dl className="pf-facts">
          <Fact k="Largest holding" v={formatWeight(concentration.top1Weight)}>
            {concentration.largest[0] ? `${concentration.largest[0]}, of the portfolio's value` : "of the portfolio's value"}
          </Fact>
          <Fact k="Five largest" v={formatWeight(concentration.top5Weight)}>
            {concentration.largest.length > 0 ? concentration.largest.slice(0, 5).join(', ') : 'together'}
          </Fact>
          <Fact k="HHI" v={hhi ? formatNumber(hhi.points) : '—'}>
            {hhi ? hhi.text : 'not in this report'}
          </Fact>
        </dl>
      ) : (
        <p className="faint pf-flush">Not in this report.</p>
      )}
    </section>
  )
}

function RiskFacts({ risk }: { risk: RiskFigures | null }) {
  const days = risk?.lookbackDays
  const beta = betaMeaning(risk?.betaVsNifty)
  return (
    <section aria-labelledby="pf-risk-title">
      <h3 className="pf-bars__title" id="pf-risk-title">
        Risk
        {days != null && <span className="pf-kicker__note">over the last {formatNumber(days)} days</span>}
      </h3>
      {risk ? (
        <>
          <dl className="pf-facts">
            <Fact k="Volatility" v={formatPct(risk.volatilityAnnualPct)}>
              annualised: how much the value swung day to day, scaled to a year
            </Fact>
            <Fact k="Beta vs NIFTY 50" v={risk.betaVsNifty == null ? '—' : risk.betaVsNifty.toFixed(2)}>
              {beta ?? 'not in this report'}
            </Fact>
            <Fact k="Max drawdown" v={risk.maxDrawdownPct == null ? '—' : formatPct(-Math.abs(risk.maxDrawdownPct))}>
              the largest fall from a high point to a later low
            </Fact>
          </dl>
          {risk.coveredWeight != null && (
            <p className="pf-risk__covered">Based on {formatWeight(risk.coveredWeight)} of your portfolio's value.</p>
          )}
          {risk.note && <p className="small-note">{risk.note}</p>}
        </>
      ) : (
        <p className="faint pf-flush">Not in this report.</p>
      )}
    </section>
  )
}

export function ConcentrationRiskPanel({ concentration, risk }: { concentration: Concentration | null; risk: RiskFigures | null }) {
  return (
    <Panel title="Concentration and risk" className="pf-risk">
      <div className="pf-risk__grid">
        <ConcentrationFacts concentration={concentration} />
        <RiskFacts risk={risk} />
      </div>
    </Panel>
  )
}

// ---------- positions and funds ----------

export function PositionsPanel({ positions, derivatives }: { positions: PortfolioPosition[]; derivatives: DerivativesExposure | null }) {
  const open = derivatives?.openPositions
  return (
    <Panel title="Open positions" className="pf-positions">
      {derivatives && (open != null || derivatives.grossExposure != null) && (
        <p className="pf-fno">
          <span>
            F&amp;O open: <b>{open == null ? '—' : formatNumber(open)}</b>
          </span>
          <span>
            Gross exposure: <b>{formatInrWhole(derivatives.grossExposure)}</b>
          </span>
        </p>
      )}
      {derivatives?.note && <p className="small-note pf-flush">{derivatives.note}</p>}
      {positions.length === 0 ? (
        <p className="empty">No open positions at the last sync.</p>
      ) : (
        <div className="tablewrap">
          <table className="table">
            <thead>
              <tr>
                <th scope="col">Symbol</th>
                <th scope="col">Broker</th>
                <th scope="col">Product</th>
                <th scope="col" className="num">
                  Qty
                </th>
                <th scope="col" className="num">
                  Avg
                </th>
                <th scope="col" className="num">
                  LTP
                </th>
                <th scope="col" className="num">
                  P&amp;L
                </th>
              </tr>
            </thead>
            <tbody>
              {positions.map((p, i) => (
                <tr key={`${p.broker}-${p.symbol}-${p.product}-${i}`}>
                  <th scope="row" className="pf-sym">
                    {p.symbol} {p.isDerivative && <Badge tone="neutral">F&amp;O</Badge>}
                  </th>
                  <td>{brokerName(p.broker)}</td>
                  <td>{p.product || '—'}</td>
                  <td className="num">{formatNumber(p.qty)}</td>
                  <td className="num">{formatPrice(p.avgPrice)}</td>
                  <td className="num">{formatPrice(p.ltp)}</td>
                  <td className={`num ${signTone(p.pnl)}`}>{formatInrSigned(p.pnl)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </Panel>
  )
}

function sum(values: (number | null)[]): number | null {
  return values.some((v) => v == null) ? null : values.reduce<number>((s, v) => s + (v as number), 0)
}

export function FundsPanel({ funds }: { funds: BrokerFunds[] }) {
  return (
    <Panel title="Funds" className="pf-funds">
      {funds.length === 0 ? (
        <p className="empty">No funds reported at the last sync.</p>
      ) : (
        <div className="tablewrap">
          <table className="table">
            <thead>
              <tr>
                <th scope="col">Broker</th>
                <th scope="col" className="num">
                  Available
                </th>
                <th scope="col" className="num">
                  Used
                </th>
                <th scope="col" className="num">
                  Total
                </th>
              </tr>
            </thead>
            <tbody>
              {funds.map((f) => (
                <tr key={f.broker}>
                  <th scope="row">{brokerName(f.broker)}</th>
                  <td className="num">{formatInrWhole(f.available)}</td>
                  <td className="num">{formatInrWhole(f.used)}</td>
                  <td className="num">{formatInrWhole(f.total)}</td>
                </tr>
              ))}
            </tbody>
            {funds.length > 1 && (
              <tfoot>
                <tr className="pf-total-row">
                  <th scope="row">All brokers</th>
                  <td className="num">{formatInrWhole(sum(funds.map((f) => f.available)))}</td>
                  <td className="num">{formatInrWhole(sum(funds.map((f) => f.used)))}</td>
                  <td className="num">{formatInrWhole(sum(funds.map((f) => f.total)))}</td>
                </tr>
              </tfoot>
            )}
          </table>
        </div>
      )}
    </Panel>
  )
}

// ---------- notes and the disclaimer ----------

export function NotesFooter({ notes, sources, disclaimer }: { notes: string[]; sources: ReactNode; disclaimer: string }) {
  return (
    <footer className="pf-foot" aria-label="About these numbers">
      {(notes.length > 0 || sources) && <h2 className="pf-kicker">About these numbers</h2>}
      {sources}
      {notes.length > 0 && (
        <ul className="pf-notes">
          {notes.map((n, i) => (
            <li key={i}>{n}</li>
          ))}
        </ul>
      )}
      <p className="pf-disclaimer" role="note">
        {disclaimer}
      </p>
    </footer>
  )
}
