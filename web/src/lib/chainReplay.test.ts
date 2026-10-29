import { describe, expect, it } from 'vitest'
import {
  LIVE,
  captureBounds,
  laterDay,
  readReplayClock,
  replayClockAsOfUtc,
  replayDay,
  replayDayOptions,
  replayGapNote,
  replayMoment,
  sessionStartUtc,
  withReplayClock,
} from './chainReplay'
import type { OptionChainRecordedExpiry } from './types'

const TODAY = '2026-10-29'

describe('reading the replay clock from the URL', () => {
  it('reads a day and a minute', () => {
    expect(readReplayClock('?u=NIFTY&d=2026-10-14&at=11:00')).toEqual({ day: '2026-10-14', time: '11:00' })
    expect(readReplayClock(new URLSearchParams('d=2026-10-14'))).toEqual({ day: '2026-10-14', time: null })
    expect(readReplayClock('?u=NIFTY')).toEqual(LIVE)
  })

  it('reads anything that is not a real day or an hh:mm as absent, never as another moment', () => {
    expect(readReplayClock('?d=2026-02-30&at=25:00')).toEqual(LIVE)
    expect(readReplayClock('?d=14/10/2026&at=11:00:30')).toEqual(LIVE)
    expect(replayDay('2026-13-01')).toBeNull()
    expect(replayDay('2028-02-29')).toBe('2028-02-29')
    expect(replayDay(null)).toBeNull()
  })

  it('keeps an old link that names only a minute: it is on the newest session', () => {
    expect(readReplayClock('?at=10:41')).toEqual({ day: null, time: '10:41' })
    expect(replayClockAsOfUtc({ day: null, time: '10:41' }, TODAY)).toBe('2026-10-29T05:11:59.000Z')
    // Until the newest session is known there is no moment to ask for.
    expect(replayClockAsOfUtc({ day: null, time: '10:41' }, null)).toBeUndefined()
  })
})

describe('the moment a rewound view asks for', () => {
  it('is the end of the chosen minute, IST, on the chosen day', () => {
    // 11:00 IST on 14 Oct is 05:30 UTC; the capture taken during 11:00 is in.
    expect(replayClockAsOfUtc({ day: '2026-10-14', time: '11:00' }, TODAY)).toBe('2026-10-14T05:30:59.000Z')
  })

  it('is the end of a past day when no minute is chosen: its last capture', () => {
    expect(replayClockAsOfUtc({ day: '2026-10-14', time: null }, TODAY)).toBe('2026-10-14T18:29:59.000Z')
  })

  it('is nothing for live', () => {
    expect(replayClockAsOfUtc(LIVE, TODAY)).toBeUndefined()
  })

  it('starts one session at its IST midnight', () => {
    expect(sessionStartUtc('2026-10-14')).toBe('2026-10-13T18:30:00.000Z')
    expect(sessionStartUtc(null)).toBeUndefined()
    expect(sessionStartUtc('14 Oct')).toBeUndefined()
  })
})

describe('writing the replay clock into the URL', () => {
  const write = (search: string, day: string | null, time: string | null, newest: string | null = TODAY) =>
    withReplayClock(search, { day, time }, newest).toString()

  it('writes a past day, with or without a minute', () => {
    expect(write('u=NIFTY', '2026-10-14', '11:00')).toBe('u=NIFTY&at=11%3A00&d=2026-10-14')
    expect(write('u=NIFTY', '2026-10-14', null)).toBe('u=NIFTY&d=2026-10-14')
  })

  it('writes nothing for the newest session without a minute: that is live, the default', () => {
    expect(write('u=NIFTY&d=2026-10-14&at=11:00', TODAY, null)).toBe('u=NIFTY')
    expect(write('u=NIFTY&d=2026-10-14&at=11:00', null, null)).toBe('u=NIFTY')
  })

  it("writes a minute on today's session with its day, so the link names the same minute tomorrow", () => {
    expect(write('u=NIFTY', null, '11:00')).toBe('u=NIFTY&at=11%3A00&d=2026-10-29')
    // Before the newest session is known the minute goes alone, as old links did.
    expect(write('u=NIFTY', null, '11:00', null)).toBe('u=NIFTY&at=11%3A00')
  })

  it('drops the expiry when the day changes, and keeps it when only the minute does', () => {
    expect(write('u=NIFTY&expiry=2026-11-03', '2026-10-14', null)).toBe('u=NIFTY&d=2026-10-14')
    expect(write('u=NIFTY&expiry=2026-11-03', null, '11:00')).toBe('u=NIFTY&expiry=2026-11-03&at=11%3A00&d=2026-10-29')
    expect(write('u=NIFTY&expiry=2026-10-20&d=2026-10-14', '2026-10-14', '13:05')).toBe('u=NIFTY&expiry=2026-10-20&d=2026-10-14&at=13%3A05')
    // Back to live from a past day: that day's expiry has most likely expired.
    expect(write('u=NIFTY&expiry=2026-10-20&d=2026-10-14', null, null)).toBe('u=NIFTY')
  })

  it('reads back what it wrote', () => {
    const search = withReplayClock('u=BANKNIFTY', { day: '2026-10-14', time: '09:20' }, TODAY)
    expect(readReplayClock(search)).toEqual({ day: '2026-10-14', time: '09:20' })
  })
})

describe('the day picker', () => {
  it('lists the recorded days newest first and marks the newest session', () => {
    const options = replayDayOptions([TODAY, '2026-10-28', '2026-10-14'], TODAY, null, TODAY)
    expect(options.map((o) => o.label)).toEqual(['Thu 29 Oct · today', 'Wed 28 Oct', 'Wed 14 Oct'])
    expect(options.map((o) => o.date)).toEqual([TODAY, '2026-10-28', '2026-10-14'])
  })

  it("calls the newest session the latest before today's first capture", () => {
    expect(replayDayOptions(['2026-10-28'], '2026-10-28', null, TODAY)[0].label).toBe('Wed 28 Oct · latest')
  })

  it('lists the live session the list has not caught up with yet', () => {
    expect(replayDayOptions(['2026-10-28'], TODAY, null, TODAY).map((o) => o.label)).toEqual(['Thu 29 Oct · today', 'Wed 28 Oct'])
  })

  it('keeps a day a link named, and says so once the list answered without it', () => {
    expect(replayDayOptions(undefined, null, '2026-10-14', TODAY)).toEqual([{ date: '2026-10-14', label: 'Wed 14 Oct' }])
    expect(replayDayOptions(['2026-10-28'], '2026-10-28', '2026-10-03', TODAY).map((o) => o.label)).toEqual([
      'Wed 28 Oct · latest',
      'Sat 3 Oct · not recorded',
    ])
  })

  it("names the year of a day that is not this year's", () => {
    expect(replayDayOptions(['2025-12-31'], null, null, '2026-01-02')[0].label).toBe('Wed 31 Dec 2025')
  })

  it('takes the later of two days, either of which may be unknown', () => {
    expect(laterDay('2026-10-28', TODAY)).toBe(TODAY)
    expect(laterDay(TODAY, undefined)).toBe(TODAY)
    expect(laterDay(null, '')).toBeNull()
  })
})

describe('the minute bounds and the words', () => {
  const recorded = (expiryDate: string, first: string, last: string, isDefault: boolean): OptionChainRecordedExpiry => ({
    expiryDate,
    captures: 10,
    firstCapturedUtc: first,
    lastCapturedUtc: last,
    typicalIntervalSeconds: 60,
    isDefault,
  })
  const day = {
    expiries: [
      recorded('2026-11-03', '2026-10-29T03:45:10Z', '2026-10-29T09:59:40Z', true),
      recorded('2026-11-10', '2026-10-29T04:00:04Z', '2026-10-29T09:55:04Z', false),
    ],
  }

  it('bounds the minute by the shown expiry, else the nearest', () => {
    expect(captureBounds(day, '2026-11-10')).toEqual({ min: '09:30', max: '15:25' })
    expect(captureBounds(day, '2026-12-29')).toEqual({ min: '09:15', max: '15:29' })
    expect(captureBounds(undefined, '2026-11-03')).toEqual({})
  })

  it('says when the capture shown is from before the day rewound to', () => {
    expect(replayGapNote('2026-10-14', '09:20', '2026-10-13T09:59:00Z')).toBe(
      'Nothing of this expiry was captured on Wed 14 Oct by 09:20 IST: this is the last capture before it, 13 Oct 15:29 IST.',
    )
    expect(replayGapNote('2026-10-14', '11:00', '2026-10-14T05:30:00Z')).toBeNull()
    expect(replayGapNote('2026-10-14', null, null)).toBeNull()
    // A later capture is the live chain still on screen while the rewound one loads, not a gap.
    expect(replayGapNote('2026-10-14', null, '2026-10-29T05:30:00Z')).toBeNull()
  })

  it('puts the moment in words', () => {
    expect(replayMoment('2026-10-14', '11:00')).toBe('Wed 14 Oct 11:00 IST')
    expect(replayMoment('2026-10-14', null)).toBe('the end of Wed 14 Oct')
  })
})
