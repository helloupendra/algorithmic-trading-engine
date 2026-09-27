import { describe, expect, it } from 'vitest'

import {
  activityResult,
  activityRows,
  alertRows,
  deployRows,
  logCounts,
  logSourceFrom,
  mergeLog,
  riskKindText,
  riskRows,
} from './systemLog'
import type { ActivityLogEntry, AlertEvent, DeployRecord, RiskEvent } from './types'

const activity = (over: Partial<ActivityLogEntry>): ActivityLogEntry => ({
  id: 1,
  occurredUtc: '2026-09-25T04:00:00Z',
  userId: 1,
  userName: 'admin',
  role: 'Admin',
  module: 'strategies',
  action: 'start',
  method: 'POST',
  path: '/api/Strategy/start',
  statusCode: 200,
  durationMs: 42,
  succeeded: true,
  targetType: null,
  targetId: null,
  summary: null,
  ipAddress: null,
  ...over,
})

const alert = (over: Partial<AlertEvent>): AlertEvent => ({
  id: 7,
  occurredUtc: '2026-09-25T05:00:00Z',
  source: 'strategy-runner',
  underlying: 'NIFTY',
  symbol: null,
  severity: 'Info',
  title: 'Run started',
  message: 'Strategy A on NIFTY, 2 lots',
  metadataJson: null,
  deliveredToTelegram: true,
  simulationRunId: 412,
  ...over,
})

const deploy = (over: Partial<DeployRecord>): DeployRecord => ({
  startedUtc: '2026-09-25T06:00:00Z',
  finishedUtc: '2026-09-25T06:00:40Z',
  outcome: 'applied',
  summary: 'Console rebuilt',
  fromCommit: 'abc1234',
  toCommit: 'def5678',
  commits: [],
  filesChanged: 3,
  steps: [],
  machine: 'desk-server',
  ...over,
})

const risk = (over: Partial<RiskEvent>): RiskEvent => ({
  id: 3,
  occurredUtc: '2026-09-25T07:00:00Z',
  kind: 'KillSwitchActivated',
  actorUserId: 1,
  actorName: 'admin',
  reason: 'Feed down',
  detailsJson: null,
  simulationRunId: null,
  symbol: null,
  ...over,
})

describe('logSourceFrom', () => {
  it('reads a stream from the URL, or all of them', () => {
    expect(logSourceFrom('deploys')).toBe('deploys')
    expect(logSourceFrom('risk')).toBe('risk')
    expect(logSourceFrom(null)).toBe('all')
    expect(logSourceFrom('logs')).toBe('all')
  })
})

describe('the streams as rows', () => {
  it("leads an activity row with the endpoint's own sentence, and keeps the request under it", () => {
    const [plain] = activityRows([activity({})])
    expect(plain).toMatchObject({ title: 'POST /api/Strategy/start', detail: 'Strategies · 42 ms', who: 'admin · Admin' })
    const [said] = activityRows([activity({ summary: 'Started Strategy A on NIFTY' })])
    expect(said.title).toBe('Started Strategy A on NIFTY')
    expect(said.detail).toBe('Strategies · POST /api/Strategy/start · 42 ms')
  })

  it('reads a refusal as a refusal, not as a failure', () => {
    expect(activityResult({ succeeded: false, statusCode: 403 })).toEqual({ label: '403 refused', tone: 'warn' })
    expect(activityResult({ succeeded: false, statusCode: 500 })).toEqual({ label: '500', tone: 'neg' })
    expect(activityResult({ succeeded: true, statusCode: 201 })).toEqual({ label: '201', tone: 'pos' })
  })

  it('says whether an alert reached Telegram or was only recorded', () => {
    expect(alertRows([alert({})])[0].detail).toBe('Strategy A on NIFTY, 2 lots · run #412 · sent to Telegram')
    expect(alertRows([alert({ deliveredToTelegram: false })])[0].detail).toContain('recorded only')
    expect(alertRows([alert({ severity: 'Error' })])[0].result).toEqual({ label: 'Error', tone: 'neg' })
  })

  it('reads a deploy by its outcome and the commits it moved between', () => {
    const [row] = deployRows([deploy({})])
    expect(row).toMatchObject({ title: 'Console rebuilt', detail: 'abc1234 → def5678 · 3 file(s) · took 40s', result: { label: 'Live', tone: 'pos' } })
    expect(deployRows([deploy({ outcome: 'failed' })])[0].result).toEqual({ label: 'Needs you', tone: 'neg' })
    // The Linux desk's record: "ok", and its notes joined by a bare ";".
    expect(deployRows([deploy({ outcome: 'ok', summary: 'console rebuilt;API rebuilt' })])[0]).toMatchObject({
      title: 'console rebuilt · API rebuilt',
      result: { label: 'Live', tone: 'pos' },
    })
  })

  it('names risk events in words, and the system when no one acted', () => {
    expect(riskKindText('KillSwitchActivated')).toBe('Kill switch activated')
    expect(riskKindText('limit_breached')).toBe('Limit breached')
    expect(riskRows([risk({ actorName: null })])[0]).toMatchObject({ title: 'Kill switch activated', who: 'system', detail: 'Feed down' })
  })
})

describe('mergeLog', () => {
  const rows = [
    ...activityRows([activity({})]),
    ...alertRows([alert({})]),
    ...deployRows([deploy({})]),
    ...riskRows([risk({})]),
  ]

  it('is one timeline, newest first', () => {
    expect(mergeLog(rows, 'all').map((r) => r.source)).toEqual(['risk', 'deploys', 'alerts', 'activity'])
  })

  it('keeps one stream, and every word of a search', () => {
    expect(mergeLog(rows, 'alerts').map((r) => r.key)).toEqual(['alert:7'])
    expect(mergeLog(rows, 'all', 'nifty lots').map((r) => r.key)).toEqual(['alert:7'])
    expect(mergeLog(rows, 'all', 'desk-server').map((r) => r.source)).toEqual(['deploys'])
    expect(mergeLog(rows, 'deploys', 'nifty')).toEqual([])
  })

  it('counts each stream for its tab', () => {
    expect(logCounts(rows)).toEqual({ all: 4, activity: 1, alerts: 1, deploys: 1, risk: 1 })
  })
})
