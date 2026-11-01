/**
 * Data → Mutual funds → one scheme's fund report (/data/mutual-funds/:code,
 * optionally ?asOf=yyyy-MM-dd). Facts only, each with its as-of day and its
 * source: the NAV history the report rests on, trailing returns against the
 * benchmark and the category, worked ₹ examples, calendar years, rolling
 * returns, risk, the benchmark figures, cost, a written summary (lazy, and the
 * page is complete without it), the sources, the notes and the disclaimer.
 *
 * A figure the API does not know is "—" with its reason in text under the
 * table, never a zero, and never only in a tooltip: a phone has no hover.
 * mutualFunds.test.ts scans this page's words for ranking or advice words.
 */

import type { ReactNode } from 'react'
import { Link, useParams, useSearchParams } from 'react-router-dom'
import { useMutualFundReport, useMutualFundSummary } from '../../lib/queries'
import { formatDateTime, formatInrWhole, formatNumber } from '../../lib/format'
import {
  categoryPosition,
  currentDrawdownText,
  dayRange,
  exampleAmounts,
  excessLabel,
  formatDay,
  formatGain,
  formatNav,
  formatPct,
  formatPoints,
  formatRatio,
  formatReturn,
  groupMissing,
  historyLine,
  lumpSumSentence,
  missingLine,
  optionLabel,
  periodLabel,
  planGapWords,
  planLabel,
  recoveryText,
  reportAsOfLine,
  reportHref,
  rollingAboveText,
  sipSentence,
  splitCategory,
  summaryView,
  type MissingNote,
  type MutualFundReport,
} from '../../lib/mutualFunds'
import { Badge, Panel, QueryBoundary, StatTile } from '../../components/ui'
import './data.css'

function toneClass(value: number | null | undefined): string {
  if (value == null || Math.abs(value) < 0.005) return ''
  return value > 0 ? 'pos' : 'neg'
}

/** A figure that is not known: a dash, with the reason in its title as well as in the notes under the table. */
function Dash({ reason }: { reason?: string | null }) {
  return (
    <span className="muted" title={reason ?? undefined}>
      —
    </span>
  )
}

/** A signed return, coloured by its sign; "—" with the reason when it is not known. */
function Ret({ value, reason }: { value: number | null | undefined; reason?: string | null }) {
  if (value == null) return <Dash reason={reason} />
  return <span className={toneClass(value)}>{formatReturn(value)}</span>
}

/**
 * The reasons for a table's "—" cells, in text, each said once: "Benchmark, 5 years: …"; a reason for whole rows
 * has no column ("10 years: …"), and one for a whole column no rows ("Category position: …").
 */
function MissingList({ groups }: { groups: { what: string; notes: MissingNote[] }[] }) {
  const line = (what: string, n: MissingNote) =>
    !what ? missingLine(n) : n.labels.length === 0 ? `${what}: ${n.reason}` : `${what}, ${missingLine(n)}`
  const lines = groups.flatMap((g) => g.notes.map((n) => ({ key: `${g.what}|${n.reason}`, text: line(g.what, n) })))
  if (lines.length === 0) return null
  return (
    <ul className="mf-list" aria-label="Why some figures are not shown">
      {lines.map((l) => (
        <li key={l.key}>{l.text}</li>
      ))}
    </ul>
  )
}

function Pair({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div>
      <span className="muted">{label}</span>
      <span>{children}</span>
    </div>
  )
}

// ---------- header ----------

function Header({ r }: { r: MutualFundReport }) {
  const s = r.scheme
  const ter = r.cost.ter
  return (
    <>
      <div className="stat-grid">
        <StatTile label="NAV" value={formatNav(r.nav)} sub={`on ${formatDay(r.asOf)}`} />
        <StatTile
          label="NAV history stored"
          value={r.history.firstDate ? `from ${formatDay(r.history.firstDate)}` : 'none'}
          sub={`${formatNumber(r.history.navs)} NAVs · ${r.history.complete ? 'complete' : 'older NAVs may still arrive'}`}
          tone={r.history.complete ? undefined : 'warn'}
        />
        <StatTile
          label="From its highest NAV"
          value={r.risk.currentDrawdownPct == null ? '—' : formatPct(r.risk.currentDrawdownPct)}
          tone={r.risk.currentDrawdownPct != null && r.risk.currentDrawdownPct < -0.005 ? 'neg' : undefined}
          sub={
            r.risk.highestNav != null && r.risk.highestNavDate
              ? `highest ${formatNav(r.risk.highestNav)} on ${formatDay(r.risk.highestNavDate)}`
              : 'highest NAV not known'
          }
        />
        <StatTile
          label="TER, this plan"
          value={ter?.thisPlanPct != null ? formatPct(ter.thisPlanPct) : '—'}
          sub={
            ter
              ? ter.thisPlanPct != null
                ? `on ${formatDay(ter.date)}`
                : 'the scheme’s plan is not known'
              : (r.cost.terMissing ?? 'not known')
          }
        />
      </div>

      <Panel title="Scheme">
        <div className="kv-grid">
          <Pair label="AMC">{s.amc || '—'}</Pair>
          <Pair label="Plan · option">
            {planLabel(s.plan)} · {s.optionLabel || optionLabel(s.option)}
          </Pair>
          <Pair label="Category">{r.categoryName || splitCategory(s.category).name}</Pair>
          <Pair label="Type">{r.schemeType || '—'}</Pair>
          <Pair label="Scheme code">
            <span className="mono">{s.schemeCode}</span>
          </Pair>
          <Pair label="ISIN">
            <span className="mono">{s.isinGrowth || '—'}</span>
          </Pair>
          {s.isinReinvest && (
            <Pair label="ISIN, reinvestment">
              <span className="mono">{s.isinReinvest}</span>
            </Pair>
          )}
          <Pair label="Status">
            <Badge tone={s.isActive ? 'pos' : 'neutral'}>{s.isActive ? 'Active' : 'Inactive'}</Badge>
          </Pair>
        </div>
        <p className={`small-note${r.history.complete ? '' : ' small-note--warn'}`}>{historyLine(r.history)}</p>
        <p className="small-note">{reportAsOfLine(r.asOf, r.nav)}</p>
      </Panel>
    </>
  )
}

// ---------- trailing returns ----------

function ReturnsPanel({ r }: { r: MutualFundReport }) {
  const excess = excessLabel(r.benchmark.indexFund)
  const rows = r.returns.map((p) => ({ p, cat: categoryPosition(r.category, p, p.years) }))
  const noRanking = r.category.missing?.trim()
  const groups = [
    // A period the fund has no return for: its reason covers the whole row, so it is said once, without a column.
    { what: '', notes: groupMissing(rows.map(({ p }) => ({ label: periodLabel(p.years), reason: p.cagrPct == null ? p.missing : null }))) },
    {
      what: 'Benchmark',
      notes: groupMissing(
        rows.map(({ p }) => ({
          label: periodLabel(p.years),
          reason: p.cagrPct != null && (p.benchmarkCagrPct == null || p.excessPct == null) ? p.benchmarkMissing : null,
        })),
      ),
    },
    {
      what: 'Category position',
      // No ranking at all is one line, not one per period.
      notes: noRanking
        ? [{ labels: [], reason: noRanking }]
        : groupMissing(rows.map(({ p, cat }) => ({ label: periodLabel(p.years), reason: p.cagrPct != null ? cat.missing : null }))),
    },
  ]

  return (
    <Panel title="Trailing returns">
      {rows.length === 0 ? (
        <p className="empty">No trailing periods in this report.</p>
      ) : (
        <div className="tablewrap">
          <table className="table table--sticky">
            <thead>
              <tr>
                <th>Period</th>
                <th className="r">Fund CAGR</th>
                <th className="r">Benchmark CAGR</th>
                <th className="r">{excess}</th>
                <th className="r">Category position</th>
              </tr>
            </thead>
            <tbody>
              {rows.map(({ p, cat }) => (
                <tr key={p.years}>
                  <td>
                    {periodLabel(p.years)}
                    <span className="cell-sub">{dayRange(p.start, p.end)}</span>
                  </td>
                  <td className="num">
                    <Ret value={p.cagrPct} reason={p.missing} />
                    {p.absolutePct != null && <span className="cell-sub">{formatReturn(p.absolutePct)} in all</span>}
                  </td>
                  <td className="num">
                    <Ret value={p.benchmarkCagrPct} reason={p.benchmarkMissing ?? p.missing} />
                  </td>
                  <td className="num">
                    {p.excessPct == null ? (
                      <Dash reason={p.benchmarkMissing ?? p.missing} />
                    ) : (
                      <span className={toneClass(p.excessPct)}>{formatPoints(p.excessPct)}</span>
                    )}
                  </td>
                  <td className="num">
                    {cat.missing ? <Dash reason={cat.missing} /> : cat.text}
                    {cat.median !== '—' && <span className="cell-sub">median {cat.median}</span>}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      <MissingList groups={groups} />
      <p className="small-note">
        CAGR is the compound annual growth of the NAV; &ldquo;in all&rdquo; is the change over the whole period.{' '}
        {excess} is the fund&rsquo;s CAGR minus the benchmark&rsquo;s over the same dates, in percentage points (pp).
        Category position is the place by return among the peers with a return for the period, 1st being the highest.
        {!noRanking && r.category.periods.length > 0 && r.category.note ? ` ${r.category.note}` : ''}
        {!noRanking && r.category.asOf ? ` Ranking as of ${formatDay(r.category.asOf)}.` : ''}
      </p>
    </Panel>
  )
}

// ---------- worked ₹ examples ----------

function ExamplesPanel({ r }: { r: MutualFundReport }) {
  const { instalment, amount } = exampleAmounts([r])
  const title =
    instalment != null && amount != null
      ? `Worked examples: ${formatInrWhole(instalment)} a month, or ${formatInrWhole(amount)} at once`
      : 'Worked examples'
  const missing = groupMissing(r.examples.map((e) => ({ label: periodLabel(e.years), reason: !e.sip || !e.lumpSum ? e.missing : null })))

  return (
    <Panel title={title}>
      {r.examples.length === 0 ? (
        <p className="empty">No worked examples in this report.</p>
      ) : (
        <>
          <div className="tablewrap">
            <table className="table table--sticky">
              <thead>
                <tr>
                  <th>Period</th>
                  <th className="r">SIP put in</th>
                  <th className="r">SIP value</th>
                  <th className="r">SIP gain</th>
                  <th className="r">XIRR</th>
                  <th className="r">Lump sum value</th>
                  <th className="r">Lump-sum gain</th>
                  <th className="r">CAGR</th>
                </tr>
              </thead>
              <tbody>
                {r.examples.map((e) => (
                  <tr key={e.years}>
                    <td>{periodLabel(e.years)}</td>
                    <td className="num">{e.sip ? formatInrWhole(e.sip.invested) : <Dash reason={e.missing} />}</td>
                    <td className="num">{e.sip ? formatInrWhole(e.sip.value) : <Dash reason={e.missing} />}</td>
                    <td className="num">
                      {e.sip ? <span className={toneClass(e.sip.gain)}>{formatGain(e.sip.gain)}</span> : <Dash reason={e.missing} />}
                    </td>
                    <td className="num">{e.sip ? formatPct(e.sip.xirrPct) : <Dash reason={e.missing} />}</td>
                    <td className="num">{e.lumpSum ? formatInrWhole(e.lumpSum.value) : <Dash reason={e.missing} />}</td>
                    <td className="num">
                      {e.lumpSum ? (
                        <span className={toneClass(e.lumpSum.gain)}>{formatGain(e.lumpSum.gain)}</span>
                      ) : (
                        <Dash reason={e.missing} />
                      )}
                    </td>
                    <td className="num">{e.lumpSum ? formatPct(e.lumpSum.cagrPct) : <Dash reason={e.missing} />}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          <ul className="mf-list mf-sentences">
            {r.examples.map((e) =>
              e.sip || e.lumpSum ? (
                <li key={e.years}>
                  <b>{periodLabel(e.years)}.</b> {e.sip ? sipSentence(e.sip) : ''} {e.lumpSum ? lumpSumSentence(e.lumpSum) : ''}
                </li>
              ) : null,
            )}
          </ul>
          <MissingList groups={[{ what: 'Not worked out', notes: missing }]} />
          <p className="small-note">
            Each SIP instalment is invested at that day&rsquo;s NAV; values are at the NAV of the last day. No tax, exit load
            or stamp duty is taken off. The same examples in the direct and the regular plan are under Cost.
          </p>
        </>
      )}
    </Panel>
  )
}

// ---------- calendar years and rolling returns ----------

function CalendarPanel({ r }: { r: MutualFundReport }) {
  const years = [...r.calendarYears].sort((a, b) => b.year - a.year)
  const fundMissing = groupMissing(years.map((y) => ({ label: String(y.year), reason: y.returnPct == null ? y.missing : null })))
  const benchmarkMissing = groupMissing(
    years.map((y) => ({ label: String(y.year), reason: y.returnPct != null && y.benchmarkReturnPct == null ? y.benchmarkMissing : null })),
  )
  return (
    <Panel title="Calendar-year returns">
      {years.length === 0 ? (
        <p className="empty">No calendar years in this report.</p>
      ) : (
        <div className="tablewrap tablewrap--rows8">
          <table className="table">
            <thead>
              <tr>
                <th>Year</th>
                <th className="r">Fund</th>
                <th className="r">Benchmark</th>
              </tr>
            </thead>
            <tbody>
              {years.map((y) => (
                <tr key={y.year}>
                  <td>
                    {y.year}
                    {y.yearToDate && <span className="cell-sub">year to date</span>}
                    {y.start && y.end && <span className="cell-sub">{dayRange(y.start, y.end)}</span>}
                  </td>
                  <td className="num">
                    <Ret value={y.returnPct} reason={y.missing} />
                  </td>
                  <td className="num">
                    <Ret value={y.benchmarkReturnPct} reason={y.benchmarkMissing ?? y.missing} />
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      <MissingList
        groups={[
          { what: '', notes: fundMissing },
          { what: 'Benchmark', notes: benchmarkMissing },
        ]}
      />
      <p className="small-note">
        Each return runs between the two NAV days under its year; the current year runs to the report&rsquo;s last day.
        Not annualised.
      </p>
    </Panel>
  )
}

function RollingPanel({ r }: { r: MutualFundReport }) {
  const roll = r.rolling
  const title = roll ? `Rolling ${roll.years}-year returns` : 'Rolling returns'
  if (!roll) {
    return (
      <Panel title={title}>
        <p className="empty">{r.rollingMissing ?? 'Not in this report.'}</p>
      </Panel>
    )
  }
  const above = rollingAboveText(roll)
  return (
    <Panel title={title}>
      <div className="kv-grid">
        <Pair label="Windows">
          {formatNumber(roll.windows)}, ending {dayRange(roll.firstEnd, roll.lastEnd)}
        </Pair>
        <Pair label="Lowest">
          <span className={toneClass(roll.lowestPct)}>{formatReturn(roll.lowestPct)}</span>, ending {formatDay(roll.lowestEnd)}
        </Pair>
        <Pair label="Highest">
          <span className={toneClass(roll.highestPct)}>{formatReturn(roll.highestPct)}</span>, ending {formatDay(roll.highestEnd)}
        </Pair>
        <Pair label="Median">{formatReturn(roll.medianPct)}</Pair>
        <Pair label="Below zero">{formatPct(roll.belowZeroPct, 1)} of windows</Pair>
        <Pair label="Above the benchmark">{above ?? '—'}</Pair>
      </div>
      {!above && <p className="small-note">Above the benchmark: no window has a benchmark return over the same dates.</p>}
      <p className="small-note">
        The trailing {roll.years}-year return, worked out for every day it can be from the NAVs stored.
      </p>
    </Panel>
  )
}

// ---------- risk ----------

function RiskPanel({ r }: { r: MutualFundReport }) {
  const v = r.risk.volatility
  const rf = r.risk.riskFree
  const sharpeShown = v != null && (v.sharpe != null || v.sortino != null)
  return (
    <Panel title="Risk">
      <div className="kv-grid">
        <Pair label="Volatility">{v ? `${formatPct(v.annualisedPct)} a year` : '—'}</Pair>
        {v && (
          <Pair label="Over">
            {periodLabel(v.years)}: {dayRange(v.start, v.end)}
          </Pair>
        )}
        {v && <Pair label="Daily returns">{`${formatNumber(v.returns)} (about ${Math.round(v.returnsPerYear)} a year)`}</Pair>}
        {sharpeShown && v.sharpe != null && <Pair label="Sharpe">{formatRatio(v.sharpe)}</Pair>}
        {sharpeShown && v.sortino != null && <Pair label="Sortino">{formatRatio(v.sortino)}</Pair>}
        {sharpeShown && rf && (
          <Pair label="Risk-free rate">
            {formatPct(rf.ratePct)}, {rf.source}
            {rf.asOf ? `, ${formatDay(rf.asOf)}` : ''}
          </Pair>
        )}
      </div>
      {!v && <p className="small-note">Volatility: {r.risk.volatilityMissing ?? 'not in this report.'}</p>}
      {!sharpeShown && <p className="small-note">Sharpe and Sortino: {r.risk.sharpeMissing ?? 'not in this report.'}</p>}
      <p className="small-note">Now: {currentDrawdownText(r.risk)}</p>

      <h3 className="section-title mf-gap">Drawdowns</h3>
      {r.risk.drawdowns.length === 0 ? (
        <p className="empty">No drawdowns in this report{r.risk.drawdownsMissing.length > 0 ? ':' : '.'}</p>
      ) : (
        <div className="tablewrap">
          <table className="table table--sticky">
            <thead>
              <tr>
                <th>Window</th>
                <th className="r">Deepest fall</th>
                <th>Peak</th>
                <th>Trough</th>
                <th>Recovery</th>
              </tr>
            </thead>
            <tbody>
              {r.risk.drawdowns.map((d) => (
                <tr key={d.years ?? 'all'}>
                  <td>
                    {periodLabel(d.years)}
                    <span className="cell-sub">{dayRange(d.start, d.end)}</span>
                  </td>
                  <td className="num">
                    <span className={toneClass(d.maxDrawdownPct)}>{formatPct(d.maxDrawdownPct)}</span>
                  </td>
                  <td>
                    {formatDay(d.peak)}
                    <span className="cell-sub">NAV {formatNav(d.peakNav)}</span>
                  </td>
                  <td>
                    {formatDay(d.trough)}
                    <span className="cell-sub">NAV {formatNav(d.troughNav)}</span>
                  </td>
                  <td>{recoveryText(d, r.asOf)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      {r.risk.drawdownsMissing.length > 0 && (
        <ul className="mf-list" aria-label="Drawdown windows left out">
          {r.risk.drawdownsMissing.map((line) => (
            <li key={line}>{line}</li>
          ))}
        </ul>
      )}
      <p className="small-note">
        Volatility is the standard deviation of daily NAV returns, annualised. A drawdown is the fall of the NAV from its
        highest point so far; recovery is the first day it was back at that peak.
      </p>
    </Panel>
  )
}

// ---------- benchmark ----------

function BenchmarkPanel({ r }: { r: MutualFundReport }) {
  const b = r.benchmark
  return (
    <Panel title="Benchmark">
      <p className="mf-lead">
        {b.name ? <b>{b.name}</b> : <b>No benchmark</b>}
        {b.index && b.index !== b.name ? <span className="muted"> · {b.index}</span> : null}
        {b.indexFund && (
          <>
            {' '}
            <Badge tone="neutral">Index fund</Badge>
          </>
        )}
      </p>
      {b.note && <p className="mf-verbatim">{b.note}</p>}
      {b.name && (
        <div className="kv-grid mf-gap">
          <Pair label="Values stored">{b.collected ? dayRange(b.firstDate, b.lastDate) : 'none yet'}</Pair>
          <Pair label="Last day both have">{b.lastCommonDate ? formatDay(b.lastCommonDate) : '—'}</Pair>
          <Pair label="Beta">
            {b.beta ? `${formatRatio(b.beta.beta)} over ${periodLabel(b.beta.years)}` : '—'}
          </Pair>
          <Pair label="Tracking error">{b.trackingError ? formatPct(b.trackingError.valuePct) : '—'}</Pair>
          <Pair label="Downside capture">{b.capture ? formatPct(b.capture.downsidePct) : '—'}</Pair>
          <Pair label="Upside capture">{b.capture ? formatPct(b.capture.upsidePct) : '—'}</Pair>
        </div>
      )}
      {(b.beta || b.trackingError || b.capture || b.missing.length > 0) && (
      <ul className="mf-list">
        {b.beta && (
          <li>
            Beta: {dayRange(b.beta.start, b.beta.end)}, {formatNumber(b.beta.returns)} daily returns of the fund against the
            index&rsquo;s.
          </li>
        )}
        {b.trackingError && (
          <li>
            Tracking error: {dayRange(b.trackingError.start, b.trackingError.end)}, {formatNumber(b.trackingError.returns)}{' '}
            daily returns; the annualised spread of the fund&rsquo;s daily return minus the index&rsquo;s.
          </li>
        )}
        {b.capture && (
          <li>
            Capture: {dayRange(b.capture.start, b.capture.end)}, {formatNumber(b.capture.months)} months (
            {formatNumber(b.capture.upMonths)} with the index up, {formatNumber(b.capture.downMonths)} down); the
            fund&rsquo;s average monthly return in the up (or down) months as a percent of the index&rsquo;s.
          </li>
        )}
        {b.missing.map((m) => (
          <li key={m}>{m}</li>
        ))}
      </ul>
      )}
    </Panel>
  )
}

// ---------- cost ----------

function CostPanel({ r }: { r: MutualFundReport }) {
  const c = r.cost
  const t = c.ter
  const code = r.scheme.schemeCode
  const missing = groupMissing(c.planGap.map((g) => ({ label: periodLabel(g.years), reason: g.missing })))
  const otherPlan = (label: string, other: number | null) =>
    other == null ? null : other === code ? (
      <Pair label={label}>
        <span className="mono">{other}</span> <span className="muted">(this report)</span>
      </Pair>
    ) : (
      <Pair label={label}>
        <Link className="mono" to={reportHref(other)}>
          {other}
        </Link>
      </Pair>
    )

  return (
    <Panel title="Cost">
      <h3 className="section-title">Total expense ratio (TER)</h3>
      {t ? (
        <div className="kv-grid">
          <Pair label="As of">{formatDay(t.date)}</Pair>
          <Pair label="This plan">{formatPct(t.thisPlanPct)}</Pair>
          <Pair label="Regular plan">{formatPct(t.regularPct)}</Pair>
          <Pair label="Direct plan">{formatPct(t.directPct)}</Pair>
          <Pair label="Name in AMFI's TER file">
            <span className="mf-wrap">{t.terSchemeName || '—'}</span>
          </Pair>
        </div>
      ) : (
        <p className="empty">{c.terMissing ?? 'No TER in this report.'}</p>
      )}
      {t && t.thisPlanPct == null && <p className="small-note">This plan&rsquo;s TER is not shown: the scheme&rsquo;s plan is not known.</p>}

      <h3 className="section-title mf-gap">Direct and regular plan, same dates</h3>
      {(c.directCode != null || c.regularCode != null) && (
        <div className="kv-grid">
          {otherPlan('Direct plan', c.directCode)}
          {otherPlan('Regular plan', c.regularCode)}
        </div>
      )}
      {c.planGap.length === 0 ? (
        <p className="empty">{c.planGapMissing ?? 'The direct and regular plans are not compared in this report.'}</p>
      ) : (
        <>
          <div className="tablewrap mf-gap">
            <table className="table table--sticky">
              <thead>
                <tr>
                  <th>Period</th>
                  <th className="r">SIP, direct</th>
                  <th className="r">SIP, regular</th>
                  <th>SIP difference</th>
                  <th className="r">Lump sum, direct</th>
                  <th className="r">Lump sum, regular</th>
                  <th>Lump-sum difference</th>
                </tr>
              </thead>
              <tbody>
                {c.planGap.map((g) => (
                  <tr key={g.years}>
                    <td>{periodLabel(g.years)}</td>
                    <td className="num">{g.sipDirect != null ? formatInrWhole(g.sipDirect) : <Dash reason={g.missing} />}</td>
                    <td className="num">{g.sipRegular != null ? formatInrWhole(g.sipRegular) : <Dash reason={g.missing} />}</td>
                    <td>{g.sipGap != null ? planGapWords(g.sipGap) : <Dash reason={g.missing} />}</td>
                    <td className="num">{g.lumpSumDirect != null ? formatInrWhole(g.lumpSumDirect) : <Dash reason={g.missing} />}</td>
                    <td className="num">{g.lumpSumRegular != null ? formatInrWhole(g.lumpSumRegular) : <Dash reason={g.missing} />}</td>
                    <td>{g.lumpSumGap != null ? planGapWords(g.lumpSumGap) : <Dash reason={g.missing} />}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          <MissingList groups={[{ what: 'Not compared', notes: missing }]} />
          <p className="small-note">
            The worked examples&rsquo; SIP and lump sum, on the same dates, in the direct and in the regular plan of this
            scheme: the values on the last day.
          </p>
        </>
      )}
    </Panel>
  )
}

// ---------- the written summary (lazy) ----------

/** Asked for with the page's own ?asOf (none for the latest NAV day), so it covers the same figures as the report. */
function SummaryPanel({ r, asOfQuery }: { r: MutualFundReport; asOfQuery: string | null }) {
  const summary = useMutualFundSummary(r.scheme.schemeCode, asOfQuery)
  let body: ReactNode
  if (summary.isPending) {
    body = (
      <p className="empty" role="status">
        Asking for the written summary; writing one from the figures above can take up to half a minute. The rest of the
        report does not depend on it.
      </p>
    )
  } else if (summary.isError) {
    body = (
      <p className="empty">
        No summary this time: {summary.error instanceof Error ? summary.error.message : 'the request failed'}. The figures
        above do not depend on it.
      </p>
    )
  } else {
    const view = summaryView(summary.data, r.asOf)
    body =
      view.kind === 'note' ? (
        <p className="empty">{view.text}</p>
      ) : (
        <div className="mf-summary">
          <p className="mf-summary__text">{view.summary.text}</p>
          <p className="mf-summary__meta">
            Written by {view.summary.model || 'a model'}, checked against the figures above ·{' '}
            {formatDateTime(view.summary.writtenUtc)} IST
          </p>
        </div>
      )
  }
  return <Panel title="In plain words">{body}</Panel>
}

// ---------- sources, notes, disclaimer ----------

function SourcesPanel({ r }: { r: MutualFundReport }) {
  return (
    <Panel title="Sources">
      {r.sources.length === 0 ? (
        <p className="empty">No sources listed in this report.</p>
      ) : (
        <div className="tablewrap">
          <table className="table">
            <thead>
              <tr>
                <th>Figures</th>
                <th>Source</th>
                <th>As of</th>
                <th>Note</th>
              </tr>
            </thead>
            <tbody>
              {r.sources.map((s) => (
                <tr key={`${s.key}|${s.what}`}>
                  <td className="mf-wrap">{s.what}</td>
                  <td className="mf-wrap">{s.source}</td>
                  <td>{s.asOf ? formatDay(s.asOf) : <span className="muted">—</span>}</td>
                  <td className="mf-wrap muted">{s.note || ''}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      <p className="small-note">Report built {formatDateTime(r.generatedUtc)} IST.</p>
    </Panel>
  )
}

function Report({ r, asOfQuery }: { r: MutualFundReport; asOfQuery: string | null }) {
  const s = r.scheme
  return (
    <>
      <header className="page__header">
        <div>
          <p className="mf-crumb">
            <Link to="/data/mutual-funds">Mutual funds</Link> / fund report
          </p>
          <h1 className="page__title">{s.schemeName}</h1>
          <p className="page__subtitle">
            {s.amc || '—'} · {planLabel(s.plan)} · {s.optionLabel || optionLabel(s.option)} ·{' '}
            {r.categoryName || splitCategory(s.category).name}
          </p>
        </div>
      </header>

      <Header r={r} />
      <ReturnsPanel r={r} />
      <ExamplesPanel r={r} />
      <div className="two-col">
        <CalendarPanel r={r} />
        <RollingPanel r={r} />
      </div>
      <RiskPanel r={r} />
      <BenchmarkPanel r={r} />
      <CostPanel r={r} />
      <SummaryPanel r={r} asOfQuery={asOfQuery} />
      <SourcesPanel r={r} />
      {r.notes.length > 0 && (
        <Panel title="Notes">
          <ul className="mf-list mf-flush">
            {r.notes.map((n, i) => (
              <li key={i}>{n}</li>
            ))}
          </ul>
        </Panel>
      )}
      {r.disclaimer && (
        <p className="mf-disclaimer" role="note">
          {r.disclaimer}
        </p>
      )}
    </>
  )
}

export function MutualFundReportPage() {
  const { code: raw } = useParams()
  const [params] = useSearchParams()
  const code = raw && /^\d+$/.test(raw) ? Number(raw) : null
  const asOfParam = params.get('asOf')
  const asOf = asOfParam && /^\d{4}-\d{2}-\d{2}$/.test(asOfParam) ? asOfParam : null
  const report = useMutualFundReport(code, asOf)

  if (code == null) {
    return (
      <div className="page data-page">
        <p className="mf-crumb">
          <Link to="/data/mutual-funds">Mutual funds</Link> / fund report
        </p>
        <p className="empty">&ldquo;{raw}&rdquo; is not a scheme code. Search a scheme on Mutual funds to open its report.</p>
      </div>
    )
  }

  return (
    <div className="page data-page">
      {report.data ? null : (
        <header className="page__header">
          <div>
            <p className="mf-crumb">
              <Link to="/data/mutual-funds">Mutual funds</Link> / fund report
            </p>
            <h1 className="page__title">Scheme {code}</h1>
          </div>
        </header>
      )}
      <QueryBoundary query={report}>{(r) => <Report r={r} asOfQuery={asOf} />}</QueryBoundary>
    </div>
  )
}
