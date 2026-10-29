import { describe, expect, it } from 'vitest'

import { coverageColumnKey, coverageColumns, formatBars, indexLabel, indexTileSymbols, instrumentKind, instrumentName } from './symbols'

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

describe('instrumentName', () => {
  it('names the indices whose symbol spells them otherwise as the exchange does', () => {
    expect(instrumentName({ symbol: 'NSE:NIFTY50-INDEX', description: 'NIFTY50-INDEX' })).toBe('NIFTY 50')
    expect(instrumentName({ symbol: 'NSE:NIFTYBANK-INDEX', description: '' })).toBe('NIFTY BANK')
    expect(instrumentName({ symbol: 'nse:indiavix-index' })).toBe('INDIA VIX')
  })

  it("takes the master's description when it is a name, and not when it is the code again", () => {
    expect(instrumentName({ symbol: 'NSE:SBIN-EQ', description: 'STATE BANK OF INDIA' })).toBe('STATE BANK OF INDIA')
    expect(instrumentName({ symbol: 'NSE:SBIN-EQ', description: 'SBIN-EQ' })).toBe('SBIN')
    expect(instrumentName({ symbol: 'BSE:BANKEX-INDEX', description: 'bse:bankex-index' })).toBe('BANKEX')
    expect(instrumentName({ symbol: 'NSE:FINNIFTY-INDEX', description: null })).toBe('FINNIFTY')
  })

  it('reads an option off its symbol, as contracts are labelled elsewhere', () => {
    expect(instrumentName({ symbol: 'NSE:NIFTY26N1024500CE', description: 'NIFTY 10 Nov 26 24500 CE' })).toBe('NIFTY 24500 CE · 10 Nov')
    expect(instrumentName({ symbol: 'NSE:BANKNIFTY26NOV57600PE' })).toBe('BANKNIFTY 57600 PE · Nov')
  })

  it('falls back to the description, then the bare symbol, for anything else', () => {
    expect(instrumentName({ symbol: 'MCX:CRUDEOIL26NOVFUT', description: 'CRUDEOIL 19 Nov 26 FUT' })).toBe('CRUDEOIL 19 Nov 26 FUT')
    expect(instrumentName({ symbol: 'NSE:NIFTY26NOVFUT', description: '' })).toBe('NIFTY26NOVFUT')
  })
})

describe('instrumentKind', () => {
  it('says what an instrument is and where it trades', () => {
    expect(instrumentKind({ symbol: 'NSE:NIFTY50-INDEX', exchange: 'NSE' })).toBe('NSE index')
    expect(instrumentKind({ symbol: 'BSE:SENSEX-INDEX', exchange: 'BSE' })).toBe('BSE index')
    expect(instrumentKind({ symbol: 'NSE:SBIN-EQ', exchange: 'NSE' })).toBe('NSE equity')
    expect(instrumentKind({ symbol: 'NSE:NIFTY26N1024500CE', exchange: 'NSE' })).toBe('NSE option')
    expect(instrumentKind({ symbol: 'NSE:NIFTY26NOVFUT', exchange: 'NSE' })).toBe('NSE future')
  })

  it('reads an MCX contract as a future or an option, not only as a commodity', () => {
    expect(instrumentKind({ symbol: 'MCX:CRUDEOIL26NOVFUT', exchange: 'MCX' })).toBe('MCX future')
    expect(instrumentKind({ symbol: 'MCX:CRUDEOIL26NOV5000CE', exchange: 'MCX' })).toBe('MCX option')
  })

  it("calls a cash-market listing outside the -EQ series a share, and takes the exchange from the symbol when it is not given", () => {
    expect(instrumentKind({ symbol: 'BSE:RELIANCE-A', exchange: 'BSE', segment: 'CM' })).toBe('BSE equity')
    expect(instrumentKind({ symbol: 'NSE:SBIN-EQ' })).toBe('NSE equity')
    expect(instrumentKind({ symbol: 'XYZ' })).toBe('')
  })
})
