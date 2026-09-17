import { describe, expect, it } from 'vitest'
import {
  RULE_FIELDS,
  RULE_GROUPS,
  countSet,
  describeRules,
  formatWindows,
  parseWindows,
  rulesFromParams,
  rulesToParams,
} from './backtestRules'

describe('backtest rules', () => {
  it('names every field once, inside a block the engine reads', () => {
    const keys = RULE_FIELDS.map((f) => `${f.block}.${f.key}`)
    expect(new Set(keys).size).toBe(keys.length)
    expect(new Set(RULE_FIELDS.map((f) => f.block))).toEqual(
      new Set(['filters', 'limits', 'exits', 'contract', 'costs']),
    )
  })

  it('sends only what was filled in, in the shape the engine parses', () => {
    const params = rulesToParams({
      windows_ist: '09:20-11:00,13:00-15:15',
      weekdays: 'Tue,Thu',
      expiry_day: 'skip',
      max_trades_per_day: '1',
      block_direction_after_loss: 'true',
      exit_on_vwap_cross: '',                 // an unticked toggle is not a rule
      time_exit_minutes: '30',
      supertrend: '10,3',
      strike_offset: '1',
      slippage_percent: '0.5',
      min_rsi: '   ',                         // blank stays out
    })

    expect(params).toEqual({
      filters: {
        windows_ist: [['09:20', '11:00'], ['13:00', '15:15']],
        weekdays: ['Tue', 'Thu'],
        expiry_day: 'skip',
        supertrend: [10, 3],
      },
      limits: { max_trades_per_day: 1, block_direction_after_loss: true },
      exits: { time_exit_minutes: 30 },
      contract: { strike_offset: 1 },
      costs: { slippage_percent: 0.5 },
    })
    expect(rulesToParams({})).toEqual({})
  })

  it('leaves out a weekday list that selects every day', () => {
    expect(rulesToParams({ weekdays: 'Mon,Tue,Wed,Thu,Fri' })).toEqual({})
  })

  it('reads a run back into the form', () => {
    const draft = rulesFromParams({
      filters: { windows_ist: [['09:20', '11:00']], require_vwap_side: true, ema_period: 20 },
      limits: { max_trades_per_day: 2 },
      contract: { flip: true },
    })
    expect(draft.windows_ist).toBe('09:20-11:00')
    expect(draft.require_vwap_side).toBe('true')
    expect(draft.ema_period).toBe('20')
    expect(draft.max_trades_per_day).toBe('2')
    expect(draft.flip).toBe('true')
    expect(rulesFromParams(null)).toEqual({})
  })

  it('round-trips a draft through the parameters', () => {
    const draft = { windows_ist: '09:20-11:00', max_open_groups: '1', breakeven_after_percent: '15' }
    expect(rulesFromParams(rulesToParams(draft))).toMatchObject(draft)
  })

  it('reads windows written by hand and ignores nonsense', () => {
    expect(parseWindows('09:20-11:00, 13:00-15:15')).toEqual([['09:20', '11:00'], ['13:00', '15:15']])
    expect(parseWindows('morning')).toEqual([])
    expect(formatWindows([['09:20', '11:00'], ['', '']])).toBe('09:20-11:00')
  })

  it('counts and describes what a run will enforce', () => {
    const draft = { max_trades_per_day: '1', cooldown_after_loss_minutes: '30', time_exit_minutes: '45' }
    const limits = RULE_GROUPS.find((g) => g.id === 'limits')!
    expect(countSet(draft, limits)).toBe(2)
    expect(describeRules(draft)).toBe('how often it may trade (2) · extra exits (1)')
    expect(describeRules({})).toBeNull()
  })
})
