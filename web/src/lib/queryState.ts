/**
 * What a polled query knows, read so that a failing endpoint reads as failing
 * on every frame.
 *
 * TanStack Query v5 puts a query that has never had data back to `pending` at
 * the start of each refetch, so `isError` alone flips the screen between
 * "failed" and "checking…" with every poll while the endpoint stays down. A
 * failure is remembered by `errorUpdateCount` until an answer arrives.
 */

/** The parts of a query result this reads. */
export interface PolledQuery {
  data?: unknown
  isError: boolean
  errorUpdateCount: number
}

/**
 * - `waiting`: no answer yet and no failure;
 * - `failed`: no answer has ever come, and a read has failed;
 * - `stale`: an answer came before, and the latest read failed;
 * - `ok`: the latest read answered.
 */
export type PolledState = 'waiting' | 'failed' | 'stale' | 'ok'

export function polledState(q: PolledQuery): PolledState {
  if (q.data === undefined) return q.isError || q.errorUpdateCount > 0 ? 'failed' : 'waiting'
  return q.isError ? 'stale' : 'ok'
}

/** True while the endpoint is not answering: never answered and a read failed, or the latest read failed. */
export function polledDown(q: PolledQuery): boolean {
  const state = polledState(q)
  return state === 'failed' || state === 'stale'
}
