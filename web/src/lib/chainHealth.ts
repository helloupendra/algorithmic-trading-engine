import { formatAge } from './format'
import type { Tone } from './system'

export interface ChainWriterHealth {
  provider: string
  writer: string
  required: boolean
  enabled: boolean
  state: string
  captureWindowSeconds: number
  staleAfterSeconds: number
  lastCapturedUtc: string | null
  captureLagSeconds: number | null
  underlyings: { underlying: string; marketOpen: boolean; lastCapturedUtc: string | null; captureLagSeconds: number | null; state: string; lastOutcome: string | null }[]
}
export interface OptionChainHealth {
  selectedProvider: string
  state: string
  conflictingWriters: boolean
  checkedUtc: string
  writers: ChainWriterHealth[]
}
export function chainState(state: string): { label: string; tone: Tone | 'neutral' } {
  switch (state) {
    case 'healthy': return { label: 'Healthy', tone: 'pos' }
    case 'disabled': return { label: 'Disabled backup', tone: 'neutral' }
    case 'idle': return { label: 'Market closed', tone: 'neutral' }
    case 'stopped': return { label: 'Stopped', tone: 'neg' }
    case 'stale': return { label: 'Stale', tone: 'warn' }
    case 'degraded': return { label: 'Capture failed', tone: 'warn' }
    case 'conflict': return { label: 'Writer conflict', tone: 'warn' }
    case 'unconfigured': return { label: 'No underlyings', tone: 'warn' }
    default: return { label: 'Unknown', tone: 'neutral' }
  }
}
export function writerDetail(writer: ChainWriterHealth): string {
  const problems = writer.underlyings.filter(u => ['stale', 'stopped', 'degraded'].includes(u.state))
  return [writer.required ? 'Required writer' : 'Backup', writer.enabled ? 'enabled' : 'disabled',
    writer.lastCapturedUtc ? `last capture ${formatAge(writer.lastCapturedUtc)}` : writer.required || writer.enabled ? `no capture in the last ${writer.captureWindowSeconds / 60} min` : 'capture not required',
    `stale after ${writer.staleAfterSeconds}s while market open`,
    ...problems.map(u => `${u.underlying}: ${chainState(u.state).label.toLowerCase()}${u.lastOutcome ? ` (${u.lastOutcome})` : ''}`),
  ].join(' · ')
}
