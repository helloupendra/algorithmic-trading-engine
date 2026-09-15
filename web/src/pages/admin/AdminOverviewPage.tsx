/**
 * System — Overview: is the platform healthy?
 *
 * One screen for the machine and what runs on it: the trading switches an
 * operator checks first, the server (disk, memory, CPU, how fast the disk is
 * filling), the database on it, the processes, and the Drive archive.
 *
 * It used to carry a "Data freshness" note and a live quotes table. Those are
 * market data, not system state, and they live in the Data module (Overview
 * and Live feeds). What this page lacked instead was the server: on
 * 2026-09-15 the disk lost half its free space in a day and nobody could see
 * it without asking.
 */

import type { ReactNode } from 'react'
import { Link } from 'react-router-dom'
import type { UseQueryResult } from '@tanstack/react-query'
import {
  useAlerterStatus,
  useBackendStatus,
  useChainPollerStatus,
  useFeeds,
  useIngestorStatuses,
  useKillSwitch,
  useMarketSession,
  useProviders,
  useSystemHost,
} from '../../lib/queries'
import { connectorsSummary } from '../../lib/pulse'
import { feedDiagnostics } from '../../lib/feeds'
import { formatAge, formatDateTime, formatTime } from '../../lib/format'
import {
  describePolicy,
  ec2Line,
  formatBytes,
  formatBytesPair,
  formatUptime,
  growthLine,
  policyJobProblem,
  usageTone,
} from '../../lib/system'
import type { SystemHostReport, Tone } from '../../lib/system'
import { Badge, InlineError, Loading, Panel, StatTile } from '../../components/ui'
import { IconDatabase, IconPulse, IconRefresh, IconServer } from '../../components/icons'

const NOT_AVAILABLE = 'not available on this host'

/** Pending and failed states of the host report, without repeating the stale note per panel. */
function HostSection({
  query,
  children,
}: {
  query: UseQueryResult<SystemHostReport>
  children: (report: SystemHostReport) => ReactNode
}) {
  if (query.data) return <>{children(query.data)}</>
  if (query.isError) return <InlineError error={query.error} />
  return <Loading />
}

function Meter({
  label,
  value,
  sub,
  percent,
  tone,
}: {
  label: string
  value: ReactNode
  sub: ReactNode
  /** Draws the bar when known. */
  percent?: number | null
  tone?: Tone
}) {
  return (
    <div className="metric sys-meter">
      <div className="metric__label">{label}</div>
      <div className={`metric__value ${tone ?? ''}`}>{value}</div>
      <div className="metric__sub">{sub}</div>
      {percent != null && (
        <div
          className="progress"
          role="meter"
          aria-label={label}
          aria-valuemin={0}
          aria-valuemax={100}
          aria-valuenow={Math.round(percent)}
        >
          <div
            className={`progress__bar ${tone ? `progress__bar--${tone}` : ''}`}
            style={{ width: `${Math.min(100, Math.max(0, percent))}%` }}
          />
        </div>
      )}
    </div>
  )
}

function pct(value: number | null | undefined): string {
  return value == null ? '—' : `${Math.round(value)}%`
}

/* ------------------------------------------------------------------ server */

function ServerPanel({ report }: { report: SystemHostReport }) {
  const { host, ec2 } = report
  const identity = ec2Line(ec2)
  const growth = growthLine(report)
  const { disk, memory, swap, cpu, load } = host
  const loadPerCore = load && cpu.cores > 0 ? load.one / cpu.cores : null

  return (
    <>
      <div className="sys-id">
        {identity ? (
          <>
            <Badge tone="accent">EC2</Badge>
            <span className="mono">{identity}</span>
          </>
        ) : (
          <Badge tone="neutral">Not an EC2 host</Badge>
        )}
        <span className="faint">
          {host.machineName} · {host.os} · {host.architecture}
        </span>
      </div>

      <div className="sys-meters">
        <Meter
          label="Disk /"
          value={disk ? formatBytesPair(disk.usedBytes, disk.totalBytes) : '—'}
          sub={
            disk
              ? `${pct(disk.usedPercent)} used · ${formatBytes(disk.freeBytes)} free`
              : NOT_AVAILABLE
          }
          percent={disk?.usedPercent}
          tone={usageTone(disk?.usedPercent)}
        />
        <Meter
          label="Memory"
          value={memory ? formatBytesPair(memory.usedBytes, memory.totalBytes) : '—'}
          sub={memory ? `${pct(memory.usedPercent)} used · ${formatBytes(memory.availableBytes)} available` : NOT_AVAILABLE}
          percent={memory?.usedPercent}
          tone={usageTone(memory?.usedPercent)}
        />
        <Meter
          label="Swap"
          value={swap ? (swap.totalBytes > 0 ? formatBytesPair(swap.usedBytes, swap.totalBytes) : 'None') : '—'}
          sub={swap ? (swap.totalBytes > 0 ? `${pct(swap.usedPercent)} used` : 'no swap configured') : NOT_AVAILABLE}
          percent={swap?.usedPercent}
          tone={usageTone(swap?.usedPercent)}
        />
        <Meter
          label="CPU"
          value={cpu.usedPercent != null ? pct(cpu.usedPercent) : '—'}
          sub={
            cpu.usedPercent != null
              ? `${cpu.cores} cores · over ${cpu.sampleSeconds ?? 0}s`
              : `${cpu.cores} cores · usage ${NOT_AVAILABLE}`
          }
          percent={cpu.usedPercent}
          tone={usageTone(cpu.usedPercent)}
        />
        <Meter
          label="Load 1 / 5 / 15 min"
          value={load ? `${load.one.toFixed(2)} ${load.five.toFixed(2)} ${load.fifteen.toFixed(2)}` : '—'}
          sub={loadPerCore != null ? `${loadPerCore.toFixed(2)} per core` : NOT_AVAILABLE}
          tone={loadPerCore == null ? undefined : loadPerCore > 2 ? 'neg' : loadPerCore > 1 ? 'warn' : undefined}
        />
        <Meter
          label="Uptime"
          value={host.uptimeSeconds != null ? formatUptime(host.uptimeSeconds) : '—'}
          sub={host.uptimeSeconds != null ? 'since the machine booted' : NOT_AVAILABLE}
        />
      </div>

      <p className={`sys-growth ${growth.tone ?? ''}`}>{growth.text}</p>
      {report.growth && report.growth.bytesPerDay != null && (
        <details className="sys-details">
          <summary>What grows, and how this is estimated</summary>
          <ul className="sys-growth__tables">
            {report.growth.tables.map((t) => (
              <li key={t.table}>
                <span className="mono">{t.table}</span>{' '}
                <span className="muted">
                  {formatBytes(t.bytesPerDay)} per day · {t.method}
                </span>
              </li>
            ))}
          </ul>
          <p className="small-note">{report.growth.basis}</p>
        </details>
      )}
    </>
  )
}

/* ---------------------------------------------------------------- database */

function compressionText(table: SystemHostReport['database']['tables'][number]): string {
  if (!table.isHypertable) return 'plain table'
  const c = table.compression
  if (!c) return 'compression off'
  const chunks = `${c.compressedChunks} of ${c.totalChunks} chunks compressed`
  return c.beforeBytes != null && c.afterBytes != null && c.compressedChunks > 0
    ? `${chunks} · ${formatBytes(c.beforeBytes)} → ${formatBytes(c.afterBytes)}`
    : chunks
}

function DatabasePanel({ report }: { report: SystemHostReport }) {
  const db = report.database
  if (db.error) {
    return <InlineError error={new Error(`The database did not answer: ${db.error}`)} />
  }

  const largest = db.tables[0]?.bytes ?? 0
  const diskUsed = report.host.procAvailable ? report.host.disk?.usedBytes : null

  return (
    <>
      <p className="sys-lead">
        <b>{formatBytes(db.sizeBytes)}</b>
        <span className="muted">
          {' '}
          in total
          {diskUsed && db.sizeBytes ? ` · ${pct((100 * db.sizeBytes) / diskUsed)} of the disk's used space` : ''}
          {db.timescaleDb ? ' · TimescaleDB' : ''}
        </span>
      </p>

      <div className="tablewrap">
        <table className="table">
          <thead>
            <tr>
              <th>Largest tables</th>
              <th className="r">Size</th>
              <th aria-hidden="true" />
              <th>Compression</th>
            </tr>
          </thead>
          <tbody>
            {db.tables.map((t) => (
              <tr key={t.name}>
                <td className="mono">{t.name}</td>
                <td className="r">{formatBytes(t.bytes)}</td>
                <td className="sys-share-cell">
                  <div className="sys-share">
                    <span style={{ width: `${largest > 0 ? Math.max(1, (100 * t.bytes) / largest) : 0}%` }} />
                  </div>
                </td>
                <td className="muted">{compressionText(t)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>

      {db.policies.length > 0 && (
        <div className="sys-policies">
          <div className="metric__label">Tick table policies</div>
          {db.policies.map((p) => {
            const problem = policyJobProblem(p)
            return (
              <div key={p.table} className="sys-policy">
                <span className="mono">{p.table}</span>
                <span className="muted">{describePolicy(p)}</span>
                {problem && <Badge tone="warn">{problem}</Badge>}
              </div>
            )
          })}
        </div>
      )}
    </>
  )
}

/* ----------------------------------------------------------------- archive */

function ArchivePanel({ report }: { report: SystemHostReport }) {
  const archive = report.archive
  if (!archive) {
    return (
      <p className="muted sys-flush">
        Not set up yet: there is no archive manifest on this host. <span className="mono">scripts/archive_to_drive.py</span>{' '}
        writes one on its first run.
      </p>
    )
  }

  return (
    <>
      <p className="sys-lead">
        {archive.unverifiedDays > 0 ? (
          <Badge tone="warn">{archive.unverifiedDays} day(s) not verified</Badge>
        ) : (
          <Badge tone="pos">Every archived day verified</Badge>
        )}
        <span className="faint"> manifest updated {formatAge(archive.manifestUpdatedUtc)}</span>
        {archive.unreadableLines > 0 && <span className="warn"> · {archive.unreadableLines} unreadable line(s)</span>}
      </p>
      <div className="tablewrap">
        <table className="table">
          <thead>
            <tr>
              <th>Table</th>
              <th>Last verified day</th>
              <th className="r">Verified</th>
              <th className="r">Unverified</th>
            </tr>
          </thead>
          <tbody>
            {archive.tables.map((t) => (
              <tr key={t.table}>
                <td className="mono">{t.table}</td>
                <td>{t.lastVerifiedDay ?? <span className="warn">none yet</span>}</td>
                <td className="r">{t.verifiedDays}</td>
                <td className={`r ${t.unverifiedDays > 0 ? 'warn' : 'muted'}`}>{t.unverifiedDays}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </>
  )
}

/* ---------------------------------------------------------------- services */

function ServiceRow({ name, state, detail }: { name: string; state: ReactNode; detail: ReactNode }) {
  return (
    <div className="sys-service">
      <span className="sys-service__name">{name}</span>
      <span className="sys-service__state">{state}</span>
      <span className="sys-service__detail muted">{detail}</span>
    </div>
  )
}

/** Running / Stopped, and "…" until the answer is in — a pending read is not a stopped process. */
function RunState({ running, known }: { running: boolean | undefined; known: boolean }) {
  if (!known || running === undefined) return <Badge tone="neutral">…</Badge>
  return running ? <Badge tone="pos">Running</Badge> : <Badge tone="warn">Stopped</Badge>
}

function ServicesPanel({ host }: { host: SystemHostReport | undefined }) {
  const backend = useBackendStatus()
  const feeds = useFeeds()
  const poller = useChainPollerStatus()
  const alerter = useAlerterStatus()

  return (
    <div className="sys-services">
      <ServiceRow
        name="API backend"
        state={
          backend.isDown ? (
            <Badge tone="neg">Down</Badge>
          ) : backend.data ? (
            <Badge tone="pos">Up</Badge>
          ) : (
            <Badge tone="neutral">…</Badge>
          )
        }
        detail={
          backend.data
            ? [
                `up ${formatUptime(backend.data.uptimeSeconds)}`,
                host ? `${formatBytes(host.api.workingSetBytes)} memory` : null,
                backend.data.version ? `v${backend.data.version}` : null,
                backend.data.environment,
              ]
                .filter(Boolean)
                .join(' · ')
            : ''
        }
      />
      {feeds.data
        ? feeds.data.map((f) => (
            <ServiceRow
              key={f.key}
              name={`${f.displayName} feed`}
              state={<RunState running={f.isRunning} known />}
              detail={f.isRunning ? `pid ${f.processId ?? '?'} · ${f.source}` : 'not running'}
            />
          ))
        : (
          <ServiceRow
            name="Live feeds"
            state={feeds.isError ? <Badge tone="warn">Unknown</Badge> : <RunState running={undefined} known={false} />}
            detail={feeds.isError ? 'status could not be read' : ''}
          />
        )}
      <ServiceRow
        name="Option chain poller"
        state={<RunState running={poller.data?.isRunning} known={!!poller.data} />}
        detail={poller.data ? `last chain written ${formatAge(poller.data.lastCapturedUtc)}` : ''}
      />
      <ServiceRow
        name="Signal alerter"
        state={<RunState running={alerter.data?.isRunning} known={!!alerter.data} />}
        detail={alerter.data?.isRunning && alerter.data.startedUtc ? `started ${formatAge(alerter.data.startedUtc)}` : ''}
      />
    </div>
  )
}

/* -------------------------------------------------------------------- page */

export function AdminOverviewPage() {
  const session = useMarketSession()
  const killSwitch = useKillSwitch()
  const providers = useProviders()
  const feedList = useFeeds()
  const ingestors = useIngestorStatuses()
  const host = useSystemHost()

  // Every connector and every feed, not one vendor's: with FYERS, Dhan and
  // TrueData a "FYERS session" tile answered a third of the question.
  const connectors = connectorsSummary(providers.data, feedList.data, session.data?.isTradingDay !== false, Date.now())
  const running = feedDiagnostics(feedList.data, ingestors.data, Date.now()).rows.filter((r) => r.processTone === 'pos')
  const feedsBeating = running.length > 0 && running.every((r) => r.heartbeatTone === 'pos')

  return (
    <div className="page">
      <header className="page__header">
        <div>
          <h1 className="page__title">System</h1>
          <p className="page__subtitle">Is the platform healthy: the trading switches, the server, its database and the processes on it.</p>
        </div>
        <div className="sys-asof">
          {host.data && (
            <span className="faint" title={formatDateTime(host.data.generatedUtc)}>
              as of {formatTime(host.data.generatedUtc)} · refreshes every 15 s
            </span>
          )}
          {host.isError && host.data && <span className="warn">Refresh failed — showing the last reading.</span>}
        </div>
      </header>

      <div className="stat-grid">
        <StatTile
          label="Kill switch"
          value={killSwitch.data ? (killSwitch.data.isActive ? 'ACTIVE' : 'Off') : '…'}
          tone={killSwitch.data ? (killSwitch.data.isActive ? 'neg' : 'pos') : undefined}
          to="/admin/system/risk"
          sub={
            killSwitch.data?.updatedUtc
              ? `${killSwitch.data.isActive ? 'halted' : 'last set'} by ${killSwitch.data.updatedBy ?? 'unknown'} ${formatAge(killSwitch.data.updatedUtc)}`
              : 'manage →'
          }
        />
        <StatTile
          label="Connectors"
          value={connectors ? connectors.pulse.label.replace(/^Connectors /, '') : '…'}
          tone={connectors ? (connectors.pulse.tone === 'idle' || connectors.pulse.tone === 'live' ? undefined : connectors.pulse.tone) : undefined}
          to="/admin/broker"
          sub={connectors ? connectors.lines.filter((l) => l.state !== 'not-set-up').map((l) => `${l.name} ${l.state === 'ready' ? '✓' : '✗'}`).join(' · ') : undefined}
        />
        <StatTile
          label="Live data"
          value={feedList.data && ingestors.data ? (running.length ? running.map((r) => r.name).join(' + ') : 'No feed') : '…'}
          tone={
            !feedList.data || !ingestors.data
              ? undefined
              : running.length === 0
                ? session.data?.isMarketOpen ? 'neg' : undefined
                : feedsBeating && running.length === 1 ? 'pos' : 'warn'
          }
          to="/admin/data/live"
          sub={
            running.length > 1
              ? 'more than one feed running'
              : running[0]?.heartbeatUtc
                ? `heartbeat ${formatAge(running[0].heartbeatUtc)} · ${running[0].symbols ?? 0} symbols`
                : running.length ? 'no heartbeat yet' : 'start one from Live feeds →'
          }
        />
        <StatTile
          label="Market (NSE)"
          value={session.data ? (session.data.isMarketOpen ? 'OPEN' : 'CLOSED') : '…'}
          tone={session.data?.isMarketOpen ? 'pos' : undefined}
          to="/admin/system/calendar"
          sub={session.data && `next open ${formatDateTime(session.data.nextMarketOpenUtc)}`}
        />
      </div>

      <Panel
        title={
          <>
            <IconServer /> Server
          </>
        }
      >
        <HostSection query={host}>{(report) => <ServerPanel report={report} />}</HostSection>
      </Panel>

      <div className="two-col sys-cols">
        <Panel
          title={
            <>
              <IconDatabase /> Database
            </>
          }
        >
          <HostSection query={host}>{(report) => <DatabasePanel report={report} />}</HostSection>
        </Panel>

        <div className="stack-list">
          <Panel
            title={
              <>
                <IconPulse /> Processes
              </>
            }
            actions={
              <Link className="btn btn--sm btn--ghost" to="/admin/data/live">
                Live feeds →
              </Link>
            }
          >
            <ServicesPanel host={host.data} />
          </Panel>

          <Panel
            title={
              <>
                <IconRefresh /> Archive to Google Drive
              </>
            }
          >
            <HostSection query={host}>{(report) => <ArchivePanel report={report} />}</HostSection>
          </Panel>
        </div>
      </div>

      <Panel title="Operations">
        <div className="chip-row">
          <Link className="btn btn--sm" to="/admin/system/risk">Risk &amp; kill switch →</Link>
          <Link className="btn btn--sm" to="/admin/system/alerts">Alerts →</Link>
          <Link className="btn btn--sm" to="/admin/system/calendar">Market calendar →</Link>
          <Link className="btn btn--sm" to="/admin/system/logs">Activity log →</Link>
          <Link className="btn btn--sm" to="/admin/system/deployments">Deployments →</Link>
          <Link className="btn btn--sm" to="/admin/broker">Connectors →</Link>
          <Link className="btn btn--sm" to="/admin/users">Users &amp; access →</Link>
        </div>
      </Panel>
    </div>
  )
}
