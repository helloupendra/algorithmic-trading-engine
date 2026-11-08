/**
 * The sidebar's foot: where the NSE derivatives session stands (how far
 * through it, how long is left) and whether MCX is open. It says only what
 * the API has answered: while a session is unknown its line is absent, never
 * a comfortable default, and a holiday or a special session (Muhurat) is
 * named rather than read as an ordinary close.
 */

import { useEffect, useState } from 'react'
import { useMarketSession } from '../../lib/queries'
import type { MarketSessionInfo } from '../../lib/types'

const IST = new Intl.DateTimeFormat('en-IN', { timeZone: 'Asia/Kolkata', hour: '2-digit', minute: '2-digit', hour12: false })
const IST_DAY = new Intl.DateTimeFormat('en-IN', { timeZone: 'Asia/Kolkata', weekday: 'short' })

const time = (iso: string) => IST.format(new Date(iso))
const dayTime = (iso: string) => `${IST_DAY.format(new Date(iso))} ${time(iso)}`

/** "1 h 45 min", "12 min", "under a minute". */
export function leftText(ms: number): string {
  const min = Math.floor(ms / 60_000)
  if (min < 1) return 'under a minute'
  const h = Math.floor(min / 60)
  const m = min % 60
  return h > 0 ? `${h} h${m ? ` ${m} min` : ''}` : `${m} min`
}

function useNow(stepMs: number): number {
  const [now, setNow] = useState(() => Date.now())
  useEffect(() => {
    const id = window.setInterval(() => setNow(Date.now()), stepMs)
    return () => window.clearInterval(id)
  }, [stepMs])
  return now
}

/** The NSE block: open with its progress, or closed with the reason and the next open. */
function NseBlock({ s, now }: { s: MarketSessionInfo; now: number }) {
  const special = s.specialSessionName ? ` · ${s.specialSessionName}` : ''
  if (s.isMarketOpen) {
    const open = Date.parse(s.sessionOpenUtc)
    const close = Date.parse(s.sessionCloseUtc)
    const span = close - open
    const done = span > 0 ? Math.min(1, Math.max(0, (now - open) / span)) : 0
    return (
      <div className="sess__nse">
        <p className="sess__head">
          <i className="sess__dot sess__dot--on" aria-hidden="true" />
          <b>NSE is open{special}</b>
          <span>IST</span>
        </p>
        <div className="sess__bar" role="progressbar" aria-label="NSE session" aria-valuemin={0} aria-valuemax={100} aria-valuenow={Math.round(done * 100)}>
          <span style={{ width: `${(done * 100).toFixed(1)}%` }} />
        </div>
        <p className="sess__left">{leftText(close - now)} left</p>
        <p className="sess__ends">
          <span>{time(s.sessionOpenUtc)}</span>
          <span>{time(s.sessionCloseUtc)}</span>
        </p>
      </div>
    )
  }
  const why = s.isHoliday && s.holidayName ? ` for ${s.holidayName}` : ''
  return (
    <div className="sess__nse">
      <p className="sess__head">
        <i className="sess__dot" aria-hidden="true" />
        <b>NSE closed{why}</b>
      </p>
      <p className="sess__left">Opens {dayTime(s.nextMarketOpenUtc)} IST</p>
    </div>
  )
}

export function SessionCard() {
  const nse = useMarketSession('NSE', 'FO')
  const mcx = useMarketSession('MCX', 'COM')
  const now = useNow(30_000)
  // Nothing known yet (or the API is down): no card, rather than a guess.
  if (!nse.data && !mcx.data) return null
  const m = mcx.data
  return (
    <section className="sess" aria-label="Market session">
      {nse.data && <NseBlock s={nse.data} now={now} />}
      {m && (
        <p className="sess__mcx">
          <i className={`sess__dot${m.isMarketOpen ? ' sess__dot--on' : ''}`} aria-hidden="true" />
          {m.isMarketOpen ? `MCX open until ${time(m.sessionCloseUtc)}` : `MCX closed · opens ${dayTime(m.nextMarketOpenUtc)}`}
        </p>
      )}
    </section>
  )
}
