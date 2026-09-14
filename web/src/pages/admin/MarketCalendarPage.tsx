/**
 * System module — Market calendar: the exchanges' own holiday lists.
 *
 * Every session check on the platform answers from this calendar: the morning
 * job, every live feed, the topbar. It exists because on 2026-09-14, Ganesh
 * Chaturthi, the platform knew only weekends; the morning job waited for a
 * broker sign-in until 14:30 and the feeds took a closed market for a silent one.
 *
 * One row per date and one column per exchange, the way the circulars read
 * side by side: NSE and BSE close whole days, MCX often only its morning.
 * Admins add or correct a date when an exchange amends its list; a change is
 * live at once.
 */

import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api } from '../../lib/api'
import { Badge, EmptyState, InlineError, Loading, Panel } from '../../components/ui'

type Closure = 'FullDay' | 'MorningSession' | 'EveningSession'

interface Holiday {
  id: number
  exchange: string
  date: string
  weekday: string
  name: string
  closure: Closure
  source: string | null
  updatedBy: string | null
  updatedUtc: string
}

interface SpecialSession {
  id: number
  exchange: string
  date: string
  weekday: string
  name: string
  openIst: string
  closeIst: string
  source: string | null
}

interface ExchangeStatus {
  exchange: string
  yearsLoaded: number[]
  warning: string | null
  today: Holiday | null
  next: Holiday | null
}

interface CalendarResponse {
  year: number
  holidays: Holiday[]
  specialSessions: SpecialSession[]
  exchanges: ExchangeStatus[]
}

interface HolidayForm {
  exchange: string
  date: string
  name: string
  closure: Closure
  source: string
}

const EXCHANGES = ['NSE', 'BSE', 'MCX'] as const

const CLOSURE_LABEL: Record<Closure, string> = {
  FullDay: 'Closed',
  MorningSession: 'Morning closed',
  EveningSession: 'Evening closed',
}

const EMPTY_FORM: HolidayForm = { exchange: 'NSE', date: '', name: '', closure: 'FullDay', source: '' }

/** "2026-09-14" → "14 Sep", read as an IST calendar date. */
function dayMonth(iso: string): string {
  // en-GB, not en-IN: en-IN spells September "Sept", every other month in three letters.
  return new Date(`${iso}T12:00:00+05:30`).toLocaleDateString('en-GB', { day: '2-digit', month: 'short', timeZone: 'Asia/Kolkata' })
}

function istYear(): number {
  return Number(new Date().toLocaleString('en-IN', { year: 'numeric', timeZone: 'Asia/Kolkata' }))
}

export function MarketCalendarPage() {
  const [year, setYear] = useState(istYear)
  const [form, setForm] = useState<HolidayForm>(EMPTY_FORM)
  const qc = useQueryClient()

  const calendar = useQuery({
    queryKey: ['market-calendar', year],
    queryFn: () => api.get<CalendarResponse>(`/api/MarketCalendar?year=${year}`),
  })

  // A change moves today's session answer too, so the topbar re-asks at once.
  const refresh = () => {
    qc.invalidateQueries({ queryKey: ['market-calendar'] })
    qc.invalidateQueries({ queryKey: ['session'] })
  }
  const save = useMutation({
    mutationFn: (body: HolidayForm) => api.post<Holiday>('/api/MarketCalendar/holidays', body),
    onSuccess: () => {
      setForm(EMPTY_FORM)
      refresh()
    },
  })
  const remove = useMutation({
    mutationFn: (id: number) => api.delete<void>(`/api/MarketCalendar/holidays/${id}`),
    onSuccess: refresh,
  })

  const data = calendar.data
  const dates = data ? [...new Set(data.holidays.map((h) => h.date))].sort() : []
  const byDay = new Map((data?.holidays ?? []).map((h) => [`${h.date}|${h.exchange}`, h]))

  function confirmRemove(h: Holiday) {
    if (window.confirm(`Remove ${h.exchange} ${h.name} on ${dayMonth(h.date)}? The exchange will be treated as open that day.`)) {
      remove.mutate(h.id)
    }
  }

  return (
    <div className="page">
      <header className="page__header">
        <div>
          <h1 className="page__title">Market calendar</h1>
          <p className="page__subtitle">
            The exchanges' official holidays. The morning job, every live feed and the topbar ask this before
            treating a quiet market as a broken one. Each date carries the circular it came from.
          </p>
        </div>
      </header>

      {calendar.isPending ? (
        <Loading label="Loading the calendar…" />
      ) : calendar.isError && !data ? (
        <InlineError error={calendar.error} />
      ) : data ? (
        <>
          <div className="stat-grid" style={{ gridTemplateColumns: 'repeat(auto-fit, minmax(220px, 1fr))' }}>
            {data.exchanges.map((e) => (
              <div className="stat" key={e.exchange}>
                <div className="stat__value">
                  {e.today ? (
                    <span className="warn">{CLOSURE_LABEL[e.today.closure]}</span>
                  ) : (
                    <span className="pos">Trading day</span>
                  )}
                </div>
                <div className="stat__label">{e.exchange} today{e.today ? ` · ${e.today.name}` : ''}</div>
                <div className="stat__sub">
                  {e.warning ? (
                    <span className="warn">{e.warning}</span>
                  ) : e.next ? (
                    <>
                      Next: {dayMonth(e.next.date)} ({e.next.weekday.slice(0, 3)}) · {e.next.name}
                      {e.next.closure !== 'FullDay' && <> · {CLOSURE_LABEL[e.next.closure].toLowerCase()}</>}
                    </>
                  ) : (
                    <>No later holiday loaded</>
                  )}
                </div>
              </div>
            ))}
          </div>

          <Panel
            title={`Holidays ${data.year}`}
            actions={
              <span style={{ display: 'inline-flex', gap: 6 }}>
                <button type="button" className="btn btn--ghost btn--sm" onClick={() => setYear((y) => y - 1)}>
                  ← {year - 1}
                </button>
                <button type="button" className="btn btn--ghost btn--sm" onClick={() => setYear((y) => y + 1)}>
                  {year + 1} →
                </button>
              </span>
            }
          >
            {dates.length === 0 ? (
              <EmptyState>
                No holidays are loaded for {data.year}. Until they are, every weekday is treated as a trading day.
                Exchanges usually publish next year's list in mid-December.
              </EmptyState>
            ) : (
              <div className="tablewrap">
                <table className="table">
                  <thead>
                    <tr>
                      <th>Date</th>
                      <th>Occasion</th>
                      {EXCHANGES.map((ex) => (
                        <th key={ex}>{ex}</th>
                      ))}
                      <th>Source</th>
                    </tr>
                  </thead>
                  <tbody>
                    {dates.map((date) => {
                      const rows = EXCHANGES.map((ex) => byDay.get(`${date}|${ex}`))
                      const first = rows.find((r): r is Holiday => !!r)!
                      const sources = [...new Set(rows.flatMap((r) => (r?.source ? [r.source] : [])))]
                      return (
                        <tr key={date}>
                          <td className="mono" style={{ whiteSpace: 'nowrap' }}>
                            {dayMonth(date)} <span className="faint">{first.weekday.slice(0, 3)}</span>
                          </td>
                          <td>{first.name}</td>
                          {rows.map((r, i) => (
                            <td key={EXCHANGES[i]} style={{ whiteSpace: 'nowrap' }}>
                              {r ? (
                                <>
                                  <Badge tone={r.closure === 'FullDay' ? 'neg' : 'warn'}>{CLOSURE_LABEL[r.closure]}</Badge>{' '}
                                  <button
                                    type="button"
                                    className="btn btn--ghost btn--sm calendar-remove"
                                    title={`Remove ${r.exchange} ${r.name}`}
                                    onClick={() => confirmRemove(r)}
                                    disabled={remove.isPending}
                                  >
                                    ×
                                  </button>
                                </>
                              ) : (
                                <span className="faint">Open</span>
                              )}
                            </td>
                          ))}
                          <td className="faint" style={{ fontSize: 12 }}>
                            {sources.join(' · ') || '—'}
                          </td>
                        </tr>
                      )
                    })}
                  </tbody>
                </table>
              </div>
            )}
            {remove.isError && <InlineError error={remove.error} />}
          </Panel>

          {data.specialSessions.length > 0 && (
            <Panel title="Special sessions">
              <div className="tablewrap">
                <table className="table">
                  <thead>
                    <tr>
                      <th>Date</th>
                      <th>Exchange</th>
                      <th>Session</th>
                      <th>Hours (IST)</th>
                      <th>Source</th>
                    </tr>
                  </thead>
                  <tbody>
                    {data.specialSessions.map((s) => (
                      <tr key={s.id}>
                        <td className="mono">
                          {dayMonth(s.date)} <span className="faint">{s.weekday.slice(0, 3)}</span>
                        </td>
                        <td>{s.exchange}</td>
                        <td>{s.name}</td>
                        <td className="mono">
                          {s.openIst}–{s.closeIst}
                        </td>
                        <td className="faint" style={{ fontSize: 12 }}>
                          {s.source ?? '—'}
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            </Panel>
          )}

          <Panel title="Add or correct a holiday">
            <form
              onSubmit={(e) => {
                e.preventDefault()
                save.mutate(form)
              }}
            >
              <div className="form-row">
                <div className="field">
                  <label className="field__label" htmlFor="mc-exchange">
                    Exchange
                  </label>
                  <select
                    id="mc-exchange"
                    className="field__input"
                    value={form.exchange}
                    onChange={(e) =>
                      setForm((f) => ({ ...f, exchange: e.target.value, closure: e.target.value === 'MCX' ? f.closure : 'FullDay' }))
                    }
                  >
                    {EXCHANGES.map((ex) => (
                      <option key={ex} value={ex}>
                        {ex}
                      </option>
                    ))}
                  </select>
                </div>
                <div className="field">
                  <label className="field__label" htmlFor="mc-date">
                    Date
                  </label>
                  <input
                    id="mc-date"
                    type="date"
                    className="field__input"
                    value={form.date}
                    onChange={(e) => setForm((f) => ({ ...f, date: e.target.value }))}
                    required
                  />
                </div>
                <div className="field">
                  <label className="field__label" htmlFor="mc-closure">
                    Closed
                  </label>
                  <select
                    id="mc-closure"
                    className="field__input"
                    value={form.closure}
                    onChange={(e) => setForm((f) => ({ ...f, closure: e.target.value as Closure }))}
                    disabled={form.exchange !== 'MCX'}
                  >
                    <option value="FullDay">Whole day</option>
                    <option value="MorningSession">Morning session (MCX)</option>
                    <option value="EveningSession">Evening session (MCX)</option>
                  </select>
                </div>
              </div>
              <div className="form-row">
                <div className="field" style={{ flex: 1 }}>
                  <label className="field__label" htmlFor="mc-name">
                    Occasion
                  </label>
                  <input
                    id="mc-name"
                    className="field__input"
                    value={form.name}
                    maxLength={120}
                    onChange={(e) => setForm((f) => ({ ...f, name: e.target.value }))}
                    placeholder="Ganesh Chaturthi"
                    required
                  />
                </div>
                <div className="field" style={{ flex: 1 }}>
                  <label className="field__label" htmlFor="mc-source">
                    Circular
                  </label>
                  <input
                    id="mc-source"
                    className="field__input"
                    value={form.source}
                    maxLength={200}
                    onChange={(e) => setForm((f) => ({ ...f, source: e.target.value }))}
                    placeholder="NSE/CMTR/71775 (12 Dec 2025)"
                    required
                  />
                </div>
              </div>
              <p className="field__help">
                Saving a date that already exists for the exchange replaces it. The change is live immediately.
              </p>
              {save.isError && <InlineError error={save.error} />}
              <button type="submit" className="btn btn--primary btn--sm" disabled={save.isPending}>
                {save.isPending ? 'Saving…' : 'Save holiday'}
              </button>
            </form>
          </Panel>
        </>
      ) : null}
    </div>
  )
}
