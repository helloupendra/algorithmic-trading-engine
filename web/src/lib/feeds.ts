/**
 * What the feeds panel may show for the answer it has so far.
 *
 * Kept out of the component because the rule it enforces is the one that has
 * already failed on this page once: for ~2 s after load the header offered a
 * green Start on a feed that was running, because "no answer yet" rendered as
 * "stopped". A Start on a running feed is the one click here that does harm —
 * a second process, or for TrueData a second session the vendor refuses — so
 * a control is only offered on an answer the API has actually given on its
 * latest attempt.
 */

import type { LiveFeed } from './types'

/** The one action a row may offer; `none` whenever its state is not a current answer. */
export type FeedControl = 'start' | 'stop' | 'none'

export interface FeedRow {
  feed: LiveFeed
  state: string
  tone: 'pos' | 'muted' | 'warn'
  control: FeedControl
}

export type FeedsView =
  /** No answer yet. Nothing about any feed may be claimed. */
  | { kind: 'checking' }
  /** The first request failed, so the list itself is unknown. */
  | { kind: 'failed'; error: unknown }
  /** `stale`: an earlier answer, kept on screen because the latest refresh failed. */
  | { kind: 'ready'; rows: FeedRow[]; stale: boolean }

/** The parts of a TanStack query result this reads. */
export interface FeedsQueryState {
  isPending: boolean
  isError: boolean
  error: unknown
  data: LiveFeed[] | undefined
}

export function describeFeedState(feed: LiveFeed): string {
  if (!feed.isRunning) return 'Stopped'
  const pid = feed.processId != null ? `pid ${feed.processId}` : null
  if (feed.source === 'adopted') return pid ? `Running (adopted, ${pid})` : 'Running (adopted)'
  return pid ? `Running (${pid})` : 'Running'
}

export function feedsView(query: FeedsQueryState): FeedsView {
  if (query.isPending) return { kind: 'checking' }
  if (query.data === undefined) return { kind: 'failed', error: query.error }

  // A failed refresh keeps the last list readable — one dropped poll must not
  // blank the panel — but it is no longer a basis for acting on: the feed may
  // have been started or stopped since.
  const stale = query.isError
  return {
    kind: 'ready',
    stale,
    rows: query.data.map((feed) => ({
      feed,
      state: stale ? `${describeFeedState(feed)} — last known` : describeFeedState(feed),
      tone: stale ? 'warn' : feed.isRunning ? 'pos' : 'muted',
      control: stale ? 'none' : feed.isRunning ? 'stop' : 'start',
    })),
  }
}
