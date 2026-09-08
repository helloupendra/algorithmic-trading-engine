import { useAuth } from '../../lib/auth'
import { useInstrumentMasters, useRefreshInstrumentMasters } from '../../lib/queries'
import { formatAge, formatDateTime, formatNumber } from '../../lib/format'
import { Badge, InlineError, Panel, QueryBoundary } from '../../components/ui'
import { IconDownload } from '../../components/icons'
import type { InstrumentMasterRefreshResult, InstrumentMasterStatus } from '../../lib/types'

/**
 * The FYERS symbol masters on this host, and one button that downloads the
 * latest five and imports them. Before this existed the MCX master had never
 * been downloaded on the live box, so no commodity symbol could be found and
 * no commodity data fetched; the NSE F&O file was eight days old and missing
 * that week's contracts. The job runs on the server and the panel follows it.
 */
export function SymbolMastersPanel() {
  const { isAdmin } = useAuth()
  const masters = useInstrumentMasters()
  const refresh = useRefreshInstrumentMasters()
  const job = masters.data?.job
  const running = !!job?.isRunning
  const done = job?.results.length ?? 0
  const total = masters.data?.masters.length ?? 0

  return (
    <Panel
      title={
        <>
          <IconDownload /> Symbol masters
        </>
      }
      actions={
        isAdmin ? (
          <button
            type="button"
            className="btn btn--primary btn--sm"
            disabled={running || refresh.isPending}
            onClick={() => refresh.mutate()}
            title="Download today's NSE, BSE and MCX symbol files from FYERS and import them"
          >
            {running ? `Refreshing ${job?.current ?? '…'} (${done}/${total})` : 'Download latest'}
          </button>
        ) : (
          <span className="muted" style={{ fontSize: 12 }}>admins refresh these</span>
        )
      }
    >
      {refresh.isError && <InlineError error={refresh.error} />}
      <QueryBoundary query={masters} empty="No masters known.">
        {(data) => (
          <>
            <div className="tablewrap">
              <table className="table">
                <thead>
                  <tr>
                    <th>Master</th>
                    <th>Covers</th>
                    <th className="r" title="Unexpired contracts / all rows stored for this exchange and segment">
                      In database
                    </th>
                    <th title="The CSV in data/instruments on this host">File</th>
                    <th>Last refresh</th>
                  </tr>
                </thead>
                <tbody>
                  {data.masters.map((m) => (
                    <MasterRow key={m.name} m={m} live={job?.results.find((r) => r.name === m.name) ?? null} current={job?.current === m.name} />
                  ))}
                </tbody>
              </table>
            </div>
            <p className="small-note">
              Files come from FYERS's public symbol lists and are imported into the instrument table the
              strategies, search and backfill resolve against. Refresh after every expiry week and whenever a
              symbol cannot be found. Folder: <span className="mono">{data.directory}</span>
            </p>
          </>
        )}
      </QueryBoundary>
    </Panel>
  )
}

function MasterRow({
  m,
  live,
  current,
}: {
  m: InstrumentMasterStatus
  /** This job's result for the master, once it has been reached. */
  live: InstrumentMasterRefreshResult | null
  current: boolean
}) {
  const last = live ?? m.lastRefresh
  return (
    <tr>
      <td className="mono">
        {m.name}
        <span className="cell-sub">
          {m.exchange} · {m.segment}
        </span>
      </td>
      <td className="muted" style={{ whiteSpace: 'normal', maxWidth: 360 }}>
        {m.label}
      </td>
      <td className="r">
        {m.rowsInDb === 0 ? (
          <Badge tone="warn">none</Badge>
        ) : (
          <>
            {formatNumber(m.activeRowsInDb)}
            <span className="cell-sub">of {formatNumber(m.rowsInDb)} rows</span>
          </>
        )}
      </td>
      <td>
        {m.filePresent ? (
          <>
            {formatBytes(m.fileBytes)}
            <span className="cell-sub" title={formatDateTime(m.fileModifiedUtc)}>
              downloaded {formatAge(m.fileModifiedUtc)}
            </span>
          </>
        ) : (
          <Badge tone="warn">not downloaded</Badge>
        )}
      </td>
      <td>
        {current ? (
          <Badge tone="live">{last?.downloadedBytes ? 'importing…' : 'downloading…'}</Badge>
        ) : last ? (
          <RefreshSummary r={last} />
        ) : (
          <span className="faint">never</span>
        )}
      </td>
    </tr>
  )
}

function RefreshSummary({ r }: { r: InstrumentMasterRefreshResult }) {
  if (!r.ok) {
    return (
      <>
        <Badge tone="neg">failed</Badge>
        <span className="cell-sub" title={r.error ?? undefined}>
          {formatAge(r.finishedUtc ?? r.startedUtc)}
          {r.error ? ` · ${r.error}` : ''}
        </span>
      </>
    )
  }
  return (
    <>
      <Badge tone="pos">ok</Badge>
      <span className="cell-sub" title={r.message ?? undefined}>
        {formatAge(r.finishedUtc ?? r.startedUtc)}
        {r.by ? ` by ${r.by}` : ''} · {formatNumber(r.totalRowsRead)} rows · +{formatNumber(r.inserted)} new
        {r.updated > 0 ? ` · ${formatNumber(r.updated)} updated` : ''}
      </span>
    </>
  )
}

function formatBytes(n: number | null): string {
  if (n == null) return '—'
  if (n >= 1_048_576) return `${(n / 1_048_576).toFixed(1)} MB`
  if (n >= 1024) return `${Math.round(n / 1024)} KB`
  return `${n} B`
}
