/**
 * Backtesting module — two readings of a run's results that the totals hide:
 * the max drawdown in words under the curve (when it began, how deep, when
 * it was made good), and net P&L by month, year by year. The arithmetic is
 * in lib/drawdown.ts and lib/backtestMonths.ts, where it is tested; this file
 * only lays it out and says what each figure is made of.
 */

import { useMemo } from 'react'
import { Panel } from '../../components/ui'
import { ExportCsvButton } from '../../components/ExportCsvButton'
import { IconClock } from '../../components/icons'
import { csvFileName, toCsv } from '../../lib/csv'
import { formatInrSigned, formatInrWhole, formatNumber } from '../../lib/format'
import { maxDrawdownEpisode } from '../../lib/drawdown'
import {
  MONTH_HEADERS,
  describeRunCosts,
  monthLabel,
  readRunCosts,
  summariseMonths,
  type MonthCell,
  type MonthlyReading,
} from '../../lib/backtestMonths'
import type { BacktestRunView } from '../../lib/types'
import { PnlValue } from '../strategies/shared'
import { formatDayWithYear } from './readings'
import { istDate } from './shared'

const DAY_MS = 86_400_000

/** Calendar days between the IST days of two instants. */
function daysBetween(fromIso: string, toIso: string): number | null {
  const a = istDate(fromIso)
  const b = istDate(toIso)
  if (!a || !b) return null
  return Math.round((Date.parse(`${b}T00:00:00Z`) - Date.parse(`${a}T00:00:00Z`)) / DAY_MS)
}

/* ------------------------------------------------------------ max drawdown */

/**
 * The run's Max drawdown, dated. The figure is the API's, measured over every
 * stored point; the dates are read from the curve the page was sent, which
 * keeps that fall's peak, trough and recovery. Should the two ever disagree
 * (an API from before the curve kept them), the figure stands alone rather
 * than wearing the dates of some other fall.
 */
export function MaxDrawdownLine({ view, active }: { view: BacktestRunView; active: boolean }) {
  const episode = useMemo(() => maxDrawdownEpisode(view.equityCurve), [view.equityCurve])
  const amount = Math.abs(view.metrics.maxDrawdownAmount)
  const percent = Math.abs(view.metrics.maxDrawdownPercent)
  const sofar = active ? ' so far' : ''

  if (view.equityCurve.length === 0) return null
  if (amount === 0) {
    return <p className="bt-fall">The balance never stood below a high it had reached{sofar}.</p>
  }

  const dated = episode != null && Math.abs(episode.amount - amount) < 1
  const lasted = dated && episode.recoveredUtc ? daysBetween(episode.peakUtc, episode.recoveredUtc) : null

  return (
    <p className="bt-fall">
      <span>
        Max drawdown{sofar} <b className="mono neg">{formatInrSigned(-amount)}</b>{' '}
        <span className="muted">({percent.toFixed(1)}% of its peak)</span>
      </span>
      {dated ? (
        <>
          <span>
            from <b className="mono">{formatInrWhole(episode.peakEquity)}</b> on {formatDayWithYear(episode.peakUtc)}
          </span>
          <span>
            to <b className="mono">{formatInrWhole(episode.troughEquity)}</b> on {formatDayWithYear(episode.troughUtc)}
          </span>
          <span>
            {episode.recoveredUtc ? (
              <>
                back at the peak on {formatDayWithYear(episode.recoveredUtc)}
                {lasted != null && <span className="muted"> · {formatNumber(lasted)} days after it</span>}
              </>
            ) : (
              <span className="warn">not recovered{active ? ' yet' : ' by the end of the run'}</span>
            )}
          </span>
        </>
      ) : (
        <span className="muted">its dates are not in the curve this page was sent</span>
      )}
    </p>
  )
}

/* ---------------------------------------------------------------- months */

function MonthTd({ cell, year, maxAbs }: { cell: MonthCell | null; year: number; maxAbs: number }) {
  if (!cell) return <td className="bt-months__cell" aria-label="no trade" />
  const tone = cell.pnl > 0 ? 'bt-months__cell--pos' : cell.pnl < 0 ? 'bt-months__cell--neg' : ''
  const mag = maxAbs > 0 ? Math.min(1, Math.abs(cell.pnl) / maxAbs) : 0
  const title =
    `${monthLabel(year, cell.month)}: ${formatInrSigned(cell.pnl)} net · ${formatNumber(cell.trades)} ${cell.trades === 1 ? 'trade' : 'trades'}` +
    (cell.charges > 0 ? ` · ${formatInrSigned(cell.gross)} before ${formatInrWhole(cell.charges)} of charges` : '')
  return (
    <td className={`bt-months__cell ${tone}`} style={{ '--mag': mag.toFixed(2) } as React.CSSProperties} title={title}>
      <PnlValue value={cell.pnl} />
      <span className="bt-months__sub">
        {formatNumber(cell.trades)} {cell.trades === 1 ? 'trade' : 'trades'}
      </span>
    </td>
  )
}

function monthsCsv(reading: MonthlyReading): string {
  const rows = reading.years.flatMap((y) => y.months.filter((m): m is MonthCell => m != null).map((m) => ({ year: y.year, m })))
  return toCsv(rows, [
    { header: 'Year', value: (r) => r.year },
    { header: 'Month', value: (r) => r.m.month },
    { header: 'Net P&L (₹)', value: (r) => Math.round(r.m.pnl * 100) / 100 },
    { header: 'Price P&L (₹)', value: (r) => Math.round(r.m.gross * 100) / 100 },
    { header: 'Charges (₹)', value: (r) => Math.round(r.m.charges * 100) / 100 },
    { header: 'Trades', value: (r) => r.m.trades },
  ])
}

/**
 * Year × month of net P&L, a total per year, and a line for each thing that
 * keeps the months from adding up to the run's Net P&L.
 */
export function MonthlyPnlPanel({ view, active }: { view: BacktestRunView; active: boolean }) {
  const costs = useMemo(() => readRunCosts(view), [view])
  const reading = useMemo(
    () => summariseMonths(view.positions, costs, { fromDate: view.fromDate, toDate: view.toDate }),
    [view.positions, view.fromDate, view.toDate, costs],
  )
  const traded = reading.years.some((y) => y.trades > 0)
  const maxAbs = Math.max(0, ...reading.years.flatMap((y) => y.months.map((m) => (m ? Math.abs(m.pnl) : 0))))
  const total = reading.years.reduce((s, y) => s + y.pnl, 0)
  const trades = reading.years.reduce((s, y) => s + y.trades, 0)

  return (
    <Panel
      title={
        <>
          <IconClock /> Net P&L by month
        </>
      }
      actions={
        <>
          <span className="muted" style={{ fontSize: 12 }}>
            IST month of the close · {reading.netted === 'all' ? `charges: ${describeRunCosts(costs)}` : 'before the real costs'}
          </span>
          <ExportCsvButton
            fileName={csvFileName('backtest', String(view.runId), 'months', view.strategyName, view.underlying, view.fromDate, view.toDate)}
            csv={() => monthsCsv(reading)}
            disabled={!traded}
            title="Every month that traded, with its price P&L, charges and trades"
          />
        </>
      }
    >
      {!traded ? (
        <p className="empty">{active ? 'No position has closed yet — a month fills in when one does.' : 'No position closed in this run, so there is no month to show.'}</p>
      ) : (
        <>
          <div className="tablewrap">
            <table className="table bt-months">
              <thead>
                <tr>
                  <th>Year</th>
                  {MONTH_HEADERS.map((m) => (
                    <th key={m} className="r">
                      {m}
                    </th>
                  ))}
                  <th className="r bt-months__total">Year</th>
                </tr>
              </thead>
              <tbody>
                {reading.years.map((y) => (
                  <tr key={y.year}>
                    <td className="bt-months__year">{y.year}</td>
                    {y.months.map((cell, i) => (
                      <MonthTd key={i} cell={cell} year={y.year} maxAbs={maxAbs} />
                    ))}
                    <td className="bt-months__cell bt-months__total">
                      {y.trades > 0 ? (
                        <>
                          <PnlValue value={y.pnl} />
                          <span className="bt-months__sub">
                            {formatNumber(y.trades)} {y.trades === 1 ? 'trade' : 'trades'}
                          </span>
                        </>
                      ) : null}
                    </td>
                  </tr>
                ))}
              </tbody>
              {reading.years.length > 1 && (
                <tfoot>
                  <tr>
                    <td className="bt-months__year">All</td>
                    <td colSpan={12} />
                    <td className="bt-months__cell bt-months__total">
                      <PnlValue value={total} />
                      <span className="bt-months__sub">
                        {formatNumber(trades)} {trades === 1 ? 'trade' : 'trades'}
                      </span>
                    </td>
                  </tr>
                </tfoot>
              )}
            </table>
          </div>
          <MonthNotes view={view} reading={reading} total={total} active={active} />
        </>
      )}
    </Panel>
  )
}

function MonthNotes({ view, reading, total, active }: { view: BacktestRunView; reading: MonthlyReading; total: number; active: boolean }) {
  const costs = readRunCosts(view)
  const notes: string[] = ['Each position is booked in the IST month it closed; a blank month closed none.']
  if (costs.kind === 'flat') {
    notes.push(`Net of ${describeRunCosts(costs)}, on both fills of each position — ${formatInrWhole(reading.charges)} in these months.`)
  } else if (costs.kind === 'none') {
    notes.push('The run set no charges, so net is the price P&L.')
  } else {
    notes.push(
      `The run was charged the real costs — ${formatInrWhole(view.pnl.charges)} of brokerage, STT, exchange, SEBI, stamp duty and GST — ` +
        'which the runner keeps as one total, not per trade, so the months are before them. Slippage is already in the fill prices.',
    )
  }
  if (reading.open > 0) {
    notes.push(`${formatNumber(reading.open)} open ${reading.open === 1 ? 'position is' : 'positions are'} in no month until ${reading.open === 1 ? 'it closes' : 'they close'}.`)
  }
  if (reading.unsized > 0) {
    notes.push(
      `${formatNumber(reading.unsized)} closed ${reading.unsized === 1 ? 'position' : 'positions'} could not be sized from the run's fills, so ${reading.unsized === 1 ? 'its' : 'their'} charges are not in the months.`,
    )
  }
  if (!active) {
    notes.push(
      Math.abs(total - view.pnl.total) < 1
        ? `The months add up to the run's Net P&L, ${formatInrSigned(view.pnl.total)}.`
        : `The months add up to ${formatInrSigned(total)}; the run's Net P&L is ${formatInrSigned(view.pnl.total)}.`,
    )
  }
  return (
    <ul className="bt-notes">
      {notes.map((n) => (
        <li key={n}>{n}</li>
      ))}
    </ul>
  )
}
