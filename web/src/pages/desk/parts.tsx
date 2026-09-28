/**
 * The Desk's small parts: a panel's head, the quiet "waiting" and the brief
 * "could not read" every panel uses, and the marks drawn inline (a trace, a
 * rail, a range meter, a sentiment bar). Drawn as plain SVG: each is a few
 * lines, far cheaper than a chart library for a 60px mark.
 */

import type { ReactNode } from 'react'
import { Link } from 'react-router-dom'
import { compactInr } from '../../lib/desk'
import { toneClass } from './data'
import { formatInrSigned } from '../../lib/format'

export function PanelHead({ title, meta, tools, more }: {
  title: string
  meta?: ReactNode
  /** A small control kept at the right, before the "more" link. */
  tools?: ReactNode
  more?: { to: string; label: string } | null
}) {
  return (
    <div className="dk-hd">
      <h2>{title}</h2>
      {meta && <span className="dk-meta">{meta}</span>}
      {tools && <span className="dk-tools">{tools}</span>}
      {more && (
        <Link className="dk-more" to={more.to}>
          {more.label} →
        </Link>
      )}
    </div>
  )
}

/** Data not here yet: said quietly, never shown as a zero. */
export function Waiting({ children }: { children: ReactNode }) {
  return <p className="dk-wait">{children}</p>
}

function failText(error: unknown): string {
  const status = (error as { status?: number } | null)?.status
  if (status === 403) return 'not available to this account'
  if (status === 404) return 'not on this server yet'
  return 'could not be read; trying again'
}

/** A request that failed, briefly: what, and why when the status says. */
export function Failed({ what, error }: { what: string; error: unknown }) {
  return (
    <p className="dk-fail" role="status">
      {what}: {failText(error)}.
    </p>
  )
}

/** An account's colour, as the short rule beside its name. */
export function Swatch({ tone, cell = false }: { tone: 1 | 2 | null; cell?: boolean }) {
  return <i className={`dk-sw${tone ? ` dk-sw--${tone}` : ''}${cell ? ' dk-sw--cell' : ''}`} aria-hidden="true" />
}

export function Chip({ tone, title, children }: { tone?: 'pos' | 'neg' | 'warn' | 'live' | 'brand'; title?: string; children: ReactNode }) {
  return (
    <span className={`dk-chip${tone ? ` dk-chip--${tone}` : ''}`} title={title}>
      {children}
    </span>
  )
}

export function Dot({ tone }: { tone?: 'pos' | 'neg' | 'warn' | 'live' | 'info' | null }) {
  return <i className={`dk-dot${tone && tone !== 'info' ? ` dk-dot--${tone}` : ''}`} aria-hidden="true" />
}

/** A net figure: in full ("+₹2,551"), or dense ("+2.6k") with the full figure in its title. */
export function Money({ value, compact = false, className = '' }: { value: number; compact?: boolean; className?: string }) {
  return (
    <span className={`dk-n ${toneClass(value)} ${className}`.trim()} title={compact ? formatInrSigned(value) : undefined}>
      {compact ? compactInr(value) : formatInrSigned(value)}
    </span>
  )
}

/** A small intraday trace against the previous close (dashed). */
export function Spark({ values, base, width = 62, height = 20 }: { values: number[]; base?: number | null; width?: number; height?: number }) {
  if (values.length < 2) return <svg width={width} height={height} aria-hidden="true" />
  const lo = Math.min(...values, base ?? Infinity)
  const hi = Math.max(...values, base ?? -Infinity)
  const x = (i: number) => (i / (values.length - 1)) * (width - 2) + 1
  const y = (v: number) => height - 2 - ((v - lo) / (hi - lo || 1)) * (height - 4)
  const d = values.map((v, i) => `${i ? 'L' : 'M'}${x(i).toFixed(1)} ${y(v).toFixed(1)}`).join('')
  const up = values[values.length - 1] >= (base ?? values[0])
  return (
    <svg width={width} height={height} style={{ display: 'block' }} aria-hidden="true">
      {base != null && <line x1="0" x2={width} y1={y(base)} y2={y(base)} stroke="var(--line-strong)" strokeDasharray="2 2" />}
      <path d={d} fill="none" stroke={up ? 'var(--pos)' : 'var(--neg)'} strokeWidth="1.3" strokeLinejoin="round" />
    </svg>
  )
}

/** The range so far against the forecast: the 80% band as a track, the median as a tick, the fill as far as today has gone. */
export function RangeMeter({ soFar, median, low80, high80, width = 64 }: { soFar: number | null; median: number; low80: number; high80: number; width?: number }) {
  const max = high80 * 1.08
  const x = (v: number) => (Math.min(v, max) / max) * (width - 4) + 2
  return (
    <svg width={width} height="12" style={{ display: 'block', flex: 'none' }} aria-hidden="true">
      <line x1={x(low80)} x2={x(high80)} y1="6" y2="6" stroke="var(--line-strong)" strokeWidth="5" />
      {soFar != null && <line x1="2" x2={x(soFar)} y1="6" y2="6" stroke="var(--brand)" strokeWidth="2" />}
      <line x1={x(median)} x2={x(median)} y1="1" y2="11" stroke="var(--text-2)" strokeWidth="1.2" />
    </svg>
  )
}

/** The news model's reading, −1 … +1: a bar from the middle, and the number. Null reads "not scored". */
export function Sentiment({ value }: { value: number | null }) {
  if (value == null) {
    return (
      <span className="dk-senti dk-t3" title="Not scored yet">
        —
      </span>
    )
  }
  const w = 28
  const c = w / 2
  const end = c + value * (c - 1)
  const colour = value > 0.15 ? 'var(--pos)' : value < -0.15 ? 'var(--neg)' : 'var(--text-3)'
  const text = `${value > 0 ? '+' : value < 0 ? '−' : ''}${Math.abs(value).toFixed(1)}`
  return (
    <span className="dk-senti" title={`Model reading ${text}: the words, not a signal`}>
      <svg width={w} height="8" aria-hidden="true">
        <line x1="1" x2={w - 1} y1="4" y2="4" stroke="var(--line-strong)" strokeWidth="2" />
        <line x1={c} x2={end} y1="4" y2="4" stroke={colour} strokeWidth="2.5" />
        <line x1={c} x2={c} y1="1" y2="7" stroke="var(--text-3)" />
      </svg>
      <b style={{ color: colour }}>{text}</b>
    </span>
  )
}
