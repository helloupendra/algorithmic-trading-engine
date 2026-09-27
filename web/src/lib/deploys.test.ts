import { describe, expect, it } from 'vitest'

import { deployState, deploySummary } from './deploys'

describe('deployState', () => {
  it("reads both scripts' word for a deploy that went live", () => {
    expect(deployState('applied')).toBe('live')
    expect(deployState('ok')).toBe('live')
  })

  it('keeps a skip and a failure apart, and knows nothing it was not told', () => {
    expect(deployState('skipped')).toBe('skipped')
    expect(deployState('failed')).toBe('failed')
    expect(deployState('rolled-back')).toBeNull()
    expect(deployState(undefined)).toBeNull()
  })
})

describe('deploySummary', () => {
  it("reads desk.sh's notes as a list", () => {
    expect(deploySummary('console rebuilt;API rebuilt and restarted')).toBe('console rebuilt · API rebuilt and restarted')
    expect(deploySummary('nothing to rebuild (docs/scripts only)')).toBe('nothing to rebuild (docs/scripts only)')
    expect(deploySummary('a;;b')).toBe('a · b')
    // A note's own semicolon is followed by a space, and is the note's.
    expect(deploySummary('API rebuilt;engine changed: live runs keep the old code; new runs use the new')).toBe(
      'API rebuilt · engine changed: live runs keep the old code; new runs use the new',
    )
  })
})
