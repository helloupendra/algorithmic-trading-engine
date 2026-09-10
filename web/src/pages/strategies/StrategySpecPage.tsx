import { Suspense, lazy, useState } from 'react'
import { Link, useNavigate, useParams } from 'react-router-dom'
import { useStrategies } from '../../lib/queries'
import { useStrategySpec } from '../../lib/specs'
import { contractRequirementsOf, describeRequirement } from '../../lib/contracts'
import { formatResolution } from '../../lib/symbols'
import { formatDateTime } from '../../lib/format'
import { Badge, Loading, QueryBoundary } from '../../components/ui'
import { IconArrowRight, IconPlay } from '../../components/icons'
import type { StrategyListItem } from '../../lib/types'
import { CategoryBadge, LaunchDialog } from './shared'
import { headingSlug } from './StrategySpecPanel'
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

/**
 * Everything the catalog knows about the strategy, as a two-column sheet.
 * This is what the Library's reference table used to spread over eight
 * columns; here it reads top to bottom, for every strategy, spec or not.
 */
function DetailsSheet({ s, runHref }: { s: StrategyListItem; runHref: (runId: number) => string }) {
  const contracts = contractRequirementsOf(s.contractRequirements)
  const params = paramRows(s.defaultParametersJson)
  return (
    <dl className="spec-page__sheet">
      <dt>Category</dt>
      <dd>
        <CategoryBadge category={s.category} />
        {s.instrumentKind && <span className="muted"> · {s.instrumentKind}</span>}
      </dd>

      <dt>Underlyings</dt>
      <dd>
        {s.supportedUnderlyings.length === 0 ? (
          <span className="muted">any</span>
        ) : (
          <span className="chip-row">
            {s.supportedUnderlyings.map((u) => (
              <span key={u} className="badge badge--neutral mono">{u}</span>
            ))}
          </span>
        )}
      </dd>

      <dt>Legs</dt>
      <dd>{s.legsSummary || <span className="muted">—</span>}</dd>

      <dt>Contracts it trades</dt>
      <dd>
        {contracts.length === 0 ? (
          <span className="muted">ATM CE + ATM PE (declares none; the runner's default)</span>
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

      <dt>Data it needs</dt>
      <dd>
        {s.dataRequirements.length === 0 ? (
          <span className="muted">live spot ticks only</span>
        ) : (
          <ul className="spec-page__list">
            {s.dataRequirements.map((d, i) => (
              <li key={i}>
                {d.symbolType} @ {formatResolution(d.resolution)}
              </li>
            ))}
          </ul>
        )}
      </dd>

      <dt>Default lots</dt>
      <dd>{s.defaultLots > 0 ? s.defaultLots : 1}</dd>

      <dt>Default parameters</dt>
      <dd>
        {params.length === 0 ? (
          <span className="muted">none — the strategy has no tunables</span>
        ) : (
          <table className="table spec-page__params">
            <tbody>
              {params.map((p) => (
                <tr key={p.key}>
                  <td className="mono">{p.key}</td>
                  <td className="mono">{p.value}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </dd>

      <dt>Source</dt>
      <dd className="mono">{s.sourceFile || '—'}</dd>

      <dt>Right now</dt>
      <dd>
        {s.isActive ? (
          <>
            <Badge tone="pos">running</Badge>
            {s.underlying ? ` on ${s.underlying}` : ''}
            {s.startedUtc ? ` since ${formatDateTime(s.startedUtc)}` : ''}
            {s.runId != null && (
              <>
                {' · '}
                <Link to={runHref(s.runId)}>run #{s.runId}</Link>
              </>
            )}
          </>
        ) : (
          <span className="muted">not running</span>
        )}
      </dd>
    </dl>
  )
}

/**
 * One strategy, read top to bottom: what the catalog knows (the sheet), then
 * how it works (the specification, with a table of contents beside it).
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
  // section is left out: its content is the chips at the top of the document.
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
          {strategy && <p className="page__subtitle spec-page__subtitle">{strategy.description}</p>}
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
              <section className="spec-page__section">
                <h2 className="section-title">At a glance</h2>
                <DetailsSheet s={strategy} runHref={runHref} />
              </section>

              <section className="spec-page__section">
                <h2 className="section-title">How it works</h2>
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
                    sections). Everything above comes from the code and is current.
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
