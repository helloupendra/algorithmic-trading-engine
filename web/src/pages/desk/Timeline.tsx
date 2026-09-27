/**
 * Today on the desk (admin): the live incidents first, then the day newest
 * first — runs started and stopped, Sentinel's checkups, forecasts issued and
 * scored, incidents opened and resolved, deploys. The merge is done here
 * from each source's own endpoint (lib/desk.ts, deskTimeline) until a
 * server-side timeline exists.
 */

import { Link } from 'react-router-dom'
import { deskTimeline, istHm, liveIncidents } from '../../lib/desk'
import { SEVERITY_LABEL, severityTone } from '../../lib/incidents'
import { allows } from '../../lib/modules'
import { useCheckups, useDeployHistory, useIncidents } from '../../lib/queries'
import type { Forecast } from '../../lib/analysis'
import type { DeskLinks, DeskView } from './data'
import { useDayForecasts } from './data'
import { Chip, Dot, Failed, PanelHead, Waiting } from './parts'

const SHOWN = 10

/** A line of the timeline, with its rupee figures kept whole ("−₹5,120" never breaks after the sign). */
function Text({ text }: { text: string }) {
  return (
    <>
      {text.split(/([−+]?₹[\d,]+)/).map((part, i) =>
        i % 2 ? (
          <span key={i} className="dk-n">
            {part}
          </span>
        ) : (
          part
        ),
      )}
    </>
  )
}

function WithForecasts({ view, links }: { view: DeskView; links: DeskLinks }) {
  const forecasts = useDayForecasts(view)
  return <Body view={view} links={links} forecasts={forecasts.data} />
}

export function Timeline({ view, links }: { view: DeskView; links: DeskLinks }) {
  return allows(view.access, 'analysis') ? <WithForecasts view={view} links={links} /> : <Body view={view} links={links} forecasts={undefined} />
}

function Body({ view, links, forecasts }: { view: DeskView; links: DeskLinks; forecasts: Forecast[] | undefined }) {
  const incidents = useIncidents({ status: 'any', take: 50 })
  const checkups = useCheckups(30)
  const deploys = useDeployHistory(60_000)
  const live = liveIncidents(incidents.data)
  const events = deskTimeline({
    today: view.day,
    runs: view.runs,
    incidents: incidents.data,
    checkups: checkups.data,
    forecasts,
    deploys: deploys.data?.entries,
  })
  const title = view.phase === 'pre' ? 'This morning' : 'Today on the desk'
  const loading = !incidents.data && !incidents.isError && !view.runs
  const failures = [incidents.isError && 'incidents', checkups.isError && 'checkups', deploys.isError && 'deploys'].filter(Boolean)
  return (
    <>
      <PanelHead title={title} meta="runs, checkups, forecasts, incidents, deploys" more={links.incidents ? { to: links.incidents, label: 'Incidents' } : null} />
      {live.length > 0 && (
        <div className="dk-incs">
          {live.slice(0, 3).map((i) => {
            const tone = severityTone(i.severity)
            return (
              <Link key={i.id} to={links.incidents ?? '#'} className="dk-inc" title={i.summary}>
                <Chip tone={tone === 'neutral' ? undefined : tone}>{SEVERITY_LABEL[i.severity] ?? i.severity}</Chip>
                <span>{i.title}</span>
                <span className="dk-t3 dk-xs dk-n">
                  {istHm(i.firstSeenUtc)} · {i.status}
                </span>
              </Link>
            )
          })}
        </div>
      )}
      {loading ? (
        <Waiting>Reading the day…</Waiting>
      ) : events.length === 0 ? (
        <Waiting>Nothing on the desk yet today.</Waiting>
      ) : (
        <ul className="dk-list dk-tl">
          {events.slice(0, SHOWN).map((e) => (
            <li key={e.key}>
              <span className="dk-tm">{e.time}</span>
              <Dot tone={e.tone} />
              <span className={e.tone === 'neg' ? '' : 'dk-t2'}>
                <Text text={e.text} />
              </span>
            </li>
          ))}
        </ul>
      )}
      {events.length > SHOWN && <div className="dk-foot">{events.length - SHOWN} earlier today</div>}
      {failures.length > 0 && <Failed what={`Some of the day (${failures.join(', ')})`} error={null} />}
    </>
  )
}
