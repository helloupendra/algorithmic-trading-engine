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

import { heartbeatFeed } from './pulse'
import type { IngestorStatus, LiveFeed } from './types'

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

// --- diagnostics ----------------------------------------------------------------

/** How old a heartbeat may be and still speak for a feed that is not running. */
export const RECENT_HEARTBEAT_MS = 30 * 60 * 1000

export interface FeedDiagnosticRow {
  key: string
  name: string
  /** "Running · pid 222061", "Running (adopted) · pid 11", "Stopped". */
  process: string
  processTone: 'pos' | 'muted' | 'warn'
  /** The newest heartbeat's own words: "Running", "Disconnected"; null without a recent one. */
  heartbeat: string | null
  heartbeatTone: 'pos' | 'warn' | 'neg' | 'muted'
  heartbeatUtc: string | null
  /** "recap" when the heartbeat came from an evening replay. */
  mode: 'live' | 'recap' | null
  symbols: number | null
  error: string | null
  /** A contradiction worth a sentence: a heartbeat with no process, or a process with no heartbeat. */
  note: string | null
}

export interface FeedDiagnostics {
  rows: FeedDiagnosticRow[]
  /** Heartbeats too old to describe anything running now, listed so nothing is hidden. */
  older: { sourceName: string; status: string; lastHeartbeatUtc: string }[]
}

/**
 * An error text worth showing. Python wrote a dropped socket's missing code and
 * reason as "None None", which the page showed in red with nothing to act on.
 */
export function meaningfulError(text: string | null | undefined): string | null {
  const t = (text ?? '').trim()
  if (!t) return null
  return /^((none|null|undefined|nan)\s*)+$/i.test(t) ? null : t
}

/**
 * One row per feed: its process, its newest heartbeat and that heartbeat's
 * error. Joined by the feed's key, never by "any heartbeat is healthy": when
 * FYERS was the only feed that shortcut was harmless, and with Dhan running it
 * told the page FYERS was running outside the console.
 */
export function feedDiagnostics(
  feeds: LiveFeed[] | undefined,
  heartbeats: IngestorStatus[] | undefined,
  nowMs: number,
): FeedDiagnostics {
  const newest = new Map<string, { row: IngestorStatus; mode: 'live' | 'recap' }>()
  const older: FeedDiagnostics['older'] = []
  for (const row of heartbeats ?? []) {
    const owner = heartbeatFeed(row.sourceName)
    const recent = nowMs - Date.parse(row.lastHeartbeatUtc) <= RECENT_HEARTBEAT_MS
    const seen = owner ? newest.get(owner.key) : undefined
    if (!owner || !recent) {
      older.push({ sourceName: row.sourceName, status: row.status, lastHeartbeatUtc: row.lastHeartbeatUtc })
      continue
    }
    if (!seen || Date.parse(row.lastHeartbeatUtc) > Date.parse(seen.row.lastHeartbeatUtc)) {
      if (seen) older.push({ sourceName: seen.row.sourceName, status: seen.row.status, lastHeartbeatUtc: seen.row.lastHeartbeatUtc })
      newest.set(owner.key, { row, mode: owner.mode })
    } else {
      older.push({ sourceName: row.sourceName, status: row.status, lastHeartbeatUtc: row.lastHeartbeatUtc })
    }
  }

  const rows: FeedDiagnosticRow[] = (feeds ?? []).map((feed) => {
    const beat = newest.get(feed.key)
    const healthy = beat?.row.isHealthy === true
    let heartbeatTone: FeedDiagnosticRow['heartbeatTone'] = 'muted'
    if (beat) {
      if (healthy) heartbeatTone = 'pos'
      else if (beat.row.status === 'Refused') heartbeatTone = 'neg'
      else heartbeatTone = 'warn'
    }

    let note: string | null = null
    if (!feed.isRunning && healthy) {
      note = 'Heartbeats are arriving but the API holds no process for it: it was started outside this console, and Stop here cannot reach it.'
    } else if (feed.isRunning && beat && !healthy) {
      note = 'The process is up but its heartbeat has gone quiet; check its output.'
    } else if (feed.isRunning && !beat) {
      note = 'The process is up but has not reported a heartbeat yet.'
    }

    return {
      key: feed.key,
      name: feed.displayName,
      process: describeFeedState(feed),
      processTone: feed.isRunning ? 'pos' : 'muted',
      heartbeat: beat ? (healthy ? beat.row.status : `${beat.row.status} · quiet`) : null,
      heartbeatTone,
      heartbeatUtc: beat?.row.lastHeartbeatUtc ?? null,
      mode: beat?.mode ?? null,
      symbols: beat ? beat.row.currentSubscribedSymbols.length : null,
      // An old error on a feed that has since stopped cleanly is history, not a fault.
      error: beat ? meaningfulError(beat.row.lastError) : null,
      note,
    }
  })

  older.sort((a, b) => Date.parse(b.lastHeartbeatUtc) - Date.parse(a.lastHeartbeatUtc))
  return { rows, older }
}
