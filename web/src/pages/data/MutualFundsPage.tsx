/**
 * Data → Mutual funds: what the desk holds of AMFI's NAVs, before anything is
 * picked (the data-discoverability rule). Schemes by plan, option, category
 * and AMC, the NAV range and how current it is, the last nightly sync and the
 * history backfill. Phase 1 of the mutual fund module: coverage only, no
 * analytics yet (docs/modules/mutual_funds_module.md).
 */

import { useMutualFundCoverage } from '../../lib/queries'
import { formatDateTime, formatNumber } from '../../lib/format'
import {
  backfillSummary,
  formatDay,
  freshness,
  optionLabel,
  planLabel,
  splitCategory,
  type MutualFundCount,
  type MutualFundCoverage,
} from '../../lib/mutualFunds'
import { Badge, Panel, QueryBoundary, StatTile } from '../../components/ui'
import './data.css'

function CountTable({ rows, label, name }: { rows: MutualFundCount[]; label: string; name: (key: string) => string }) {
  return (
    <div className="tablewrap tablewrap--rows8">
      <table className="table">
        <thead>
          <tr>
            <th>{label}</th>
            <th className="r">Schemes</th>
            <th className="r">Active</th>
          </tr>
        </thead>
        <tbody>
          {rows.map((r) => (
            <tr key={r.key}>
              <td>{name(r.key)}</td>
              <td className="num">{formatNumber(r.schemes)}</td>
              <td className="num">{formatNumber(r.active)}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}

function CategoryTable({ rows }: { rows: MutualFundCount[] }) {
  return (
    <div className="tablewrap tablewrap--rows8">
      <table className="table">
        <thead>
          <tr>
            <th>Category</th>
            <th className="col--wide">Type</th>
            <th className="r">Schemes</th>
            <th className="r">Active</th>
          </tr>
        </thead>
        <tbody>
          {rows.map((r) => {
            const { type, name } = splitCategory(r.key)
            return (
              <tr key={r.key} title={r.key}>
                <td>
                  {name}
                  {/* The type column is hidden on a phone; "Income" is both open and close ended. */}
                  <span className="col--phone muted">{type}</span>
                </td>
                <td className="col--wide muted">{type}</td>
                <td className="num">{formatNumber(r.schemes)}</td>
                <td className="num">{formatNumber(r.active)}</td>
              </tr>
            )
          })}
        </tbody>
      </table>
    </div>
  )
}

function Coverage({ data }: { data: MutualFundCoverage }) {
  const fresh = freshness(data.nav)
  const history = backfillSummary(data.backfill)
  const last = data.lastSync
  const toneOf = (tone: string) => (tone === 'pos' || tone === 'warn' || tone === 'neg' ? tone : undefined)

  return (
    <>
      <div className="stat-grid">
        <StatTile
          label="Schemes"
          value={formatNumber(data.schemes)}
          sub={`${formatNumber(data.activeSchemes)} active: a NAV within 30 days`}
        />
        <StatTile
          label="NAVs stored"
          value={`${data.nav.rowsApproximate ? '≈ ' : ''}${formatNumber(data.nav.rows)}`}
          sub={data.nav.firstDate ? `${formatDay(data.nav.firstDate)} to ${formatDay(data.nav.lastDate)}` : 'none yet'}
        />
        <StatTile label="Freshness" value={fresh.label} tone={toneOf(fresh.tone)} sub={fresh.detail} />
        <StatTile label="History" value={history.label} tone={toneOf(history.tone)} sub={history.detail} />
      </div>

      <Panel title="Last sync">
        {last ? (
          <div className="kv-grid">
            <div>
              <span className="muted">When</span>
              <span>
                {formatDateTime(last.startedUtc)} IST · {last.trigger}
              </span>
            </div>
            <div>
              <span className="muted">Result</span>
              <span>
                <Badge tone={last.ok ? 'pos' : 'neg'}>{last.ok ? 'Worked' : 'Failed'}</Badge>
              </span>
            </div>
            <div>
              <span className="muted">AMFI file for</span>
              <span>{formatDay(last.asOfDate)}</span>
            </div>
            <div>
              <span className="muted">NAVs written</span>
              <span>{formatNumber(last.navRowsWritten)}</span>
            </div>
          </div>
        ) : (
          <p className="empty">No sync has run yet.</p>
        )}
        {last && <p className={last.ok ? '' : 'neg'}>{last.message}</p>}
        <p className="small-note">
          {data.syncEnabled
            ? "AMFI's daily file is read at 23:30 IST every day, again at 07:00 IST when the evening fell short of the last business day, and an hour after a failure."
            : 'The daily sync is switched off (MutualFunds:SyncEnabled).'}
          {data.syncRunning ? ' A sync is running now.' : ''}
        </p>
      </Panel>

      <div className="two-col">
        <Panel title="By plan">
          <CountTable rows={data.byPlan} label="Plan" name={planLabel} />
        </Panel>
        <Panel title="By option">
          <CountTable rows={data.byOption} label="Option" name={optionLabel} />
        </Panel>
      </div>

      <Panel title={`By category · ${formatNumber(data.byCategory.length)}`}>
        <CategoryTable rows={data.byCategory} />
      </Panel>

      <Panel title={`By AMC · ${formatNumber(data.byAmc.length)}`}>
        <CountTable rows={data.byAmc} label="AMC" name={(key) => key || '—'} />
      </Panel>

      <p className="small-note">
        Source: AMFI (portal.amfiindia.com), its daily NAV file and NAV history. Plan and option are cleaned from
        AMFI&rsquo;s own columns, then the scheme name, then the ISINs; &ldquo;Not stated&rdquo; is what none of them
        says (ETFs, and plans from before direct plans existed). Categories are AMFI&rsquo;s section lines as written,
        so older names stay apart from SEBI&rsquo;s current ones. Facts only: nothing here ranks or recommends a fund.
      </p>
    </>
  )
}

export function MutualFundsPage() {
  const coverage = useMutualFundCoverage()

  return (
    <div className="page data-page">
      <header className="page__header">
        <div>
          <h1 className="page__title">Mutual funds</h1>
          <p className="page__subtitle">
            Every scheme AMFI publishes and its daily NAV: what is stored, how current it is and how far back it goes.
          </p>
        </div>
      </header>

      <QueryBoundary query={coverage}>{(data) => <Coverage data={data} />}</QueryBoundary>
    </div>
  )
}
