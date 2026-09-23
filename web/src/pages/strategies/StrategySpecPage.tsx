import { Suspense, lazy, useState } from 'react'
import type { ReactNode } from 'react'
import { Link, useNavigate, useParams } from 'react-router-dom'
import { useStrategies } from '../../lib/queries'
import { headingSlug, useStrategySpec } from '../../lib/specs'
import type { SpecFacts } from '../../lib/specs'
import { contractRequirementsOf, describeRequirement } from '../../lib/contracts'
import { formatResolution } from '../../lib/symbols'
import { formatDateTime } from '../../lib/format'
import { SIDE_LABEL, builtInExit, cadenceText, shortLegs, tradeSide } from '../../lib/strategyLibrary'
import { Loading, QueryBoundary } from '../../components/ui'
import { IconArrowRight, IconPlay } from '../../components/icons'
import type { StrategyListItem } from '../../lib/types'
import { CategoryBadge, LaunchDialog } from './shared'
import { TrackRecordPanel } from './TrackRecordPanel'
import './spec.css'

const StrategySpecPanel = lazy(() => import('./StrategySpecPanel'))
const README_URL = 'https://github.com/helloupendra/algorithmic-trading-engine/blob/main/docs/strategies/README.md'

/** The strategy's default parameters as name/value rows; "{}" and junk read as none. */
function paramRows(json: string): { key: string; value: string }[] {
  try {
    const obj = JSON.parse(json)
    if (!obj || typeof obj !== 'object') return []
    return Object.entries(obj).map(([key, value]) => ({ key, value: typeof value === 'string' ? value : JSON.stringify(value) }))
  } catch {
    return []
  }
}

/** A link into the document below, only when the spec has that section. */
function SpecLink({ headings, heading, children }: { headings: string[]; heading: string; children: ReactNode }) {
  const found = headings.find((h) => headingSlug(h) === headingSlug(heading))
  if (!found) return null
  return (
    <a className="spec-sum__link" href={`#${headingSlug(found)}`}>
      {children} <IconArrowRight style={{ width: 11, height: 11 }} />
    </a>
  )
}

function SummaryItem({ title, wide = false, children }: { title: string; wide?: boolean; children: ReactNode }) {
  return (
    <div className={`spec-sum__item${wide ? ' spec-sum__item--wide' : ''}`}>
      <h3 className="spec-sum__title">{title}</h3>
      {children}
    </div>
  )
}

/** Descriptions run from one line to a paragraph; past three lines the rest is one click away. */
function Description({ text }: { text: string }) {
  const [open, setOpen] = useState(false)
  if (!text) return <p className="spec-sum__text faint">No description provided by the strategy.</p>
  const long = text.length > 320
  return (
    <>
      <p className={`spec-sum__text${long && !open ? ' spec-sum__text--clamp' : ''}`}>{text}</p>
      {long && (
        <button type="button" className="spec-sum__more-btn" onClick={() => setOpen((v) => !v)} aria-expanded={open}>
          {open ? 'Show less' : 'Read all'}
        </button>
      )}
    </>
  )
}

/**
 * The strategy in six short answers — what it does, its legs, when it enters,
 * how it exits, the data it needs and its parameters — read from the catalog
 * entry and the spec's facts. The full specification follows below it; the
 * links jump there. Anything neither source states is said to be unknown.
 */
function Summary({ s, facts, headings, hasSpec }: { s: StrategyListItem; facts: SpecFacts | null; headings: string[]; hasSpec: boolean }) {
  const contracts = contractRequirementsOf(s.contractRequirements)
  const params = paramRows(s.defaultParametersJson)
  const legs = shortLegs(s.legsSummary)
  const cadence = cadenceText(facts)
  const exit = builtInExit(facts)
  const lots = s.defaultLots > 0 ? s.defaultLots : 1
  const unknown = <span className="faint">{hasSpec ? 'The spec’s facts do not say.' : 'Not stated until the spec is written.'}</span>

  return (
    <section className="spec-sum" aria-label="Summary">
      <div className="spec-sum__grid">
        <SummaryItem title="What it does" wide>
          <Description text={s.description} />
        </SummaryItem>

        <SummaryItem title="Legs">
          <p className="spec-sum__lead mono">{legs ?? 'No orders — alerts only'}</p>
          {s.legsSummary && s.legsSummary !== legs && <p className="spec-sum__sub">{s.legsSummary}</p>}
          <p className="spec-sum__sub">
            {SIDE_LABEL[tradeSide(s.legsSummary)]} · default {lots} {lots === 1 ? 'lot' : 'lots'}
          </p>
        </SummaryItem>

        <SummaryItem title="When it enters">
          <p className="spec-sum__lead">{cadence ?? unknown}</p>
          <div className="spec-sum__links">
            <SpecLink headings={headings} heading="Entry">Entry rule</SpecLink>
            <SpecLink headings={headings} heading="Timeframe">Session window</SpecLink>
          </div>
        </SummaryItem>

        <SummaryItem title="How it exits">
          <p className="spec-sum__lead">
            {exit == null ? unknown : exit ? 'Has its own exit rule' : 'No exit of its own'}
          </p>
          {exit != null && (
            <p className="spec-sum__sub">
              {exit
                ? 'The run’s risk rules still apply on top of it.'
                : 'Positions are held until the run’s risk rules (stop-loss, target), a manual stop or the platform’s end-of-session square-off.'}
            </p>
          )}
          <div className="spec-sum__links">
            <SpecLink headings={headings} heading="Exit">Exit rules</SpecLink>
          </div>
        </SummaryItem>

        <SummaryItem title="Data it needs">
          <p className="spec-sum__lead">
            {facts?.data ? (
              facts.data
            ) : s.dataRequirements.length > 0 ? (
              s.dataRequirements.map((d) => `${d.symbolType} @ ${formatResolution(d.resolution)}`).join(', ')
            ) : (
              unknown
            )}
          </p>
          <div className="spec-sum__chips" aria-label="Underlyings">
            {s.supportedUnderlyings.length === 0 ? (
              <span className="faint">any underlying</span>
            ) : (
              s.supportedUnderlyings.map((u) => (
                <span key={u} className="spec-sum__chip mono">
                  {u}
                </span>
              ))
            )}
          </div>
        </SummaryItem>

        <SummaryItem title="Parameters" wide={params.length > 4}>
          {params.length === 0 ? (
            <p className="spec-sum__sub">None — the only knob is the run’s lot count.</p>
          ) : (
            <dl className="spec-sum__params">
              {params.map((p) => (
                <div key={p.key}>
                  <dt className="mono">{p.key}</dt>
                  <dd className="mono">{p.value}</dd>
                </div>
              ))}
            </dl>
          )}
          <div className="spec-sum__links">
            <SpecLink headings={headings} heading="Parameters">What each one does</SpecLink>
          </div>
        </SummaryItem>
      </div>

      <details className="spec-sum__more">
        <summary>Technical details</summary>
        <dl className="spec-sum__tech">
          <dt>Contracts it trades</dt>
          <dd>
            {contracts.length === 0 ? (
              <span className="muted">ATM CE + ATM PE (declares none; the runner’s default)</span>
            ) : (
              <ul className="spec-page__list">
                {contracts.map((r) => (
                  <li key={r.key}>
                    <span className="mono">{r.key}</span> — {describeRequirement(r)}
                  </li>
                ))}
              </ul>
            )}
          </dd>
          {s.dataRequirements.length > 0 && (
            <>
              <dt>Declared data</dt>
              <dd>{s.dataRequirements.map((d) => `${d.symbolType} @ ${formatResolution(d.resolution)}`).join(', ')}</dd>
            </>
          )}
          {facts?.evaluates_on && (
            <>
              <dt>Evaluates on</dt>
              <dd className="mono">
                {facts.evaluates_on}
                {facts.resolution ? ` · ${facts.resolution}` : ''}
              </dd>
            </>
          )}
          <dt>Instrument kind</dt>
          <dd>{s.instrumentKind || '—'}</dd>
          <dt>Source</dt>
          <dd className="mono">{s.sourceFile || '—'}</dd>
        </dl>
      </details>
    </section>
  )
}

/**
 * Which underlyings the strategy is running on right now, each a link to its
 * run.
 *
 * It used to carry a seven-day paper P&L beside them. The track record below
 * now answers the same question over every run there has ever been, and the
 * two sat a screen apart saying different numbers — "7d paper +₹40,234 · 15
 * runs" above "+₹2,08,752 · 65 runs" — with nothing to say why they differed.
 * One of them had to go, and a window of seven days was never the one worth
 * keeping. Dropping it also drops the five-hundred-row history request this
 * line made on every visit.
 */
function StateLine({ s, runHref }: { s: StrategyListItem; runHref: (runId: number) => string }) {
  const runs = s.activeRuns

  return (
    <div className="spec-sum__state">
      {runs.length > 0 ? (
        <span className="spec-sum__live">
          <span className="spec-sum__dot" aria-hidden="true" />
          Running on{' '}
          {runs.map((r, i) => (
            <span key={r.runId}>
              {i > 0 && ', '}
              <Link to={runHref(r.runId)} title={`Run #${r.runId}, started ${formatDateTime(r.startedUtc)} by ${r.startedBy}`}>
                {r.underlying}
              </Link>
            </span>
          ))}
        </span>
      ) : (
        <span className="faint">Not running</span>
      )}
    </div>
  )
}

/**
 * One strategy, read top to bottom: a short summary (what it does, legs,
 * entry, exit, data, parameters), then how it works — the full specification,
 * with a table of contents beside it.
 */
export function StrategySpecPage({ mode = 'admin' }: { mode?: 'admin' | 'trader' }) {
  const { id } = useParams()
  const navigate = useNavigate()
  // The same document for both areas; only where the buttons lead differs.
  // A trader deploys through the wizard (with this strategy pre-selected)
  // and has no backtesting module or admin runner to be sent to.
  const trader = mode === 'trader'
  const backHref = trader ? '/trader/deploy' : '/admin/strategies/library'
  const backLabel = trader ? '← Deploy' : '← Strategy library'
  const runHref = (runId: number) => (trader ? `/trader/strategies/runs/${runId}` : `/admin/strategies/runs/${runId}`)
  const strategyId = Number(id)
  const strategies = useStrategies()
  const spec = useStrategySpec(Number.isInteger(strategyId) && strategyId > 0 ? strategyId : null)
  const [launching, setLaunching] = useState(false)

  const strategy = strategies.data?.find((s) => s.id === strategyId) ?? null
  // Contents from the document's own H2 lines. The machine-readable facts
  // section is left out: its content is the summary at the top of the page.
  const headings = (spec.data?.markdown ?? '')
    .split('\n')
    .filter((l) => l.startsWith('## ') && !/^## Facts/.test(l))
    .map((l) => l.slice(3).trim())
  const hasSpec = !!spec.data?.hasSpec && spec.data.markdown != null

  return (
    <div className="page spec-page">
      <header className="page__header">
        <div>
          <Link to={backHref} className="spec-page__back">
            {backLabel}
          </Link>
          <h1 className="page__title spec-page__title">
            {strategy?.name ?? spec.data?.name ?? `Strategy ${id}`}
            {strategy && <CategoryBadge category={strategy.category} />}
          </h1>
          {strategy && <StateLine s={strategy} runHref={runHref} />}
        </div>
        {strategy && (
          <div className="toolbar">
            {trader ? (
              <button
                type="button"
                className="btn btn--primary"
                onClick={() => navigate(`/trader/deploy?strategy=${strategy.id}`)}
              >
                <IconPlay style={{ width: 13, height: 13 }} /> Deploy this strategy…
              </button>
            ) : (
              <>
                <button type="button" className="btn btn--primary" onClick={() => setLaunching(true)}>
                  <IconPlay style={{ width: 13, height: 13 }} /> Start…
                </button>
                <Link className="btn" to="/admin/backtesting/new">
                  Backtest… <IconArrowRight style={{ width: 12, height: 12 }} />
                </Link>
              </>
            )}
          </div>
        )}
      </header>

      <QueryBoundary query={strategies}>
        {() =>
          !strategy ? (
            <p className="empty">
              No strategy with id {id} in the catalog. <Link to={backHref}>Go back</Link>.
            </p>
          ) : (
            <>
              {spec.isPending ? (
                <Loading label="Loading summary…" />
              ) : (
                <Summary s={strategy} facts={spec.data?.facts ?? null} headings={headings} hasSpec={hasSpec} />
              )}

              {/* What it has done, between what it is meant to do and how it
                  does it. A reader who has just learned the rule asks next
                  whether the rule has ever paid — and the answer to that is a
                  record of real runs, not the summary above it. */}
              <section className="spec-page__section">
                <h2 className="section-title">
                  Track record
                  <span className="spec-page__section-note">
                    {trader ? 'your live runs of this strategy' : 'every live run of this strategy'}
                  </span>
                </h2>
                <TrackRecordPanel
                  strategyId={strategyId}
                  runHref={runHref}
                  isAdmin={!trader}
                  historyHref={`${trader ? '/trader/strategies/history' : '/admin/strategies/history'}?strategy=${strategy.id}`}
                />
              </section>

              <section className="spec-page__section">
                <h2 className="section-title">Full specification</h2>
                {spec.isPending ? (
                  <Loading label="Loading spec…" />
                ) : hasSpec ? (
                  <div className={`spec-page__layout${headings.length > 0 ? ' spec-page__layout--toc' : ''}`}>
                    {headings.length > 0 && (
                      <nav className="spec-page__toc" aria-label="On this page">
                        <div className="spec-page__toc-title">On this page</div>
                        <ol>
                          {headings.map((h) => (
                            <li key={h}>
                              <a href={`#${headingSlug(h)}`}>{h.replace(/\s*\(.*?\)\s*$/, '')}</a>
                            </li>
                          ))}
                        </ol>
                      </nav>
                    )}
                    <div className="spec-page__doc">
                      <Suspense fallback={<Loading label="Loading spec…" />}>
                        <StrategySpecPanel strategyId={strategyId} hideFacts />
                      </Suspense>
                    </div>
                  </div>
                ) : (
                  <p className="spec-page__pending">
                    The written specification for <b>{strategy.name}</b> is not done yet — the rule as
                    maths, the exits and a worked example from a real run (the{' '}
                    <a href={README_URL} target="_blank" rel="noreferrer">authoring guide</a> lists the
                    sections). The summary above comes from the code and is current.
                  </p>
                )}
              </section>
            </>
          )
        }
      </QueryBoundary>

      {launching && strategy && (
        <LaunchDialog strategy={strategy} onClose={() => setLaunching(false)} onStarted={() => navigate('/admin/strategies/live')} />
      )}
    </div>
  )
}
