/**
 * The Desk (/desk): the one screen kept open all day, for admins and traders
 * alike. One status strip, then one sheet of panels in the order of the
 * part of the day's jobs: readiness and the outlook before the open, runs and
 * net P&L in the session, the result and what is held overnight after the
 * close. It reorders itself at 09:15 and 15:30; the phase switches let the
 * viewer look at any part, and the pin holds one for the rest of the day.
 *
 * The API scopes every answer to the viewer (a trader's runs are their own)
 * and each panel checks its own grant, so the same page serves both roles.
 * The decisions live in lib/desk.ts; this file wires them to the queries.
 */

import { useEffect, useMemo, useState } from 'react'
import type { ReactNode } from 'react'
import { createPortal } from 'react-dom'
import { useAuth } from '../../lib/auth'
import { accessFor, allows } from '../../lib/modules'
import type { PanelKey, PhasePick, PhasePin, Scope } from '../../lib/desk'
import {
  PHASES,
  buildGrid,
  carriedRunIds,
  clockPhase,
  dayLabel,
  dayOf,
  deskAccounts,
  deskLayout,
  isTradingRun,
  readPin,
  scopeRuns,
  shownPhase,
  validScope,
  writePin,
} from '../../lib/desk'
import { deskLegsPoll, useMarketSession, useOpenPositions } from '../../lib/queries'
import { IconPin } from '../../components/icons'
import { useStripSlot } from '../../components/shell/stripSlot'
import type { DeskLinks, DeskView } from './data'
import { useDeskLinks, useNow, useShownDay } from './data'
import { StatusStrip } from './StatusStrip'
import { PlanGrid, RunsGrid } from './RunsGrid'
import { DayPnl } from './DayPnl'
import { Indices } from './Indices'
import { Legs } from './Legs'
import { Timeline } from './Timeline'
import { News } from './News'
import { Movers } from './Movers'
import { Checkup } from './Checkup'
import { Flows, Forecast, Overnight, Scores, Week } from './Outlook'
import './desk.css'

/** The browser's storage, or null where reading it throws (private windows, blocked site data). */
function deskStorage(): Storage | null {
  try {
    return window.localStorage
  } catch {
    return null
  }
}

const PANELS: Record<PanelKey, (p: { view: DeskView; links: DeskLinks }) => ReactNode> = {
  grid: RunsGrid,
  plan: PlanGrid,
  pnl: DayPnl,
  indices: Indices,
  legs: Legs,
  timeline: Timeline,
  news: ({ view, links }) => <News view={view} links={links} limit={view.phase === 'pre' ? 5 : 6} />,
  movers: Movers,
  checkup: Checkup,
  overnight: Overnight,
  forecast: Forecast,
  scores: Scores,
  flows: Flows,
  week: Week,
}

/** "updated 3 s ago": its own one-second clock, so the page does not re-render every second. */
function Updated({ at }: { at: number }) {
  const now = useNow(1000)
  if (!at) return null
  const s = Math.max(0, Math.round((now - at) / 1000))
  return <span className="dk-updated">updated {s < 60 ? `${s} s` : `${Math.round(s / 60)} min`} ago</span>
}

function DeskBar({ view, pin, onPick, onPin, onScope, updatedAt }: {
  view: DeskView
  pin: boolean
  onPick: (phase: DeskView['phase']) => void
  onPin: () => void
  onScope: (scope: Scope) => void
  updatedAt: number
}) {
  return (
    <div className="dk-bar">
      <span className="dk-seg" role="group" aria-label="Part of the day">
        {PHASES.map((p) => (
          <button
            key={p.key}
            type="button"
            aria-pressed={view.phase === p.key}
            title={p.key === view.clock ? `${p.title}: where the clock is now` : `Look at the desk as it is ${p.title.toLowerCase()}`}
            onClick={() => onPick(p.key)}
          >
            <span className="dk-long">{p.label}</span>
            <span className="dk-short">{p.short}</span>
            {p.key === view.clock && <span className="dk-now">now</span>}
          </button>
        ))}
      </span>
      <button
        type="button"
        className="dk-pin"
        aria-pressed={pin}
        onClick={onPin}
        title={pin ? 'Held for today. Click to follow the clock again.' : 'Hold this view for today, even as the market opens or closes'}
        aria-label={pin ? 'Stop holding this view' : 'Hold this view for today'}
      >
        <IconPin />
      </button>
      {view.isAdmin && view.allAccounts.length > 1 && (
        <span className="dk-seg" role="group" aria-label="Accounts">
          <button type="button" aria-pressed={view.scope === 'all'} onClick={() => onScope('all')}>
            <span className="dk-long">All accounts</span>
            <span className="dk-short">All</span>
          </button>
          {view.allAccounts.map((a) => (
            <button key={a.id} type="button" aria-pressed={view.scope === a.id} onClick={() => onScope(a.id)}>
              {a.name}
            </button>
          ))}
        </span>
      )}
      <span className="dk-grow" />
      <Updated at={updatedAt} />
    </div>
  )
}

export function DeskPage() {
  const { user } = useAuth()
  const access = useMemo(() => accessFor(user), [user])
  const links = useDeskLinks(access)
  const slot = useStripSlot()
  // Fifteen seconds is soon enough to reorder the Desk at 09:15 and 15:30.
  const nowMs = useNow(15_000)
  const strategies = allows(access, 'strategies')
  const shownDay = useShownDay(nowMs, strategies)
  const { today, day } = shownDay
  const nse = useMarketSession()
  const mcx = useMarketSession('MCX', 'COM')
  const clock = clockPhase(nowMs, nse.data)

  const [pick, setPick] = useState<PhasePick | null>(null)
  const [pin, setPin] = useState<PhasePin | null>(() => readPin(deskStorage()))
  const shown = shownPhase(clock, pick, pin, today)
  // A pick lasts until the clock moves; drop it then, so an old one cannot come back.
  useEffect(() => {
    if (pick && pick.from !== clock) setPick(null)
  }, [pick, clock])

  const dayRuns = shownDay.runs
  const trading = useMemo(() => dayRuns?.filter(isTradingRun), [dayRuns])
  const allAccounts = useMemo(() => deskAccounts(trading ?? []), [trading])
  const [scopeWanted, setScope] = useState<Scope>('all')
  const scope = access.isAdmin ? validScope(scopeWanted, allAccounts) : 'all'
  const accounts = useMemo(() => (scope === 'all' ? allAccounts : allAccounts.filter((a) => a.id === scope)), [scope, allAccounts])
  const runs = useMemo(() => (trading ? scopeRuns(trading, scope) : undefined), [trading, scope])

  // Which runs carried a leg into a manual book, in any account: a carried leg names its run.
  const open = useOpenPositions(deskLegsPoll(clock === 'live'), strategies)
  const grid = useMemo(
    () => (runs ? buildGrid(runs, accounts, carriedRunIds(open.data?.positions)) : null),
    [runs, accounts, open.data],
  )

  const view: DeskView = {
    phase: shown.phase,
    clock,
    day,
    today,
    nowMs,
    access,
    isAdmin: access.isAdmin,
    scope,
    accounts,
    allAccounts,
    runs,
    runsError: shownDay.error,
    grid,
    scopeName: scope === 'all' ? null : (accounts[0]?.name ?? null),
    nse: nse.data,
    mcx: mcx.data,
    nextSession: dayOf(nse.data?.nextMarketOpenUtc),
  }

  function pickPhase(phase: DeskView['phase']) {
    if (shown.how === 'pinned') {
      // Choosing another part of the day moves the pin with it.
      const next = { phase, date: today }
      writePin(deskStorage(), next)
      setPin(next)
      return
    }
    setPick({ phase, from: clock })
  }

  function togglePin() {
    const next = shown.how === 'pinned' ? null : { phase: shown.phase, date: today }
    writePin(deskStorage(), next)
    setPin(next)
    setPick(null)
  }

  const bar = (
    <DeskBar
      view={view}
      pin={shown.how === 'pinned'}
      onPick={pickPhase}
      onPin={togglePin}
      onScope={setScope}
      updatedAt={shownDay.updatedAt}
    />
  )

  const rows = deskLayout(shown.phase, access)
  return (
    <div className="dk">
      {slot ? createPortal(bar, slot) : bar}
      {view.day !== today && (
        <p className="dk-note" role="status">
          No session today: the desk shows {dayLabel(day)}, the last day with runs.
        </p>
      )}
      <StatusStrip view={view} />
      {rows.length > 0 && (
        <div className="dk-sheet">
          {rows.flat().map((s) => {
            const Panel = PANELS[s.key]
            const Under = s.under ? PANELS[s.under] : null
            return (
              <section key={s.key} className="dk-cell" style={{ gridColumn: `span ${s.span}` }}>
                {Under ? (
                  <>
                    <div className="dk-stack">
                      <Panel view={view} links={links} />
                    </div>
                    <div className="dk-stack">
                      <Under view={view} links={links} />
                    </div>
                  </>
                ) : (
                  <Panel view={view} links={links} />
                )}
              </section>
            )
          })}
        </div>
      )}
    </div>
  )
}
