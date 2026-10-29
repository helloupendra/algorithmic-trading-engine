/**
 * Markets → Alerts: "tell me when NIFTY crosses 24,800", "when NIFTY's PCR
 * goes above 1.3".
 *
 * Each user's own level alerts (an admin sees everyone's), what each target
 * reads now, and every time one fired. The service in the API
 * (LevelAlertService) checks them every five seconds in the symbol's own
 * market hours, against the prices the feeds already stream and the chains
 * the recorder already captures. A fire reaches the top bar's badge at once
 * and, for an admin's rule, Telegram's Desk System chat.
 *
 * The form shows the number the rule will be compared with before it is
 * saved, and says so when the level is already behind the price: a cross
 * needs the value on the near side first, which is the part nobody expects.
 *
 * The chain's toolbar and the watchlist's rows open this page with the form
 * filled in (?kind=&target=&level=).
 */

import { useEffect, useRef, useState } from 'react'
import { useSearchParams } from 'react-router-dom'
import { useAuth } from '../../../lib/auth'
import { formatDateTime } from '../../../lib/format'
import { UNDERLYINGS } from '../../../lib/optionChain'
import { scannerHealth } from '../../../lib/patterns'
import {
  EMPTY_FORM,
  conditionsFor,
  deliveryLine,
  formFromLink,
  formProblems,
  formToRequest,
  formatLevel,
  parseLevel,
  readingLine,
  rearmAt,
  ruleToForm,
  sideNote,
  statusLine,
  statusTone,
} from '../../../lib/levelAlerts'
import type { LevelAlertFire, LevelAlertRule, LevelAlertStatus, LevelForm, LevelKind } from '../../../lib/levelAlerts'
import {
  useDeleteLevelAlert,
  useLevelAlertFires,
  useLevelAlertNow,
  useLevelAlertRules,
  useLevelAlertStatus,
  useLevelAlertSummary,
  useMarkLevelAlertsSeen,
  useSaveLevelAlert,
} from '../../../lib/levelAlertQueries'
import { Badge, EmptyState, InlineError, Panel, QueryBoundary } from '../../../components/ui'
import { SymbolCombobox } from '../../../components/SymbolCombobox'
import { DateField } from '../../../components/DateField'
import './level-alerts.css'

/** Today in IST as yyyy-MM-dd: the first day a rule may end on. */
function istToday(): string {
  return new Date().toLocaleDateString('en-CA', { timeZone: 'Asia/Kolkata' })
}

// ------------------------------------------------------------------ status --

function StatusStrip({ status, isAdmin, firedToday }: { status: LevelAlertStatus; isAdmin: boolean; firedToday: number | undefined }) {
  const health = scannerHealth(status, Date.now(), 'LevelAlerts:Enabled (or PatternAlerts:Enabled) is false on this API.')
  const live = status.watching.filter((w) => w.state === 'live').length
  return (
    <div className="stat-grid">
      <div className="stat">
        <div className={`stat__value ${health.tone === 'neutral' ? '' : health.tone}`}>{health.label}</div>
        <div className="stat__label">Service</div>
        <div className="stat__sub">{health.detail}</div>
      </div>
      <div className="stat">
        <div className="stat__value">{status.watching.length}</div>
        <div className="stat__label">{isAdmin ? 'Targets watched' : 'Your targets watched'}</div>
        <div className="stat__sub">
          {live} with a fresh reading now · prices every {status.intervalSeconds}s, chains every {status.pcrIntervalSeconds}s
        </div>
      </div>
      <div className="stat">
        <div className="stat__value">{firedToday ?? '—'}</div>
        <div className="stat__label">Your alerts today</div>
        <div className="stat__sub">Since 00:00 IST</div>
      </div>
      <div className="stat">
        <div className={`stat__value ${isAdmin && !status.telegramConfigured ? 'warn' : ''}`}>
          {isAdmin ? (status.telegramConfigured ? 'Telegram on' : 'Telegram off') : 'Console'}
        </div>
        <div className="stat__label">Delivery</div>
        <div className="stat__sub">
          {isAdmin
            ? status.telegramConfigured
              ? `An admin's alerts also go to the ${status.telegramSystemChatConfigured ? 'Desk System chat' : 'trades chat (no Desk System chat is set)'}, at most ${status.telegramMaxMessages} messages in ${status.telegramWindowMinutes} min.`
              : 'No bot token or chat id on the server; alerts are shown here only.'
            : 'Here and in the top bar. Telegram is the desk’s own channel and carries an admin’s alerts only.'}
          {isAdmin && status.telegramMessagesSuppressed + status.telegramMessagesFailed > 0 && status.lastTelegramProblem && (
            <span className="warn"> {status.lastTelegramProblem}</span>
          )}
        </div>
      </div>
    </div>
  )
}

// ------------------------------------------------------------------- editor --

function Editor({
  form,
  onChange,
  onClose,
  isAdmin,
  defaults,
}: {
  form: LevelForm
  onChange: (f: LevelForm) => void
  onClose: () => void
  isAdmin: boolean
  defaults: { price: number; pcr: number }
}) {
  const save = useSaveLevelAlert()
  const now = useLevelAlertNow(form.kind, form.target)
  const [tried, setTried] = useState(false)
  const set = (change: Partial<LevelForm>) => onChange({ ...form, ...change })
  const level = parseLevel(form.level)
  const problems = formProblems(form)
  const reading = now.data
  const line = reading ? readingLine(reading, Date.now()) : null
  const note = sideNote(form.kind, form.condition, level, reading)
  const rearm = form.repeat ? rearmAt(form.kind, form.condition, level, parseLevel(form.rearmBy), defaults) : null
  const verb = conditionsFor(form.kind).find((c) => c.value === form.condition)?.label.toLowerCase() ?? form.condition
  const subject = reading?.displayName ?? (form.target.trim().toUpperCase() || (form.kind === 'pcr' ? 'the PCR' : 'the price'))

  const pickKind = (kind: LevelKind) => {
    if (kind !== form.kind) set({ kind, target: kind === 'pcr' ? UNDERLYINGS[0] : '', condition: 'above', level: '', rearmBy: '' })
  }

  return (
    <form
      className="la-editor"
      onSubmit={(e) => {
        e.preventDefault()
        setTried(true)
        if (problems.length === 0) save.mutate({ id: form.id, request: formToRequest(form) }, { onSuccess: onClose })
      }}
    >
      <div className="la-editor__row">
        <div className="field">
          <span className="field__label">Watch</span>
          <div className="seg" role="group" aria-label="What to watch">
            {(['price', 'pcr'] as const).map((k) => (
              <button key={k} type="button" className={`seg__btn ${form.kind === k ? 'is-active' : ''}`} aria-pressed={form.kind === k} onClick={() => pickKind(k)}>
                {k === 'price' ? 'Price' : 'PCR'}
              </button>
            ))}
          </div>
        </div>

        <div className="field la-editor__target">
          <label className="field__label" htmlFor="la-target">
            {form.kind === 'price' ? 'Symbol' : 'Chain (nearest expiry)'}
          </label>
          {form.kind === 'price' ? (
            <SymbolCombobox id="la-target" value={form.target} onChange={(target) => set({ target })} placeholder="NSE:NIFTY50-INDEX" />
          ) : (
            <select id="la-target" className="field__input" value={form.target} onChange={(e) => set({ target: e.target.value })}>
              {[...new Set([...UNDERLYINGS, form.target].filter(Boolean))].map((u) => (
                <option key={u} value={u}>
                  {u}
                </option>
              ))}
            </select>
          )}
          <span className={`field__help ${line?.tone === 'warn' ? 'warn' : ''}`} role="status">
            {now.isError ? (
              <span className="warn">Could not read it now.</span>
            ) : line ? (
              <>
                Now: {line.text}
                {reading?.value != null && (
                  <button type="button" className="btn btn--ghost btn--sm la-use" onClick={() => set({ level: String(reading.value) })}>
                    Use {formatLevel(form.kind, reading.value)}
                  </button>
                )}
              </>
            ) : form.kind === 'price' ? (
              'Search by name: "nifty", "sbin", "crude fut".'
            ) : (
              'Total put OI over total call OI of the nearest expiry, as the chain page shows it.'
            )}
          </span>
        </div>
      </div>

      <div className="la-editor__row">
        <div className="field">
          <label className="field__label" htmlFor="la-condition">
            When it
          </label>
          <select id="la-condition" className="field__input" value={form.condition} onChange={(e) => set({ condition: e.target.value as LevelForm['condition'] })}>
            {conditionsFor(form.kind).map((c) => (
              <option key={c.value} value={c.value}>
                {c.label}
              </option>
            ))}
          </select>
        </div>
        <div className="field">
          <label className="field__label" htmlFor="la-level">
            Level
          </label>
          <input
            id="la-level"
            className="field__input mono"
            inputMode="decimal"
            value={form.level}
            placeholder={form.kind === 'pcr' ? '1.30' : '24800'}
            aria-invalid={tried && level == null}
            onChange={(e) => set({ level: e.target.value })}
          />
        </div>
        <div className="field">
          <label className="field__label" htmlFor="la-expires">
            Last day (optional)
          </label>
          <div className="la-inline">
            <DateField id="la-expires" value={form.expiresOn} min={istToday()} onChange={(expiresOn) => set({ expiresOn })} />
            {form.expiresOn && (
              <button type="button" className="btn btn--ghost btn--sm" onClick={() => set({ expiresOn: '' })}>
                No end
              </button>
            )}
          </div>
        </div>
      </div>

      <div className="la-editor__row">
        <div className="field">
          <span className="field__label">How often</span>
          <div className="la-checks">
            <label className="coverage-chip">
              <input type="radio" name="la-repeat" checked={!form.repeat} onChange={() => set({ repeat: false })} />
              Once, then stop
            </label>
            <label className="coverage-chip">
              <input type="radio" name="la-repeat" checked={form.repeat} onChange={() => set({ repeat: true })} />
              Every time
            </label>
          </div>
        </div>
        {form.repeat && (
          <div className="field">
            <label className="field__label" htmlFor="la-rearm">
              Arm again after a move back of
            </label>
            <div className="la-inline">
              <input
                id="la-rearm"
                className="field__input mono la-rearm"
                inputMode="decimal"
                value={form.rearmBy}
                placeholder={String(form.kind === 'pcr' ? defaults.pcr : defaults.price)}
                onChange={(e) => set({ rearmBy: e.target.value })}
              />
              <span className="muted">{form.kind === 'pcr' ? 'PCR points' : '% of the level'}</span>
            </div>
            {rearm && <span className="field__help">It fires, then waits until {rearm}.</span>}
          </div>
        )}
        <div className="field la-editor__note">
          <label className="field__label" htmlFor="la-note">
            Note (sent with the alert)
          </label>
          <input id="la-note" className="field__input" maxLength={200} value={form.note} placeholder="breakout above the week's high" onChange={(e) => set({ note: e.target.value })} />
        </div>
      </div>

      <div className="la-checks" style={{ marginTop: 10 }}>
        {isAdmin && (
          <label className="coverage-chip">
            <input type="checkbox" checked={form.notify} onChange={(e) => set({ notify: e.target.checked })} />
            Also to Telegram (Desk System)
          </label>
        )}
        {form.id !== null && (
          <label className="coverage-chip">
            <input type="checkbox" checked={form.active} onChange={(e) => set({ active: e.target.checked })} />
            Watching (off: paused)
          </label>
        )}
      </div>

      <p className="la-preview">
        <b>
          {subject} {verb} {level != null ? formatLevel(form.kind, level) : '…'}
        </b>
        {form.repeat ? ' · every time' : ' · once'}
        {note && <span className="la-preview__note">{note}</span>}
      </p>

      {tried && problems.length > 0 && (
        <div className="alert alert--warn alert--stack" role="status">
          {problems.map((p) => (
            <div key={p}>{p}</div>
          ))}
        </div>
      )}
      {save.isError && <InlineError error={save.error} />}
      <div className="la-actions">
        <button type="submit" className="btn btn--primary btn--sm" disabled={save.isPending}>
          {save.isPending ? 'Saving…' : form.id === null ? 'Create alert' : 'Save alert'}
        </button>
        <button type="button" className="btn btn--ghost btn--sm" onClick={onClose}>
          Cancel
        </button>
      </div>
    </form>
  )
}

// -------------------------------------------------------------------- rules --

function RulesTable({
  rules,
  showOwner,
  onEdit,
}: {
  rules: LevelAlertRule[]
  showOwner: boolean
  onEdit: (f: LevelForm) => void
}) {
  const save = useSaveLevelAlert()
  const remove = useDeleteLevelAlert()
  const nowMs = Date.now()
  const flip = (rule: LevelAlertRule, active: boolean) => save.mutate({ id: rule.id, request: formToRequest({ ...ruleToForm(rule), active }) })

  return (
    <>
      <div className="tablewrap">
        <table className="table">
          <thead>
            <tr>
              <th>Alert</th>
              <th>Now</th>
              <th>State</th>
              <th>Last fired</th>
              {showOwner && <th>Owner</th>}
              <th />
            </tr>
          </thead>
          <tbody>
            {rules.map((r) => {
              const line = readingLine(r.now, nowMs)
              return (
                <tr key={r.id}>
                  <td className="la-rule">
                    {r.description}
                    <span className="cell-sub">
                      {[
                        r.repeat ? `repeats, re-arms after ${r.rearmBy ?? ''}${r.rearmUnit === 'percent' ? '%' : ' points'}` : 'once',
                        r.expiresOn ? `until ${r.expiresOn.slice(8, 10)}/${r.expiresOn.slice(5, 7)}` : null,
                        r.telegramApplies && r.notify ? 'Telegram' : null,
                      ]
                        .filter(Boolean)
                        .join(' · ')}
                    </span>
                    {r.note && <span className="cell-sub la-note">“{r.note}”</span>}
                  </td>
                  <td className={line.tone === 'warn' ? 'warn' : undefined}>
                    <span className="mono">{line.text}</span>
                    {r.side && r.now?.state === 'live' && <span className="cell-sub">{r.side === 'at' ? 'on the level' : `${r.side} the level`}</span>}
                  </td>
                  <td>
                    <Badge tone={statusTone(r.status)}>{r.status}</Badge>
                    <span className="cell-sub">{statusLine(r)}</span>
                  </td>
                  <td className="mono">{r.lastFiredUtc ? formatDateTime(r.lastFiredUtc) : '—'}</td>
                  {showOwner && <td>{r.ownerUserName ?? r.userId}</td>}
                  <td>
                    <span className="la-actions">
                      {r.status === 'active' && (
                        <button type="button" className="btn btn--ghost btn--sm" disabled={save.isPending} onClick={() => flip(r, false)}>
                          Pause
                        </button>
                      )}
                      {(r.status === 'paused' || r.status === 'fired') && (
                        <button type="button" className="btn btn--ghost btn--sm" disabled={save.isPending} onClick={() => flip(r, true)}>
                          {r.status === 'fired' ? 'Re-arm' : 'Resume'}
                        </button>
                      )}
                      <button type="button" className="btn btn--ghost btn--sm" onClick={() => onEdit(ruleToForm(r))}>
                        Edit
                      </button>
                      <button
                        type="button"
                        className="btn btn--ghost btn--sm"
                        disabled={remove.isPending}
                        onClick={() => {
                          if (window.confirm(`Delete "${r.description}"? What it fired stays in the history.`)) remove.mutate(r.id)
                        }}
                      >
                        Delete
                      </button>
                    </span>
                  </td>
                </tr>
              )
            })}
          </tbody>
        </table>
      </div>
      {save.isError && <InlineError error={save.error} />}
      {remove.isError && <InlineError error={remove.error} />}
    </>
  )
}

// -------------------------------------------------------------------- fired --

function FiredTable({ fires, showOwner, fresh }: { fires: LevelAlertFire[]; showOwner: boolean; fresh: ReadonlySet<number> }) {
  return (
    <div className="tablewrap tablewrap--tall">
      <table className="table">
        <thead>
          <tr>
            <th>Fired (IST)</th>
            <th>Alert</th>
            <th className="r">Value</th>
            <th>Delivered</th>
            {showOwner && <th>Owner</th>}
          </tr>
        </thead>
        <tbody>
          {fires.map((f) => (
            <tr key={f.id} className={fresh.has(f.id) ? 'la-fresh' : undefined}>
              <td className="mono">
                {formatDateTime(f.firedUtc)}
                {fresh.has(f.id) && (
                  <span className="cell-sub">
                    <Badge tone="accent">new</Badge>
                  </span>
                )}
              </td>
              <td className="la-rule">
                {f.title}
                <span className="cell-sub">{f.message.slice(f.title.length).replace(/^:\s*/, '')}</span>
              </td>
              <td className="r mono">
                {formatLevel(f.kind, f.value)}
                <span className="cell-sub">from {formatLevel(f.kind, f.previousValue)}</span>
              </td>
              <td className="la-delivery">{deliveryLine(f)}</td>
              {showOwner && <td>{f.ownerUserName ?? f.userId}</td>}
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}

/**
 * The caller's own new fires, as they were when the page first showed them,
 * so the "new" marks survive the seen-mark that clears the top bar's badge.
 */
function useFreshOnView(fires: LevelAlertFire[] | undefined, me: number | undefined): ReadonlySet<number> {
  const { mutate: markSeen } = useMarkLevelAlertsSeen()
  const [fresh, setFresh] = useState<ReadonlySet<number>>(() => new Set())
  const marked = useRef(new Set<number>())

  useEffect(() => {
    if (!fires || me == null) return
    const unseen = fires.filter((f) => f.userId === me && !f.seen && !marked.current.has(f.id)).map((f) => f.id)
    if (unseen.length === 0) return
    unseen.forEach((id) => marked.current.add(id))
    setFresh((s) => new Set([...s, ...unseen]))
    markSeen()
  }, [fires, me, markSeen])

  return fresh
}

// --------------------------------------------------------------------- page --

export function LevelAlertsPage() {
  const { isAdmin, user } = useAuth()
  const [params, setParams] = useSearchParams()
  const [form, setForm] = useState<LevelForm | null>(() => formFromLink(params))
  const [mine, setMine] = useState(false)
  const status = useLevelAlertStatus()
  const summary = useLevelAlertSummary()
  const rules = useLevelAlertRules(isAdmin && mine)
  const fires = useLevelAlertFires(isAdmin && mine)
  const fresh = useFreshOnView(fires.data, user?.id)
  const showOwner = isAdmin && !mine
  const defaults = { price: status.data?.defaultPriceRearmPercent ?? 0.2, pcr: status.data?.defaultPcrRearmPoints ?? 0.05 }

  // A link from the chain or the watchlist opens the form once; the address is then the page's own again.
  useEffect(() => {
    const linked = formFromLink(params)
    if (!linked) return
    setForm(linked)
    setParams({}, { replace: true })
  }, [params, setParams])

  return (
    <div className="page la-page">
      <header className="page__header">
        <div>
          <h1 className="page__title">Level alerts</h1>
          <p className="page__subtitle">
            Be told when a price crosses or touches a level, or a chain’s PCR goes above or below one. Checked every few
            seconds in the symbol’s own market hours, against the prices the feeds already stream.
          </p>
        </div>
        <button type="button" className="btn btn--primary btn--sm" onClick={() => setForm({ ...EMPTY_FORM })}>
          New alert
        </button>
      </header>

      <QueryBoundary query={status}>
        {(s) => (
          <>
            <StatusStrip status={s} isAdmin={isAdmin} firedToday={summary.data?.firedToday} />
            {isAdmin && s.skipped.length > 0 && (
              <div className="alert alert--warn alert--stack" role="status">
                {s.skipped.map((x) => (
                  <div key={x}>{x}</div>
                ))}
              </div>
            )}
          </>
        )}
      </QueryBoundary>

      {form && (
        <Panel title={form.id === null ? 'New alert' : 'Edit alert'}>
          <Editor form={form} onChange={setForm} onClose={() => setForm(null)} isAdmin={isAdmin} defaults={defaults} />
        </Panel>
      )}

      <Panel
        title={isAdmin && !mine ? 'Everyone’s alerts' : 'Your alerts'}
        actions={
          isAdmin && (
            <div className="seg" role="group" aria-label="Whose alerts">
              <button type="button" className={`seg__btn ${!mine ? 'is-active' : ''}`} aria-pressed={!mine} onClick={() => setMine(false)}>
                Everyone
              </button>
              <button type="button" className={`seg__btn ${mine ? 'is-active' : ''}`} aria-pressed={mine} onClick={() => setMine(true)}>
                Mine
              </button>
            </div>
          )
        }
      >
        <QueryBoundary query={rules}>
          {(list) =>
            list.length === 0 ? (
              <EmptyState>
                No level alerts yet. Start one with New alert, or from the Alert button on the option chain or a watchlist
                row.
              </EmptyState>
            ) : (
              <RulesTable rules={list} showOwner={showOwner} onEdit={setForm} />
            )
          }
        </QueryBoundary>
      </Panel>

      <Panel
        title="Fired"
        actions={
          fires.data && (
            <span className="small-note muted" style={{ margin: 0 }}>
              newest {Math.min(fires.data.length, 200)}
            </span>
          )
        }
      >
        <QueryBoundary query={fires}>
          {(list) =>
            list.length === 0 ? (
              <EmptyState>Nothing has fired yet.</EmptyState>
            ) : (
              <FiredTable fires={list} showOwner={showOwner} fresh={fresh} />
            )
          }
        </QueryBoundary>
      </Panel>

      <Panel title="How a level alert decides">
        <ul className="la-guide">
          <li>
            A cross is two fresh readings on opposite sides of the level. “Above” is strictly above: from at or below the
            level to above it. A price already past the level when the rule is made fires only after it has come back.
          </li>
          <li>
            A price is fresh for {status.data ? status.data.priceFreshSeconds : 60} seconds after the exchange stamped it, a
            chain capture for {status.data ? Math.round(status.data.pcrFreshSeconds / 60) : 3} minutes (the recorder captures
            once a minute). Nothing older fires, and neither does a price from before the open.
          </li>
          <li>
            Each symbol is read in its own exchange’s hours: NSE and BSE 09:15–15:30, MCX from 09:00 into the evening. The
            first reading of a session is a baseline, so an overnight gap through the level is not a cross.
          </li>
          <li>
            A PCR is total put OI over total call OI of the nearest expiry at its newest capture, to two decimals, as the
            chain page shows it.
          </li>
          <li>
            “Once” stops after it fires. “Every time” fires, then waits until the value has moved back from the level by the
            distance you set, so a price sitting on its level is one alert, not one per tick.
          </li>
        </ul>
      </Panel>
    </div>
  )
}
