/**
 * "As of" on the Chain and OI history tabs: a recorded day, a minute on it
 * and the way back to live. The clock itself lives in the URL
 * (useReplayClock), so both tabs, and a link to either, stop at the same
 * capture.
 *
 * The days offered are the ones the recorder captured for the underlying
 * (GET /api/OptionChain/days/recorded), never a calendar to guess in: a day
 * with no chain is not offered rather than opening on an empty page.
 */

import { useOptionChainRecordedExpiries } from '../../../lib/queries'
import { LIVE, captureBounds, replayDayOptions } from '../../../lib/chainReplay'
import { istDate } from '../../../lib/optionChain'
import type { ReplayState } from './useReplayClock'

export function ReplayClockControl({
  underlying,
  replay,
  expiry,
}: {
  underlying: string
  replay: ReplayState
  /** The expiry on screen: its first and last capture that day bound the minute. */
  expiry: string | null
}) {
  const { clock, days, newestDay, sessionDay, setClock } = replay
  // The Chain tab's own query for the day's expiry picker, so this costs no request there.
  const recorded = useOptionChainRecordedExpiries(underlying, sessionDay ?? undefined)
  const bounds = captureBounds(recorded.data?.underlying === underlying ? recorded.data : undefined, expiry)
  const options = replayDayOptions(days, newestDay, clock.day, istDate(Date.now()))
  const rewound = clock.day != null || clock.time != null

  return (
    <div className="oc-control oc-replay" role="group" aria-label="Replay clock (IST)" title="Rewind to a recorded day, and a minute on it (IST)">
      <span className="oc-control__label">As of</span>
      <select
        className="field__input field__input--sm"
        value={sessionDay ?? ''}
        onChange={(e) => setClock({ day: e.target.value || null, time: clock.time })}
        aria-label="Replay day (IST)"
        disabled={options.length === 0}
      >
        {options.length === 0 && <option value="">reading the recorded days…</option>}
        {options.map((o) => (
          <option key={o.date} value={o.date}>
            {o.label}
          </option>
        ))}
      </select>
      <input
        type="time"
        className="field__input field__input--sm oc-time"
        value={clock.time ?? ''}
        min={bounds.min}
        max={bounds.max}
        onChange={(e) => setClock({ day: clock.day, time: e.target.value || null })}
        aria-label="Replay time (IST)"
      />
      {rewound && (
        <button type="button" className="btn btn--sm btn--primary" onClick={() => setClock(LIVE)}>
          Back to live
        </button>
      )}
    </div>
  )
}
