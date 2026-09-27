import { useEffect, useState } from 'react'

/** "Mon 28 Sep 2026". Assembled from parts: en-GB alone now spells the month "Sept". */
function istDateLabel(now: Date): string {
  const parts = new Intl.DateTimeFormat('en-US', { timeZone: 'Asia/Kolkata', weekday: 'short', day: 'numeric', month: 'short', year: 'numeric' })
    .formatToParts(now)
  const get = (type: Intl.DateTimeFormatPartTypes) => parts.find((p) => p.type === type)?.value ?? ''
  return `${get('weekday')} ${get('day')} ${get('month')} ${get('year')}`
}

/** Today in IST, "Mon 28 Sep 2026", whatever the browser's time zone; it turns over at midnight IST. */
export function useIstDate(): string {
  const [label, setLabel] = useState(() => istDateLabel(new Date()))
  useEffect(() => {
    const id = window.setInterval(() => setLabel(istDateLabel(new Date())), 60_000)
    return () => window.clearInterval(id)
  }, [])
  return label
}
