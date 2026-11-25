import { useMemo } from 'react'

import { entryPlan, parseParams, trendRiderRules, type EntryPlan } from '../../lib/entryPlan'
import { useRunDayBars, useStoredCandles, useStrategies } from '../../lib/queries'
import { foldSession, istDay, shiftDay, type Bar } from '../../lib/runStory'
import type { StrategyLiveView } from '../../lib/types'

const MCX = /^(CRUDEOIL|CRUDEOILM|NATURALGAS|NATGASMINI|GOLD|GOLDM|SILVER|SILVERM)$/i

/**
 * A TrendRider-family run's entry plan for its day (lib/entryPlan.ts): where
 * and when it buys, or why it will not today. Null for any other strategy, an
 * MCX run, or until the candles are in. `nowMs` is the clock the plan is read
 * at: now, or a replay's cursor.
 *
 * It asks for the same candles as the run story (same query keys), so a page
 * showing both fetches them once.
 */
export function useEntryPlan(view: StrategyLiveView | null | undefined, nowMs: number): EntryPlan | null {
  const name = view?.name ?? ''
  const family = /^TrendRider/i.test(name) && !view?.isManualBook
  const strategies = useStrategies({ enabled: family })
  const params = useMemo(
    () => (family ? parseParams(strategies.data?.find((s) => s.name === name)?.defaultParametersJson) : null),
    [family, strategies.data, name],
  )
  const mcx = MCX.test(view?.underlying ?? '') || /^MCX:/i.test(view?.spotSymbol ?? '')
  const rules = useMemo(() => (family ? trendRiderRules(name, params, { mcx }) : null), [family, name, params, mcx])

  const startedMs = view?.startedUtc ? Date.parse(view.startedUtc) : Date.now()
  const day = view?.session === 'recap' && view.recapDate ? view.recapDate : istDay(startedMs)
  const isToday = day === istDay(Date.now())
  const live = !!view?.isActive && isToday
  const symbol = rules && view?.spotSymbol ? view.spotSymbol : null
  const dayQ = useRunDayBars(symbol, day, isToday, live)
  const histQ = useStoredCandles(symbol, '5', shiftDay(day, -21), shiftDay(day, -1))

  const hist5 = useMemo<Bar[]>(
    () => (histQ.data ?? []).map((c) => ({ t: Date.parse(c.timestampUtc), o: c.open, h: c.high, l: c.low, c: c.close })),
    [histQ.data],
  )
  const five = useMemo(() => foldSession(dayQ.data ?? [], day, 5), [dayQ.data, day])

  if (!rules || !histQ.data || !dayQ.data) return null
  // The strategy reads closed candles only, and counts the entries it has made that day.
  const closed = five.filter((c) => c.t + 5 * 60_000 <= nowMs)
  const entries = (view?.activity ?? []).filter(
    (a) => a.type === 'OPEN_GROUP' && istDay(Date.parse(a.atUtc)) === day && Date.parse(a.atUtc) <= nowMs,
  ).length
  return entryPlan(rules, hist5, closed, day, nowMs, entries)
}
