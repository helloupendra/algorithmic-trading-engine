import { describe, expect, it } from 'vitest'

import { coverageColumnKey, coverageColumns, formatBars, indexLabel, indexTileSymbols } from './symbols'

describe('indexTileSymbols', () => {
  it('keeps the well-known indices first and adds every other index on the recording list', () => {
    const preferred = ['NSE:NIFTYBANK-INDEX', 'NSE:NIFTY50-INDEX']
    const watched = ['NSE:SBIN-EQ', 'NSE:MIDCPNIFTY-INDEX', 'NSE:NIFTY50-INDEX', 'BSE:BANKEX-INDEX', 'NSE:NIFTY2591123500CE']
    expect(indexTileSymbols(preferred, watched)).toEqual([
      'NSE:NIFTYBANK-INDEX',
      'NSE:NIFTY50-INDEX',
      'BSE:BANKEX-INDEX',
      'NSE:MIDCPNIFTY-INDEX',
    ])
  })

  it('labels an index the code never named from its symbol', () => {
    expect(indexLabel('BSE:BANKEX-INDEX')).toBe('BANKEX')
    expect(indexLabel('NSE:INDIAVIX-INDEX')).toBe('INDIAVIX')
    expect(indexLabel('MIDCPNIFTY')).toBe('MIDCPNIFTY')
  })
})

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

  it('gives each column a short heading for a phone, still telling live from stored', () => {
    expect(coverageColumns(rows).map((c) => c.short)).toEqual(['1m', '1m live', '5m', '15m', 'D'])
  })
})

describe('formatBars', () => {
  it('says bar for one and bars otherwise, with Indian grouping', () => {
    expect(formatBars(1)).toBe('1 bar')
    expect(formatBars(0)).toBe('0 bars')
    expect(formatBars(2969)).toBe('2,969 bars')
    expect(formatBars(2037921)).toBe('20,37,921 bars')
  })
})
