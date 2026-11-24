import { describe, expect, it } from 'vitest'
import { contractName, lookBadge, nowText, readAiTraderDesk } from './aiTraderDesk'

describe('readAiTraderDesk', () => {
  it('reads the desk, its positions, runs and the notes per script', () => {
    const d = readAiTraderDesk({
      status: 'on',
      mode: 'live',
      everyMinutes: 10,
      clockIst: '10:35',
      tradingDay: true,
      openNow: true,
      hours: '09:20–14:45',
      rules: {
        underlyings: ['NIFTY'],
        maxLotsPerTrade: 2,
        dailyLossLimit: 10000,
        strategies: ['TrendRiderV2OB'],
        strategyNotes: { TrendRiderV2OB: 'the best of the family', Bad: 7 },
      },
      accountId: 42,
      accountName: 'ai-trader',
      netToday: -1250.5,
      chargesToday: 80,
      openedToday: 2,
      lossLeft: 8749.5,
      positions: [
        { id: 7, symbol: 'NSE:NIFTY25N2525600CE', lots: 2, lotSize: 65, openedUtc: '2026-11-25T04:45:00Z', entry: 120.5, mark: 110, closedUtc: null, stopLoss: 90, target: 180, pnl: -1365 },
        { id: 8, symbol: 'NSE:NIFTY25N2525500PE', lots: 1, closedUtc: '2026-11-25T06:00:00Z', pnl: 400 },
        { symbol: 'no id' },
      ],
      runs: [{ runId: 900, strategy: 'TrendRiderV2OB', underlying: 'NIFTY', status: 'Running', startedUtc: '2026-11-25T04:00:00Z', net: 300, running: true }],
      lastLook: { id: 5, clockIst: '10:30', action: 'none', allowed: true, rule: 'ok', reason: 'Waiting.' },
    })
    expect(d.status).toBe('on')
    expect(d.openNow).toBe(true)
    expect(d.strategyNotes).toEqual({ TrendRiderV2OB: 'the best of the family' })
    expect(d.positions.map((p) => [p.id, p.open])).toEqual([
      [7, true],
      [8, false],
    ])
    expect(d.runs[0].running).toBe(true)
    expect(d.lastLook?.reason).toBe('Waiting.')
  })

  it('reads an empty or broken answer as switched off with nothing in it', () => {
    const d = readAiTraderDesk(null)
    expect(d.status).toBe('off')
    expect(d.positions).toEqual([])
    expect(d.runs).toEqual([])
    expect(d.lastLook).toBeNull()
    expect(d.accountName).toBe('ai-trader')
  })
})

describe('contractName', () => {
  it('names an option by index, strike and side', () => {
    expect(contractName('NSE:NIFTY25N2525600CE')).toBe('NIFTY 25600 CE')
    expect(contractName('NSE:NIFTY2670724350CE')).toBe('NIFTY 24350 CE')
    expect(contractName('NSE:BANKNIFTY25NOV56000PE')).toBe('BANKNIFTY 56000 PE')
  })

  it('keeps anything else as it is, without the exchange', () => {
    expect(contractName('NSE:NIFTY50-INDEX')).toBe('NIFTY50-INDEX')
  })
})

describe('lookBadge', () => {
  const base = { allowed: true, rule: 'ok', executed: true, mode: 'live' }
  it('says what a look did in plain words', () => {
    expect(lookBadge({ ...base, action: 'none' }).label).toBe('Waited')
    expect(lookBadge({ ...base, action: 'buy' })).toEqual({ label: 'Bought', tone: 'pos' })
    expect(lookBadge({ ...base, action: 'exit' }).label).toBe('Sold')
    expect(lookBadge({ ...base, action: 'start_strategy' }).label).toBe('Started a script')
    expect(lookBadge({ ...base, action: 'buy', allowed: false, rule: 'daily-loss' })).toEqual({ label: 'Wanted to buy: refused', tone: 'warn' })
    expect(lookBadge({ ...base, action: '', rule: 'no-answer' }).label).toBe('No answer')
  })
})

describe('nowText', () => {
  const d = { status: 'on', mode: 'live', tradingDay: true, openNow: false, hours: '09:20–14:45', everyMinutes: 10, clockIst: '08:50' }
  it('says whether it is working now', () => {
    expect(nowText({ ...d, openNow: true }).tone).toBe('live')
    expect(nowText(d).text).toContain('starts looking at 09:20')
    expect(nowText({ ...d, clockIst: '15:40' }).text).toContain('done for today')
    expect(nowText({ ...d, tradingDay: false }).text).toContain('no market today')
    expect(nowText({ ...d, status: 'off' }).text).toContain('Switched off')
  })
})
