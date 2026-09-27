/**
 * The Desk's shared state and the reads more than one panel needs. Each hook
 * reads through the same query keys as the pages that own the data, so the
 * status strip and a panel asking the same question make one request.
 */

import { useEffect, useMemo, useState } from 'react'
import type { Access } from '../../lib/modules'
import { allows, navFor } from '../../lib/modules'
import type { DeskAccount, DeskGrid, DeskLeg, Phase, Plan, Scope } from '../../lib/desk'
import { byUnderlying, deskLegs, figureTone, morningCheckupId, todaysPlan } from '../../lib/desk'
import {
  useCheckup,
  useCheckupLatest,
  useCheckups,
  useDeskPositions,
  useForecasts,
} from '../../lib/queries'
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
  runsError: unknown
  grid: DeskGrid | null
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

/** The underlyings the Desk asks for open legs on: today's runs' and the desk's four. */
export function legUnderlyings(runs: readonly LiveRunSummary[] | undefined): string[] {
  const base = ['NIFTY', 'BANKNIFTY', 'SENSEX', 'CRUDEOIL']
  return [...new Set([...base, ...(runs ?? []).map((r) => r.underlying.toUpperCase())])].sort(byUnderlying)
}

/**
 * Every open leg in scope, across runs and manual books, largest first; null
 * until every underlying has answered, so a count is never a partial one.
 */
export function useDeskLegs(view: DeskView): { legs: DeskLeg[] | null; error: unknown } {
  const unds = useMemo(() => legUnderlyings(view.runs), [view.runs])
  const enabled = allows(view.access, 'strategies')
  const answers = useDeskPositions(unds, view.clock === 'live', enabled)
  const ready = answers.every((a) => a.data !== undefined)
  const error = answers.find((a) => a.isError)?.error ?? null
  // A few dozen rows at most: cheaper to rebuild than to memoise on an array of answers.
  const legs = ready
    ? deskLegs(
        unds.map((u, i) => ({ underlying: u, rows: answers[i].data })),
        { today: view.today, nextSession: view.nextSession, userName: view.scopeName },
      )
    : null
  return { legs, error }
}

/**
 * The morning plan, as today's checkups read it. Admin only: the checkups are
 * Sentinel's. The latest checkup is asked for anyway (the strip shows its
 * verdict); the morning one is fetched in full only when the latest is not it.
 */
export function useDeskPlan(view: DeskView): { plan: Plan | null; ready: boolean } {
  const latest = useCheckupLatest()
  const list = useCheckups(30)
  const morningId = morningCheckupId(list.data, view.day)
  const needMorning = morningId != null && latest.data?.latest?.id !== morningId
  const morning = useCheckup(needMorning ? morningId : null)
  const plan = todaysPlan([latest.data?.latest, needMorning ? morning.data : null], view.day)
  const ready = latest.data !== undefined && list.data !== undefined && (!needMorning || morning.data !== undefined)
  return { plan, ready }
}

/** The forecasts of the Desk's day (analysis grant). */
export function useDayForecasts(view: DeskView) {
  return useForecasts({ from: view.day, to: view.day })
}
