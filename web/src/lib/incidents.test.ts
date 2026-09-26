import { describe, expect, it } from 'vitest'

import {
  AGENT_SILENCE_MINUTES,
  agentSilenceMinutes,
  countFor,
  countTone,
  emptyMessage,
  incidentsQuery,
  listState,
  maskSecrets,
  occurrencesText,
  readIncidentList,
  resolveConfirmText,
  severityRank,
  silenceNote,
  sortNewestFirst,
  watchmanNote,
} from './incidents'
import type { Incident, IncidentSummary } from './types'

/**
 * The Incidents page's rules, pinned: loudest first, newest first without the
 * table reshuffling on every poll, and "not known" never shown as "nothing
 * wrong".
 */

function incident(over: Partial<Incident>): Incident {
  return {
    id: 1,
    fingerprint: 'feed-silent:dhan',
    agent: 'health',
    rule: 'feed-silent',
    severity: 'high',
    status: 'open',
    title: 'Dhan feed is connected but no ticks reach Redis',
    summary: '',
    location: 'dhan',
    evidence: [],
    suggestion: '',
    occurrences: 1,
    firstSeenUtc: '2026-09-24T05:57:35Z',
    lastSeenUtc: '2026-09-24T06:10:00Z',
    resolvedUtc: null,
    acknowledgedBy: null,
    acknowledgedUtc: null,
    ...over,
  }
}

describe('severityRank', () => {
  it('orders critical > high > medium > low', () => {
    const ranks = ['low', 'medium', 'high', 'critical'].map(severityRank)
    expect(ranks).toEqual([...ranks].sort((a, b) => a - b))
    expect(new Set(ranks).size).toBe(4)
  })

  it('puts a severity it does not know below low instead of throwing', () => {
    expect(severityRank('urgent')).toBeLessThan(severityRank('low'))
  })
})

describe('sortNewestFirst', () => {
  it('orders by when the problem was first seen, not last seen', () => {
    // Every live incident is re-seen on every check; sorting by last seen
    // would reshuffle the table under the reader each poll.
    const older = incident({ id: 1, firstSeenUtc: '2026-09-24T05:57:00Z', lastSeenUtc: '2026-09-24T09:00:30Z' })
    const newer = incident({ id: 2, firstSeenUtc: '2026-09-24T08:00:00Z', lastSeenUtc: '2026-09-24T09:00:00Z' })
    expect(sortNewestFirst([older, newer]).map((x) => x.id)).toEqual([2, 1])
  })

  it('puts the louder one first when two opened in the same check', () => {
    // Real stamps: Sentinel stamps each finding as it stores it, so one
    // check's incidents are milliseconds apart, never identical. An exact-time
    // tie-break would never fire.
    const low = incident({ id: 5, severity: 'low', firstSeenUtc: '2026-09-24T05:57:35.912345Z' })
    const critical = incident({ id: 3, severity: 'critical', firstSeenUtc: '2026-09-24T05:57:35.101234Z' })
    const medium = incident({ id: 4, severity: 'medium', firstSeenUtc: '2026-09-24T05:57:35.504321Z' })
    expect(sortNewestFirst([low, critical, medium]).map((x) => x.severity)).toEqual(['critical', 'medium', 'low'])
  })

  it('does not split one check across a second boundary', () => {
    const high = incident({ id: 1, severity: 'high', firstSeenUtc: '2026-09-24T05:57:59.998Z' })
    const critical = incident({ id: 2, severity: 'critical', firstSeenUtc: '2026-09-24T05:58:00.003Z' })
    expect(sortNewestFirst([high, critical]).map((x) => x.id)).toEqual([2, 1])
    // …and the louder one still leads when it is the older of the two.
    const critical2 = incident({ id: 3, severity: 'critical', firstSeenUtc: '2026-09-24T05:57:59.998Z' })
    const low = incident({ id: 4, severity: 'low', firstSeenUtc: '2026-09-24T05:58:00.003Z' })
    expect(sortNewestFirst([low, critical2]).map((x) => x.id)).toEqual([3, 4])
  })

  it('keeps separate checks in time order whatever their severity', () => {
    const oldCritical = incident({ id: 1, severity: 'critical', firstSeenUtc: '2026-09-24T05:57:00Z' })
    const newLow = incident({ id: 2, severity: 'low', firstSeenUtc: '2026-09-24T05:58:00Z' })
    expect(sortNewestFirst([oldCritical, newLow]).map((x) => x.id)).toEqual([2, 1])
  })

  it('does not reorder the array it was given', () => {
    const items = [incident({ id: 1, firstSeenUtc: '2026-09-24T01:00:00Z' }), incident({ id: 2 })]
    sortNewestFirst(items)
    expect(items.map((x) => x.id)).toEqual([1, 2])
  })
})

describe('incidentsQuery', () => {
  it('sends the status and a take, and leaves empty filters out', () => {
    const qs = new URLSearchParams(incidentsQuery({ status: 'live', agent: '', severity: '' }))
    expect(qs.get('status')).toBe('live')
    expect(qs.get('take')).toBe('200')
    expect(qs.has('agent')).toBe(false)
    expect(qs.has('severity')).toBe(false)
  })

  it('carries the agent and severity when chosen', () => {
    const qs = new URLSearchParams(incidentsQuery({ status: 'any', agent: 'trading', severity: 'critical', take: 50 }))
    expect(qs.get('agent')).toBe('trading')
    expect(qs.get('severity')).toBe('critical')
    expect(qs.get('take')).toBe('50')
  })
})

describe('readIncidentList', () => {
  it('reads a bare array and an { items } envelope', () => {
    expect(readIncidentList([incident({ id: 7 })]).map((x) => x.id)).toEqual([7])
    expect(readIncidentList({ items: [incident({ id: 8 })] }).map((x) => x.id)).toEqual([8])
  })

  it('throws on a body it cannot read, so the page shows an error and not an empty table', () => {
    // An HTML fallback page, a null, an object of the wrong shape: none of
    // these may render as "Nothing open".
    for (const body of ['<!doctype html>', null, undefined, { rows: [] }, 42]) {
      expect(() => readIncidentList(body)).toThrow()
    }
  })

  it('reads a genuinely empty list as empty', () => {
    expect(readIncidentList([])).toEqual([])
  })
})

describe('countFor and countTone', () => {
  const summary: IncidentSummary = {
    counts: { critical: 1, high: 0, medium: 2, low: 0 },
    newestTitle: null,
    newestUtc: null,
  }

  it('returns a real zero as zero and a missing count as null', () => {
    expect(countFor(summary, 'high')).toBe(0)
    expect(countFor(summary, 'critical')).toBe(1)
    expect(countFor(undefined, 'critical')).toBeNull()
    expect(countFor({ ...summary, counts: {} as IncidentSummary['counts'] }, 'low')).toBeNull()
  })

  it('colours only a non-zero count, so a quiet desk is not painted', () => {
    expect(countTone('critical', 1)).toBe('neg')
    expect(countTone('high', 3)).toBe('neg')
    expect(countTone('medium', 2)).toBe('warn')
    expect(countTone('low', 4)).toBeUndefined()
    expect(countTone('critical', 0)).toBeUndefined()
    expect(countTone('critical', null)).toBeUndefined()
  })
})

describe('emptyMessage', () => {
  it('says what the table holds, and never that Sentinel is checking', () => {
    // An empty table is not evidence that the watchman is awake; the page
    // cannot see a heartbeat, so it must not claim one.
    for (const view of ['live', 'resolved', 'any'] as const) {
      for (const agent of [undefined, 'health', 'trading', 'logs', 'security', 'feeds']) {
        expect(emptyMessage(view, agent)).not.toMatch(/checks|every \d|every minute/)
      }
    }
    expect(emptyMessage('live')).toBe('No open incidents recorded.')
  })

  it('names the agent when the list is filtered to one', () => {
    expect(emptyMessage('live', 'security')).toBe('No open incidents recorded from the security agent.')
    expect(emptyMessage('resolved', 'logs')).toBe('Nothing from the logs agent has been resolved yet.')
    expect(emptyMessage('any', 'trading')).toBe('The trading agent has not recorded anything yet.')
    expect(emptyMessage('live', 'feeds')).toBe('No open incidents recorded from the feeds agent.')
  })
})

describe('listState', () => {
  it('waits while the first answer is on its way', () => {
    expect(listState({ isPending: true, isError: false, data: undefined })).toBe('loading')
  })

  it('shows an error, not an empty table, when there is nothing to show', () => {
    expect(listState({ isPending: false, isError: true, data: undefined })).toBe('error')
    // An empty answer followed by a failed refresh is "not known", not "nothing open".
    expect(listState({ isPending: false, isError: true, data: [] })).toBe('error')
  })

  it('reads a successful empty answer as empty', () => {
    expect(listState({ isPending: false, isError: false, data: [] })).toBe('empty')
  })

  it('keeps rows on screen after a failed refresh (the page marks them old)', () => {
    expect(listState({ isPending: false, isError: true, data: [incident({})] })).toBe('rows')
    expect(listState({ isPending: false, isError: false, data: [incident({})] })).toBe('rows')
  })
})

describe('silenceNote', () => {
  const now = Date.parse('2026-09-24T06:00:00Z')

  it('says nothing when the API does not report Sentinel checks at all', () => {
    expect(silenceNote(undefined, now)).toBeNull()
  })

  it('warns when Sentinel has never reported a check', () => {
    expect(silenceNote(null, now)).toMatch(/proves nothing/)
  })

  it('stays quiet while checks are recent and speaks once they stop', () => {
    expect(silenceNote('2026-09-24T05:58:00Z', now)).toBeNull()
    expect(silenceNote('2026-09-24T05:48:00Z', now)).toMatch(/12 min ago/)
  })
})

describe('watchmanNote', () => {
  const asOf = Date.parse('2026-09-24T06:00:00Z')
  const fresh = '2026-09-24T05:59:30Z'
  const stale = '2026-09-24T05:48:00Z'

  it('says the page cannot tell when there is no heartbeat and nothing live', () => {
    // The day Sentinel ships, or any day it has crashed: four zeros must not
    // read as a quiet desk.
    const note = watchmanNote({ lastCheckUtc: undefined, liveRows: [], asOfMs: asOf })
    expect(note?.tone).toBe('unknown')
    expect(note?.text).toMatch(/no heartbeat/)
  })

  it('stays quiet while every live incident is being re-seen', () => {
    const rows = [incident({ id: 1, lastSeenUtc: fresh }), incident({ id: 2, agent: 'trading', lastSeenUtc: fresh })]
    expect(watchmanNote({ lastCheckUtc: undefined, liveRows: rows, asOfMs: asOf })).toBeNull()
  })

  it('warns that Sentinel has stopped when no live incident has been re-seen', () => {
    const rows = [
      incident({ id: 1, lastSeenUtc: stale }),
      incident({ id: 2, agent: 'trading', status: 'acknowledged', lastSeenUtc: '2026-09-24T05:50:00Z' }),
    ]
    const note = watchmanNote({ lastCheckUtc: undefined, liveRows: rows, asOfMs: asOf })
    expect(note?.tone).toBe('warn')
    expect(note?.text).toMatch(/not re-checked any open incident for 10 min/)
  })

  it('names the one agent that stopped while the others run', () => {
    const rows = [
      incident({ id: 1, agent: 'health', lastSeenUtc: fresh }),
      incident({ id: 2, agent: 'security', lastSeenUtc: stale }),
    ]
    const note = watchmanNote({ lastCheckUtc: undefined, liveRows: rows, asOfMs: asOf })
    expect(note?.tone).toBe('warn')
    expect(note?.text).toMatch(/^The security agent has not re-checked 1 open incident for 12 min/)
  })

  it('says nothing while the live list is unknown', () => {
    expect(watchmanNote({ lastCheckUtc: undefined, liveRows: undefined, asOfMs: asOf })).toBeNull()
  })

  it('lets a heartbeat decide once the API sends one', () => {
    // A recent round that found nothing is a real zero.
    expect(watchmanNote({ lastCheckUtc: fresh, liveRows: [], asOfMs: asOf })).toBeNull()
    expect(watchmanNote({ lastCheckUtc: stale, liveRows: [], asOfMs: asOf })?.text).toMatch(/12 min ago/)
    // Never reported: the day it ships, before its first round.
    expect(watchmanNote({ lastCheckUtc: null, liveRows: [], asOfMs: asOf })?.text).toMatch(/proves nothing/)
  })

  it('still names a dead agent when the heartbeat is fresh', () => {
    // The heartbeat is the newest round of any agent; one agent can stop
    // while the others keep it fresh.
    const rows = [incident({ id: 1, agent: 'logs', lastSeenUtc: stale })]
    const note = watchmanNote({ lastCheckUtc: fresh, liveRows: rows, asOfMs: asOf })
    expect(note?.tone).toBe('warn')
    expect(note?.text).toMatch(/^The logs agent has not re-checked/)
  })

  it('gives the security agent, which looks every five minutes, longer before it calls it stopped', () => {
    // Re-seen on schedule every five minutes, it must not read as "may have crashed" at five.
    const nineMinAgo = '2026-09-24T05:51:00Z'
    const security = [incident({ id: 1, agent: 'security', lastSeenUtc: nineMinAgo })]
    expect(watchmanNote({ lastCheckUtc: fresh, liveRows: security, asOfMs: asOf })).toBeNull()
    const health = [incident({ id: 2, agent: 'health', lastSeenUtc: nineMinAgo })]
    expect(watchmanNote({ lastCheckUtc: fresh, liveRows: health, asOfMs: asOf })?.text).toMatch(
      /^The health agent has not re-checked 1 open incident for 9 min/,
    )
  })

  it('judges the rows by when they arrived, not by the wall clock', () => {
    // A tab asleep for 20 min wakes with the old answer still on screen.
    const rows = [incident({ id: 1, lastSeenUtc: fresh })]
    const answeredAt = asOf
    const wokeAt = asOf + 20 * 60_000
    expect(watchmanNote({ lastCheckUtc: undefined, liveRows: rows, asOfMs: wokeAt, liveAsOfMs: answeredAt })).toBeNull()
  })
})

describe('agentSilenceMinutes', () => {
  // The interval_seconds each agent declares in sentinel/agents/*.py.
  const CADENCE_SECONDS: Record<string, number> = { health: 30, trading: 60, logs: 30, security: 300 }

  it('allows every agent at least two checks and a minute before it is called stopped', () => {
    for (const [agent, seconds] of Object.entries(CADENCE_SECONDS)) {
      expect(agentSilenceMinutes(agent) * 60).toBeGreaterThanOrEqual(2 * seconds + 60)
    }
    expect(agentSilenceMinutes('security')).toBeGreaterThanOrEqual(11)
    expect(Object.keys(AGENT_SILENCE_MINUTES).sort()).toEqual(Object.keys(CADENCE_SECONDS).sort())
  })

  it('gives an agent it does not know the longest wait, not a false alarm', () => {
    expect(agentSilenceMinutes('newagent')).toBe(Math.max(...Object.values(AGENT_SILENCE_MINUTES)))
    expect(agentSilenceMinutes(undefined)).toBe(Math.max(...Object.values(AGENT_SILENCE_MINUTES)))
  })
})

describe('resolveConfirmText', () => {
  it('warns that a problem still present comes back as a new incident with a new alert', () => {
    const text = resolveConfirmText({ id: 12, title: 'Dhan feed silent' })
    expect(text).toMatch(/#12/)
    expect(text).toMatch(/new incident/)
    expect(text).toMatch(/alert/)
  })
})

// The shared redaction spec as cases. The same table is in Sentinel's
// tests/test_sentinel_notify.py and the API's IncidentsControllerTests.cs: a
// case added here is added there.
const MASKED: ReadonlyArray<[string, string]> = [
  ['POSTGRES_PASSWORD=hunter2hunter2', 'POSTGRES_PASSWORD=…'],
  ['TELEGRAM_BOT_TOKEN=abcdefghij', 'TELEGRAM_BOT_TOKEN=…'],
  ['DHAN_PIN=1234', 'DHAN_PIN=…'],
  ['FYERS_SECRET_KEY=ABCD1234XYZ', 'FYERS_SECRET_KEY=…'],
  ['JWT_SECRET_KEY=supersecretjwtkey', 'JWT_SECRET_KEY=…'],
  ['DHAN_API_SECRET=abcdef123', 'DHAN_API_SECRET=…'],
  ['ANGEL_API_KEY=abcdef12', 'ANGEL_API_KEY=…'],
  ['{"trading_pin": "4821"}', '{"trading_pin": "…"}'],
  ['"access_token": "abcDEF123456"', '"access_token": "…"'],
  ['refreshToken=Zm9vYmFyYmF6', 'refreshToken=…'],
  ['X-Api-Key: abcd1234', 'X-Api-Key: …'],
  ['Host=db;Password=pa55word;Database=algotrading', 'Host=db;Password=…;Database=algotrading'],
  ['GET /login?client_secret=XYZ987654&state=1', 'GET /login?client_secret=…&state=1'],
  ['TOTP=123456', 'TOTP=…'],
  ['password="correct horse battery"', 'password="…"'],
  ['postgresql://postgres:S3cretPassw0rd@localhost:5432/algotrading', 'postgresql://postgres:…@localhost:5432/algotrading'],
  ['redis://:S3cretPassw0rd@localhost:6379/0', 'redis://:…@localhost:6379/0'],
  ['Authorization: Basic dXNlcjpTM2NyZXRQYXNzdzByZA==', 'Authorization: Basic …'],
  ["curl -H 'Authorization: Bearer abcdefghijklmnop'", "curl -H 'Authorization: Bearer …'"],
  ["headers={'Authorization': 'Bearer abc.def.ghi-jkl'}", "headers={'Authorization': 'Bearer …'}"],
  ['Bearer abcdefghijklmnopqrstuvwxyz123', 'Bearer …'],
  ['url: /bot8123456789:AAHdqTcvCH1vGWJxfSeofSAs0K5PALDsaw0/sendMessage', 'url: /bot…/sendMessage'], // pragma: allowlist secret
  ['chat 123456789:AAHdqTcvCH1vGWJxfSeofSAs0K5PALDsaw0 said', 'chat … said'], // pragma: allowlist secret
  ['token eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJhZG1pbiIsImV4cCI6MX0.c2lnbmF0dXJlLXZhbHVlLWhlcmU expired', 'token … expired'], // pragma: allowlist secret
]

// The desk's own prose. A mask that garbles it is a failure too.
const LEFT_ALONE: readonly string[] = [
  "Generate a new Dhan token before tomorrow's 08:45 start",
  'SSH password guessing from 1.2.3.4 was banned',
  "Fyers rejected the desk's credential (token expired or invalid)",
  'FYERS token expired at 08:45; strategies are running deaf.',
  'Password reset for user coderforchange',
  "Sentinel's secret scan found a key in config/x.json",
  'Generate a new Dhan token\n\nEvidence:\n• feed silent',
  'Skipping: 3 runners already stopped',
  'input_tokens: 512',
  'spinning=3',
  'if token == expected:',
  'Dhan feed: 8 reconnect(s) carried no ticks — waiting 80s',
  '429 Client Error: Too Many Requests for url: http://localhost:5025/api/UserAuth/login',
  'https://example.com:8443/path',
  'NSE feed silent for 120 s; newest tick 11:27:35 IST on NSE:NIFTY50-INDEX',
]

describe('maskSecrets', () => {
  it.each(MASKED)('masks %s and keeps its label', (text, expected) => {
    expect(maskSecrets(text)).toBe(expected)
    expect(maskSecrets(expected)).toBe(expected) // masking twice is masking once
  })

  it.each(LEFT_ALONE)('leaves the desk\'s own prose alone: %s', (text) => {
    expect(maskSecrets(text)).toBe(text)
  })

  it('hides tokens and passwords a crash traceback can carry', () => {
    const jwt = 'eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJhZG1pbiIsImV4cCI6MX0.c2lnbmF0dXJlLXZhbHVlLWhlcmU' // pragma: allowlist secret
    const cases = [
      `requests.exceptions.HTTPError: 401 headers={'Authorization': 'Bearer ${jwt}'}`,
      `token=${jwt}`,
      'psycopg2.OperationalError: password=Sup3rS3cret! host=localhost',
      'Host=db;Username=algo;Password=Sup3rS3cret!;Database=trading',
      'postgresql://algo:Sup3rS3cret!@localhost:5432/trading',
      '{"password": "Sup3rS3cret!"}',
      'https://api.telegram.org/bot123456789:AAHdqTcvCH1vGWJxfSeofSAs0K5PALDsaw0/sendMessage', // pragma: allowlist secret
    ]
    for (const text of cases) {
      const masked = maskSecrets(text)
      expect(masked).not.toContain('Sup3rS3cret')
      expect(masked).not.toContain(jwt.slice(0, 20))
      expect(masked).not.toContain('AAHdqTcvCH1vGWJxfSeofSAs0K5PALDsaw0')
      expect(masked).toContain('…')
    }
  })

  it('stays quick on a long line', () => {
    const started = performance.now()
    maskSecrets('token_'.repeat(20_000) + ' ' + 'a'.repeat(100_000))
    expect(performance.now() - started).toBeLessThan(500)
  })
})

describe('occurrencesText', () => {
  it('reads as a count a trader says out loud', () => {
    expect(occurrencesText(1)).toBe('once')
    expect(occurrencesText(12)).toBe('12 times')
    expect(occurrencesText(0)).toBe('—')
  })
})
