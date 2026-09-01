/**
 * Market news: India markets, global business and commodities, aggregated
 * server-side from public RSS feeds and cached for five minutes.
 */

import { useState } from 'react'
import { useMarketNews } from '../../lib/queries'
import { formatAge } from '../../lib/format'
import { Panel, QueryBoundary } from '../../components/ui'

const TABS = [
  { key: 'india', label: 'India markets' },
  { key: 'global', label: 'Global' },
  { key: 'commodities', label: 'Commodities' },
] as const

type Category = (typeof TABS)[number]['key']

export function MarketNewsPage() {
  const [category, setCategory] = useState<Category>('india')
  const news = useMarketNews(category)

  return (
    <div className="page">
      <header className="page__header">
        <h1 className="page__title">Market news</h1>
        <p className="page__subtitle">
          Headlines from public feeds (Economic Times, BBC), refreshed every few minutes.
        </p>
      </header>

      <div className="toolbar">
        {TABS.map((tab) => (
          <button
            key={tab.key}
            type="button"
            className={`btn btn--sm ${category === tab.key ? 'btn--primary' : 'btn--ghost'}`}
            onClick={() => setCategory(tab.key)}
          >
            {tab.label}
          </button>
        ))}
        {news.data && (
          <span className="muted small-note" style={{ marginTop: 0 }}>
            fetched {formatAge(news.data.fetchedUtc)}
          </span>
        )}
      </div>

      <Panel>
        <QueryBoundary query={news} empty="No headlines right now — the feeds may be unreachable.">
          {(data) => (
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
          )}
        </QueryBoundary>
      </Panel>
    </div>
  )
}
