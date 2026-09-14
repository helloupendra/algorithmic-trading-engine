/**
 * System module — Pattern alerts: candle patterns on live bars, as they close.
 *
 * The scanner (CandlePatternAlertService in the API) rolls the live 1-minute
 * bars of every watched symbol into 3–60-minute candles aligned to the
 * exchange's open, and records each pattern once when its candle closes —
 * to Telegram too when a rule says so. This page shows what it recorded
 * today, what the candles still forming would be if they closed now, which
 * watched symbols are not receiving data, and the rules themselves.
 *
 * A sibling of the Alerts page rather than a section of it: that page is the
 * delivery channel every producer shares; this is one producer, with rules
 * and a live view of its own. Its alerts appear in that stream too.
 */

import { useState } from 'react'
import { Link } from 'react-router-dom'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api } from '../../lib/api'
import { formatAge, formatPrice } from '../../lib/format'
import {
  EMPTY_RULE_FORM,
  NO_FILTERS,
  candleSpan,
  directionTone,
  filterAlerts,
  filterChoices,
  formToRequest,
  istClock,
  ruleSummary,
  ruleToForm,
  scannerHealth,
  toggle,
} from '../../lib/patterns'
import type {
  AlertFilters,
  PatternAlert,
  PatternCatalog,
  PatternForming,
  PatternFormingResponse,
  PatternHit,
  PatternRule,
  PatternScannerStatus,
  RuleForm,
} from '../../lib/patterns'
import { Badge, EmptyState, InlineError, Loading, Panel, QueryBoundary } from '../../components/ui'
import './pattern-alerts.css'

const REVERSALS = ['bullish_engulfing', 'bearish_engulfing', 'hammer', 'shooting_star', 'morning_star', 'evening_star']

function useCatalog() {
  return useQuery({
    queryKey: ['patterns', 'catalog'],
    queryFn: () => api.get<PatternCatalog>('/api/PatternAlerts/catalog'),
    staleTime: Infinity,
  })
}

function useStatus() {
  return useQuery({
    queryKey: ['patterns', 'status'],
    queryFn: () => api.get<PatternScannerStatus>('/api/PatternAlerts/status'),
    refetchInterval: 10_000,
  })
}

function useForming() {
  return useQuery({
    queryKey: ['patterns', 'forming'],
    queryFn: () => api.get<PatternFormingResponse>('/api/PatternAlerts/forming'),
    refetchInterval: 15_000,
  })
}

function useTodaysAlerts() {
  return useQuery({
    queryKey: ['patterns', 'events'],
    queryFn: () => api.get<PatternAlert[]>('/api/PatternAlerts/events?limit=1000'),
    refetchInterval: 20_000,
  })
}

function useRules() {
  return useQuery({
    queryKey: ['patterns', 'rules'],
    queryFn: () => api.get<PatternRule[]>('/api/PatternAlerts/rules'),
  })
}

function Hits({ hits }: { hits: PatternHit[] }) {
  if (hits.length === 0) return <span className="faint">—</span>
  return (
    <span className="pa-hits">
      {hits.map((h) => (
        <Badge key={h.key} tone={directionTone(h.direction)}>
          {h.name}
        </Badge>
      ))}
    </span>
  )
}

// ------------------------------------------------------------------ status --

function StatusStrip({ status }: { status: PatternScannerStatus }) {
  const health = scannerHealth(status, Date.now())
  return (
    <div className="stat-grid">
      <div className="stat">
        <div className={`stat__value ${health.tone === 'neutral' ? '' : health.tone}`}>{health.label}</div>
        <div className="stat__label">Scanner</div>
        <div className="stat__sub">{health.detail}</div>
      </div>
      <div className="stat">
        <div className="stat__value">{status.alertsToday}</div>
        <div className="stat__label">Alerts today</div>
        <div className="stat__sub">
          {status.deliveredToday} sent to Telegram
          {status.todayByTimeframe.length > 0 && <> · {status.todayByTimeframe.map((t) => `${t.key} ${t.count}`).join(', ')}</>}
        </div>
      </div>
      <div className="stat">
        <div className="stat__value">{status.symbols.length}</div>
        <div className="stat__label">Symbols watched</div>
        <div className="stat__sub">
          {status.watchCount} symbol × timeframe pairs from {status.ruleCount} enabled rule{status.ruleCount === 1 ? '' : 's'}
        </div>
      </div>
      <div className="stat">
        <div className={`stat__value ${status.telegramConfigured ? 'pos' : 'warn'}`}>
          {status.telegramConfigured ? 'Telegram on' : 'Telegram off'}
        </div>
        <div className="stat__label">Delivery</div>
        <div className="stat__sub">
          {status.telegramConfigured
            ? `One message per closing minute, at most ${status.telegramMaxMessages} in ${status.telegramWindowMinutes} min.`
            : 'No bot token or chat id on the server; alerts are recorded here only.'}
          {status.telegramMessagesSuppressed + status.telegramMessagesFailed > 0 && status.lastTelegramProblem && (
            <span className="warn"> {status.lastTelegramProblem}</span>
          )}
        </div>
      </div>
    </div>
  )
}

function WatchedSymbols({ status }: { status: PatternScannerStatus }) {
  const rows = [...status.symbols].sort(
    (a, b) => Number(!!b.problem) - Number(!!a.problem) || a.displayName.localeCompare(b.displayName),
  )
  const problems = rows.filter((r) => r.problem).length

  return (
    <Panel
      title="Watched symbols"
      actions={
        <span className="small-note muted" style={{ margin: 0 }}>
          {status.lastScanUtc ? `as of the scan ${formatAge(status.lastScanUtc)}` : 'waiting for the first scan'}
        </span>
      }
    >
      {problems > 0 && (
        <div className="alert alert--warn" role="status">
          {/* One span: .alert is a flex row and would split the link into its own column. */}
          <span>
            {problems} watched symbol{problems === 1 ? ' is' : 's are'} not receiving live bars, so no pattern can be
            recognised on {problems === 1 ? 'it' : 'them'}. Add {problems === 1 ? 'it' : 'them'} to a live feed on{' '}
            <Link to="/admin/data/live">Live feeds</Link> or remove {problems === 1 ? 'it' : 'them'} from the rule.
          </span>
        </div>
      )}
      {status.unresolved.length > 0 && (
        <div className="alert alert--warn alert--stack" role="status" style={{ marginTop: problems > 0 ? 8 : 0 }}>
          {status.unresolved.map((u) => (
            <div key={u}>{u}</div>
          ))}
        </div>
      )}
      {rows.length === 0 ? (
        <EmptyState>
          {status.lastScanUtc ? 'No enabled rule watches anything.' : 'The scanner has not finished a scan since the API started.'}
        </EmptyState>
      ) : (
        <div className="tablewrap tablewrap--rows8" style={{ marginTop: 10 }}>
          <table className="table">
            <thead>
              <tr>
                <th>Symbol</th>
                <th>Session</th>
                <th>Bars today</th>
                <th>Last bar</th>
                <th>State</th>
              </tr>
            </thead>
            <tbody>
              {rows.map((s) => (
                <tr key={s.symbol}>
                  <td>
                    {s.displayName}
                    <span className="cell-sub mono">{s.symbol}</span>
                  </td>
                  <td>{s.inSession ? `${s.exchange} open` : <span className="faint">{s.exchange} closed</span>}</td>
                  <td className="mono">{s.inSession ? s.barsToday : '—'}</td>
                  <td className="mono">{s.lastBarUtc ? `${istClock(s.lastBarUtc)} IST` : '—'}</td>
                  <td>
                    {s.problem ? (
                      <Badge tone="warn">{s.problem}</Badge>
                    ) : s.inSession ? (
                      <Badge tone="pos">receiving</Badge>
                    ) : (
                      <span className="faint">not in session</span>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </Panel>
  )
}

// ----------------------------------------------------------------- forming --

function FormingPanel() {
  const forming = useForming()
  const [timeframe, setTimeframe] = useState<number | null>(null)

  const items = forming.data?.items ?? []
  const timeframes = [...new Set(items.map((i) => i.timeframe))].sort((a, b) => a - b)
  const open = items.filter((i) => i.inSession && (timeframe === null || i.timeframe === timeframe))
  const closedExchanges = [...new Set(items.filter((i) => !i.inSession).map((i) => i.exchange))].sort()

  return (
    <Panel
      title="Forming now"
      actions={
        timeframes.length > 1 && (
          <div className="seg" role="group" aria-label="Timeframe">
            <button type="button" className={`seg__btn ${timeframe === null ? 'is-active' : ''}`} onClick={() => setTimeframe(null)}>
              All
            </button>
            {timeframes.map((t) => (
              <button
                key={t}
                type="button"
                className={`seg__btn ${timeframe === t ? 'is-active' : ''}`}
                onClick={() => setTimeframe(t)}
              >
                {t}m
              </button>
            ))}
          </div>
        )
      }
    >
      <p className="muted" style={{ marginTop: 0, maxWidth: '80ch' }}>
        The candle still forming on each watched symbol and timeframe, and the patterns it would be if it closed this
        instant. Shown here only: a pattern is recorded, and sent, once its candle has closed.
      </p>
      <QueryBoundary query={forming}>
        {() => (
          <>
            {closedExchanges.length > 0 && (
              <p className="small-note muted" style={{ marginTop: 0 }}>
                Not in session now: {closedExchanges.join(', ')}. NSE and BSE candles form from 09:15 IST, MCX from 09:00.
              </p>
            )}
            {open.length === 0 ? (
              <EmptyState>No watched exchange is in session, so no candle is forming.</EmptyState>
            ) : (
              <div className="tablewrap tablewrap--tall">
                <table className="table">
                  <thead>
                    <tr>
                      <th>Symbol · candle (IST)</th>
                      <th>If it closed now</th>
                      <th>Minutes</th>
                      <th>Open</th>
                      <th>High</th>
                      <th>Low</th>
                      <th>Close so far</th>
                      <th>Last closed</th>
                    </tr>
                  </thead>
                  <tbody>
                    {open.map((f) => (
                      <FormingRow key={`${f.symbol}|${f.timeframe}`} f={f} />
                    ))}
                  </tbody>
                </table>
              </div>
            )}
          </>
        )}
      </QueryBoundary>
    </Panel>
  )
}

function FormingRow({ f }: { f: PatternForming }) {
  const waiting = f.open === null
  return (
    <tr>
      <td>
        {f.displayName} <span className="faint">{f.timeframe}m</span>
        <span className="cell-sub mono">{candleSpan(f.barStartUtc, f.barEndUtc)}</span>
      </td>
      <td>
        <Hits hits={f.wouldBe} />
      </td>
      <td className="mono">
        {f.minutesElapsed} of {f.minutesExpected}
        {!waiting && f.minutesWithData < Math.min(f.minutesElapsed + 1, f.minutesExpected) && (
          <span className="cell-sub">{f.minutesWithData} with data</span>
        )}
      </td>
      {waiting ? (
        <td colSpan={4} className="faint">
          no 1-minute bar yet in this candle
        </td>
      ) : (
        <>
          <td className="mono">{formatPrice(f.open)}</td>
          <td className="mono">{formatPrice(f.high)}</td>
          <td className="mono">{formatPrice(f.low)}</td>
          <td className="mono">{formatPrice(f.close)}</td>
        </>
      )}
      <td>
        <Hits hits={f.lastClosed} />
        {f.lastClosed.length > 0 && <span className="cell-sub">{istClock(f.lastClosedStartUtc)} candle</span>}
      </td>
    </tr>
  )
}

// ------------------------------------------------------------------ alerts --

function AlertsPanel() {
  const alerts = useTodaysAlerts()
  const [filters, setFilters] = useState<AlertFilters>(NO_FILTERS)

  const all = alerts.data ?? []
  const choices = filterChoices(all)
  const shown = filterAlerts(all, filters)
  const set = (key: keyof AlertFilters) => (e: React.ChangeEvent<HTMLSelectElement>) =>
    setFilters((f) => ({ ...f, [key]: e.target.value }))

  return (
    <Panel
      title="Today's alerts"
      actions={
        alerts.data && (
          <span className="small-note muted" style={{ margin: 0 }}>
            {shown.length === all.length ? `${all.length} since 00:00 IST` : `${shown.length} of ${all.length}`}
          </span>
        )
      }
    >
      <div className="pa-filters">
        <div className="field">
          <label className="field__label" htmlFor="pa-f-symbol">
            Symbol
          </label>
          <select id="pa-f-symbol" className="field__input" value={filters.symbol} onChange={set('symbol')}>
            <option value="">All symbols</option>
            {choices.symbols.map((s) => (
              <option key={s.value} value={s.value}>
                {s.label}
              </option>
            ))}
          </select>
        </div>
        <div className="field">
          <label className="field__label" htmlFor="pa-f-tf">
            Timeframe
          </label>
          <select id="pa-f-tf" className="field__input" value={filters.timeframe} onChange={set('timeframe')}>
            <option value="">All timeframes</option>
            {choices.timeframes.map((t) => (
              <option key={t} value={String(t)}>
                {t} minutes
              </option>
            ))}
          </select>
        </div>
        <div className="field">
          <label className="field__label" htmlFor="pa-f-pattern">
            Pattern
          </label>
          <select id="pa-f-pattern" className="field__input" value={filters.pattern} onChange={set('pattern')}>
            <option value="">All patterns</option>
            {choices.patterns.map((p) => (
              <option key={p.value} value={p.value}>
                {p.label}
              </option>
            ))}
          </select>
        </div>
        <div className="field">
          <label className="field__label" htmlFor="pa-f-dir">
            Direction
          </label>
          <select id="pa-f-dir" className="field__input" value={filters.direction} onChange={set('direction')}>
            <option value="">Any direction</option>
            <option value="bullish">Bullish</option>
            <option value="bearish">Bearish</option>
            <option value="neutral">Neutral</option>
          </select>
        </div>
      </div>

      <QueryBoundary query={alerts}>
        {() =>
          all.length === 0 ? (
            <EmptyState>
              No pattern has closed today on a watched symbol. Alerts appear here within about twenty seconds of their
              candle closing.
            </EmptyState>
          ) : shown.length === 0 ? (
            <EmptyState>Nothing today matches these filters.</EmptyState>
          ) : (
            <div className="tablewrap tablewrap--tall">
              <table className="table">
                <thead>
                  <tr>
                    <th>Symbol · candle (IST)</th>
                    <th>Pattern</th>
                    <th>Open</th>
                    <th>High</th>
                    <th>Low</th>
                    <th>Close</th>
                    <th>Telegram</th>
                  </tr>
                </thead>
                <tbody>
                  {shown.map((a) => (
                    <tr key={a.id}>
                      <td>
                        {a.displayName} <span className="faint">{a.timeframe}m</span>
                        <span className="cell-sub mono">
                          {candleSpan(a.barStartUtc, a.barEndUtc)}
                          {a.minutesInBar < a.minutesExpected && ` · ${a.minutesInBar} of ${a.minutesExpected} min had data`}
                        </span>
                      </td>
                      <td>
                        <Badge tone={directionTone(a.direction)}>{a.patternName}</Badge>
                        <span className="cell-sub">{a.direction}</span>
                      </td>
                      <td className="mono">{formatPrice(a.open)}</td>
                      <td className="mono">{formatPrice(a.high)}</td>
                      <td className="mono">{formatPrice(a.low)}</td>
                      <td className="mono">{formatPrice(a.close)}</td>
                      <td>
                        {a.deliveredToTelegram ? (
                          <Badge tone="pos">sent</Badge>
                        ) : (
                          <span title={a.notifySkippedReason ?? (a.notify ? 'not sent: rate limit or Telegram unavailable' : '')}>
                            <Badge tone="neutral">recorded only</Badge>
                            <span className="cell-sub pa-reason">
                              {a.notifySkippedReason ?? (a.notify ? 'not delivered' : '')}
                            </span>
                          </span>
                        )}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )
        }
      </QueryBoundary>
    </Panel>
  )
}

// ------------------------------------------------------------------- rules --

function RulesPanel({ catalog }: { catalog: PatternCatalog }) {
  const qc = useQueryClient()
  const rules = useRules()
  const [form, setForm] = useState<RuleForm | null>(null)

  const refresh = () => qc.invalidateQueries({ queryKey: ['patterns'] })

  const save = useMutation({
    mutationFn: (f: RuleForm) =>
      f.id === null
        ? api.post<PatternRule>('/api/PatternAlerts/rules', formToRequest(f))
        : api.put<PatternRule>(`/api/PatternAlerts/rules/${f.id}`, formToRequest(f)),
    onSuccess: () => {
      setForm(null)
      refresh()
    },
  })

  const flip = useMutation({
    mutationFn: ({ rule, change }: { rule: PatternRule; change: Partial<RuleForm> }) =>
      api.put<PatternRule>(`/api/PatternAlerts/rules/${rule.id}`, formToRequest({ ...ruleToForm(rule), ...change })),
    onSuccess: refresh,
  })

  const remove = useMutation({
    mutationFn: (id: number) => api.delete<void>(`/api/PatternAlerts/rules/${id}`),
    onSuccess: refresh,
  })

  return (
    <Panel
      title="Rules"
      actions={
        <button
          type="button"
          className="btn btn--primary btn--sm"
          onClick={() => {
            save.reset()
            setForm({ ...EMPTY_RULE_FORM })
          }}
        >
          New rule
        </button>
      }
    >
      <p className="muted" style={{ marginTop: 0, maxWidth: '80ch' }}>
        A rule names what to watch — explicit symbols or groups resolved on every scan, so a future rolls to the next
        contract by itself — on which timeframes, for which patterns, and whether hits go to Telegram. Changes apply
        from the next scan.
      </p>

      <QueryBoundary query={rules}>
        {(list) =>
          list.length === 0 ? (
            <EmptyState>No rules. Nothing is being watched.</EmptyState>
          ) : (
            <div className="tablewrap">
              <table className="table">
                <thead>
                  <tr>
                    <th>Rule</th>
                    <th>Watches now</th>
                    <th>On</th>
                    <th>Telegram</th>
                    <th />
                  </tr>
                </thead>
                <tbody>
                  {list.map((r) => (
                    <tr key={r.id}>
                      <td className="pa-rule-name">
                        {r.name}
                        <span className="cell-sub">{ruleSummary(r, catalog)}</span>
                      </td>
                      <td title={r.resolvedSymbols.join('\n')}>
                        {r.resolvedSymbols.length} symbol{r.resolvedSymbols.length === 1 ? '' : 's'}
                        <span className="cell-sub">
                          {r.resolvedSymbols.slice(0, 3).map((s) => s.slice(s.indexOf(':') + 1)).join(', ')}
                          {r.resolvedSymbols.length > 3 ? '…' : ''}
                        </span>
                      </td>
                      <td>
                        <input
                          type="checkbox"
                          aria-label={`Enable ${r.name}`}
                          checked={r.isEnabled}
                          disabled={flip.isPending}
                          onChange={(e) => flip.mutate({ rule: r, change: { isEnabled: e.target.checked } })}
                        />
                      </td>
                      <td>
                        <input
                          type="checkbox"
                          aria-label={`Send ${r.name} to Telegram`}
                          checked={r.notify}
                          disabled={flip.isPending}
                          onChange={(e) => flip.mutate({ rule: r, change: { notify: e.target.checked } })}
                        />
                      </td>
                      <td>
                        <span className="pa-actions">
                          <button
                            type="button"
                            className="btn btn--ghost btn--sm"
                            onClick={() => {
                              save.reset()
                              setForm(ruleToForm(r))
                            }}
                          >
                            Edit
                          </button>
                          <button
                            type="button"
                            className="btn btn--ghost btn--sm"
                            disabled={remove.isPending}
                            onClick={() => {
                              if (window.confirm(`Delete the rule "${r.name}"? Alerts it already recorded stay.`)) remove.mutate(r.id)
                            }}
                          >
                            Delete
                          </button>
                        </span>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )
        }
      </QueryBoundary>
      {flip.isError && <InlineError error={flip.error} />}
      {remove.isError && <InlineError error={remove.error} />}

      {form && (
        <RuleEditor
          catalog={catalog}
          form={form}
          onChange={setForm}
          onCancel={() => setForm(null)}
          onSave={() => save.mutate(form)}
          saving={save.isPending}
          error={save.isError ? save.error : null}
        />
      )}
    </Panel>
  )
}

function RuleEditor({
  catalog,
  form,
  onChange,
  onCancel,
  onSave,
  saving,
  error,
}: {
  catalog: PatternCatalog
  form: RuleForm
  onChange: (f: RuleForm) => void
  onCancel: () => void
  onSave: () => void
  saving: boolean
  error: unknown
}) {
  const patternOrder = catalog.patterns.map((p) => p.key)
  const groupOrder = catalog.groups.map((g) => g.key)
  const set = (change: Partial<RuleForm>) => onChange({ ...form, ...change })

  return (
    <form
      style={{ marginTop: 16 }}
      onSubmit={(e) => {
        e.preventDefault()
        onSave()
      }}
    >
      <h3 className="section-title">{form.id === null ? 'New rule' : `Edit: ${form.name}`}</h3>

      <div className="form-row">
        <div className="field">
          <label className="field__label" htmlFor="pa-name">
            Name
          </label>
          <input
            id="pa-name"
            className="field__input"
            value={form.name}
            maxLength={120}
            required
            placeholder="Bank stocks — 15m reversals"
            onChange={(e) => set({ name: e.target.value })}
          />
        </div>
        <div className="field">
          <span className="field__label">State</span>
          <div className="pa-checks">
            <label className="coverage-chip">
              <input type="checkbox" checked={form.isEnabled} onChange={(e) => set({ isEnabled: e.target.checked })} />
              Enabled
            </label>
            <label className="coverage-chip">
              <input type="checkbox" checked={form.notify} onChange={(e) => set({ notify: e.target.checked })} />
              Send to Telegram
            </label>
          </div>
        </div>
      </div>

      <div className="field" style={{ marginTop: 12 }}>
        <span className="field__label">Symbol groups</span>
        <div className="grant-grid">
          {catalog.groups.map((g) => (
            <label key={g.key} className="grant">
              <span className="grant__head">
                <input
                  type="checkbox"
                  checked={form.groups.includes(g.key)}
                  onChange={() => set({ groups: toggle(form.groups, g.key, groupOrder) })}
                />
                {g.label}
              </span>
              <span className="grant__desc">{g.description}</span>
            </label>
          ))}
        </div>
      </div>

      <div className="form-row" style={{ marginTop: 12 }}>
        <div className="field">
          <label className="field__label" htmlFor="pa-futures">
            Nearest future of
          </label>
          <input
            id="pa-futures"
            className="field__input mono"
            value={form.futures}
            placeholder="CRUDEOIL, NATURALGAS"
            onChange={(e) => set({ futures: e.target.value })}
          />
          <span className="field__help">Underlyings, comma-separated. Resolved to today's nearest expiry.</span>
        </div>
        <div className="field">
          <label className="field__label" htmlFor="pa-symbols">
            Symbols
          </label>
          <textarea
            id="pa-symbols"
            className="field__input pa-textarea"
            value={form.symbols}
            placeholder="NSE:SBIN-EQ, NSE:ICICIBANK-EQ"
            onChange={(e) => set({ symbols: e.target.value })}
          />
          <span className="field__help">Canonical EXCHANGE:NAME, as on the recording list.</span>
        </div>
      </div>

      <div className="field" style={{ marginTop: 12 }}>
        <span className="field__label">Timeframes</span>
        <div className="pa-checks">
          {catalog.timeframes.map((t) => (
            <label key={t} className="coverage-chip">
              <input
                type="checkbox"
                checked={form.timeframes.includes(t)}
                onChange={() => set({ timeframes: toggle(form.timeframes, t, catalog.timeframes) })}
              />
              {t} min
            </label>
          ))}
        </div>
      </div>

      <div className="field" style={{ marginTop: 12 }}>
        <span className="field__label" style={{ display: 'flex', gap: 8, alignItems: 'center', flexWrap: 'wrap' }}>
          Patterns
          <button type="button" className="btn btn--ghost btn--sm" onClick={() => set({ patterns: [...patternOrder] })}>
            All
          </button>
          <button type="button" className="btn btn--ghost btn--sm" onClick={() => set({ patterns: [...REVERSALS] })}>
            Reversals
          </button>
          <button type="button" className="btn btn--ghost btn--sm" onClick={() => set({ patterns: [] })}>
            None
          </button>
        </span>
        <div className="pa-checks">
          {catalog.patterns.map((p) => (
            <label key={p.key} className="coverage-chip" title={p.definition}>
              <input
                type="checkbox"
                checked={form.patterns.includes(p.key)}
                onChange={() => set({ patterns: toggle(form.patterns, p.key, patternOrder) })}
              />
              {p.name}
            </label>
          ))}
        </div>
      </div>

      {error !== null && <InlineError error={error} />}
      <div className="pa-actions" style={{ marginTop: 14 }}>
        <button type="submit" className="btn btn--primary btn--sm" disabled={saving}>
          {saving ? 'Saving…' : form.id === null ? 'Create rule' : 'Save rule'}
        </button>
        <button type="button" className="btn btn--ghost btn--sm" onClick={onCancel}>
          Cancel
        </button>
      </div>
    </form>
  )
}

// ------------------------------------------------------------------- guide --

function GuidePanel({ catalog }: { catalog: PatternCatalog }) {
  return (
    <Panel title="What each pattern suggests">
      {/* A list, not a table: this is prose, and prose must wrap on a phone rather than scroll sideways. */}
      <dl className="pa-guide">
        {catalog.patterns.map((p) => (
          <div key={p.key} className="pa-guide__item">
            <dt>
              {p.name}{' '}
              <Badge tone={directionTone(p.direction)}>{p.key === 'marubozu' ? 'its colour' : p.direction}</Badge>
              <span className="faint"> · {p.bars} candle{p.bars === 1 ? '' : 's'}</span>
            </dt>
            <dd>
              {p.suggests}
              <span className="pa-guide__definition">
                {p.definition}
                {p.portedFrom && <> Same thresholds as the strategies' <code>{p.portedFrom}</code>.</>}
              </span>
            </dd>
          </div>
        ))}
      </dl>
      <p className="small-note muted" style={{ maxWidth: '84ch' }}>
        Candles are aligned to the exchange's open (NSE and BSE 09:15, MCX 09:00), so a 15-minute candle runs 09:15–09:30
        and an hourly one 09:15–10:15; times are the candle's start, as charts label them. Two- and three-candle patterns
        read only candles of the same session with none missing in between. A pattern suggests; it does not confirm —
        the next candle does.
      </p>
      <p className="small-note muted" style={{ maxWidth: '84ch' }}>
        Chart patterns such as head and shoulders, double tops and triangles are not detected yet: they span dozens of
        candles and need swing highs and lows found first. Alerts from your own indicators (a breakout above a level, an
        RSI cross) are not here yet either.
      </p>
    </Panel>
  )
}

export function PatternAlertsPage() {
  const status = useStatus()
  const catalog = useCatalog()

  return (
    <div className="page">
      <header className="page__header">
        <div>
          <h1 className="page__title">Pattern alerts</h1>
          <p className="page__subtitle">
            Candle patterns on live bars — doji, hammer, engulfing, stars and more — on 3 to 60-minute candles, recorded
            the moment a candle closes and sent to Telegram when a rule asks.
          </p>
        </div>
        <Link to="/admin/system/alerts" className="btn btn--ghost btn--sm">
          All alerts
        </Link>
      </header>

      <QueryBoundary query={status}>
        {(s) => (
          <>
            <StatusStrip status={s} />
            <WatchedSymbols status={s} />
          </>
        )}
      </QueryBoundary>

      <FormingPanel />
      <AlertsPanel />

      {catalog.isPending ? (
        <Loading label="Loading the pattern catalog…" />
      ) : catalog.data ? (
        <>
          <RulesPanel catalog={catalog.data} />
          <GuidePanel catalog={catalog.data} />
        </>
      ) : (
        <InlineError error={catalog.error} />
      )}
    </div>
  )
}
