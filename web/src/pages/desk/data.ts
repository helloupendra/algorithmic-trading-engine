/**
 * The Desk's shared state and the reads more than one panel needs. Each hook
 * reads through the same query keys as the pages that own the data, so the
 * status strip and a panel asking the same question make one request.
 */

import { useCallback, useEffect, useMemo, useState } from 'react'
import type { Access } from '../../lib/modules'
import { allows, navFor } from '../../lib/modules'
import type { DeskAccount, DeskGrid, DeskLeg, Phase, PlanView, Scope } from '../../lib/desk'
import { buildGrid, dayOf, deskDay, deskLegs, figureTone, istDay, planView, shiftDay } from '../../lib/desk'
import {
  RUN_HISTORY_PAGE,
  deskLegsPoll,
  useCheckupLatest,
  useDeskPlan as useDeskPlanQuery,
  useFeeds,
  useForecasts,
  useLiveRunHistory,
  useLiveRuns,
  useMarketSession,
  useOpenPositions,
  useProviders,
  useStructuralSharing,
} from '../../lib/queries'
import { checkupNow } from '../../lib/checkupNow'
import { useLiveConnection, useLivePrices } from '../../lib/live'
import { answerAsOf } from '../../lib/asOf'
import { withLegMarks, withLiveMarks } from '../../lib/liveMarks'
import type { RunLegs } from '../../lib/liveMarks'
import type { LiveRunSummary, MarketSessionInfo } from '../../lib/types'

/** Everything a panel needs to know about the Desk it sits on. */
export interface DeskView {
  /** The phase shown, and the one the clock is in. */
  phase: Phase
  clock: Phase
  /** The day reported on (IST): today, or the last session on a day without one. */
  day: string
  /** The calendar day in IST. */
  today: string
  nowMs: number
  access: Access
  isAdmin: boolean
  scope: Scope
  /** The accounts in scope, and every account with runs. */
  accounts: DeskAccount[]
  allAccounts: DeskAccount[]
  /** The day's trading runs in scope; undefined until they arrive. */
  runs: LiveRunSummary[] | undefined
  /** When the run list was asked for (lib/asOf.ts): a price pushed after it is newer than its figures. */
  runsAsOf: number
  runsError: unknown
  /**
   * The grid as the run list answered it: its rows, cells and marks. What a
   * panel shows as money is useLiveGrid's, the same grid with the live runs'
   * open books at the pushed prices.
   */
  grid: DeskGrid | null
  /** Runs a leg was carried from, read off the manual books: the grid's C mark. */
  carried: ReadonlySet<number>
  /** Every open leg in view, by run, that the live runs' open books are re-priced from. */
  legs: RunLegs | null
  /** The one account's name when the scope is one account. */
  scopeName: string | null
  nse: MarketSessionInfo | undefined
  mcx: MarketSessionInfo | undefined
  /** The IST date of the next NSE open. */
  nextSession: string | null
}

/** The clock, re-read every `everyMs`: often enough to move the phase at 09:15 and 15:30 on time. */
export function useNow(everyMs: number): number {
  const [now, setNow] = useState(() => Date.now())
  useEffect(() => {
    const id = window.setInterval(() => setNow(Date.now()), everyMs)
    return () => window.clearInterval(id)
  }, [everyMs])
  return now
}

/**
 * An element's width in CSS pixels, kept current as it resizes: what a chart
 * drawn in pixels (not stretched from a viewBox) needs. Zero until measured.
 */
export function useWidth<T extends HTMLElement>(): [(el: T | null) => void, number] {
  const [el, setEl] = useState<T | null>(null)
  const [width, setWidth] = useState(0)
  useEffect(() => {
    if (!el) return
    // The observer reports the first size as soon as it starts watching.
    const observer = new ResizeObserver((entries) => setWidth(Math.round(entries[0].contentRect.width)))
    observer.observe(el)
    return () => observer.disconnect()
  }, [el])
  return [useCallback((next: T | null) => setEl(next), []), width]
}

/**
 * The day a page about today's runs reports on, and those runs (alert-only
 * runs included): today, or on a day with no session and no runs the last
 * day that had some, so a Saturday opens on Friday rather than on nothing.
 * The Desk, the Orders page and the Tracks view read it through the same
 * query keys, so moving between them asks nothing twice.
 */
export function useShownDay(nowMs: number, enabled: boolean) {
  const today = istDay(nowMs)
  const nse = useMarketSession()
  const todayRuns = useLiveRunHistory({ fromDate: today, toDate: today, take: RUN_HISTORY_PAGE }, enabled)
  const noSession = nse.data?.isTradingDay === false && todayRuns.data?.length === 0
  const recent = useLiveRunHistory({ fromDate: shiftDay(today, -7), toDate: shiftDay(today, -1), take: RUN_HISTORY_PAGE }, enabled && noSession)
  const day = deskDay(today, nse.data?.isTradingDay, todayRuns.data?.length ?? 0, recent.data)
  const runs = day === today ? todayRuns.data : recent.data?.filter((r) => dayOf(r.startedUtc) === day)
  return {
    today,
    day,
    runs,
    /** When the list the runs came from was asked for: the base the pushed prices are laid over. */
    asOf: day === today ? answerAsOf(todayRuns) : answerAsOf(recent),
    nse: nse.data,
    error: todayRuns.isError ? todayRuns.error : recent.isError ? recent.error : null,
    updatedAt: todayRuns.dataUpdatedAt,
  }
}

/**
 * The Desk's grid with every live run's open book at the pushed prices of
 * its legs (liveMarks.runsWithTicks over the open legs the Desk already
 * reads), and the grid itself while nothing newer is known. Every panel that
 * shows money reads this one, so the strip, the grid's cells and totals, the
 * Day P&L rows and its curve's end are the same numbers at every push; a
 * panel that shows none (indices, news, the outlook) is not re-rendered by a
 * price it does not show.
 */
export function useLiveGrid(view: DeskView): DeskGrid | null {
  // `listed` is what `grid` was built from; `runs` is that list re-priced.
  const { grid, accounts, carried, runs: listed } = view
  const runs = useLiveRuns(listed, view.runsAsOf, view.legs)
  const live = useMemo(
    () => (!grid || !runs || runs === listed ? grid : buildGrid(runs, accounts, carried)),
    [grid, runs, listed, accounts, carried],
  )
  // A row whose runs no price reached keeps its object, so its memoised line does not re-render.
  return useStructuralSharing(live)
}

/** The class a rupee figure is coloured with; flat for anything that rounds to ₹0. */
export function toneClass(value: number | null | undefined): string {
  const t = figureTone(value)
  return t === 'flat' ? 'dk-flat' : t ?? ''
}

/**
 * Where a panel's "more" link goes, from the workspace registry: the tab's
 * page when this viewer has it, or none, so a panel never links to a page
 * its reader would be refused.
 */
export function useDeskLinks(access: Access) {
  return useMemo(() => {
    const pages = navFor(access).flatMap((w) => w.pages)
    const to = (tab: string) => pages.find((p) => p.tab.key === tab)?.to ?? null
    return {
      runs: to('runs') ?? to('history'),
      history: to('history'),
      // A run's page is one URL for everyone; the API refuses someone else's.
      runBase: '/trade/runs',
      positions: to('positions'),
      chain: to('chain'),
      movers: to('movers'),
      news: to('news'),
      factors: to('factors'),
      forecasts: to('forecasts'),
      incidents: to('incidents'),
      checkups: to('health') ? '/system/checkups' : null,
      risk: to('risk'),
      book: to('ticket'),
    }
  }, [access])
}

export type DeskLinks = ReturnType<typeof useDeskLinks>

/**
 * Every open leg in scope, across runs and manual books, largest first (GET
 * /api/Positions/open, one request for every underlying); null until it has
 * answered, so a count is never a guess. Each leg's LTP and P&L move with its
 * pushed price; the order is the answer's, so rows do not swap on every tick.
 */
export function useDeskLegs(view: DeskView): { legs: DeskLeg[] | null; error: unknown } {
  const open = useOpenPositions(deskLegsPoll(view.clock === 'live', useLiveConnection()), allows(view.access, 'strategies'))
  const positions = open.data?.positions
  const answeredAt = answerAsOf(open)
  const prices = useLivePrices(useMemo(() => (positions ?? []).map((p) => p.symbol), [positions]))
  const ordered = useMemo(
    () =>
      positions
        ? deskLegs(positions, { today: view.today, nextSession: view.nextSession, userId: view.scope === 'all' ? null : view.scope })
        : null,
    [positions, view.today, view.nextSession, view.scope],
  )
  const legs = useMemo(
    () => (ordered && positions ? withLegMarks(ordered, withLiveMarks(positions, prices, { nowMs: Date.now(), answeredAtMs: answeredAt })) : ordered),
    [ordered, positions, prices, answeredAt],
  )
  return { legs, error: open.isError ? open.error : null }
}

/**
 * The underlyings held in the Desk's scope, from the same open legs as
 * useDeskLegs but without their prices: the news panel's "held" tab needs
 * which names are held, and a panel that holds the legs' prices is
 * re-rendered by every push of every leg.
 */
export function useHeldUnderlyings(view: DeskView): ReadonlySet<string> {
  const open = useOpenPositions(deskLegsPoll(view.clock === 'live', useLiveConnection()), allows(view.access, 'strategies'))
  const positions = open.data?.positions
  const userId = view.scope === 'all' ? null : view.scope
  return useMemo(
    () => new Set((positions ?? []).filter((p) => userId == null || p.userId === userId).map((p) => p.underlying.toUpperCase())),
    [positions, userId],
  )
}

/**
 * The morning plan against what is live (admin: GET /api/Desk/plan), in the
 * Desk's scope. `missing` when the server has no plan file, which is a thing
 * to say, not an empty plan.
 */
export function useDeskPlan(view: DeskView): {
  plan: PlanView | null
  file: string | null
  ready: boolean
  /** No plan file on the server; `searched` says where it was looked for. */
  missing: boolean
  searched: string[]
  error: unknown
} {
  const q = useDeskPlanQuery(view.isAdmin && allows(view.access, 'strategies'))
  const scope = view.scope
  const plan = useMemo(() => (q.data ? planView(q.data, scope === 'all' ? null : [scope]) : null), [q.data, scope])
  const error = q.error as { status?: number; body?: { searched?: unknown } } | null
  const missing = error?.status === 404
  const searched = missing && Array.isArray(error?.body?.searched) ? error.body.searched.map(String) : []
  return {
    plan,
    file: q.data?.file ?? null,
    ready: q.data !== undefined || q.isError,
    missing,
    searched,
    error: q.isError && !missing ? q.error : null,
  }
}

/** The forecasts of the Desk's day (analysis grant). */
export function useDayForecasts(view: DeskView) {
  return useForecasts({ from: view.day, to: view.day })
}

/**
 * Sentinel's latest checkup with what the Desk can read now laid over it
 * (lib/checkupNow.ts): the plan for every account, not the Desk's scope, as
 * Sentinel checks it; the connectors; the feeds. Every read goes through the
 * key the rest of the Desk already asks, so this adds no request. `waiting`
 * polls the checkup fast while an asked-for one is on its way.
 */
export function useCheckupNow(view: DeskView, waiting = false) {
  const latest = useCheckupLatest(waiting)
  const strategies = allows(view.access, 'strategies')
  const plan = useDeskPlanQuery(view.isAdmin && strategies)
  const runs = useLiveRunHistory({ fromDate: view.today, toDate: view.today, take: RUN_HISTORY_PAGE }, strategies)
  const providers = useProviders()
  const feeds = useFeeds()
  const c = latest.data?.latest ?? null
  const now = useMemo(
    () =>
      c
        ? checkupNow(c, {
            nowMs: view.nowMs,
            plan: { data: plan.data, atMs: plan.dataUpdatedAt },
            runs: runs.data,
            providers: { data: providers.data, atMs: providers.dataUpdatedAt },
            feeds: { data: feeds.data, atMs: feeds.dataUpdatedAt },
            nse: view.nse,
            mcx: view.mcx,
          })
        : null,
    [c, view.nowMs, plan.data, plan.dataUpdatedAt, runs.data, providers.data, providers.dataUpdatedAt, feeds.data, feeds.dataUpdatedAt, view.nse, view.mcx],
  )
  return { latest, checkup: c, now }
}
