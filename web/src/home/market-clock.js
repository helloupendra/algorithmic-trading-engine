// The header's market line: whether NSE is open, and when it closes or next
// opens, from the API's own exchange calendar (GET /api/MarketSession/check,
// open to anyone), so a holiday or a special session such as Muhurat trading
// is named rather than guessed. The HTML already says the cash hours, which
// are true at any time; until the API answers, or if it cannot, that line
// stays, because a guess from the visitor's clock cannot know a holiday.

import { istNow, marketLine } from '../lib/marketLine'

async function refresh() {
  const nodes = document.querySelectorAll('[data-market-clock]')
  if (nodes.length === 0) return
  try {
    const res = await fetch('/api/MarketSession/check?exchange=NSE&segment=FO', { headers: { Accept: 'application/json' } })
    if (!res.ok) return
    const line = marketLine(await res.json())
    nodes.forEach((node) => {
      node.textContent = line.text
      const box = node.closest('[data-market-status]')
      if (box) {
        box.dataset.marketStatus = line.open ? 'open' : 'closed'
        box.title = `Time in India now: ${istNow()}`
      }
    })
  } catch {
    // Offline or the API is restarting: the cash hours already on the page stand.
  }
}

if (typeof document !== 'undefined') {
  let timer
  const schedule = () => {
    window.clearTimeout(timer)
    if (document.hidden) return
    void refresh()
    timer = window.setTimeout(schedule, 60_000)
  }
  schedule()
  document.addEventListener('visibilitychange', schedule)
}
