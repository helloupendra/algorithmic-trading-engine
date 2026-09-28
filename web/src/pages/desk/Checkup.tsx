/**
 * Desk checkup (admin): Sentinel's latest checklist, with what to do on each
 * item that needs a person. Before the open it is the readiness list; after
 * the close, the after-close one. On a phone the passed checks fold into one
 * line, so the ones that need a hand are what is read.
 *
 * A checkup is what Sentinel saw when it ran. The items the Desk reads itself
 * (the morning plan, the Dhan and FYERS sessions, the feeds) are re-read now
 * and shown as they are, with what Sentinel saw kept one click away; the
 * rest stay as Sentinel said, marked with the checkup's time
 * (lib/checkupNow.ts). Once the open has passed and nothing on the morning
 * list needs a hand, it folds to one line: "Before the open: done 09:19".
 */

import { useEffect, useState } from 'react'
import { Link } from 'react-router-dom'
import { itemLink, pendingText, slotLabel, staleNote, stateBadge, waitOutcome, waitTimeoutMessage } from '../../lib/checkup'
import { foldReadiness } from '../../lib/checkupNow'
import type { ItemNow } from '../../lib/checkupNow'
import { istHm } from '../../lib/desk'
import { formatDateTime } from '../../lib/format'
import { maskSecrets } from '../../lib/incidents'
import { useRunCheckup } from '../../lib/queries'
import type { DeskLinks, DeskView } from './data'
import { useCheckupNow } from './data'
import { Chip, Dot, Failed, PanelHead, Waiting } from './parts'

const STATE_DOT: Record<string, 'pos' | 'warn' | 'neg' | null> = { ok: 'pos', warn: 'warn', fail: 'neg', info: null, skip: null }

const chipTone = (tone: string) => (tone === 'pos' ? 'pos' : tone === 'neg' ? 'neg' : tone === 'warn' ? 'warn' : undefined)

function ItemRow({ it, at }: { it: ItemNow; at: string }) {
  const link = itemLink(it.item.link)
  const state = stateBadge(it.state)
  const said = `${stateBadge(it.item.state).label}: ${maskSecrets(it.item.detail)}`
  return (
    // A passed check folds away on a phone; one that came right since the checkup is the news, and stays.
    <tr className={it.state === 'ok' && !it.changed ? 'dk-okrow' : undefined}>
      <td style={{ width: 14 }} title={state.label}>
        <Dot tone={STATE_DOT[it.state] ?? null} />
      </td>
      <td className="dk-t2" style={{ width: 120 }}>
        {link ? (
          <Link to={link} style={{ color: 'inherit', textDecoration: 'none' }}>
            {it.item.title}
          </Link>
        ) : (
          it.item.title
        )}
      </td>
      <td title={it.changed ? `At ${at} Sentinel saw: ${said}` : undefined}>
        {maskSecrets(it.detail)}
        {it.action && <div className="warn dk-s">{maskSecrets(it.action)}</div>}
        {it.changed && (
          <details className="dk-was">
            <summary>Sentinel at {at}</summary>
            <p>
              {said}
              {it.item.action && <> {maskSecrets(it.item.action)}</>}
            </p>
          </details>
        )}
      </td>
      <td className="dk-when" title={it.source === 'now' ? 'Read again just now' : `As Sentinel saw it at ${at}`}>
        {it.source === 'now' ? <span className="dk-nowmark">now</span> : at}
      </td>
    </tr>
  )
}

export function Checkup({ view, links }: { view: DeskView; links: DeskLinks }) {
  // A checkup asked for from here, and when the wait began.
  const [wait, setWait] = useState<{ runId: number; startedMs: number } | null>(null)
  const [timedOut, setTimedOut] = useState<string | null>(null)
  const [unfolded, setUnfolded] = useState(false)
  const { latest, checkup: c, now } = useCheckupNow(view, wait !== null)
  const run = useRunCheckup()
  const pending = latest.data?.pending ?? null
  const latestId = c?.id ?? null
  const pendingStatus = pending?.status ?? null

  useEffect(() => {
    if (!wait) return
    const outcome = waitOutcome({ ...wait, nowMs: view.nowMs, latestId })
    if (outcome === 'done') {
      setWait(null)
    } else if (outcome === 'timeout') {
      setWait(null)
      setTimedOut(waitTimeoutMessage(pendingStatus))
    }
  }, [wait, view.nowMs, latestId, pendingStatus])

  function checkAgain() {
    setTimedOut(null)
    run.mutate(undefined, { onSuccess: (answer) => setWait({ runId: answer.id, startedMs: Date.now() }) })
  }

  const busy = run.isPending || wait !== null || pending !== null
  const head = (
    <PanelHead
      title="Desk checkup"
      meta={c ? `${slotLabel(c.slot).toLowerCase()} · ${istHm(c.completedUtc)} · Sentinel` : 'Sentinel'}
      tools={
        view.isAdmin ? (
          <button
            type="button"
            className="dk-btn"
            disabled={busy}
            onClick={checkAgain}
            title="Ask Sentinel to run the checklist again now. It only reads the desk."
          >
            {busy ? 'Checking…' : 'Check again'}
          </button>
        ) : null
      }
      more={links.checkups ? { to: links.checkups, label: 'All checkups' } : null}
    />
  )
  const asking = (
    <>
      {(wait || pending) && (
        <p className="dk-wait" role="status" style={{ marginBottom: 6 }}>
          {wait || !pending
            ? 'Sentinel is checking the desk; the new checkup shows here when it is done.'
            : pendingText({ ...pending, requestedBy: maskSecrets(pending.requestedBy) })}
        </p>
      )}
      {timedOut && <p className="dk-fail" style={{ marginBottom: 6 }}>{timedOut}</p>}
      {run.isError && <Failed what="Asking Sentinel" error={run.error} />}
    </>
  )
  if (latest.isError && !latest.data) return <>{head}<Failed what="The checkup" error={latest.error} /></>
  if (!latest.data) return <>{head}<Waiting>Reading the latest checkup…</Waiting></>
  if (!c || !now) return <>{head}{asking}<Waiting>No checkup has finished yet.</Waiting></>

  const at = istHm(c.completedUtc)
  const stale = staleNote(latest.data.lastCompletedUtc, view.nowMs, formatDateTime)
  const foldable = foldReadiness(c.slot, view.clock, now)
  const folded = foldable && !unfolded
  const passed = now.items.filter((i) => i.state === 'ok' && !i.changed).length
  const toggle = foldable && (
    <button type="button" className="dk-linkbtn" aria-expanded={!folded} onClick={() => setUnfolded(!unfolded)}>
      {folded ? `Show the ${now.items.length} checks` : 'Fold the checks'}
    </button>
  )
  return (
    <>
      {head}
      {asking}
      <div className="dk-hd dk-ck" style={{ marginTop: -2 }}>
        <Chip tone={chipTone(now.badge.tone)}>{now.badge.label}</Chip>
        <span className="dk-s">{maskSecrets(now.headline)}</span>
        <span className="dk-xs dk-t3">{now.voice === 'now' ? 'as of now' : `Sentinel at ${at}`}</span>
      </div>
      {(now.voice === 'now' || toggle) && (
        <div className="dk-ck-said">
          {now.voice === 'now' && (
            <span className="dk-note">
              At {at} Sentinel said: {maskSecrets(c.headline)}.
            </span>
          )}
          {toggle}
        </div>
      )}
      {stale && <p className="dk-fail">{stale}</p>}
      {!folded && (
        <>
          <table className="dk-t dk-t--wrap dk-ck-t">
            <tbody>
              {now.items.map((it, i) => (
                // Keys are not promised unique: one check can report more than one item.
                <ItemRow key={`${it.item.key}-${i}`} it={it} at={at} />
              ))}
            </tbody>
          </table>
          {passed > 0 && <div className="dk-okmore dk-xs dk-t3">{passed} more checks passed</div>}
        </>
      )}
    </>
  )
}
