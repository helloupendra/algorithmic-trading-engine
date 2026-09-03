/**
 * Backtesting module — small backfill dialog for one underlying: pick the
 * resolutions and the date range, pull index candles from FYERS in 30-day
 * chunks (the API skips chunks it already holds) and show what arrived.
 */

import { useState } from 'react'
import { Link } from 'react-router-dom'
import { useBacktestBackfill } from '../../lib/queries'
import { formatNumber } from '../../lib/format'
import { resolutionLabel } from '../../lib/symbols'
import { InlineError } from '../../components/ui'
import { IconDownload, IconX } from '../../components/icons'
import { useDialogChrome } from '../strategies/shared'
import { addDays, todayIst } from './shared'

const RESOLUTIONS = ['1', '5', '15', 'D'] as const

export function BackfillDialog({
  underlying,
  spotSymbol,
  brokerLinked,
  onClose,
}: {
  underlying: string
  spotSymbol: string | null
  brokerLinked: boolean
  onClose: () => void
}) {
  const backfill = useBacktestBackfill()
  const cardRef = useDialogChrome(onClose)
  const today = todayIst()

  const [selected, setSelected] = useState<ReadonlySet<string>>(() => new Set(['5']))
  const [fromDate, setFromDate] = useState(addDays(today, -30))
  const [toDate, setToDate] = useState(today)

  function toggle(res: string) {
    setSelected((prev) => {
      const next = new Set(prev)
      if (next.has(res)) next.delete(res)
      else next.add(res)
      return next
    })
  }

  const rangeOk = !!fromDate && !!toDate && fromDate <= toDate && toDate <= today
  const canRun = brokerLinked && selected.size > 0 && rangeOk && !backfill.isPending

  return (
    <div
      className="modal"
      onMouseDown={(e) => {
        if (e.target === e.currentTarget) onClose()
      }}
    >
      <div
        className="modal__card modal__card--sm"
        role="dialog"
        aria-modal="true"
        aria-labelledby="backfill-title"
        tabIndex={-1}
        ref={cardRef}
      >
        <div className="modal__body">
          <div className="modal__head">
            <h2 className="modal__title" id="backfill-title">
              <IconDownload style={{ width: 15, height: 15, verticalAlign: '-2px' }} /> Backfill {underlying}
            </h2>
            <button
              type="button"
              className="btn btn--ghost btn--sm"
              onClick={onClose}
              aria-label="Close"
              title="Close (Esc)"
            >
              <IconX style={{ width: 14, height: 14 }} />
            </button>
          </div>

          <p className="card__muted" style={{ fontSize: 12.5 }}>
            Index candles for <span className="mono">{spotSymbol ?? underlying}</span> from FYERS, fetched
            in 30-day chunks; chunks already fully stored are skipped.
          </p>

          {!brokerLinked && (
            <div className="alert alert--warn" role="status">
              <span>
                FYERS is not linked — history cannot be fetched until the{' '}
                <Link to="/admin/broker">broker session</Link> is restored.
              </span>
            </div>
          )}

          <div className="field">
            <span className="field__label">Resolutions</span>
            <div className="chip-row">
              {RESOLUTIONS.map((res) => (
                <label key={res} className="coverage-chip" style={{ cursor: 'pointer' }}>
                  <input type="checkbox" checked={selected.has(res)} onChange={() => toggle(res)} />
                  {resolutionLabel(res)}
                </label>
              ))}
            </div>
          </div>

          <div className="form-row">
            <div className="field">
              <label className="field__label" htmlFor="bf-from">
                From
              </label>
              <input
                id="bf-from"
                className="field__input"
                type="date"
                max={today}
                value={fromDate}
                onChange={(e) => setFromDate(e.target.value)}
              />
            </div>
            <div className="field">
              <label className="field__label" htmlFor="bf-to">
                To
              </label>
              <input
                id="bf-to"
                className="field__input"
                type="date"
                max={today}
                value={toDate}
                onChange={(e) => setToDate(e.target.value)}
              />
            </div>
          </div>
          {!rangeOk && (
            <span className="field__help warn">The range must run forwards and end no later than today.</span>
          )}

          {backfill.isError && <InlineError error={backfill.error} />}
          {backfill.isSuccess && backfill.data && (
            <div className="alert alert--success" role="status" style={{ flexDirection: 'column', alignItems: 'flex-start', gap: 4 }}>
              <span>{backfill.data.message}</span>
              {backfill.data.perResolution.map((p) => (
                <span key={p.resolution} className="mono" style={{ fontSize: 12 }}>
                  {resolutionLabel(p.resolution)}: {formatNumber(p.candlesFetched)} candles ·{' '}
                  {formatNumber(p.chunks)} chunks ({formatNumber(p.skippedChunks)} already stored)
                </span>
              ))}
            </div>
          )}

          <div className="modal__foot">
            <button type="button" className="btn btn--ghost" onClick={onClose}>
              {backfill.isSuccess ? 'Done' : 'Cancel'}
            </button>
            <button
              type="button"
              className="btn btn--primary"
              disabled={!canRun}
              onClick={() =>
                backfill.mutate({ underlying, resolutions: [...selected], fromDate, toDate })
              }
            >
              <IconDownload style={{ width: 14, height: 14 }} />
              {backfill.isPending ? 'Fetching…' : 'Backfill'}
            </button>
          </div>
        </div>
      </div>
    </div>
  )
}
