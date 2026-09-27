/**
 * Day P&L: each account's net today, after charges, on a rail against the
 * platform's max daily loss, then where it came from, strategy by strategy.
 *
 * There is no per-minute P&L series yet (live runs record none), so there is
 * no curve here, and the panel says so rather than drawing one from guesses.
 * There are no per-account limits either (the owner chose not to add them);
 * the one limit is the platform's, from Risk → limits, and the API checks it
 * per run on each new order. The rail is read against that and labelled as
 * such. A trader cannot read the limits (admin-only), so their rail is absent.
 */

import type { AccountTotals } from '../../lib/desk'
import { compactInr, lossLimitShare } from '../../lib/desk'
import { formatInrSigned, formatInrWhole } from '../../lib/format'
import { useDeskRiskLimits } from '../../lib/queries'
import type { DeskLinks, DeskView } from './data'
import { toneClass } from './data'
import { Failed, Money, PanelHead, Rail, Swatch, Waiting } from './parts'

/** One account: its net, the rail against the platform limit, and the gross the charges turned into it. */
function AccountRow({ totals, limit, multi }: { totals: AccountTotals; limit: number | null; multi: boolean }) {
  const f = totals.figures
  const share = lossLimitShare(f.net, limit)
  const hasRail = limit != null && limit < 0
  return (
    <div className="dk-acct">
      {multi ? <Swatch tone={totals.account.tone} /> : <span />}
      <span className="dk-acct__nm">{totals.account.name}</span>
      <Money value={f.net} className="dk-acct__nv" />
      {hasRail && (
        <div className="dk-acct__rr">
          <Rail net={f.net} limit={limit} label={`${totals.account.name}: net against the platform max daily loss of ${formatInrSigned(limit)}`} />
          <span>{share != null && share > 0 ? `${Math.round(share * 100)}% of the max daily loss` : 'no loss against the limit'}</span>
        </div>
      )}
      <div className="dk-acct__rr">
        <span>
          gross <span className={toneClass(f.gross)}>{formatInrSigned(f.gross)}</span> · charges {formatInrWhole(f.charges)} · {f.trades} trades
        </span>
      </div>
    </div>
  )
}

/** Where the net came from: one diverging bar per strategy, scaled to the largest. */
function ByStrategy({ view }: { view: DeskView }) {
  const rows = view.grid!.rows
  const max = Math.max(1, ...rows.map((r) => Math.abs(r.figures.net)))
  return (
    <div className="dk-bystrat">
      <div className="dk-hd" style={{ marginBottom: 4 }}>
        <span className="dk-meta">By strategy · net, charges in the title</span>
      </div>
      {rows.map((r) => {
        const w = (Math.abs(r.figures.net) / max) * 50
        return (
          <div key={r.strategy} className="dk-bar-row" title={`${r.label}: net ${formatInrSigned(r.figures.net)} after ${formatInrWhole(r.figures.charges)} charges`}>
            <span>{r.label}</span>
            <span className="dk-bar-track" aria-hidden="true">
              <i
                style={{
                  left: r.figures.net < 0 ? `${50 - w}%` : '50%',
                  width: `${w}%`,
                  background: r.figures.net < 0 ? 'var(--neg)' : 'var(--pos)',
                }}
              />
            </span>
            <span className={`dk-n ${toneClass(r.figures.net)}`} style={{ textAlign: 'right' }}>
              {compactInr(r.figures.net)}
            </span>
          </div>
        )
      })}
    </div>
  )
}

export function DayPnl({ view, links }: { view: DeskView; links: DeskLinks }) {
  const limits = useDeskRiskLimits(view.isAdmin)
  const limit = limits.data?.maxDailyLoss ?? null
  const head = (
    <PanelHead
      title="Day P&L"
      meta={view.accounts.length > 1 ? 'per account · net after charges' : 'net after charges'}
      more={view.isAdmin && links.risk ? { to: links.risk, label: 'Risk' } : null}
    />
  )
  if (view.runsError && !view.grid) return <>{head}<Failed what="The runs" error={view.runsError} /></>
  if (!view.grid) return <>{head}<Waiting>Reading today’s runs…</Waiting></>
  const { grid } = view
  if (grid.rows.length === 0) return <>{head}<Waiting>Nothing traded yet.</Waiting></>
  const multi = grid.totals.length > 1
  return (
    <>
      {head}
      {grid.totals.map((t) => (
        <AccountRow key={t.account.id} totals={t} limit={limit} multi={multi} />
      ))}
      {multi && (
        <div className="dk-acct">
          <span />
          <span className="dk-acct__nm">All accounts</span>
          <Money value={grid.figures.net} className="dk-acct__nv" />
          <div className="dk-acct__rr">
            <span>{`gross ${formatInrSigned(grid.figures.gross)} · charges ${formatInrWhole(grid.figures.charges)}`}</span>
          </div>
        </div>
      )}
      <ByStrategy view={view} />
      <div className="dk-foot" style={{ display: 'block' }}>
        {view.isAdmin && limit != null && limit < 0 && (
          <p className="dk-note">
            Rail: the platform’s max daily loss, {formatInrSigned(limit)}, which the API checks per run on each new order. There are
            no per-account limits.
          </p>
        )}
        {view.isAdmin && limits.isError && <p className="dk-note">The risk limits could not be read, so there is no rail.</p>}
        <p className="dk-note">Totals only: a curve through the day arrives with the P&L series.</p>
      </div>
    </>
  )
}
