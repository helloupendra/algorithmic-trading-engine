/**
 * Market news: the broad market feeds and one tab per sector, aggregated
 * server-side from the publishers' own RSS and cached for five minutes. One
 * page for both consoles — an admin reads it under Data, a trader under
 * Markets — because the news is the news, and there is nothing here that one
 * of them may see and the other may not.
 *
 * The tabs come from the API (GET /api/MarketIntel/news/categories) rather than
 * a list held here, so a sector added on the server appears without a change in
 * the console. The chosen tab lives in the URL, which makes a sector a link
 * someone can send — "?c=pharma" — instead of a place they have to navigate to.
 */

import { useMemo } from 'react'
import { useSearchParams } from 'react-router-dom'
import { useMarketNews, useMarketNewsCategories } from '../../lib/queries'
import { formatAge } from '../../lib/format'
import { EmptyState, InlineError, Loading, Panel, QueryBoundary } from '../../components/ui'
import type { NewsCategory } from '../../lib/types'

/** The tab rows, in the order they are shown. */
const GROUPS: { key: NewsCategory['group']; label: string }[] = [
  { key: 'markets', label: 'Markets' },
  { key: 'sectors', label: 'Sectors' },
]

const DEFAULT_CATEGORY = 'india'

function TabRow({
  label,
  categories,
  selected,
  onSelect,
}: {
  label: string
  categories: NewsCategory[]
  selected: string
  onSelect: (key: string) => void
}) {
  if (categories.length === 0) return null
  return (
    <div className="newstabs" role="group" aria-label={label}>
      <span className="newstabs__label">{label}</span>
      {categories.map((c) => (
        <button
          key={c.key}
          type="button"
          className={`btn btn--sm ${selected === c.key ? 'btn--primary' : 'btn--ghost'}`}
          aria-pressed={selected === c.key}
          onClick={() => onSelect(c.key)}
        >
          {c.label}
        </button>
      ))}
    </div>
  )
}

export function MarketNewsPage() {
  const categories = useMarketNewsCategories()
  const [params, setParams] = useSearchParams()

  const wanted = params.get('c')
  // A category the server does not offer (an old link, a typo) falls back
  // rather than asking for headlines that cannot come.
  const selected = useMemo(() => {
    const known = categories.data
    if (!known || known.length === 0) return wanted ?? DEFAULT_CATEGORY
    if (wanted && known.some((c) => c.key === wanted)) return wanted
    return known.some((c) => c.key === DEFAULT_CATEGORY) ? DEFAULT_CATEGORY : known[0].key
  }, [categories.data, wanted])

  const news = useMarketNews(categories.data ? selected : null)

  const current = categories.data?.find((c) => c.key === selected)

  function select(key: string) {
    setParams(key === DEFAULT_CATEGORY ? {} : { c: key }, { replace: true })
  }

  return (
    <div className="page">
      <header className="page__header">
        <div>
          <h1 className="page__title">Market news</h1>
          <p className="page__subtitle">
            Headlines from the publishers' own feeds — Economic Times, Business Standard, Mint and BBC —
            refreshed every few minutes. Informational market data, not advice.
          </p>
        </div>
        {news.data && (
          <span className="muted small-note" style={{ margin: 0 }}>
            fetched {formatAge(news.data.fetchedUtc)}
          </span>
        )}
      </header>

      {categories.isPending ? (
        <Loading label="Loading categories…" />
      ) : categories.isError ? (
        <InlineError error={categories.error} />
      ) : (
        <div className="newstabs__wrap">
          {GROUPS.map((g) => (
            <TabRow
              key={g.key}
              label={g.label}
              categories={(categories.data ?? []).filter((c) => c.group === g.key)}
              selected={selected}
              onSelect={select}
            />
          ))}
        </div>
      )}

      <Panel>
        <QueryBoundary query={news}>
          {(data) =>
            // A NewsResponse is an object, so QueryBoundary's own empty check
            // (null, or an empty array) never fires for a category whose feeds
            // all came back dry. Said here instead of painting an empty list.
            data.items.length === 0 ? (
              <EmptyState>
                No headlines for {current?.label ?? selected} right now — the feeds may be unreachable.
              </EmptyState>
            ) : (
              <ul className="newslist">
                {data.items.map((item) => (
                  <li key={item.link} className="newsitem">
                    <a href={item.link} target="_blank" rel="noreferrer noopener" className="newsitem__title">
                      {item.title}
                    </a>
                    {item.summary && <p className="newsitem__summary">{item.summary}</p>}
                    <div className="newsitem__meta">
                      <span>{item.source}</span>
                      {item.publishedUtc && <span>· {formatAge(item.publishedUtc)}</span>}
                    </div>
                  </li>
                ))}
              </ul>
            )
          }
        </QueryBoundary>
      </Panel>
    </div>
  )
}
