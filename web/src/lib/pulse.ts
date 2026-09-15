/**
 * The topbar's pulse row as data: which markets are trading, and which feed is
 * feeding them.
 *
 * Kept out of AppLayout so the rules can be tested. Both were written on the
 * evening of 2026-09-11, when the bar read "NSE closed · MCX open · Feed live
 * (1)" while TrueData replayed the NSE session and three strategies traded on
 * it. None of that was visible:
 *
 * - A pill says what is ON. While one market trades, the closed one is not a
 *   chip of its own; its next open is in the tooltip. When nothing trades, one
 *   quiet "Markets closed" says so. A replay is its own pill, because it is not
 *   the market and must never read as it.
 * - A feed is named. "Feed live (1)" could not say which vendor it was, or that
 *   the one feed was a replay. A feed that is off has no pill, except in the
 *   one case where off is the problem: NSE is open and nothing is feeding it.
 *
 * Nothing is claimed from a question that has not been answered: an unknown
 * session is not "closed", and an unknown feed list is not "no feed".
 */

import type { IngestorStatus, LiveFeed, MarketSessionInfo, Provider } from './types'

export type PulseTone = 'pos' | 'neg' | 'warn' | 'live' | 'idle'

export interface Pulse {
  key: string
  label: string
  tone: PulseTone
  title: string
}

export type FeedMode = 'live' | 'recap'

const IST: Intl.DateTimeFormatOptions = { timeZone: 'Asia/Kolkata', hour12: false }

function timeIst(iso: string): string {
  return new Date(iso).toLocaleTimeString('en-IN', { ...IST, hour: '2-digit', minute: '2-digit' })
}

function dayTimeIst(iso: string): string {
  return new Date(iso).toLocaleString('en-IN', { ...IST, weekday: 'short', hour: '2-digit', minute: '2-digit' })
}

function secondsSince(iso: string, nowMs: number): number {
  return Math.max(0, Math.round((nowMs - new Date(iso).getTime()) / 1000))
}

/**
 * The feed a heartbeat row belongs to. The names are set by the Python feeds:
 * FYERS keeps the name it had before there was a second vendor, and every
 * other vendor reports "python-<key>-feed", or "python-<key>-recap" while it
 * plays a replay (core/live/feed_runner.py, market_data/live/vendors/truedata.py).
 * Null for a row this cannot place: it is left out, never guessed at.
 */
export function heartbeatFeed(sourceName: string): { key: string; mode: FeedMode } | null {
  if (sourceName === 'python-live-ingestor') return { key: 'fyers', mode: 'live' }
  const match = /^python-(.+)-(feed|recap)$/.exec(sourceName)
  if (!match) return null
  return { key: match[1], mode: match[2] === 'recap' ? 'recap' : 'live' }
}

interface Beat {
  row: IngestorStatus
  mode: FeedMode
}

/**
 * The newest heartbeat per feed. Rows are never deleted, so the afternoon's
 * "python-truedata-feed" still sits beside the evening's "-recap"; only the
 * latest one speaks for the feed.
 */
function latestBeats(heartbeats: IngestorStatus[]): Map<string, Beat> {
  const beats = new Map<string, Beat>()
  for (const row of heartbeats) {
    const owner = heartbeatFeed(row.sourceName)
    if (!owner) continue
    const seen = beats.get(owner.key)
    if (!seen || new Date(row.lastHeartbeatUtc) > new Date(seen.row.lastHeartbeatUtc)) {
      beats.set(owner.key, { row, mode: owner.mode })
    }
  }
  return beats
}

/** Vendors replaying a session right now, by the name the operator knows them by. */
export function recapVendors(heartbeats: IngestorStatus[] | undefined, feeds: LiveFeed[] | undefined): string[] {
  if (!heartbeats) return []
  const names = new Map((feeds ?? []).map((f) => [f.key, f.displayName]))
  const vendors: string[] = []
  for (const [key, beat] of latestBeats(heartbeats)) {
    if (beat.mode === 'recap' && beat.row.isHealthy) vendors.push(names.get(key) ?? key)
  }
  return vendors
}

export function marketPulses(
  nse: MarketSessionInfo | undefined,
  mcx: MarketSessionInfo | undefined,
  recap: string[],
): Pulse[] {
  const pulses: Pulse[] = []
  const known = (
    [
      ['NSE', nse],
      ['MCX', mcx],
    ] as const
  ).flatMap(([name, session]) => (session ? [{ name, session }] : []))
  const open = known.filter((m) => m.session.isMarketOpen)
  const closed = known.filter((m) => !m.session.isMarketOpen)

  // "NSE closed for Ganesh Chaturthi, opens Tue 09:15 IST": a holiday is the
  // reason worth naming, because "closed" on a weekday reads like a fault.
  const closedText = (m: (typeof known)[number]) =>
    `${m.name} closed${m.session.holidayName ? ` for ${m.session.holidayName}` : ''}, opens ${dayTimeIst(m.session.nextMarketOpenUtc)} IST`

  if (open.length > 0) {
    pulses.push({
      key: 'markets',
      label: `${open.map((m) => m.name).join(' · ')} open`,
      tone: 'pos',
      title: [
        ...open.map((m) => `${m.name} until ${timeIst(m.session.sessionCloseUtc)} IST`),
        ...closed.map(closedText),
      ].join(' · '),
    })
  } else if (closed.length === 2) {
    const holiday = closed.map((m) => m.session.holidayName).find((name): name is string => !!name)
    pulses.push({
      key: 'markets',
      label: holiday ? `Holiday · ${holiday}` : 'Markets closed',
      tone: 'idle',
      title: closed.map(closedText).join(' · '),
    })
  } else {
    // The other session is still unknown, so "Markets closed" would be a guess
    // about it. Say only what is known.
    for (const m of closed) {
      pulses.push({
        key: m.name.toLowerCase(),
        label: m.session.holidayName ? `${m.name} holiday` : `${m.name} closed`,
        tone: 'idle',
        title: closedText(m),
      })
    }
  }

  if (recap.length > 0) {
    pulses.push({
      key: 'recap',
      label: 'NSE recap',
      tone: 'live',
      title: `${recap.join(', ')} is replaying today's session. Not the live market: only runs started in recap mode trade on it.`,
    })
  }

  return pulses
}

/**
 * A warning pill when a session answer rests on weekends alone because the
 * exchange's holiday calendar for today is not loaded. Not knowing about a
 * holiday is not the same as knowing there is none.
 */
export function calendarPulse(...sessions: (MarketSessionInfo | undefined)[]): Pulse | null {
  const warning = sessions.map((s) => s?.calendarWarning).find((w): w is string => !!w)
  return warning
    ? { key: 'calendar', label: 'Holiday calendar missing', tone: 'warn', title: `${warning} Add it under System → Market calendar.` }
    : null
}

/**
 * One pill per feed that is on, in the API's order. `nseOpen` is what turns
 * "no feed" from a normal evening into an alarm.
 */
export function feedPulses(
  feeds: LiveFeed[] | undefined,
  heartbeats: IngestorStatus[] | undefined,
  nseOpen: boolean,
  nowMs: number,
): Pulse[] {
  if (!feeds) return []
  const beats = latestBeats(heartbeats ?? [])
  const pulses: Pulse[] = []

  for (const feed of feeds) {
    const beat = beats.get(feed.key)
    const beating = beat?.row.isHealthy === true
    if (!feed.isRunning && !beating) continue

    const name = feed.displayName
    const pid = feed.processId != null ? ` · pid ${feed.processId}` : ''

    if (beat && beating) {
      const label = `${name} ${beat.mode === 'recap' ? 'recap' : 'feed'}`
      const detail = `${beat.row.currentSubscribedSymbols.length} symbols · heartbeat ${secondsSince(beat.row.lastHeartbeatUtc, nowMs)}s ago`
      pulses.push(
        feed.isRunning
          ? { key: feed.key, label, tone: 'live', title: `${detail}${pid}` }
          : {
              key: feed.key,
              label,
              tone: 'warn',
              title: `${detail} · the API has no process for it, so Live feeds cannot stop it`,
            },
      )
      continue
    }

    // Running, but not reporting in.
    if (beat && beat.row.status !== 'Running') {
      pulses.push({
        key: feed.key,
        label: `${name} ${beat.row.status.toLowerCase()}`,
        tone: beat.row.status === 'Refused' ? 'neg' : 'warn',
        title: `${beat.row.lastError ?? beat.row.status}${pid}`,
      })
    } else {
      pulses.push({
        key: feed.key,
        label: `${name} no heartbeat`,
        tone: 'warn',
        title: beat
          ? `The process is up but last reported ${secondsSince(beat.row.lastHeartbeatUtc, nowMs)}s ago${pid}`
          : `The process is up but has not reported yet${pid}`,
      })
    }
  }

  if (pulses.length === 0 && nseOpen) {
    pulses.push({
      key: 'no-feed',
      label: 'No feed running',
      tone: 'neg',
      title: 'NSE is open and no feed is recording it. Start one from Live feeds.',
    })
  }

  return pulses
}

// --- connectors ---------------------------------------------------------------

export type ConnectorState = 'ready' | 'sign-in' | 'expired' | 'not-set-up'

export interface ConnectorLine {
  key: string
  name: string
  /** "Broker + data", "Data". */
  role: string
  state: ConnectorState
  /** What the session is, in words: "signed in · valid until 16 Sep, 06:00 IST". */
  session: string
  /** "feed running", "feed off", or null for a connector with no live feed. */
  feed: string | null
  feedRunning: boolean
}

export interface ConnectorsSummary {
  pulse: Pulse
  lines: ConnectorLine[]
  /** Names of the feeds running now. */
  liveFeeds: string[]
}

function dateTimeIst(iso: string): string {
  return new Date(iso).toLocaleString('en-IN', { ...IST, day: '2-digit', month: 'short', hour: '2-digit', minute: '2-digit' })
}

/**
 * One pill for every broker and data vendor, instead of one vendor's name.
 *
 * With FYERS alone, "FYERS linked" was the whole story. With FYERS, Dhan and
 * TrueData it is a third of it, and three pills would crowd out the market. So
 * the pill names only what needs a hand — a sign-in, or two feeds running at
 * once — and the full list opens under it.
 *
 * Connectors nobody set up are listed but never raise the alarm: a vendor not
 * added is a choice, not a fault.
 */
export function connectorsSummary(
  providers: Provider[] | undefined,
  feeds: LiveFeed[] | undefined,
  tradingDay: boolean,
  nowMs: number,
): ConnectorsSummary | null {
  if (!providers) return null
  const lines: ConnectorLine[] = providers
    .filter((p) => p.isInstalled && p.auth !== 'None')
    .map((p) => {
      const feed = feeds?.find((f) => f.key === p.key)
      const role = p.kind === 'Both' ? 'Broker + data' : p.kind === 'Execution' ? 'Broker' : 'Data'
      let state: ConnectorState
      let session: string
      if (!p.isConfigured) {
        state = 'not-set-up'
        session = 'not set up'
      } else if (p.auth === 'ApiKey') {
        state = 'ready'
        session = 'signs in automatically'
      } else {
        const expires = p.session.expiresUtc ? Date.parse(p.session.expiresUtc) : null
        if (!p.session.isConnected) {
          state = 'sign-in'
          session = 'not signed in'
        } else if ((expires != null && expires <= nowMs) || p.session.needsReconnect) {
          state = 'expired'
          session = expires != null && expires <= nowMs
            ? `token expired ${dateTimeIst(p.session.expiresUtc!)} IST`
            : 'token is from a previous day'
        } else {
          state = 'ready'
          session = p.session.expiresUtc ? `signed in · valid until ${dateTimeIst(p.session.expiresUtc)} IST` : 'signed in'
        }
      }
      return {
        key: p.key,
        name: p.displayName,
        role,
        state,
        session,
        feed: feed ? (feed.isRunning ? 'feed running' : 'feed off') : null,
        feedRunning: feed?.isRunning === true,
      }
    })

  const configured = lines.filter((l) => l.state !== 'not-set-up')
  const needHand = configured.filter((l) => l.state === 'sign-in' || l.state === 'expired')
  const liveFeeds = lines.filter((l) => l.feedRunning).map((l) => l.name)
  const detail = lines.map((l) => `${l.name}: ${l.session}${l.feed ? ` · ${l.feed}` : ''}`).join('\n')

  let pulse: Pulse
  if (needHand.length > 0) {
    const label = needHand.length === 1 ? `${needHand[0].name} sign-in needed` : `${needHand.length} connectors need sign-in`
    // A missing sign-in is an alarm only on a day the market trades.
    pulse = { key: 'connectors', label, tone: tradingDay ? 'neg' : 'idle', title: detail }
  } else if (liveFeeds.length > 1) {
    // Bars and latest quotes are kept per symbol, not per vendor: two feeds on
    // the same contracts build one bar from two vendors' volume counters.
    pulse = {
      key: 'connectors',
      label: `${liveFeeds.length} feeds running`,
      tone: 'warn',
      title: `${liveFeeds.join(' and ')} are both feeding live data; keep one running.\n${detail}`,
    }
  } else {
    pulse = {
      key: 'connectors',
      label: configured.length === 0 ? 'No connectors' : `Connectors ${configured.length}/${configured.length} ready`,
      tone: configured.length === 0 ? 'warn' : 'pos',
      title: detail,
    }
  }

  return { pulse, lines, liveFeeds }
}
