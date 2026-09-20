/**
 * The visitor's own clock, read in IST, and whether the NSE cash session is
 * open by the standard timings (09:15–15:30, Monday to Friday). The public
 * pages do not load the holiday calendar — that lives in the console — which
 * is why the label that shows this always says "holidays aside".
 */

import { useEffect, useState } from 'react'

export interface MarketClock {
  /** "14:32:07" */
  time: string
  open: boolean
  seconds: number
}

export function readMarketClock(now: Date): MarketClock {
  const time = new Intl.DateTimeFormat('en-IN', {
    timeZone: 'Asia/Kolkata', hour: '2-digit', minute: '2-digit', second: '2-digit', hour12: false,
  }).format(now)
  const parts = new Intl.DateTimeFormat('en-GB', {
    timeZone: 'Asia/Kolkata', weekday: 'short', hour: '2-digit', minute: '2-digit', hour12: false,
  }).formatToParts(now)
  const get = (t: string) => parts.find((p) => p.type === t)?.value ?? ''
  const weekday = get('weekday')
  const minutes = Number(get('hour')) * 60 + Number(get('minute'))
  const weekend = weekday === 'Sat' || weekday === 'Sun'
  return { time, open: !weekend && minutes >= 555 && minutes < 930, seconds: now.getSeconds() }
}

export function useMarketClock(): MarketClock {
  const [clock, setClock] = useState(() => readMarketClock(new Date()))
  useEffect(() => {
    const id = window.setInterval(() => setClock(readMarketClock(new Date())), 1000)
    return () => window.clearInterval(id)
  }, [])
  return clock
}
