import { Suspense, lazy } from 'react'
import { Link, useParams } from 'react-router-dom'
import { useStrategies } from '../../lib/queries'
import { useStrategySpec } from '../../lib/specs'
import { Loading, QueryBoundary } from '../../components/ui'
import { IconArrowRight } from '../../components/icons'
import { CategoryBadge } from './shared'
import { headingSlug } from './StrategySpecPanel'
import './spec.css'

const StrategySpecPanel = lazy(() => import('./StrategySpecPanel'))

/**
 * One strategy's specification as a page of its own, read top to bottom:
 * the strategy's line from the catalog, then the document with a table of
 * contents beside it. It replaced a panel that opened under the Library
 * table, which put a 600-line document at the bottom of a page built for a
 * table and read like an afterthought.
 */
export function StrategySpecPage() {
  const { id } = useParams()
  const strategyId = Number(id)
  const strategies = useStrategies()
  const spec = useStrategySpec(Number.isInteger(strategyId) && strategyId > 0 ? strategyId : null)

  const strategy = strategies.data?.find((s) => s.id === strategyId) ?? null
  // The contents come from the document itself (its H2 lines), so a spec
  // with an extra section is not misrepresented by a fixed list.
  const headings = (spec.data?.markdown ?? '')
    .split('\n')
    .filter((l) => l.startsWith('## '))
    .map((l) => l.slice(3).trim())

  return (
    <div className="page spec-page">
      <header className="page__header">
        <div>
          <Link to="/admin/strategies/library" className="spec-page__back">
            ← Strategy library
          </Link>
          <h1 className="page__title spec-page__title">
            {strategy?.name ?? spec.data?.name ?? `Strategy ${id}`}
            {strategy && <CategoryBadge category={strategy.category} />}
          </h1>
          {strategy && <p className="page__subtitle spec-page__subtitle">{strategy.description}</p>}
        </div>
      </header>

      <QueryBoundary query={strategies}>
        {() =>
          !strategy && !strategies.isPending ? (
            <p className="empty">
              No strategy with id {id} in the catalog. <Link to="/admin/strategies/library">Back to the library</Link>.
            </p>
          ) : (
            <div className="spec-page__layout">
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
                  <div className="spec-page__toc-foot">
                    <Link to="/admin/backtesting/new">
                      Backtest it <IconArrowRight style={{ width: 12, height: 12 }} />
                    </Link>
                  </div>
                </nav>
              )}
              <div className="spec-page__doc">
                <Suspense fallback={<Loading label="Loading spec…" />}>
                  <StrategySpecPanel strategyId={strategyId} />
                </Suspense>
              </div>
            </div>
          )
        }
      </QueryBoundary>
    </div>
  )
}
