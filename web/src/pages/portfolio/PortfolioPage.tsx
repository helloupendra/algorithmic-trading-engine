/**
 * Portfolio (/portfolio): a signed-in user's own demat and broker accounts,
 * linked read-only, and a report of facts about them. One sheet, top to
 * bottom: the linked accounts with Sync now, the totals, the written summary,
 * the holdings, the allocation, concentration and risk, open positions,
 * funds, the value over time, and the notes with the disclaimer, which are
 * there in every state.
 *
 * Facts only, never advice (SEBI requires registration for advice): the page
 * describes what the user holds and never says what to do with it.
 * portfolio.test.ts scans this folder's text for the words that would.
 *
 * Every user and admin has it; the API answers for the caller alone.
 */

import { useEffect, useState } from 'react'
import { useSearchParams } from 'react-router-dom'
import {
  connectionView,
  formatStamp,
  latestSyncMs,
  nameFor,
  redirectResult,
  retryAfterSeconds,
  safeRedirectUrl,
  sentence,
  shownSummary,
  syncWaitSeconds,
  usePortfolioConnections,
  usePortfolioHistory,
  usePortfolioReport,
  useStartConnect,
  useSyncPortfolio,
  DISCLAIMER,
} from '../../lib/portfolio'
import type { PortfolioConnection, PortfolioReport, SyncResult, TokenField } from '../../lib/portfolio'
import { InlineError, Loading, Panel } from '../../components/ui'
import { IconX } from '../../components/icons'
import { errorText, useNow } from '../ai/common'
import { useWidth } from '../desk/data'
import { ConnectionsPanel, DisconnectDialog, FirstRunPanel, TokenDialog } from './Connections'
import { HoldingsPanel } from './Holdings'
import { HistoryChart } from './HistoryChart'
import {
  AllocationPanel,
  ConcentrationRiskPanel,
  FundsPanel,
  NotesFooter,
  PositionsPanel,
  Summary,
  Totals,
} from './sections'
import './portfolio.css'

interface Notice {
  tone: 'pos' | 'neg' | 'warn'
  text: string
}

function joinNames(names: string[]): string {
  if (names.length <= 1) return names.join('')
  return `${names.slice(0, -1).join(', ')} and ${names[names.length - 1]}`
}

/** What a sync did, in a line: who was read, who failed and why. */
function syncNotice(result: SyncResult, connections: PortfolioConnection[] | undefined): Notice {
  const ok = result.results.filter((r) => r.ok).map((r) => nameFor(connections, r.broker))
  const failed = result.results.filter((r) => !r.ok)
  const at = result.takenUtc ? ` at ${formatStamp(result.takenUtc)} IST` : ''
  const parts: string[] = []
  if (ok.length) parts.push(`Read ${joinNames(ok)}${at}.`)
  for (const f of failed) parts.push(`${nameFor(connections, f.broker)} could not be read: ${sentence(f.error ?? 'no reason given')}`)
  if (!parts.length) parts.push('The sync read no account.')
  return { tone: failed.length === 0 ? 'pos' : ok.length === 0 ? 'neg' : 'warn', text: parts.join(' ') }
}

/** Sync now, disabled with a countdown while the once-a-minute limit runs. Its own clock, so the page does not tick. */
function SyncButton({
  lastMs,
  blockedUntil,
  pending,
  linked,
  onSync,
}: {
  lastMs: number | null
  blockedUntil: number | null
  pending: boolean
  linked: boolean
  onSync: () => void
}) {
  const now = useNow(1000)
  const wait = Math.max(syncWaitSeconds(now, lastMs), blockedUntil ? Math.max(0, Math.ceil((blockedUntil - now) / 1000)) : 0)
  const label = pending ? 'Syncing…' : wait > 0 ? `Sync again in ${wait} s` : 'Sync now'
  return (
    <button
      type="button"
      className="btn btn--sm btn--primary pf-sync"
      onClick={onSync}
      disabled={pending || wait > 0 || !linked}
      title={!linked ? 'Link an account first' : wait > 0 ? 'A sync is allowed once a minute' : 'Read every linked account now'}
    >
      {label}
    </button>
  )
}

function NoticeBar({ notice, onDismiss }: { notice: Notice; onDismiss: () => void }) {
  const cls = notice.tone === 'pos' ? 'alert--success' : notice.tone === 'neg' ? 'alert--error' : 'alert--warn'
  return (
    <div className={`alert ${cls} pf-notice`} role={notice.tone === 'neg' ? 'alert' : 'status'}>
      <span>{notice.text}</span>
      <button type="button" className="btn btn--ghost btn--sm pf-notice__x" onClick={onDismiss} aria-label="Dismiss">
        <IconX style={{ width: 13, height: 13 }} />
      </button>
    </div>
  )
}

/** A read that failed, said with what it was, and a way to ask again. */
function ReadFailed({ what, error, onRetry }: { what: string; error: unknown; onRetry: () => void }) {
  return (
    <div className="alert alert--error" role="alert">
      <span>
        {what} could not be read: {sentence(errorText(error))}
      </span>
      <button type="button" className="btn btn--sm" onClick={onRetry}>
        Try again
      </button>
    </div>
  )
}

function HistoryPanel({ enabled }: { enabled: boolean }) {
  const history = usePortfolioHistory(90, enabled)
  const [ref, width] = useWidth<HTMLDivElement>()
  const points = history.data ?? []
  return (
    <Panel
      title={
        <>
          Value over time <span className="faint pf-title-note">last 90 days, a point a daily snapshot</span>
        </>
      }
      className="pf-history"
      actions={
        points.length >= 2 && (
          <ul className="pf-legend" aria-label="Lines">
            <li>
              <i className="pf-legend__swatch pf-legend__swatch--current" aria-hidden="true" /> Current value
            </li>
            <li>
              <i className="pf-legend__swatch pf-legend__swatch--invested" aria-hidden="true" /> Invested
            </li>
          </ul>
        )
      }
    >
      <div ref={ref}>
        {history.isPending ? (
          <Loading label="Reading the history…" />
        ) : history.isError ? (
          <InlineError error={history.error} />
        ) : points.filter((p) => p.current != null).length < 2 ? (
          <p className="empty">The line starts once two daily snapshots exist.</p>
        ) : (
          <HistoryChart points={points} width={width} />
        )}
      </div>
    </Panel>
  )
}

/** The report's own sections, in order. */
function ReportView({ report, connections, nowMs }: { report: PortfolioReport; connections: PortfolioConnection[]; nowMs: number }) {
  const failed = report.sources.filter((s) => !s.ok)
  const expired = connections.filter((c) => connectionView(c, nowMs).status === 'expired')
  const summary = shownSummary(report.summary)
  const positions = report.positions ?? []
  const showPositions = positions.length > 0 || (report.derivatives?.openPositions ?? 0) > 0
  const read = report.sources.filter((s) => s.ok).map((s) => nameFor(connections, s.broker))

  return (
    <>
      {(failed.length > 0 || expired.length > 0) && (
        <div className="pf-alerts">
          {failed.map((s) => (
            <div key={`f-${s.broker}`} className="alert alert--warn" role="status">
              <span>
                <b>{nameFor(connections, s.broker)}</b> could not be read in the last sync: {sentence(s.error ?? 'no reason given')}
                {read.length > 0 && ` The figures below are from ${joinNames(read)}.`}
              </span>
            </div>
          ))}
          {expired.map((c) => {
            const source = report.sources.find((s) => s.broker === c.broker)
            const at = source?.takenUtc ?? c.lastSyncUtc
            return (
              <div key={`e-${c.broker}`} className="alert alert--warn" role="status">
                <span>
                  The <b>{c.name}</b> link expired {formatStamp(c.expiresUtc)} IST.
                  {at ? ` Its figures are as last read, ${formatStamp(at)} IST.` : ''} Reconnect it above to read it again.
                </span>
              </div>
            )
          })}
        </div>
      )}

      <p className="pf-asof">
        As of <b>{formatStamp(report.asOfUtc)} IST</b>
        {read.length > 0 && <> · from {joinNames(read)}</>}
      </p>
      <Totals totals={report.totals} />
      {summary && <Summary summary={summary} />}
      <HoldingsPanel holdings={report.holdings} />
      <AllocationPanel allocation={report.allocation} />
      <ConcentrationRiskPanel concentration={report.concentration} risk={report.risk} />
      {(showPositions || (report.funds && report.funds.length > 0)) && (
        <div className="pf-pair">
          {showPositions && <PositionsPanel positions={positions} derivatives={report.derivatives} />}
          {report.funds && report.funds.length > 0 && <FundsPanel funds={report.funds} />}
        </div>
      )}
      <HistoryPanel enabled />
    </>
  )
}

export function PortfolioPage() {
  const [params, setParams] = useSearchParams()
  const connections = usePortfolioConnections()
  const list = connections.data
  const report = usePortfolioReport(list !== undefined)
  const start = useStartConnect()
  const sync = useSyncPortfolio()
  const nowMs = useNow(30_000)

  // A broker's redirect lands here with ?connected= or ?error=: said once, then taken off the URL.
  const [notice, setNotice] = useState<Notice | null>(() => {
    const r = redirectResult(params)
    if (!r) return null
    return r.kind === 'connected'
      ? { tone: 'pos', text: `${nameFor(undefined, r.broker)} is linked. Sync now reads its holdings.` }
      : { tone: 'neg', text: `The broker link did not complete: ${r.message}` }
  })
  useEffect(() => {
    if (!params.has('connected') && !params.has('error')) return
    const next = new URLSearchParams(params)
    next.delete('connected')
    next.delete('error')
    setParams(next, { replace: true })
    // Once, on arrival: later changes to the query are the page's own.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  const [syncNote, setSyncNote] = useState<Notice | null>(null)
  const [lastOk, setLastOk] = useState<number | null>(null)
  const [blockedUntil, setBlockedUntil] = useState<number | null>(null)
  const [tokenFor, setTokenFor] = useState<{ connection: PortfolioConnection; fields: TokenField[] } | null>(null)
  const [unlinkFor, setUnlinkFor] = useState<PortfolioConnection | null>(null)

  const serverLast = latestSyncMs(list ?? [])
  const lastMs = serverLast == null ? lastOk : lastOk == null ? serverLast : Math.max(serverLast, lastOk)
  const linked = (list ?? []).some((c) => connectionView(c, nowMs).status === 'connected')
  const allUnlinked = list !== undefined && list.every((c) => c.status === 'not_connected')
  const firstRun = allUnlinked && report.data?.kind !== 'report' && !report.isPending
  const pendingBroker = start.isPending ? (start.variables ?? null) : null

  function runSync() {
    setSyncNote(null)
    sync.mutate(undefined, {
      onSuccess: (result) => {
        setLastOk(Date.now())
        setSyncNote(syncNotice(result, list))
      },
      onError: (error) => {
        const after = retryAfterSeconds(error)
        if (after != null) setBlockedUntil(Date.now() + after * 1000)
        setSyncNote({ tone: after != null ? 'warn' : 'neg', text: after != null ? errorText(error) : `The sync failed: ${errorText(error)}` })
      },
    })
  }

  function connect(c: PortfolioConnection) {
    setNotice(null)
    start.mutate(c.broker, {
      onSuccess: (s) => {
        if (s.mode === 'token') {
          setTokenFor({ connection: c, fields: s.fields.length > 0 ? s.fields : c.tokenFields })
          return
        }
        const url = safeRedirectUrl(s.url, window.location.origin)
        if (!url) {
          setNotice({ tone: 'neg', text: `${c.name}'s sign-in address was not a secure web address, so the page did not open it.` })
          return
        }
        window.location.assign(url)
      },
      onError: (error) => setNotice({ tone: 'neg', text: `${c.name} could not be linked: ${errorText(error)}` }),
    })
  }

  const syncButton = (
    <SyncButton lastMs={lastMs} blockedUntil={blockedUntil} pending={sync.isPending} linked={linked} onSync={runSync} />
  )
  const answer = report.data
  const disclaimer = answer?.kind === 'report' ? answer.report.disclaimer : DISCLAIMER
  const notes = answer?.kind === 'report' ? answer.report.dataNotes : []
  const sources =
    answer?.kind === 'report' && answer.report.sources.length > 0 ? (
      <p className="pf-sources">
        {answer.report.sources.map((s, i) => (
          <span key={s.broker}>
            {i > 0 && ' · '}
            {nameFor(list, s.broker)}: {s.ok ? `read ${formatStamp(s.takenUtc)} IST` : 'not read in the last sync'}
          </span>
        ))}
      </p>
    ) : null

  return (
    <div className="page pf">
      <header className="page__header">
        <div>
          <h1 className="page__title">Portfolio</h1>
          <p className="page__subtitle">Your own demat and broker accounts, read-only: what you own, what it is worth, and how it is spread.</p>
        </div>
      </header>

      {notice && <NoticeBar notice={notice} onDismiss={() => setNotice(null)} />}

      {connections.isPending ? (
        <Loading label="Reading your linked accounts…" />
      ) : connections.isError && !list ? (
        <ReadFailed what="Your linked accounts" error={connections.error} onRetry={() => void connections.refetch()} />
      ) : firstRun && list ? (
        <FirstRunPanel connections={list} pendingBroker={pendingBroker} onConnect={connect} />
      ) : list ? (
        <ConnectionsPanel
          connections={list}
          nowMs={nowMs}
          pendingBroker={pendingBroker}
          onConnect={connect}
          onDisconnect={setUnlinkFor}
          sync={syncButton}
        />
      ) : null}

      {syncNote && <NoticeBar notice={syncNote} onDismiss={() => setSyncNote(null)} />}

      {list && !firstRun && (
        report.isPending ? (
          <Loading label="Reading your portfolio…" />
        ) : report.isError && !answer ? (
          <ReadFailed what="Your portfolio" error={report.error} onRetry={() => void report.refetch()} />
        ) : answer?.kind === 'none' ? (
          <Panel title="Nothing synced yet" className="pf-unsynced">
            <p className="pf-flush">{sentence(answer.message)}</p>
            <p className="small-note">
              {linked
                ? 'Sync now, above, reads the holdings, open positions and funds of every linked account.'
                : 'Link an account above first; Sync now then reads it.'}
            </p>
          </Panel>
        ) : answer?.kind === 'report' ? (
          <ReportView report={answer.report} connections={list} nowMs={nowMs} />
        ) : null
      )}

      <NotesFooter notes={notes} sources={sources} disclaimer={disclaimer} />

      {tokenFor && (
        <TokenDialog
          connection={tokenFor.connection}
          fields={tokenFor.fields}
          onClose={() => setTokenFor(null)}
          onLinked={(name) => {
            setTokenFor(null)
            setNotice({ tone: 'pos', text: `${name} is linked. Sync now reads its holdings.` })
          }}
        />
      )}
      {unlinkFor && (
        <DisconnectDialog
          connection={unlinkFor}
          onClose={() => setUnlinkFor(null)}
          onDone={(message) => {
            setUnlinkFor(null)
            setNotice({ tone: 'pos', text: message })
          }}
        />
      )}
    </div>
  )
}
