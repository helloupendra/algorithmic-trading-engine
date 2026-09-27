/**
 * News & filings (admin: the market-intelligence endpoints are admin-only
 * for now). Headlines the recorder stored, NSE filings of the names the desk
 * cares about (held, watched, NIFTY 50), and the local model's sentiment on
 * each — labelled as the model's reading of the words, not a signal: it has
 * not been scored the way the forecasts are.
 */

import { useMemo, useState } from 'react'
import type { NewsTab } from '../../lib/desk'
import { NEWS_TABS, istHm, newsLines, shiftDay, tickerOf } from '../../lib/desk'
import { useDeskWatchlist, useIntelAnnouncements, useIntelNews } from '../../lib/queries'
import type { DeskLinks, DeskView } from './data'
import { useDeskLegs } from './data'
import { Chip, Failed, PanelHead, Sentiment, Waiting } from './parts'

export function News({ view, links, limit = 6 }: { view: DeskView; links: DeskLinks; limit?: number }) {
  const [tab, setTab] = useState<NewsTab>('all')
  // Overnight news matters before the open, so the list reaches back to yesterday.
  const from = shiftDay(view.day, -1)
  const news = useIntelNews(from, true)
  const filings = useIntelAnnouncements(from, true)
  const watch = useDeskWatchlist(true)
  const { legs } = useDeskLegs(view)
  const names = useMemo(
    () => ({
      held: new Set((legs ?? []).map((l) => l.underlying)),
      watched: new Set((watch.data ?? []).map((w) => tickerOf(w.symbol))),
    }),
    [legs, watch.data],
  )
  const lines = newsLines(news.data?.items, filings.data?.items, tab, names).slice(0, limit)
  const waiting = !news.data && !filings.data && !news.isError && !filings.isError
  return (
    <>
      <PanelHead title="News & filings" meta="sentiment: model reading, not a signal" more={links.news ? { to: links.news, label: 'All news' } : null} />
      <div className="dk-tabs" role="group" aria-label="Show">
        {NEWS_TABS.map((t) => (
          <button key={t.key} type="button" aria-pressed={tab === t.key} onClick={() => setTab(t.key)}>
            {t.label}
          </button>
        ))}
      </div>
      {news.isError && !news.data && <Failed what="Headlines" error={news.error} />}
      {filings.isError && !filings.data && <Failed what="Filings" error={filings.error} />}
      {waiting ? (
        <Waiting>Reading the news…</Waiting>
      ) : lines.length === 0 ? (
        <Waiting>{tab === 'held' ? 'Nothing on held or watched names.' : tab === 'results' ? 'No results news.' : 'Nothing recorded since yesterday.'}</Waiting>
      ) : (
        <ul className="dk-list dk-news">
          {lines.map((l) => (
            <li key={l.key}>
              <span className="dk-tm">{istHm(new Date(l.atMs).toISOString())}</span>
              <div>
                <div>
                  {l.kind === 'filing' && (
                    <>
                      <Chip tone="brand">filing</Chip>{' '}
                    </>
                  )}
                  {l.link ? (
                    <a className="dk-news__t" href={l.link} target="_blank" rel="noreferrer noopener">
                      {l.title}
                    </a>
                  ) : (
                    <span className="dk-news__t">{l.title}</span>
                  )}
                </div>
                <div className="dk-xs dk-t3">
                  {l.source}
                  {l.symbols.length > 0 && ` · ${l.symbols.slice(0, 3).join(', ')}`}
                </div>
              </div>
              <Sentiment value={l.sentiment} />
            </li>
          ))}
        </ul>
      )}
    </>
  )
}
