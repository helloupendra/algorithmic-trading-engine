import { describe, expect, it } from 'vitest'

import {
  describePolicy,
  ec2Line,
  formatBytes,
  formatBytesPair,
  formatUptime,
  growthLine,
  policyJobProblem,
  usageTone,
} from './system'
import type { SystemHostReport, SystemTablePolicy } from './system'

const GB = 1024 ** 3

function report(growth: SystemHostReport['growth']): Pick<SystemHostReport, 'growth' | 'host'> {
  return {
    growth,
    host: {
      machineName: 'ip-172-31-0-1',
      os: 'Ubuntu 24.04',
      architecture: 'X64',
      procAvailable: true,
      disk: { path: '/', totalBytes: 38 * GB, usedBytes: 29 * GB, freeBytes: 8.5 * GB, usedPercent: 77 },
      memory: null,
      swap: null,
      cpu: { cores: 2, usedPercent: null, sampleSeconds: null },
      load: null,
      uptimeSeconds: null,
    },
  }
}

function growth(bytesPerDay: number | null, daysUntilFull: number | null): NonNullable<SystemHostReport['growth']> {
  return {
    computedForUtcDay: '2026-09-15',
    bytesPerDay,
    daysUntilFull,
    sampleDays: bytesPerDay == null ? [] : ['2026-09-14', '2026-09-11', '2026-09-10'],
    days: [],
    tables: [],
    basis: 'Estimate.',
    error: null,
  }
}

describe('usageTone', () => {
  it('is pos under 70%, warn from 70% to 85%, neg over 85%', () => {
    expect(usageTone(12)).toBe('pos')
    expect(usageTone(69.9)).toBe('pos')
    expect(usageTone(70)).toBe('warn')
    expect(usageTone(85)).toBe('warn')
    expect(usageTone(85.1)).toBe('neg')
  })

  it('gives no tone to a number nobody has', () => {
    expect(usageTone(null)).toBeUndefined()
    expect(usageTone(undefined)).toBeUndefined()
    expect(usageTone(Number.NaN)).toBeUndefined()
  })
})

describe('formatBytes', () => {
  it('uses the binary units df -h shows', () => {
    expect(formatBytes(38 * GB)).toBe('38.0 GB')
    expect(formatBytes(8.5 * GB)).toBe('8.5 GB')
    expect(formatBytes(512 * 1024 ** 2)).toBe('512 MB')
    expect(formatBytes(900)).toBe('900 B')
    expect(formatBytes(null)).toBe('—')
    expect(formatBytes(-1)).toBe('—')
  })
})

describe('formatBytesPair', () => {
  it('names the unit once when both sides share it', () => {
    expect(formatBytesPair(29.2 * GB, 37.7 * GB)).toBe('29.2 / 37.7 GB')
    expect(formatBytesPair(635 * 1024 ** 2, 2 * GB)).toBe('635 MB / 2.0 GB')
    expect(formatBytesPair(null, 2 * GB)).toBe('—')
  })
})

describe('formatUptime', () => {
  it('reads as days and hours, then hours and minutes', () => {
    expect(formatUptime(5 * 86_400 + 3 * 3600 + 120)).toBe('5d 3h')
    expect(formatUptime(3 * 3600 + 12 * 60)).toBe('3h 12m')
    expect(formatUptime(240)).toBe('4m')
    expect(formatUptime(null)).toBe('—')
  })
})

describe('describePolicy', () => {
  const base: SystemTablePolicy = {
    table: 'live_ticks',
    compressionEnabled: true,
    compressAfter: '1 day',
    compressionJob: { lastRunStatus: 'Success', lastSuccessUtc: null, totalFailures: 0 },
    dropAfter: '90 days',
    retentionJob: { lastRunStatus: 'Success', lastSuccessUtc: null, totalFailures: 0 },
  }

  it('says both policies in words', () => {
    expect(describePolicy(base)).toBe('compress after 1 day, drop after 90 days')
  })

  it('states "kept forever" and "no compression" instead of leaving a blank', () => {
    expect(describePolicy({ ...base, dropAfter: null, retentionJob: null })).toBe('compress after 1 day, kept forever')
    expect(describePolicy({ ...base, compressionEnabled: false, compressAfter: null, compressionJob: null })).toBe(
      'no compression, drop after 90 days',
    )
    expect(describePolicy({ ...base, compressAfter: null, compressionJob: null })).toBe(
      'compression on, no schedule, drop after 90 days',
    )
  })

  it('names a job whose last run did not succeed', () => {
    expect(policyJobProblem(base)).toBeNull()
    expect(policyJobProblem({ ...base, compressionJob: { lastRunStatus: 'Failed', lastSuccessUtc: null, totalFailures: 3 } })).toBe(
      'compression job last run: Failed',
    )
  })
})

describe('growthLine', () => {
  it('turns the 2026-09-15 morning into an alarm, not a footnote', () => {
    const line = growthLine(report(growth(6 * GB, 8.5 / 6)))
    expect(line.tone).toBe('neg')
    expect(line.text).toBe(
      'At about 6.0 GB per trading day (estimate from 14 Sept, 11 Sept, 10 Sept), this disk fills in ~1 trading day.',
    )
  })

  it('warns under a week and is quiet beyond it', () => {
    expect(growthLine(report(growth(2 * GB, 6.2))).tone).toBe('warn')
    expect(growthLine(report(growth(2 * GB, 6.2))).text).toContain('~6 trading days')
    expect(growthLine(report(growth(0.3 * GB, 40))).tone).toBeUndefined()
    expect(growthLine(report(growth(20 * GB, 0.4))).text).toContain('under a day')
  })

  it('says when there is no estimate rather than implying the disk never fills', () => {
    expect(growthLine(report(growth(null, null))).text).toMatch(/no growth estimate yet/)
    expect(growthLine(report(null)).text).toMatch(/could not be estimated/)
    expect(growthLine(report({ ...growth(null, null), error: 'timeout' })).text).toMatch(/could not be estimated/)
  })
})

describe('ec2Line', () => {
  it('joins type, zone and id, and is null off EC2', () => {
    expect(
      ec2Line({ instanceId: 'i-0abc', instanceType: 'c7i-flex.large', availabilityZone: 'ap-south-1a', region: 'ap-south-1' }),
    ).toBe('c7i-flex.large · ap-south-1a · i-0abc')
    expect(ec2Line(null)).toBeNull()
    expect(ec2Line({ instanceId: null, instanceType: null, availabilityZone: null, region: null })).toBeNull()
  })
})
