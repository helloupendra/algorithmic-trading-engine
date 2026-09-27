import { describe, expect, it } from 'vitest'

import { coverageColumnKey, coverageColumns } from './symbols'

describe('coverageColumns', () => {
  const rows = [
    { resolution: 'D', source: 'backfill' },
    { resolution: '1m', source: 'live' },
    { resolution: '15', source: 'backfill' },
    { resolution: '1', source: 'backfill' },
    { resolution: '5', source: 'backfill' },
    { resolution: '1', source: 'backfill' },
  ]

  it('names the stored minute candles and the live minute bars apart, and never as a month', () => {
    expect(coverageColumns(rows).map((c) => c.label)).toEqual(['1 min', '1 min · live', '5 min', '15 min', 'Day'])
    // Uppercased by the table's heading style, none of them reads as "1M".
    expect(coverageColumns(rows).map((c) => c.label.toUpperCase())).not.toContain('1M')
  })

  it('keys a column by its source and resolution, as the matrix groups rows', () => {
    expect(coverageColumns(rows).map((c) => c.key)).toEqual(['backfill|1', 'live|1m', 'backfill|5', 'backfill|15', 'backfill|D'])
    expect(coverageColumnKey({ resolution: '1m', source: 'live' })).toBe('live|1m')
  })
})
