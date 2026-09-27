/**
 * Day P&L: each account's net through the day, after charges, as the
 * recorder wrote it once a minute (GET /api/Strategy/runs/pnl-series); then
 * each account's figure now, and where it came from, strategy by strategy.
 *
 * Where the recorder wrote nothing for a stretch, the curve breaks and the
 * panel says when, rather than drawing a line across what nobody saw. Nor is
 * there a loss rail: the one limit, Risk → max daily loss, is per run, on
 * P&L before charges, and an account's total held against it read as a
 * breach that was not one. The panel states the limit as it is instead
 * (admins only: traders cannot read the limits).
 */

import { useMemo } from 'react'
import type { AccountTotals } from '../../lib/desk'
import { compactInr, dayLabel } from '../../lib/desk'
import { dayCurves, gapText } from '../../lib/pnlSeries'
import { formatInrSigned, formatInrWhole } from '../../lib/format'
import { useDeskRiskLimits, useRunPnlSeries } from '../../lib/queries'
import type { DeskLinks, DeskView } from './data'
import { toneClass, useWidth } from './data'
import { PnlChart } from './PnlChart'
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

/** The curve, or a plain word on why there is none. */
function Curve({ view }: { view: DeskView }) {
  const [ref, width] = useWidth<HTMLDivElement>()
  const isToday = view.day === view.today
  const series = useRunPnlSeries(view.day, isToday)
  const userIds = useMemo(() => view.accounts.map((a) => a.id), [view.accounts])
  const curves = useMemo(
    () =>
      series.data
        ? dayCurves(series.data, { userIds, nowMs: view.nowMs, isToday, mcxCloseUtc: isToday ? view.mcx?.sessionCloseUtc : null })
        : null,
    [series.data, userIds, view.nowMs, isToday, view.mcx?.sessionCloseUtc],
  )
  let body
  if (!curves) {
    body = series.isError ? <Failed what="The day’s minutes" error={series.error} /> : <p className="dk-wait dk-chart__wait">Reading the day’s minutes…</p>
  } else if (!curves.any) {
    body = (
      <p className="dk-note dk-chart__wait">
        The recorder has no minutes for {isToday ? 'today' : dayLabel(view.day)} yet, so there is no curve: the figures below are the runs’ own.
      </p>
    )
  } else {
    body = <PnlChart curves={curves} accounts={view.accounts} width={width} />
  }
  return (
    <>
      <div ref={ref}>{body}</div>
      {curves?.any && curves.gaps.length > 0 && (
        <p className="dk-note dk-gapnote" role="note">
          No points {gapText(curves.gaps)}: the recorder was not writing then, so the curve is not drawn across it.
        </p>
      )}
    </>
  )
}

export function DayPnl({ view, links }: { view: DeskView; links: DeskLinks }) {
  const limits = useDeskRiskLimits(view.isAdmin)
  const limit = limits.data?.maxDailyLoss ?? null
  const whose = view.accounts.length > 1 ? 'per account · ' : ''
  const when = view.day === view.today ? '' : ` · ${dayLabel(view.day)}`
  const head = (
    <PanelHead
      title="Day P&L"
      meta={`${whose}net after charges${when}`}
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
      <Curve view={view} />
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
      {view.isAdmin && limit != null && limit < 0 && (
        <div className="dk-foot" style={{ display: 'block' }}>
          <p className="dk-note">
            Max daily loss: {formatInrSigned(limit)} per run, on its P&L before charges, checked when the run places a new order.
            There is no limit per account.
          </p>
        </div>
      )}
    </>
  )
}
