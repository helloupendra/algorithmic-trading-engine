/**
 * Markets → Option chain → Straddle & IV: how fast the at-the-money
 * straddle is decaying today against other days, and whether today's IV is
 * high or low for this underlying.
 *
 * The chain says what the straddle costs now; it could not say whether that
 * is cheap for 11:00 on an expiry morning, or whether an IV of 13 is the top
 * or the bottom of the month. Both answers were already in the chain the
 * recorder writes every minute, one expiry a day. The straddle chart lays the
 * chosen day over up to three others by the time of day, in rupees or as a
 * share of its first capture (the only way an expiry-day straddle and a
 * Monday one have the same shape); the IV panel shows the day's ATM IV
 * through the session and the ATM IV by day, with the rank and percentile
 * of the value now and how many sessions they are measured over.
 *
 * The day travels in the URL (?d=), so a link opens the session it names;
 * the days compared with it are the reader's own and stay on the page.
 */

import { useEffect, useMemo, useState } from 'react'
import { useSearchParams } from 'react-router-dom'
import { useIvHistory, useStraddleSeries, useStraddleSessions } from '../../../lib/queries'
import {
  MAX_COMPARE,
  NO_COMPARE,
  YEAR_OF_SESSIONS,
  compareWith,
  dayLabel,
  daysBefore,
  expiryDaysBefore,
  lastIv,
  pricingNote,
  sessionLine,
  standingBasis,
  straddleChange,
  straddleFreshness,
  toggleCompare,
} from '../../../lib/straddle'
import type { CompareSlots, IvHistory, IvHistoryDay, PremiumScale, StraddleSeries } from '../../../lib/straddle'
import { istDate, istTime, price, signedPercent, sourceLabel } from '../../../lib/optionChain'
import { formatInr, formatInrWhole } from '../../../lib/format'
import { EmptyState, InlineError, Loading, Panel } from '../../../components/ui'
import { Metric } from '../factorParts'
import { DailyIvChart, SlotKey, TimeOfDayChart } from './StraddleCharts'
import type { OverlayLine } from './StraddleCharts'
import './straddle.css'

/** Recent sessions offered as chips to lay over the chosen one. */
const CHIP_DAYS = 8

const DAY_SHAPE = /^\d{4}-\d{2}-\d{2}$/

export function StraddleView({ underlying }: { underlying: string }) {
  const [params, setParams] = useSearchParams()
  const asked = params.get('d')
  const day = asked && DAY_SHAPE.test(asked) ? asked : null
  const [slots, setSlots] = useState<CompareSlots>(NO_COMPARE)
  const [scale, setScale] = useState<PremiumScale>('rupees')

  const series = useStraddleSeries(underlying, day)
  // A slow answer for the last day asked must never be drawn under the new one's name.
  const data = series.data && series.data.underlying === underlying && (day == null || series.data.date === day) ? series.data : undefined
  const history = useIvHistory(underlying, data?.marketOpen ?? false)
  const ivHistory = history.data?.underlying === underlying ? history.data : undefined
  const days = useMemo(() => ivHistory?.days ?? [], [ivHistory])
  const chosen = data?.date ?? day

  const compareDates = useMemo(() => slots.filter((d): d is string => d != null && d !== chosen), [slots, chosen])
  const compared = useStraddleSessions(underlying, compareDates)

  // Each line keeps its slot, so the pattern a day was drawn with stays its own.
  const sessions = useMemo(() => {
    const out: { slot: number; series: StraddleSeries }[] = []
    if (data) out.push({ slot: 0, series: data })
    slots.forEach((date, i) => {
      const found = date == null ? undefined : compared[compareDates.indexOf(date)]
      if (found && found.date === date) out.push({ slot: i + 1, series: found })
    })
    return out
  }, [data, slots, compared, compareDates])

  const straddleLines = useMemo<OverlayLine[]>(
    () => sessions.map((s) => ({ key: s.series.date, slot: s.slot, points: sessionLine(s.series.points, 'straddle', scale) })),
    [sessions, scale],
  )
  const ivLines = useMemo<OverlayLine[]>(
    () => sessions.map((s) => ({ key: s.series.date, slot: s.slot, points: sessionLine(s.series.points, 'iv') })),
    [sessions],
  )

  const pickDay = (next: string) => {
    const search = new URLSearchParams(params)
    // Today is the page's own default; only another day goes in the link.
    if (next === istDate(Date.now())) search.delete('d')
    else search.set('d', next)
    setParams(search, { replace: true })
  }

  if (series.isError && !data) return <InlineError error={series.error} />
  if (!data) return <Loading label={`Loading the ${underlying} straddle…`} />

  const change = straddleChange(data.points)
  const latest = change?.last ?? null
  const iv = lastIv(data.points)
  const recent = daysBefore(days, data.date, CHIP_DAYS)
  const chips = [...recent, ...compareDates.filter((d) => !recent.includes(d))]
  const full = slots.every((d) => d != null)
  const note = pricingNote(data)

  return (
    <div className="sv">
      <div className="oc-toolbar sv-toolbar">
        <label className="oc-control">
          <span className="oc-control__label">Day</span>
          <select className="field__input field__input--sm sv-day-pick" value={data.date} onChange={(e) => pickDay(e.target.value)}>
            {dayOptions(days, data.date).map((d) => (
              <option key={d.date} value={d.date}>
                {dayLabel(d.date)}
                {d.date === d.expiryDate ? ' · expiry day' : ''}
              </option>
            ))}
          </select>
        </label>
        <span className="small muted">{recordedNote(days)}</span>
        <span className="oc-toolbar__spacer" />
        <span className="sv-quick" role="group" aria-label="Compare with a set of days">
          <button
            type="button"
            className="btn btn--ghost btn--sm"
            disabled={expiryDaysBefore(days, data.date, MAX_COMPARE).length === 0}
            onClick={() => setSlots(compareWith(expiryDaysBefore(days, data.date, MAX_COMPARE)))}
          >
            Last 3 expiry days
          </button>
          <button
            type="button"
            className="btn btn--ghost btn--sm"
            disabled={recent.length === 0}
            onClick={() => setSlots(compareWith(daysBefore(days, data.date, MAX_COMPARE)))}
          >
            Previous 3 days
          </button>
          {compareDates.length > 0 && (
            <button type="button" className="btn btn--ghost btn--sm" onClick={() => setSlots(NO_COMPARE)}>
              Clear
            </button>
          )}
        </span>
        <div className="oc-seg" role="group" aria-label="Straddle scale">
          <button type="button" aria-pressed={scale === 'rupees'} onClick={() => setScale('rupees')} title="The straddle's premium per unit, in rupees">
            ₹ premium
          </button>
          <button
            type="button"
            aria-pressed={scale === 'percentOfFirst'}
            onClick={() => setScale('percentOfFirst')}
            title="Each day as a share of its first priced capture, so days at different distances from expiry decay on one scale"
          >
            % of first
          </button>
        </div>
      </div>

      <div className="sv-compare" role="group" aria-label="Compare with">
        <span className="oc-control__label">Compare with</span>
        {chips.map((d) => {
          const slot = slots.indexOf(d)
          const on = slot >= 0
          const expiry = days.find((x) => x.date === d)?.expiryDate === d
          return (
            <button
              key={d}
              type="button"
              className="sv-chip"
              aria-pressed={on}
              disabled={!on && full}
              title={!on && full ? `${MAX_COMPARE} days at most: take one off first` : undefined}
              onClick={() => setSlots((s) => toggleCompare(s, d))}
            >
              {on && <SlotKey slot={slot + 1} />}
              {dayLabel(d)}
              {expiry && <span className="sv-chip__flag">expiry</span>}
            </button>
          )
        })}
        {chips.length === 0 && <span className="small muted">no earlier session recorded yet</span>}
      </div>

      <FreshnessLine series={data} receivedAt={series.dataUpdatedAt} fetchFailed={series.isError} />
      {day == null && !data.isToday && data.points.length > 0 && (
        <p className="small-note sv-fallback">Nothing has been captured today yet; this is the newest recorded session.</p>
      )}

      <div className="mf-metrics sv-metrics">
        <Metric
          label="ATM straddle"
          value={latest ? formatInr(latest.straddlePrice) : '—'}
          sub={latest ? perLot(latest.straddlePrice!, data.lotSize, latest.atTheMoneyStrike) : 'no ATM prices'}
        />
        <Metric
          label={change ? `Since ${istTime(change.first.capturedUtc)}` : 'Since the first capture'}
          value={change ? signedPercent(change.percent, 1) : '—'}
          // Untoned: a straddle that halves is a seller's gain and a buyer's loss, not either.
          sub={change ? `from ${formatInr(change.first.straddlePrice)}` : undefined}
        />
        <Metric label="Spot" value={latest ? price(latest.spotPrice) : '—'} sub={latest ? `at ${istTime(latest.capturedUtc)} IST` : undefined} />
        <Metric
          label="ATM IV"
          value={iv?.atTheMoneyIv != null ? `${iv.atTheMoneyIv.toFixed(1)}%` : '—'}
          sub={iv ? `at ${istTime(iv.capturedUtc)} IST` : 'not priced'}
        />
        <Metric
          label="IV rank · percentile"
          value={ivHistory?.ivRank != null ? `${Math.round(ivHistory.ivRank)} · ${Math.round(ivHistory.ivPercentile ?? 0)}` : '—'}
          sub={ivHistory ? standingShort(ivHistory) : 'loading'}
        />
        <Metric
          label="Expiry"
          value={data.expiryDate ? dayLabel(data.expiryDate) : '—'}
          sub={data.daysToExpiry == null ? undefined : data.daysToExpiry === 0 ? 'expires this session' : `${data.daysToExpiry} days after this session`}
        />
      </div>

      <Panel title={`${underlying} ATM straddle`} actions={<span className="faint small">{scale === 'rupees' ? 'premium per unit' : 'share of the first priced capture'}</span>}>
        {data.points.length === 0 ? (
          <EmptyState>
            No chain was captured for {underlying} on {dayLabel(data.date)}. The straddle is read from the chain the recorder
            writes once a minute while the market is open.
          </EmptyState>
        ) : (
          <TimeOfDayChart
            lines={straddleLines}
            unit={scale}
            fitKey={`${underlying}-${data.date}-${scale}`}
            label={`${underlying} ATM straddle on ${dayLabel(data.date)}${compareDates.length ? `, against ${compareDates.map(dayLabel).join(', ')}` : ''}`}
          />
        )}
        {note && <p className="small-note">{note} The ATM strike is the one nearest the spot in each minute, so it moves with the market.</p>}
      </Panel>

      <div className="sv-grid">
        <Panel title="ATM IV through the day">
          {data.points.length === 0 ? (
            <EmptyState>Nothing captured on this day.</EmptyState>
          ) : (
            <TimeOfDayChart lines={ivLines} unit="iv" fitKey={`${underlying}-${data.date}-iv`} label={`${underlying} ATM IV on ${dayLabel(data.date)}`} />
          )}
        </Panel>
        <IvByDayPanel underlying={underlying} history={ivHistory} error={history.isError && !ivHistory ? history.error : null} />
      </div>

      <p className="small-note">
        Every reading here is one expiry a day: the nearest on or after that day, the one the recorder captures every
        minute. ATM IV is the mean of the call's and put's IV at the money as the recorder stored it (Dhan's own figure;
        before 15 Sep, the FYERS poller's Black-Scholes on the last trade). The daily readings are taken five minutes inside
        the session (09:20 and 15:25 on NSE), past the opening auction and the last-minute rush. On an expiry day the
        contract has hours to live and its IV is at its noisiest.
      </p>
    </div>
  )
}

function IvByDayPanel({ underlying, history, error }: { underlying: string; history: IvHistory | undefined; error: unknown }) {
  if (error) return <Panel title="ATM IV by day"><InlineError error={error} /></Panel>
  if (!history) return <Panel title="ATM IV by day"><Loading label="Reading the IV history…" /></Panel>

  const sources = [...new Set(history.days.map((d) => d.source).filter((s): s is string => !!s))]
  return (
    <Panel title="ATM IV by day" actions={<span className="faint small">close, and open (dotted)</span>}>
      {history.days.length === 0 ? (
        <EmptyState>No ATM IV has been recorded for {underlying} yet.</EmptyState>
      ) : (
        <>
          <p className="sv-standing small">
            {history.currentIv != null && (
              <>
                <b className="mono">{history.currentIv.toFixed(1)}%</b>{' '}
                {history.currentBasis === 'latest'
                  ? `now (${istTime(history.currentCapturedUtc)} IST)`
                  : `at the close of ${history.currentDate ? dayLabel(history.currentDate) : 'the newest session'}`}
                {history.ivRank != null && (
                  <>
                    {' '}· IV rank <b className="mono">{Math.round(history.ivRank)}</b>
                  </>
                )}
                {history.ivPercentile != null && (
                  <>
                    {' '}· percentile <b className="mono">{Math.round(history.ivPercentile)}</b>
                  </>
                )}
                {history.rangeLow != null && history.rangeHigh != null && ` · range ${history.rangeLow.toFixed(1)}–${history.rangeHigh.toFixed(1)}%`}
                {' · '}
              </>
            )}
            <span className={history.daysUsed < YEAR_OF_SESSIONS ? 'warn' : 'muted'}>{standingBasis(history)}</span>
          </p>
          <DailyIvChart days={history.days} currentIv={history.currentIv} />
          <details className="sv-table">
            <summary className="small">
              All {history.days.length} sessions{sources.length > 1 ? ` · from ${sources.map(sourceLabel).join(' and ')}` : ''}
            </summary>
            <table className="table">
              <thead>
                <tr>
                  <th>Session</th>
                  <th>Expiry</th>
                  <th className="num">Open IV</th>
                  <th className="num">Close IV</th>
                  <th>Source</th>
                </tr>
              </thead>
              <tbody>
                {[...history.days].reverse().map((d) => (
                  <tr key={d.date}>
                    <td className="mono">{dayLabel(d.date)}</td>
                    <td className="mono">{d.daysToExpiry === 0 ? 'today' : `+${d.daysToExpiry} d`}</td>
                    <td className="num mono">{d.openIv != null ? `${d.openIv.toFixed(1)}%` : '—'}</td>
                    <td className="num mono">{d.closeIv != null ? `${d.closeIv.toFixed(1)}%` : '—'}</td>
                    <td className="muted">{d.source ? sourceLabel(d.source) : '—'}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </details>
        </>
      )}
    </Panel>
  )
}

function FreshnessLine({ series, receivedAt, fetchFailed }: { series: StraddleSeries; receivedAt: number; fetchFailed: boolean }) {
  const [now, setNow] = useState(() => Date.now())
  useEffect(() => {
    if (!series.isToday) return
    const id = setInterval(() => setNow(Date.now()), 5_000)
    return () => clearInterval(id)
  }, [series.isToday])
  // The server's clock, carried forward by the time since the answer arrived.
  const f = straddleFreshness(series, Date.parse(series.serverUtc) + (now - receivedAt))
  return (
    <p className={`oc-fresh oc-fresh--${f.tone}`} role="status">
      <span className="oc-fresh__dot" aria-hidden="true" />
      <span>{f.text}</span>
      {fetchFailed && <span className="warn"> · refresh failed, showing the last answer</span>}
    </p>
  )
}

/** The picker's sessions, newest first, with the one shown even before the history has it. */
function dayOptions(days: readonly IvHistoryDay[], shown: string): Pick<IvHistoryDay, 'date' | 'expiryDate'>[] {
  const options: Pick<IvHistoryDay, 'date' | 'expiryDate'>[] = [...days].reverse()
  if (!options.some((d) => d.date === shown)) options.unshift({ date: shown, expiryDate: '' })
  return options
}

/** What the picker holds: "38 sessions recorded since Sun 6 Sep". */
function recordedNote(days: readonly IvHistoryDay[]): string {
  if (days.length === 0) return 'reading which sessions are recorded…'
  return `${days.length} ${days.length === 1 ? 'session' : 'sessions'} recorded since ${dayLabel(days[0].date)}`
}

/** "24,550 · ₹15,944 a lot of 65": the lot is today's, and says so by being named. */
function perLot(straddle: number, lotSize: number | null, strike: number): string {
  const at = strike.toLocaleString('en-IN')
  return lotSize ? `${at} · ${formatInrWhole(straddle * lotSize)} a lot of ${lotSize}` : at
}

/** The rank's basis, short enough for a tile: "over 31 days only", "over the last year". */
function standingShort(history: IvHistory): string {
  if (history.currentIv == null || history.daysUsed === 0) return 'not enough sessions yet'
  return history.daysUsed >= YEAR_OF_SESSIONS ? 'over the last year' : `over ${history.daysUsed} ${history.daysUsed === 1 ? 'day' : 'days'} only`
}
