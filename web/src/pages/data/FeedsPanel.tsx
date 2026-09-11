/**
 * Every live feed the API can run, one row per connector, from GET /api/Feeds.
 *
 * The rows are the server's list of connectors that declare live ticks, so a
 * new vendor's feed appears here without this file changing. The TrueData
 * panel this replaced was the FYERS controls copied with the names changed, and
 * the next vendor would have been a third copy.
 *
 * FYERS is in the list as well. It is the same process as the button in the
 * page header, and both refresh each other, so they cannot disagree; the header
 * stays because it is the control the morning routine reaches for.
 */

import { useState } from 'react'
import { feedsView } from '../../lib/feeds'
import { useFeedLogs, useFeeds, useMarketSession, useStartFeed, useStopFeed } from '../../lib/queries'
import { EmptyState, InlineError, Panel } from '../../components/ui'
import { IconPlay, IconStop } from '../../components/icons'
import type { LiveFeed } from '../../lib/types'

function FeedOutput({ feed }: { feed: LiveFeed }) {
  const logs = useFeedLogs(feed.key)
  // Defensive, like the ingestor console: an API build without this route
  // answers with the SPA's HTML, which must not crash the page.
  const lines = Array.isArray(logs.data) ? logs.data : []

  const empty = logs.isPending
    ? 'Loading output…'
    : feed.source === 'adopted'
      ? 'This feed was not launched by this API instance, so its output is not captured here. It writes to logs/engine/ on the API host.'
      : 'No output yet — it appears once the feed is started from this console.'

  return (
    <div className="console" style={{ marginTop: 12 }}>
      <div className="console__bar">
        <span className="console__dot console__dot--r" />
        <span className="console__dot console__dot--y" />
        <span className="console__dot console__dot--g" />
        <span className="console__title">{feed.displayName} process output</span>
      </div>
      <div className={`console__body ${lines.length === 0 ? 'faint' : ''}`}>
        {logs.isError && lines.length === 0
          ? 'The output could not be fetched.'
          : lines.length === 0
            ? empty
            : lines.map((line, i) => <div key={i}>{line}</div>)}
      </div>
    </div>
  )
}

export function FeedsPanel() {
  const feeds = useFeeds()
  const session = useMarketSession()
  const start = useStartFeed()
  const stop = useStopFeed()
  const [outputKey, setOutputKey] = useState<string | null>(null)

  const view = feedsView(feeds)

  function confirmStop(feed: LiveFeed) {
    const pid = feed.processId != null ? ` (pid ${feed.processId})` : ''
    const adopted =
      feed.source === 'adopted'
        ? ` It was not launched by this API instance${pid}; the API will kill that process.`
        : ''
    const message = session.data?.isMarketOpen
      ? `Market is OPEN. Stopping the ${feed.displayName} feed halts its tick capture for every strategy reading it.${adopted} Stop anyway?`
      : `Stop the ${feed.displayName} feed${pid}?${adopted}`
    if (window.confirm(message)) stop.mutate(feed.key)
  }

  const outputFeed = view.kind === 'ready' ? view.rows.find((r) => r.feed.key === outputKey)?.feed : undefined

  return (
    <Panel title="Feeds by connector">
      {start.isError && <InlineError error={start.error} />}
      {stop.isError && <InlineError error={stop.error} />}

      {view.kind === 'checking' && (
        <p className="muted" role="status" style={{ margin: 0 }}>
          Checking…
        </p>
      )}

      {view.kind === 'failed' && (
        <>
          <InlineError error={view.error} />
          <p className="small-note warn">
            Whether any feed is running is unknown until the API answers, so none can be started or
            stopped from here.
          </p>
        </>
      )}

      {view.kind === 'ready' && view.rows.length === 0 && (
        <EmptyState>No connector in this build declares live ticks.</EmptyState>
      )}

      {view.kind === 'ready' && view.rows.length > 0 && (
        <>
          {view.stale && (
            <p className="small-note warn" role="status" style={{ margin: '0 0 8px' }}>
              The latest status check failed. States below are the last answer, and nothing can be
              started or stopped until the next one arrives.
            </p>
          )}
          <div className="tablewrap">
            <table className="table">
              <thead>
                <tr>
                  <th>Feed</th>
                  <th>Process</th>
                  <th className="r"></th>
                </tr>
              </thead>
              <tbody>
                {view.rows.map(({ feed, state, tone, control }) => {
                  const starting = start.isPending && start.variables === feed.key
                  const stopping = stop.isPending && stop.variables === feed.key
                  return (
                    <tr key={feed.key}>
                      <td>
                        <b>{feed.displayName}</b> <span className="faint mono">{feed.key}</span>
                      </td>
                      <td className={tone}>{state}</td>
                      <td className="r">
                        <div className="toolbar" style={{ justifyContent: 'flex-end', gap: 6 }}>
                          {control === 'stop' && (
                            <button
                              className="btn btn--danger btn--sm"
                              disabled={stopping}
                              onClick={() => confirmStop(feed)}
                            >
                              <IconStop style={{ width: 12, height: 12 }} />
                              {stopping ? 'Stopping…' : 'Stop'}
                            </button>
                          )}
                          {control === 'start' && (
                            <button
                              className="btn btn--pos btn--sm"
                              disabled={starting}
                              onClick={() => start.mutate(feed.key)}
                            >
                              <IconPlay style={{ width: 12, height: 12 }} />
                              {starting ? 'Starting…' : 'Start'}
                            </button>
                          )}
                          <button
                            className="btn btn--ghost btn--sm"
                            onClick={() => setOutputKey(outputKey === feed.key ? null : feed.key)}
                          >
                            {outputKey === feed.key ? 'Hide output' : 'Output'}
                          </button>
                        </div>
                      </td>
                    </tr>
                  )
                })}
              </tbody>
            </table>
          </div>
          <p className="small-note" style={{ marginBottom: 0 }}>
            Each feed stamps its connector key on every tick it stores, so two can run side by side and be
            compared.
          </p>
        </>
      )}

      {outputFeed && <FeedOutput feed={outputFeed} />}
    </Panel>
  )
}
