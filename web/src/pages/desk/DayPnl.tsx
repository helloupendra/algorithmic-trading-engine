/**
 * Day P&L: each account's net today, after charges, then where it came from,
 * strategy by strategy.
 *
 * There is no per-minute P&L series yet (live runs record none), so there is
 * no curve here, and the panel says so rather than drawing one from guesses.
 * Nor is there a loss rail: the one limit, Risk → max daily loss, is per run,
 * on P&L before charges, and an account's total held against it read as a
 * breach that was not one. The panel states the limit as it is instead
 * (admins only: traders cannot read the limits).
 */

import type { AccountTotals } from '../../lib/desk'
import { compactInr } from '../../lib/desk'
import { formatInrSigned, formatInrWhole } from '../../lib/format'
import { useDeskRiskLimits } from '../../lib/queries'
import type { DeskLinks, DeskView } from './data'
import { toneClass } from './data'
import { Failed, Money, PanelHead, Swatch, Waiting } from './parts'

/** One account: its net, and the gross the charges turned into it. */
function AccountRow({ totals, multi }: { totals: AccountTotals; multi: boolean }) {
  const f = totals.figures
  return (
    <div className="dk-acct">
      {multi ? <Swatch tone={totals.account.tone} /> : <span />}
      <span className="dk-acct__nm">{totals.account.name}</span>
      <Money value={f.net} className="dk-acct__nv" />
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
        <AccountRow key={t.account.id} totals={t} multi={multi} />
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
            Max daily loss: {formatInrSigned(limit)} per run, on its P&L before charges, checked when the run places a new order.
            There is no limit per account.
          </p>
        )}
        <p className="dk-note">Totals only: a curve through the day arrives with the P&L series.</p>
      </div>
    </>
  )
}
