/**
 * The moment a polled answer is "as of", for laying pushed prices over it.
 *
 * An overlay (lib/liveMarks.ts, the chain, the chart) lays a pushed tick over
 * an answer only when the tick is newer than the answer. Until 28 Sep "newer"
 * meant received after the answer ARRIVED. But the server reads its quotes
 * early in a request, so a tick that reached the browser while the request
 * was in flight is newer than the answer that lands after it, and it was
 * thrown away: the price stepped back to the polled one and stayed there
 * until that contract ticked again, up to 15 s on a run card, for good on the
 * chain. The line is now when the request was SENT. A tick received after
 * that is laid over the answer; one the answer already carries is only the
 * same price again.
 *
 * The send time travels with the answer object (a WeakMap, so it goes when
 * the answer does), and structural sharing hands it on to whichever object
 * the cache ends up holding. An answer the console wrote itself (a fold of
 * pushed prices into the cache) carries none, and its arrival is its time.
 */

import { replaceEqualDeep } from '@tanstack/react-query'

const sentAt = new WeakMap<object, number>()

const isObject = (value: unknown): value is object => typeof value === 'object' && value !== null

/** A queryFn that remembers, against the answer it brings, when its request was sent. */
export function stampSent<T>(fetch: () => Promise<T>, now: () => number = Date.now): () => Promise<T> {
  return async () => {
    const at = now()
    const data = await fetch()
    if (isObject(data)) sentAt.set(data, at)
    return data
  }
}

/**
 * TanStack's structural sharing, keeping the send time on the object the
 * cache keeps: a new copy, or the previous answer when nothing changed (then
 * the newer request's time, since that request found nothing new either).
 * Pass as `structuralSharing` beside a `stampSent` queryFn.
 */
export function keepSentStamp(previous: unknown, next: unknown): unknown {
  const shared = replaceEqualDeep(previous, next)
  const at = isObject(next) ? sentAt.get(next) : undefined
  if (at != null && isObject(shared)) sentAt.set(shared, at)
  return shared
}

/**
 * When the answer a query holds is as of: when its request was sent, else
 * (an answer written here, or a query without the stamp) when it arrived.
 * A tick received after this is newer than the answer.
 */
export function answerAsOf(query: { data?: unknown; dataUpdatedAt: number }): number {
  const at = isObject(query.data) ? sentAt.get(query.data) : undefined
  return at != null && at <= query.dataUpdatedAt ? at : query.dataUpdatedAt
}
