import { describe, expect, it } from 'vitest'
import { QueryClient } from '@tanstack/react-query'

import { answerAsOf, keepSentStamp, stampSent } from './asOf'
import { freshPrice } from './liveMarks'

/**
 * A request that takes `ms` on a clock the test moves: sent at `clock.now`,
 * answered `ms` later with `answer`.
 */
function slowRequest<T>(clock: { now: number }, ms: number, answer: () => T) {
  return () =>
    new Promise<T>((resolve) => {
      clock.now += ms
      resolve(answer())
    })
}

describe('answerAsOf', () => {
  it('is when the request was sent, so a tick received while it was in flight is laid over the answer', async () => {
    const clock = { now: 1_000_000 }
    const qc = new QueryClient()
    const key = ['strategy', 'live', 612]
    await qc.fetchQuery({
      queryKey: key,
      queryFn: stampSent(slowRequest(clock, 400, () => ({ runId: 612, ltp: 64.8 })), () => clock.now),
      structuralSharing: keepSentStamp,
    })
    const state = qc.getQueryState(key)!
    expect(answerAsOf(state)).toBe(1_000_000)
    // Pushed 250 ms into the request, which read its quotes before that.
    const tick = { lastTradedPrice: 65.4, exchangeTimestampUtc: null, receivedAtMs: 1_000_250 }
    expect(freshPrice(tick, answerAsOf(state))).toBe(65.4)
    // Measured against the answer's arrival (1_000_400 on this clock) it looked older, and the price stepped back.
    expect(freshPrice(tick, 1_000_400)).toBeNull()
  })

  it('keeps the stamp through structural sharing, on a changed answer and on an unchanged one', async () => {
    const clock = { now: 2_000_000 }
    const qc = new QueryClient()
    const key = ['positions', 'open']
    let ltp = 100
    const options = {
      queryKey: key,
      queryFn: stampSent(slowRequest(clock, 300, () => ({ positions: [{ id: 1, ltp }, { id: 2, ltp: 50 }] })), () => clock.now),
      structuralSharing: keepSentStamp,
      staleTime: 0,
    }
    await qc.fetchQuery(options)
    const first = qc.getQueryData<{ positions: unknown[] }>(key)!

    clock.now += 5_000
    ltp = 101
    await qc.fetchQuery(options)
    const second = qc.getQueryData<{ positions: unknown[] }>(key)!
    // A new top-level object, with the unchanged row shared from the last answer.
    expect(second).not.toBe(first)
    expect(second.positions[1]).toBe(first.positions[1])
    expect(answerAsOf(qc.getQueryState(key)!)).toBe(2_005_300)

    clock.now += 5_000
    await qc.fetchQuery(options)
    // Nothing changed: the cache keeps the same object, now as of the newer request.
    expect(qc.getQueryData(key)).toBe(second)
    expect(answerAsOf(qc.getQueryState(key)!)).toBe(2_010_600)
  })

  it('is the arrival for an answer written here, and for a query without the stamp', () => {
    const qc = new QueryClient()
    qc.setQueryData(['quotes', 'all'], [{ symbol: 'A' }])
    const state = qc.getQueryState(['quotes', 'all'])!
    expect(answerAsOf(state)).toBe(state.dataUpdatedAt)
    expect(answerAsOf({ data: undefined, dataUpdatedAt: 0 })).toBe(0)
    expect(answerAsOf({ data: 'text', dataUpdatedAt: 7 })).toBe(7)
  })
})
