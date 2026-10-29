import { describe, expect, it } from 'vitest'

import { csvField, csvFileName, fileStamp, istIsoTime, toCsv } from './csv'
import type { CsvColumn } from './csv'

describe('csvField', () => {
  it('leaves a plain text as it is', () => {
    expect(csvField('NIFTY')).toBe('NIFTY')
    expect(csvField('Manual book')).toBe('Manual book')
  })

  it('quotes a text holding a comma, a quote or a line break, doubling its quotes', () => {
    expect(csvField('Stop loss hit: P&L −₹5,120')).toBe('"Stop loss hit: P&L −₹5,120"')
    expect(csvField('the "Ghost" run')).toBe('"the ""Ghost"" run"')
    expect(csvField('line one\nline two')).toBe('"line one\nline two"')
    expect(csvField('carriage\r\nreturn')).toBe('"carriage\r\nreturn"')
  })

  it('writes a number in plain digits a sheet reads as a number', () => {
    expect(csvField(194000)).toBe('194000')
    expect(csvField(-45100)).toBe('-45100')
    expect(csvField(57369.65)).toBe('57369.65')
    expect(csvField(0.0004)).toBe('0.0004')
    expect(csvField(-0)).toBe('0')
  })

  it('leaves nothing, and a number that is not one, empty', () => {
    expect(csvField(null)).toBe('')
    expect(csvField(undefined)).toBe('')
    expect(csvField('')).toBe('')
    expect(csvField(Number.NaN)).toBe('')
    expect(csvField(Number.POSITIVE_INFINITY)).toBe('')
  })

  it('writes a yes-or-no as true or false', () => {
    expect(csvField(true)).toBe('true')
    expect(csvField(false)).toBe('false')
  })

  it('keeps a text a sheet would run as a formula a text', () => {
    expect(csvField('=HYPERLINK("http://x")')).toBe(`"'=HYPERLINK(""http://x"")"`)
    expect(csvField('+91 98')).toBe("'+91 98")
    expect(csvField('-5%')).toBe("'-5%")
    expect(csvField('@SUM(A1)')).toBe("'@SUM(A1)")
    // A true minus is not a formula, and a negative number is a number.
    expect(csvField('−45.1k')).toBe('−45.1k')
    expect(csvField(-5)).toBe('-5')
  })
})

describe('toCsv', () => {
  interface Leg {
    symbol: string
    lots: number
    pnl: number | null
    note?: string
  }
  const columns: CsvColumn<Leg>[] = [
    { header: 'Symbol', value: (r) => r.symbol },
    { header: 'Lots', value: (r) => r.lots },
    { header: 'P&L (₹)', value: (r) => r.pnl },
    { header: 'Note, if any', value: (r) => r.note },
  ]

  it('writes a header line and a line per row, each ended by CRLF', () => {
    const csv = toCsv(
      [
        { symbol: 'NSE:NIFTY26N1024500CE', lots: 2, pnl: -1240.5, note: 'carried, from #412' },
        { symbol: 'NSE:NIFTY26N1024500PE', lots: 2, pnl: null },
      ],
      columns,
    )
    expect(csv).toBe(
      'Symbol,Lots,P&L (₹),"Note, if any"\r\n' +
        'NSE:NIFTY26N1024500CE,2,-1240.5,"carried, from #412"\r\n' +
        'NSE:NIFTY26N1024500PE,2,,\r\n',
    )
  })

  it('writes the header alone for no rows', () => {
    expect(toCsv([], columns)).toBe('Symbol,Lots,P&L (₹),"Note, if any"\r\n')
  })
})

describe('istIsoTime', () => {
  it('writes an instant as ISO in IST, with the offset', () => {
    expect(istIsoTime('2026-10-29T05:30:00Z')).toBe('2026-10-29T11:00:00+05:30')
    // After 18:30 UTC it is the next IST day.
    expect(istIsoTime('2026-10-28T19:00:05.250Z')).toBe('2026-10-29T00:30:05+05:30')
    expect(istIsoTime('2026-10-29T11:00:00+05:30')).toBe('2026-10-29T11:00:00+05:30')
  })

  it('is empty for nothing and for a time that is not one', () => {
    expect(istIsoTime(null)).toBe('')
    expect(istIsoTime('')).toBe('')
    expect(istIsoTime('not a time')).toBe('')
  })
})

describe('fileStamp', () => {
  it("is an instant's IST day and minute", () => {
    expect(fileStamp('2026-10-29T05:30:59Z')).toBe('2026-10-29-1100')
    expect(fileStamp(undefined)).toBe('')
  })
})

describe('csvFileName', () => {
  it('joins what is in the file by dashes, leaving the empty parts out', () => {
    expect(csvFileName('NIFTY', 'chain', '2026-11-04', '', null, '2026-10-29-1100')).toBe('NIFTY-chain-2026-11-04-2026-10-29-1100.csv')
  })

  it('makes each part safe for a file name on any system', () => {
    expect(csvFileName('backtest', '#412', 'Ghost Tangent: v2/3')).toBe('backtest-412-Ghost-Tangent-v2-3.csv')
    expect(csvFileName('M&M', 'chain')).toBe('M-M-chain.csv')
    expect(csvFileName('  ', '/')).toBe('export.csv')
  })
})
