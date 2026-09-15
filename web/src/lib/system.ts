/**
 * The System page's server report (GET /api/System/host) and the wording the
 * page puts on it. Pure, so the thresholds and the sentences are tested rather
 * than eyeballed.
 *
 * Every number here may be null: /proc does not exist on a Mac, the instance
 * metadata does not answer off EC2, the database can be down. Null renders as
 * "not available", never as 0 — a zero on this page would read as "fine".
 */

export interface SystemHostReport {
  generatedUtc: string
  cacheSeconds: number
  host: {
    machineName: string
    os: string
    architecture: string
    procAvailable: boolean
    disk: { path: string; totalBytes: number; usedBytes: number; freeBytes: number; usedPercent: number | null } | null
    memory: { totalBytes: number; availableBytes: number; usedBytes: number; usedPercent: number } | null
    swap: { totalBytes: number; usedBytes: number; usedPercent: number | null } | null
    cpu: { cores: number; usedPercent: number | null; sampleSeconds: number | null }
    load: { one: number; five: number; fifteen: number } | null
    uptimeSeconds: number | null
  }
  ec2: { instanceId: string | null; instanceType: string | null; availabilityZone: string | null; region: string | null } | null
  api: {
    startedUtc: string
    uptimeSeconds: number
    workingSetBytes: number
    managedHeapBytes: number
    threads: number
    version: string | null
  }
  database: {
    sizeBytes: number | null
    timescaleDb: boolean
    tables: SystemTableSize[]
    policies: SystemTablePolicy[]
    error: string | null
  }
  growth: {
    computedForUtcDay: string
    bytesPerDay: number | null
    daysUntilFull: number | null
    sampleDays: string[]
    days: { day: string; bytes: number }[]
    tables: { table: string; bytesPerDay: number | null; method: string }[]
    basis: string
    error: string | null
  } | null
  archive: {
    tables: {
      table: string
      lastVerifiedDay: string | null
      verifiedDays: number
      unverifiedDays: number
      lastArchivedUtc: string | null
    }[]
    unverifiedDays: number
    unreadableLines: number
    manifestUpdatedUtc: string
  } | null
}

export interface SystemTableSize {
  name: string
  bytes: number
  isHypertable: boolean
  compression: { totalChunks: number; compressedChunks: number; beforeBytes: number | null; afterBytes: number | null } | null
}

export interface SystemJobRun {
  lastRunStatus: string | null
  lastSuccessUtc: string | null
  totalFailures: number | null
}

export interface SystemTablePolicy {
  table: string
  compressionEnabled: boolean
  compressAfter: string | null
  compressionJob: SystemJobRun | null
  dropAfter: string | null
  retentionJob: SystemJobRun | null
}

export type Tone = 'pos' | 'warn' | 'neg'

/**
 * Disk and memory: under 70% is fine, 70–85% wants watching, over 85% wants
 * action. Null stays null — no tone for a number nobody has.
 */
export function usageTone(percent: number | null | undefined): Tone | undefined {
  if (percent == null || !Number.isFinite(percent)) return undefined
  if (percent > 85) return 'neg'
  if (percent >= 70) return 'warn'
  return 'pos'
}

/**
 * "38.7 GB", "512 MB". Binary units with the labels df -h uses, so the page
 * and a terminal on the server give the same number for the same disk.
 */
export function formatBytes(bytes: number | null | undefined, digits = 1): string {
  if (bytes == null || !Number.isFinite(bytes) || bytes < 0) return '—'
  const units = ['B', 'KB', 'MB', 'GB', 'TB']
  let value = bytes
  let unit = 0
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024
    unit++
  }
  const shown = unit === 0 ? String(Math.round(value)) : value.toFixed(value >= 100 ? 0 : digits)
  return `${shown} ${units[unit]}`
}

/** "29.2 / 37.7 GB" when both sides share a unit, "635 MB / 2.0 GB" when they do not. */
export function formatBytesPair(used: number | null | undefined, total: number | null | undefined): string {
  const a = formatBytes(used)
  const b = formatBytes(total)
  if (a === '—' || b === '—') return '—'
  const unitA = a.split(' ')[1]
  const unitB = b.split(' ')[1]
  return unitA === unitB ? `${a.split(' ')[0]} / ${b}` : `${a} / ${b}`
}

/** "5d 3h", "3h 12m", "4m" — for machine and process uptimes. */
export function formatUptime(seconds: number | null | undefined): string {
  if (seconds == null || !Number.isFinite(seconds) || seconds < 0) return '—'
  const s = Math.floor(seconds)
  const d = Math.floor(s / 86_400)
  const h = Math.floor((s % 86_400) / 3600)
  const m = Math.floor((s % 3600) / 60)
  if (d > 0) return `${d}d ${h}h`
  if (h > 0) return `${h}h ${m}m`
  return `${m}m`
}

/**
 * "compress after 2 days, drop after 90 days" — a hypertable's TimescaleDB
 * policies in words. A table with neither says so: keeping everything forever
 * is a decision the operator should see stated, not infer from a blank.
 */
export function describePolicy(policy: SystemTablePolicy): string {
  const parts: string[] = []
  if (policy.compressAfter) parts.push(`compress after ${policy.compressAfter}`)
  else if (policy.compressionEnabled) parts.push('compression on, no schedule')
  else parts.push('no compression')

  parts.push(policy.dropAfter ? `drop after ${policy.dropAfter}` : 'kept forever')
  return parts.join(', ')
}

/** A policy job whose last run did not succeed, named for the page; null when healthy or absent. */
export function policyJobProblem(policy: SystemTablePolicy): string | null {
  const problems: string[] = []
  for (const [label, job] of [
    ['compression', policy.compressionJob],
    ['retention', policy.retentionJob],
  ] as const) {
    if (!job) continue
    if (job.lastRunStatus && job.lastRunStatus !== 'Success') problems.push(`${label} job last run: ${job.lastRunStatus}`)
  }
  return problems.length ? problems.join('; ') : null
}

export interface GrowthLine {
  text: string
  tone: Tone | undefined
}

const shortDay = new Intl.DateTimeFormat('en-IN', { day: 'numeric', month: 'short', timeZone: 'UTC' })

/**
 * "At about 2.1 GB per trading day, this disk fills in ~6 trading days."
 * Warn under a week, alarm under three days. Says plainly when there is no
 * estimate instead of implying the disk will never fill.
 */
export function growthLine(report: Pick<SystemHostReport, 'growth' | 'host'>): GrowthLine {
  const growth = report.growth
  if (!growth || growth.error) {
    return { text: 'Daily growth could not be estimated (the database did not answer).', tone: undefined }
  }
  if (growth.bytesPerDay == null) {
    return { text: 'No closed day in the last 10 recorded data, so there is no growth estimate yet.', tone: undefined }
  }

  const rate = `about ${formatBytes(growth.bytesPerDay)} per trading day`
  const from = growth.sampleDays.length
    ? ` (estimate from ${growth.sampleDays.map((d) => shortDay.format(new Date(`${d}T00:00:00Z`))).join(', ')})`
    : ' (estimate)'

  const days = growth.daysUntilFull
  if (days == null) {
    return { text: `The database grows by ${rate}${from}; the disk's free space is not known.`, tone: undefined }
  }

  const n = days < 1 ? 'under a day' : `~${Math.floor(days)} trading day${Math.floor(days) === 1 ? '' : 's'}`
  const tone: Tone | undefined = days < 3 ? 'neg' : days < 7 ? 'warn' : undefined
  return { text: `At ${rate}${from}, this disk fills in ${n}.`, tone }
}

/** "c7i-flex.large · ap-south-1a · i-0abc…" or null when this is not an EC2 host. */
export function ec2Line(ec2: SystemHostReport['ec2']): string | null {
  if (!ec2) return null
  const parts = [ec2.instanceType, ec2.availabilityZone, ec2.instanceId].filter((p): p is string => !!p)
  return parts.length ? parts.join(' · ') : null
}
