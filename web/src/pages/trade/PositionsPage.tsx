/**
 * Trade → Positions: every open leg, in every book, on every underlying
 * (GET /api/Positions/open), grouped by account and then by run: strategy
 * runs and manual books alike. An admin sees every account's, with a filter
 * to one; a trader, their own (the API scopes the answer).
 *
 * Each leg shows its mark and how old that mark is (stale past 30 s, and
 * said), its open P&L before exit charges, its own stop and target, its
 * greeks, and its carry-forward tick, which can be changed here, as can a
 * leg be squared off: the checkup's "close them on Positions, or tick
 * Carry" is done on this page. The run itself (stop, risk rules) is on its
 * own page, one click away.
 *
 * It replaces the v1 Positions page, which showed one Simulator run picked
 * from a list and none of the day's live runs.
 */

import { useMemo, useState } from 'react'
import { Link } from 'react-router-dom'
import { useAuth } from '../../lib/auth'
import { carryControl, carryHint } from '../../lib/carry'
import { dayMonth, legLabel, plainNumber, strategyLabel } from '../../lib/desk'
import type { Scope } from '../../lib/desk'
import { formatDelta, formatIv, formatThetaPerDay, greeksTitle, thetaTone } from '../../lib/greeks'
import { formatInrSigned, formatPrice } from '../../lib/format'
import { ageText, groupPositions, markState, positionAccounts, totalSums } from '../../lib/openPositions'
import type { PositionAccount, PositionRun } from '../../lib/openPositions'
import { useClosePositions, useMarketSession, useOpenPositions, useSetCarryForward } from '../../lib/queries'
import type { OpenPosition } from '../../lib/types'
import { InlineError } from '../../components/ui'
import { Chip, Money, Swatch, Waiting } from '../desk/parts'
import '../desk/desk.css'
import './trade.css'

/** Legs are closed and ticked from here: in a session the marks are read every 5 s. */
const POLL_OPEN_MS = 5_000
const POLL_CLOSED_MS = 60_000

function Mark({ p }: { p: OpenPosition }) {
  const state = markState(p)
  if (state === 'none') return <span className="warn">no mark</span>
  const when = p.markUtc ? `Marked ${new Date(p.markUtc).toLocaleString('en-IN', { timeZone: 'Asia/Kolkata', hour12: false })} IST` : 'No time on the mark'
  return (
    <>
      <span className="dk-n">{plainNumber(p.markPrice!, p.markPrice! >= 1000 ? 0 : 2)}</span>
      <span className={`tr-sub ${state === 'stale' ? 'warn' : 'dk-t3'}`} title={when}>
        {state === 'stale' ? `stale · ${p.markAgeSeconds != null ? ageText(p.markAgeSeconds) : 'age unknown'}` : ageText(p.markAgeSeconds ?? 0)}
      </span>
    </>
  )
}

function Levels({ p }: { p: OpenPosition }) {
  if (p.stopLossPrice == null && p.targetPrice == null) return <span className="dk-t3">—</span>
  return (
    <span className="dk-n">
      <span className="neg">{p.stopLossPrice == null ? '—' : formatPrice(p.stopLossPrice)}</span>
      <span className="dk-t3"> / </span>
      <span className="pos">{p.targetPrice == null ? '—' : formatPrice(p.targetPrice)}</span>
    </span>
  )
}

function LegRow({ p, run }: { p: OpenPosition; run: PositionRun }) {
  const setCarry = useSetCarryForward()
  const close = useClosePositions()
  // Whoever can see a leg here may change it: the API lists a trader's own legs and an admin's every leg.
  const tick = carryControl({ status: 'Open', carryForward: p.carryForward, symbol: p.symbol }, { canControl: true, canCarryForward: true, isManualBook: run.isManualBook })
  const g = p.greeks
  const name = legLabel(p)

  function squareOff() {
    const rest = run.isManualBook ? 'the rest of the book stays open' : 'the run keeps trading and may open another'
    if (window.confirm(`Square off ${name} at the last price?\n\nThis closes only this leg; ${rest}.`)) {
      close.mutate({ runId: p.runId, positionIds: [p.positionId] })
    }
  }

  return (
    <tr>
      <td className="tr-leg">
        <span className="tr-legname">{name}</span>
        {p.expiryDate && <span className="dk-t3 dk-xs"> {dayMonth(p.expiryDate)}</span>}
        {p.carriedFromRunId != null && (
          <>
            {' '}
            <Link
              to={`/trade/runs/${p.carriedFromRunId}`}
              className="dk-chip dk-chip--warn"
              title={`Carried into this book at the close, at its entry price, from run #${p.carriedFromRunId}${p.carriedFromStrategy ? ` (${strategyLabel(p.carriedFromStrategy)})` : ''}`}
            >
              carried from #{p.carriedFromRunId}
            </Link>
          </>
        )}
        <span className="tr-sub dk-t3 tr-only-s">
          {p.direction === 'SHORT' ? 'short' : 'long'} {p.lots} × {p.lotSize} · entry {formatPrice(p.entryPrice)}
          {g ? ` · θ ${formatThetaPerDay(g.thetaRupeesPerDay)}` : ''}
        </span>
        {(setCarry.isError || close.isError) && <InlineError error={setCarry.error ?? close.error} />}
      </td>
      <td className="tr-hide-s">
        <Chip tone={p.direction === 'SHORT' ? 'neg' : 'pos'}>{p.direction === 'SHORT' ? 'Short' : 'Long'}</Chip>
      </td>
      <td className="r dk-n tr-hide-s" title={`${p.quantity} units`}>
        {p.lots} × {p.lotSize}
      </td>
      <td className="r dk-n tr-hide-s">{formatPrice(p.entryPrice)}</td>
      <td className="r">
        <Mark p={p} />
      </td>
      <td className="r">{p.unrealizedPnl != null ? <Money value={p.unrealizedPnl} /> : <span className="dk-t3">—</span>}</td>
      <td className="r tr-hide-s">
        <Levels p={p} />
      </td>
      <td className="r dk-n tr-hide-m" title={g ? greeksTitle(g) : 'No source could price this leg'}>
        {g ? (
          <>
            {formatDelta(g.delta)}
            <span className="tr-sub dk-t3">{g.ivPercent != null ? `IV ${formatIv(g.ivPercent)}` : g.source}</span>
          </>
        ) : (
          <span className="dk-t3">—</span>
        )}
      </td>
      <td className={`r dk-n tr-hide-m ${g ? thetaTone(g.thetaRupeesPerDay) : ''}`}>{g ? formatThetaPerDay(g.thetaRupeesPerDay) : '—'}</td>
      <td className="r dk-n tr-hide-m">{g ? formatInrSigned(g.vegaRupeesPerIvPoint) : '—'}</td>
      <td className="c">
        <input
          type="checkbox"
          className="tr-carry"
          checked={tick.checked}
          disabled={tick.disabled || setCarry.isPending}
          title={tick.title}
          aria-label={`Carry ${name} forward`}
          onChange={(e) => setCarry.mutate({ runId: p.runId, positionId: p.positionId, carryForward: e.target.checked })}
        />
      </td>
      <td className="r tr-hide-s">
        <button type="button" className="tr-close" disabled={close.isPending} onClick={squareOff} title="Square off this leg at the last price">
          {close.isPending ? 'Closing…' : 'Square off'}
        </button>
      </td>
    </tr>
  )
}

function RunBlock({ run }: { run: PositionRun }) {
  return (
    <div className="tr-run">
      <div className="tr-run__head">
        <Link to={`/trade/runs/${run.runId}`} className="tr-run__name">
          {run.isManualBook ? 'Manual book' : strategyLabel(run.strategyName)}
        </Link>
        <span className="dk-t3 dk-xs">
          run #{run.runId}
          {run.isManualBook ? '' : ` · ${run.underlyings.join(', ')}`} · {run.legs} leg{run.legs === 1 ? '' : 's'}
        </span>
        <span className="tr-grow" />
        {run.thetaPerDay != null && <span className={`dk-xs tr-hide-s ${thetaTone(run.thetaPerDay)}`}>θ {formatThetaPerDay(run.thetaPerDay)}</span>}
        <Money value={run.openPnl} className="tr-run__pnl" />
      </div>
      <table className="dk-t tr-legs">
        <thead>
          <tr>
            <th>Leg</th>
            <th className="tr-hide-s">Side</th>
            <th className="r tr-hide-s">Lots × size</th>
            <th className="r tr-hide-s">Entry</th>
            <th className="r">Mark</th>
            <th className="r">P&L</th>
            <th className="r tr-hide-s">SL / target</th>
            <th className="r tr-hide-m" title="Delta per unit, with IV">
              Δ
            </th>
            <th className="r tr-hide-m" title="Theta for this leg: rupees a day of time is worth to it">
              Θ ₹/day
            </th>
            <th className="r tr-hide-m" title="Rupees this leg makes on a one-point rise in IV">
              Vega ₹
            </th>
            <th className="c" title={carryHint(run.isManualBook)}>
              Carry
            </th>
            <th className="tr-hide-s" aria-label="Actions" />
          </tr>
        </thead>
        <tbody>
          {run.positions.map((p) => (
            <LegRow key={p.positionId} p={p} run={run} />
          ))}
        </tbody>
      </table>
      {run.unmarked > 0 && (
        <p className="dk-note tr-run__note">
          {run.unmarked} leg{run.unmarked === 1 ? ' has' : 's have'} no mark, so {run.unmarked === 1 ? 'its' : 'their'} P&L is left out of the figure above.
        </p>
      )}
    </div>
  )
}

function AccountBlock({ account, tone, multi }: { account: PositionAccount; tone: 1 | 2 | null; multi: boolean }) {
  return (
    <section className="tr-acct" aria-label={account.name}>
      <header className="tr-acct__head">
        {multi && <Swatch tone={tone} />}
        <h2>{account.name}</h2>
        <span className="dk-t3 dk-xs">
          {account.legs} open leg{account.legs === 1 ? '' : 's'} in {account.runs.length} book{account.runs.length === 1 ? '' : 's'}
          {account.thetaPerDay != null && (
            <>
              {' · θ '}
              <span className={thetaTone(account.thetaPerDay)}>{formatThetaPerDay(account.thetaPerDay)}</span>
            </>
          )}
        </span>
        <span className="tr-grow" />
        <span className="dk-t3 dk-xs tr-hide-s">open P&L</span>
        <Money value={account.openPnl} className="tr-acct__pnl" />
      </header>
      {account.runs.map((run) => (
        <RunBlock key={run.runId} run={run} />
      ))}
    </section>
  )
}

export function PositionsPage() {
  const { isAdmin } = useAuth()
  const nse = useMarketSession()
  const mcx = useMarketSession('MCX', 'COM')
  const marketOpen = nse.data?.isMarketOpen === true || mcx.data?.isMarketOpen === true
  const open = useOpenPositions(marketOpen ? POLL_OPEN_MS : POLL_CLOSED_MS)
  const [scope, setScope] = useState<Scope>('all')
  const positions = open.data?.positions
  const accounts = useMemo(() => positionAccounts(positions ?? []), [positions])
  const shown: Scope = scope !== 'all' && accounts.some((a) => a.id === scope) ? scope : 'all'
  const groups = useMemo(() => groupPositions(positions ?? [], shown === 'all' ? null : shown), [positions, shown])
  const totals = totalSums(groups)
  // The Desk's account colours: the first two accounts by user id.
  const toneOf = (id: number): 1 | 2 | null => {
    const i = accounts.findIndex((a) => a.id === id)
    return i === 0 ? 1 : i === 1 ? 2 : null
  }

  return (
    <div className="page tr">
      <div className="tr-bar">
        {isAdmin && accounts.length > 1 && (
          <span className="dk-seg" role="group" aria-label="Accounts">
            <button type="button" aria-pressed={shown === 'all'} onClick={() => setScope('all')}>
              All accounts
            </button>
            {accounts.map((a) => (
              <button key={a.id} type="button" aria-pressed={shown === a.id} onClick={() => setScope(a.id)}>
                {a.name}
              </button>
            ))}
          </span>
        )}
        {open.data && (
          <span className="tr-summary">
            <b>{totals.legs}</b> open leg{totals.legs === 1 ? '' : 's'}
            {totals.legs > 0 && (
              <>
                {' · open P&L '}
                <Money value={totals.openPnl} />
                <span className="dk-t3"> before exit charges</span>
                {totals.thetaPerDay != null && (
                  <>
                    {' · θ '}
                    <span className={thetaTone(totals.thetaPerDay)}>{formatThetaPerDay(totals.thetaPerDay)}</span>
                  </>
                )}
              </>
            )}
          </span>
        )}
        <span className="tr-grow" />
        {open.data && (
          <span className="dk-t3 dk-xs">
            marks as of {new Date(open.data.asOfUtc).toLocaleTimeString('en-IN', { timeZone: 'Asia/Kolkata', hour12: false })} IST
          </span>
        )}
      </div>

      {open.data && totals.stale > 0 && (
        <p className="tr-stale" role="status">
          {totals.stale} of {totals.legs} marks are older than 30 s
          {marketOpen ? ': their P&L is not now.' : ': the markets are closed, so they are the last quotes of the session.'}
        </p>
      )}

      {open.isError && !open.data ? (
        <InlineError error={open.error} />
      ) : !open.data ? (
        <Waiting>Reading the open legs…</Waiting>
      ) : groups.length === 0 ? (
        <p className="tr-empty">No leg is open{shown === 'all' ? '' : ' in this account'}: every run and book is flat.</p>
      ) : (
        <div className="tr-sheet">
          {groups.map((a) => (
            <AccountBlock key={a.userId} account={a} tone={toneOf(a.userId)} multi={accounts.length > 1} />
          ))}
        </div>
      )}

      {open.data && totals.legs > 0 && (
        <p className="dk-note tr-foot">
          P&L is at the mark, before the charges of closing; the charges of the fills so far are already in each run’s net. Carry:
          in a strategy run a ticked leg moves to its owner’s manual book at the market close; in a manual book an unticked leg
          is squared off at its exchange’s close.
        </p>
      )}
    </div>
  )
}
