/**
 * The top bar's status items: the IST clock, the market sessions, whether
 * prices are being pushed, and for an operator the desk's health, Sentinel's
 * live incidents and the kill switch.
 *
 * Every item says only what it knows. A question not yet answered renders
 * nothing rather than a comfortable default: an unknown kill switch is not
 * "off", an unread incident count is not "0", and while the API is down the
 * session items vanish instead of repeating their last answer as if it were
 * now.
 */

import { useCallback, useEffect, useRef, useState } from 'react'
import type { FormEvent } from 'react'
import { createPortal } from 'react-dom'
import { Link } from 'react-router-dom'
import { useAuth } from '../../lib/auth'
import { useLiveConnection, useLiveNudgeWhenApiBack } from '../../lib/live'
import {
  useBackendStatus,
  useFeeds,
  useIncidentSummary,
  useIngestorStatuses,
  useKillSwitch,
  useMarketSession,
  useProviders,
  useSetKillSwitch,
} from '../../lib/queries'
import {
  backendPulse,
  calendarPulse,
  connectorsSummary,
  feedPulses,
  headlinePulse,
  livePulse,
  marketPulses,
  recapVendors,
} from '../../lib/pulse'
import type { ConnectorState, Pulse } from '../../lib/pulse'
import type { IngestorStatus, MarketSessionInfo } from '../../lib/types'
import { useMarketClock } from '../../lib/marketClock'
import { severityTone, silenceNote } from '../../lib/incidents'
import { formatDateTime } from '../../lib/format'
import { IconPower, IconShield, IconWarning, IconX } from '../icons'
import { InlineError } from '../ui'
import { useDialogChrome } from '../../pages/strategies/shared'
import { useDismiss } from './useDismiss'

type Tone = Pulse['tone']

function Dot({ tone }: { tone: Tone }) {
  return (
    <span className={`st st--${tone}`}>
      <span className="st__dot" aria-hidden="true" />
    </span>
  )
}

/** Hours, minutes and seconds in IST, whatever the browser's time zone. */
function Clock() {
  const clock = useMarketClock()
  return (
    <span className="st st--clock" title="India Standard Time">
      {clock.time} <small>IST</small>
    </span>
  )
}

/** True once `on` has held for `ms` without a break. */
function useSustained(on: boolean, ms: number): boolean {
  const [held, setHeld] = useState(false)
  useEffect(() => {
    setHeld(false)
    if (!on) return
    const id = window.setTimeout(() => setHeld(true), ms)
    return () => window.clearTimeout(id)
  }, [on, ms])
  return on && held
}

/**
 * Whether prices, fills and stops are reaching this screen as they happen
 * (livePulse). Once the socket has been down for a couple of seconds, the
 * warning, because every price on screen is then the last poll's and may be
 * seconds old; the pages poll at their old pace until it is back.
 */
function LiveItem() {
  const connection = useLiveConnection()
  const down = useSustained(connection === 'reconnecting' || connection === 'disconnected', 2_000)
  const p = livePulse(connection, down)
  if (!p) return null
  return (
    <span className={`st st--conn st--${p.tone}`} role={p.tone === 'warn' ? 'status' : undefined} title={p.title}>
      <span className="st__dot" aria-hidden="true" />
      {p.short ? (
        <>
          <span className="st__label st__wide">{p.label}</span>
          <span className="st__label st__narrow">{p.short}</span>
        </>
      ) : (
        <span className="st__label">{p.label}</span>
      )}
    </span>
  )
}

/**
 * Where the health popover's left edge goes: under the words that were
 * clicked, kept inside the window. Null on a phone, where the popover spans
 * the width and hangs from the right like the account menu.
 */
export function popoverLeft(triggerLeft: number | null, viewportWidth: number, width = 380, margin = 12): number | null {
  if (triggerLeft == null || viewportWidth <= 760) return null
  return Math.round(Math.max(margin, Math.min(triggerLeft, viewportWidth - width - margin)))
}

const CONNECTOR_TONE: Record<ConnectorState, Tone> = {
  ready: 'pos',
  'sign-in': 'neg',
  expired: 'neg',
  'not-set-up': 'idle',
}

/**
 * One item for the plumbing behind every number: the API process, the
 * connectors, the feeds and the holiday calendar. It names the loudest of
 * them, and the list opens under it. Operators only: a trader can act on none
 * of it, and what they need of the feed (are the prices fresh) their own pages
 * say next to the prices.
 */
function HealthItem({ backend, nse, mcx, heartbeats }: {
  backend: ReturnType<typeof useBackendStatus>
  nse: MarketSessionInfo | undefined
  mcx: MarketSessionInfo | undefined
  heartbeats: IngestorStatus[] | undefined
}) {
  const providers = useProviders()
  const feeds = useFeeds()
  const [open, setOpen] = useState(false)
  const [left, setLeft] = useState<number | null>(null)
  const boxRef = useRef<HTMLDivElement>(null)
  const close = useCallback(() => setOpen(false), [])
  useDismiss(boxRef, open, close)

  function toggle() {
    if (!open) setLeft(popoverLeft(boxRef.current?.getBoundingClientRect().left ?? null, window.innerWidth))
    setOpen((v) => !v)
  }

  const down = backend.isDown
  const now = Date.now()
  const backendState = backendPulse({ isDown: down, restartedAt: backend.restartedAt, status: backend.data })
  // With the API down every other answer is stale, so only the backend speaks.
  // A missing sign-in is an alarm only on a day NSE trades.
  const summary = down ? null : connectorsSummary(providers.data, feeds.data, nse?.isTradingDay !== false, now)
  const feedList = down ? [] : feedPulses(feeds.data, heartbeats, nse?.isMarketOpen === true, now)
  const calendar = down ? null : calendarPulse(nse, mcx)
  const headline = headlinePulse([calendar, summary?.pulse, ...feedList, backendState].filter((p): p is Pulse => !!p))
  if (!headline) return null
  const { pulse, more } = headline

  return (
    <div className="st-anchor" ref={boxRef}>
      <button
        type="button"
        className={`st st--health st--${pulse.tone}`}
        aria-haspopup="dialog"
        aria-expanded={open}
        aria-label={`Desk health: ${pulse.label}${more ? `, and ${more} more` : ''}`}
        title={open ? undefined : pulse.title}
        onClick={toggle}
      >
        <span className="st__dot" aria-hidden="true" />
        {pulse.short ? (
          <>
            <span className="st__label st__wide">{pulse.label}</span>
            <span className="st__label st__narrow">{pulse.short}</span>
          </>
        ) : (
          <span className="st__label">{pulse.label}</span>
        )}
        {more > 0 && <span className="st__more">+{more}</span>}
      </button>
      {open && (
        <div className="pop" role="dialog" aria-label="Desk health" style={left != null ? { left, right: 'auto' } : undefined}>
          <div className="pop__head">
            <span>Desk health</span>
            {summary && <span>Live data: {summary.liveFeeds.length ? summary.liveFeeds.join(', ') : 'no feed running'}</span>}
          </div>
          {backendState && (
            <Link to="/system" className="pop__row">
              <Dot tone={backendState.tone} />
              <span className="pop__name">{backendState.label}</span>
              <span className="pop__detail">{backendState.title}</span>
            </Link>
          )}
          {calendar && (
            <Link to="/system/calendar" className="pop__row">
              <Dot tone={calendar.tone} />
              <span className="pop__name">{calendar.label}</span>
              <span className="pop__detail">{calendar.title}</span>
            </Link>
          )}
          {feedList.length > 0 && <div className="pop__section">Feeds</div>}
          {feedList.map((p) => (
            <Link key={p.key} to="/data/feeds" className="pop__row">
              <Dot tone={p.tone} />
              <span className="pop__name">{p.label}</span>
              <span className="pop__detail">{p.title}</span>
            </Link>
          ))}
          {summary && summary.lines.length > 0 && <div className="pop__section">Connectors</div>}
          {summary?.lines.map((line) => (
            <Link key={line.key} to={`/system/connectors/${line.key}`} className="pop__row">
              <Dot tone={CONNECTOR_TONE[line.state]} />
              <span className="pop__name">
                {line.name} <small>{line.role}</small>
              </span>
              <span className="pop__detail">
                {line.session}
                {line.feed && <span className={line.feedRunning ? 'pos' : 'faint'}> · {line.feed}</span>}
              </span>
            </Link>
          ))}
          <div className="pop__foot">
            <Link to="/system/connectors">Connectors</Link>
            <Link to="/data/feeds">Feeds</Link>
            <Link to="/system/checkups">Checkups</Link>
          </div>
        </div>
      )}
    </div>
  )
}

/**
 * Sentinel's live incidents, and a warning in place of the count once
 * Sentinel itself has gone quiet: a zero from a watchman who stopped looking
 * proves nothing.
 */
function IncidentsItem() {
  const summary = useIncidentSummary()
  const s = summary.data
  if (!s) {
    return summary.isError ? (
      <Link to="/system/incidents" className="st st--badge st--idle" title="Could not read Sentinel's incidents" aria-label="Incidents: could not be read">
        <IconWarning aria-hidden="true" />
        <span className="st__label">?</span>
      </Link>
    ) : null
  }
  // As of when the summary arrived, not the wall clock: a tab woken from sleep
  // must not call Sentinel quiet in the instant before its refetch lands.
  const silence = silenceNote(s.lastCheckUtc, summary.dataUpdatedAt || Date.now())
  const live = typeof s.live === 'number' ? s.live : Object.values(s.counts ?? {}).reduce((a, b) => a + (b ?? 0), 0)
  const severity = severityTone(s.worstSeverity ?? '')
  const tone: Tone = silence ? 'warn' : live === 0 || severity === 'neutral' ? 'idle' : severity
  const title = silence ?? (live === 0 ? 'No live incidents' : `${live} live incident${live === 1 ? '' : 's'}${s.newestTitle ? `. Newest: ${s.newestTitle}` : ''}`)
  return (
    <Link to="/system/incidents" className={`st st--badge st--${tone}`} title={title} aria-label={`Incidents: ${title}`}>
      {tone === 'idle' ? <IconShield aria-hidden="true" /> : <IconWarning aria-hidden="true" />}
      <span className="st__label">{silence ? 'Sentinel quiet' : live}</span>
    </Link>
  )
}

/**
 * The kill switch. Operators see it always and can pull it from here, giving
 * the same reason the Risk page asks for; releasing it stays on the Risk page,
 * with the limits and the positions. A trader sees it only when pulled,
 * because only then does it change what they can do.
 */
function KillSwitchItem({ isAdmin }: { isAdmin: boolean }) {
  const ks = useKillSwitch()
  const [pulling, setPulling] = useState(false)

  if (!ks.data) {
    return isAdmin && ks.isError ? (
      <Link to="/trade/risk" className="st st--warn" title="Could not read the kill switch">
        <span className="st__dot" aria-hidden="true" />
        <span className="st__label">Kill switch ?</span>
      </Link>
    ) : null
  }

  if (ks.data.isActive) {
    const why = [
      'Trading is halted: every strategy is paused and new runs are refused.',
      ks.data.reason ? `Reason: “${ks.data.reason}”.` : '',
      ks.data.updatedBy ? `By ${ks.data.updatedBy} at ${formatDateTime(ks.data.updatedUtc)}.` : '',
    ]
      .filter(Boolean)
      .join(' ')
    const body = (
      <>
        <span className="st__dot" aria-hidden="true" />
        <span className="st__long">Kill switch on</span>
        <span className="st__short">Halted</span>
      </>
    )
    return isAdmin ? (
      <Link to="/trade/risk" className="st st--halted" title={`${why} Release it on the Risk page.`}>
        {body}
      </Link>
    ) : (
      <span className="st st--halted" title={why}>
        {body}
      </span>
    )
  }

  if (!isAdmin) return null
  return (
    <>
      <button
        type="button"
        className="st st--ks"
        title="Trading allowed. Pull the kill switch…"
        aria-label="Kill switch off. Pull the kill switch"
        aria-haspopup="dialog"
        onClick={() => setPulling(true)}
      >
        <IconPower aria-hidden="true" />
        <span className="st__long">Kill switch off</span>
      </button>
      {pulling && <KillSwitchDialog onClose={() => setPulling(false)} />}
    </>
  )
}

/** Pulling the kill switch, with the reason that goes into the log and the alert. */
function KillSwitchDialog({ onClose }: { onClose: () => void }) {
  const setKillSwitch = useSetKillSwitch()
  const [reason, setReason] = useState('')
  const cardRef = useDialogChrome(onClose)

  function pull(e: FormEvent) {
    e.preventDefault()
    setKillSwitch.mutate(
      // The Risk page's default, so the log reads the same whichever button pulled it.
      { activate: true, reason: reason.trim() || 'Pulled from console' },
      { onSuccess: onClose },
    )
  }

  // Portalled out of the sticky bar, whose stacking context would otherwise
  // let the phone's bottom bar paint over the dialog.
  return createPortal(
    <div className="modal" onMouseDown={(e) => e.target === e.currentTarget && onClose()}>
      <div className="modal__card modal__card--sm" role="dialog" aria-modal="true" aria-labelledby="ks-title" tabIndex={-1} ref={cardRef}>
        <form className="modal__body" onSubmit={pull}>
          <div className="modal__head">
            <h2 className="modal__title" id="ks-title">
              Pull the kill switch
            </h2>
            <button type="button" className="btn btn--ghost btn--sm" onClick={onClose} aria-label="Close" title="Close (Esc)">
              <IconX style={{ width: 14, height: 14 }} />
            </button>
          </div>
          <p className="muted" style={{ margin: 0 }}>
            This pauses every strategy <b>and squares off every open position</b> at the last mark. It is announced to
            Telegram immediately. It is released on the Risk page.
          </p>
          {setKillSwitch.isError && <InlineError error={setKillSwitch.error} />}
          <div className="field">
            <label className="field__label" htmlFor="ks-top-reason">
              Reason (recorded, and sent with the alert)
            </label>
            <input
              id="ks-top-reason"
              className="field__input"
              placeholder="what went wrong"
              value={reason}
              onChange={(e) => setReason(e.target.value)}
              autoFocus
            />
          </div>
          <div className="modal__foot">
            <button type="button" className="btn" onClick={onClose}>
              Cancel
            </button>
            <button type="submit" className="btn btn--danger" disabled={setKillSwitch.isPending}>
              {setKillSwitch.isPending ? 'Pulling…' : 'Pull the kill switch'}
            </button>
          </div>
        </form>
      </div>
    </div>,
    document.body,
  )
}

export function StatusItems() {
  const { isAdmin } = useAuth()
  const backend = useBackendStatus()
  const nseSession = useMarketSession()
  const mcxSession = useMarketSession('MCX', 'COM')
  const ingestors = useIngestorStatuses()
  // Feed names label a replay; only an operator's console asks for the feed list.
  const feeds = useFeeds({ enabled: isAdmin })

  const down = backend.isDown
  // The moment the API answers again, the live connection tries at once
  // rather than at the end of a retry wait that has grown to 30 s.
  useLiveNudgeWhenApiBack(down)
  const nse = down ? undefined : nseSession.data
  const mcx = down ? undefined : mcxSession.data
  const heartbeats = down ? undefined : ingestors.data
  const markets = marketPulses(nse, mcx, recapVendors(heartbeats, down ? undefined : feeds.data))
  // A trader hears about the API only when something is off with it.
  const traderBackend = isAdmin ? null : backendPulse({ isDown: down, restartedAt: backend.restartedAt, status: backend.data })

  return (
    <div className="shell__status">
      <Clock />
      {traderBackend && traderBackend.tone !== 'pos' && (
        <span className={`st st--${traderBackend.tone}`} title={traderBackend.title}>
          <span className="st__dot" aria-hidden="true" />
          <span className="st__label">{traderBackend.label}</span>
        </span>
      )}
      {markets.map((p) => (
        <span key={p.key} className={`st st--market st--${p.tone}`} title={p.title}>
          <span className="st__dot" aria-hidden="true" />
          {p.short ? (
            <>
              <span className="st__label st__wide">{p.label}</span>
              <span className="st__label st__narrow">{p.short}</span>
            </>
          ) : (
            <span className="st__label">{p.label}</span>
          )}
        </span>
      ))}
      {!down && <LiveItem />}
      {isAdmin && <HealthItem backend={backend} nse={nse} mcx={mcx} heartbeats={heartbeats} />}
      {isAdmin && !down && <IncidentsItem />}
      {!down && <KillSwitchItem isAdmin={isAdmin} />}
    </div>
  )
}
