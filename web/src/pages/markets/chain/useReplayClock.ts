/**
 * The replay clock the Chain and OI history tabs share, read from the URL
 * (lib/chainReplay.ts) and measured from the newest recorded session. The
 * control that moves it is ReplayClock.tsx.
 */

import { useMemo } from 'react'
import { useSearchParams } from 'react-router-dom'
import { useOptionChainRecordedDays } from '../../../lib/queries'
import { laterDay, readReplayClock, replayClockAsOfUtc, withReplayClock } from '../../../lib/chainReplay'
import type { ReplayClock } from '../../../lib/chainReplay'

export interface ReplayState {
  clock: ReplayClock
  /** The recorded days, newest first; undefined until they have answered. */
  days: readonly string[] | undefined
  /** The newest recorded session, the one the page shows live. */
  newestDay: string | null
  /** The day the tab is showing: the rewound one, else the newest. */
  sessionDay: string | null
  /** What to ask the chain for; undefined for live. */
  asOfUtc: string | undefined
  setClock: (next: ReplayClock) => void
}

/**
 * The clock in the URL, and the newest session it is measured from: the
 * newest recorded day, or the live chain's own day (`liveDay`) when that is
 * later, as it is in a session's first minutes, before the list is asked
 * again.
 */
export function useReplayClock(underlying: string, liveDay: string | null | undefined): ReplayState {
  const [params, setParams] = useSearchParams()
  const clock = useMemo(() => readReplayClock(params), [params])
  const recorded = useOptionChainRecordedDays(underlying)
  const days = recorded.data?.underlying === underlying ? recorded.data.days : undefined
  const newestDay = laterDay(days?.[0], liveDay)

  return {
    clock,
    days,
    newestDay,
    sessionDay: clock.day ?? newestDay,
    asOfUtc: replayClockAsOfUtc(clock, newestDay),
    setClock: (next) => setParams(withReplayClock(params, next, newestDay), { replace: true }),
  }
}
