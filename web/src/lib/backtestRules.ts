/**
 * The run's own trading rules, as the console edits them.
 *
 * The engine reads five optional blocks out of a run's parameters —
 * `filters` (when to trade and what the market must look like), `limits` (how
 * often), `exits` (when a position must come out), `contract` (what a bare
 * BUY/SELL takes) and `costs` — and applies them to whatever strategy the run
 * replays. This file is the console's side of that contract: one catalogue of
 * fields, one draft of what the operator typed, and the conversion back.
 *
 * Kept away from the components so the rules can be tested as data: every field
 * has a block, a type and a help line, and the form only renders them.
 */

export type RuleBlock = 'filters' | 'limits' | 'exits' | 'contract' | 'costs'
export type RuleKind = 'number' | 'time' | 'toggle' | 'select' | 'windows' | 'weekdays' | 'pair'

export interface RuleField {
  /** Key inside its block, exactly as the engine reads it. */
  key: string
  block: RuleBlock
  label: string
  kind: RuleKind
  help?: string
  /** For `select`: the options, value first. */
  options?: Array<[string, string]>
  /** For `pair`: the two sub-keys' labels (the value is stored as "a,b"). */
  pair?: [string, string]
  placeholder?: string
  /** Only meaningful for a signal that expresses a direction (a spread has none). */
  directional?: boolean
}

export interface RuleGroup {
  id: string
  title: string
  summary: string
  fields: RuleField[]
}

/** Every rule the engine understands, grouped the way the form shows them. */
export const RULE_GROUPS: RuleGroup[] = [
  {
    id: 'when',
    title: 'When to trade',
    summary: 'Windows, weekdays and expiry days',
    fields: [
      {
        key: 'windows_ist', block: 'filters', label: 'Trading windows (IST)', kind: 'windows',
        help: 'Entries are only taken inside these; add a second one for a morning and an afternoon session.',
      },
      { key: 'weekdays', block: 'filters', label: 'Weekdays', kind: 'weekdays' },
      {
        key: 'expiry_day', block: 'filters', label: 'Expiry days', kind: 'select',
        options: [['', 'Any day'], ['only', 'Expiry days only'], ['skip', 'Skip expiry days']],
      },
      {
        key: 'expiry_cutoff_ist', block: 'filters', label: 'No entry after (expiry day)', kind: 'time',
        help: 'On an expiry day only. Premiums fall fastest in the last hours.',
      },
      { key: 'block_open_minutes', block: 'filters', label: 'Skip the first N minutes', kind: 'number', placeholder: '15' },
    ],
  },
  {
    id: 'market',
    title: 'Market state',
    summary: 'Gap, volatility and how much it is moving',
    fields: [
      { key: 'min_gap_percent', block: 'filters', label: 'Gap at least (%)', kind: 'number', placeholder: '0.5' },
      { key: 'max_gap_percent', block: 'filters', label: 'Gap at most (%)', kind: 'number', placeholder: '0.5' },
      { key: 'min_vix', block: 'filters', label: 'INDIA VIX at least', kind: 'number', placeholder: '14' },
      { key: 'max_vix', block: 'filters', label: 'INDIA VIX at most', kind: 'number', placeholder: '18' },
      {
        key: 'min_vix_change_percent', block: 'filters', label: 'VIX up at least (%)', kind: 'number',
        help: 'Against the day’s first VIX reading.',
      },
      { key: 'max_vix_change_percent', block: 'filters', label: 'VIX up at most (%)', kind: 'number' },
      { key: 'min_atr_percent', block: 'filters', label: 'ATR at least (% of price)', kind: 'number', placeholder: '0.05' },
      { key: 'max_atr_percent', block: 'filters', label: 'ATR at most (% of price)', kind: 'number' },
      { key: 'min_volume_zscore', block: 'filters', label: 'Volume z-score at least', kind: 'number', help: 'Indices carry no volume, so this stands aside on them.' },
    ],
  },
  {
    id: 'trend',
    title: 'Do not fight the trend',
    summary: 'Each rule refuses a signal that goes against what it measures',
    fields: [
      { key: 'require_vwap_side', block: 'filters', label: 'Right side of VWAP', kind: 'toggle', directional: true, help: 'Needs traded volume; an index alone has none.' },
      { key: 'ema_period', block: 'filters', label: 'EMA period', kind: 'number', placeholder: '20' },
      { key: 'require_ema_side', block: 'filters', label: 'Right side of that EMA', kind: 'toggle', directional: true },
      { key: 'ema_fast', block: 'filters', label: 'Fast EMA', kind: 'number', placeholder: '9' },
      { key: 'ema_slow', block: 'filters', label: 'Slow EMA', kind: 'number', placeholder: '21', help: 'Both set: the fast EMA must be on the signal’s side of the slow one.' },
      { key: 'supertrend', block: 'filters', label: 'Supertrend (period, multiple)', kind: 'pair', pair: ['10', '3'], directional: true },
      { key: 'adx_min', block: 'filters', label: 'ADX at least', kind: 'number', placeholder: '20', directional: true, help: 'Blocks only when the trend is that strong AND against the signal.' },
      { key: 'max_move_from_open_percent', block: 'filters', label: 'Day’s move against, at most (%)', kind: 'number', placeholder: '0.4', directional: true },
      { key: 'opening_range_minutes', block: 'filters', label: 'Opening range (minutes)', kind: 'number', placeholder: '15', directional: true, help: 'Blocks a signal taken against a break of the first minutes’ range.' },
      { key: 'against_return_bars', block: 'filters', label: 'Recent move: bars', kind: 'number', placeholder: '6' },
      { key: 'against_return_percent', block: 'filters', label: '…at least (%) against', kind: 'number', placeholder: '0.2', directional: true },
      { key: 'vote_min_against', block: 'filters', label: 'Block when N readings disagree', kind: 'number', placeholder: '3', directional: true, help: 'Counts VWAP, the EMA, Supertrend, the opening range and the move from the open.' },
    ],
  },
  {
    id: 'momentum',
    title: 'Momentum and candles',
    summary: 'RSI levels and the shape of the signal candle',
    fields: [
      { key: 'rsi_period', block: 'filters', label: 'RSI period', kind: 'number', placeholder: '14' },
      { key: 'min_rsi', block: 'filters', label: 'RSI at least', kind: 'number' },
      { key: 'max_rsi', block: 'filters', label: 'RSI at most', kind: 'number' },
      { key: 'rsi_cross_up', block: 'filters', label: 'RSI must cross above', kind: 'number', placeholder: '60', directional: true },
      { key: 'rsi_cross_down', block: 'filters', label: '…or below (for a sell view)', kind: 'number', placeholder: '40', directional: true },
      { key: 'min_candle_body_ratio', block: 'filters', label: 'Candle body at least (0–1 of its range)', kind: 'number', placeholder: '0.5' },
      {
        key: 'require_pattern', block: 'filters', label: 'Confirming candle pattern', kind: 'select',
        options: [['', 'Not required'], ['auto', 'Any that fits the direction'], ['bullish_engulfing', 'Bullish engulfing'],
                  ['bearish_engulfing', 'Bearish engulfing'], ['hammer', 'Hammer'], ['shooting_star', 'Shooting star'],
                  ['marubozu', 'Marubozu']],
      },
    ],
  },
  {
    id: 'limits',
    title: 'How often it may trade',
    summary: 'Trade counts, cooldowns and when the day is over',
    fields: [
      { key: 'max_trades_per_day', block: 'limits', label: 'Trades a day, at most', kind: 'number', placeholder: '1' },
      { key: 'max_trades_per_window', block: 'limits', label: 'Trades per window, at most', kind: 'number', placeholder: '1' },
      { key: 'max_open_groups', block: 'limits', label: 'Open positions, at most', kind: 'number', placeholder: '1' },
      { key: 'cooldown_after_loss_minutes', block: 'limits', label: 'Cooldown after a loss (minutes)', kind: 'number', placeholder: '30' },
      { key: 'block_direction_after_loss', block: 'limits', label: 'No repeat of a direction that lost today', kind: 'toggle', directional: true },
      { key: 'stop_after_losses', block: 'limits', label: 'End the day after N losing trades', kind: 'number', placeholder: '2' },
      { key: 'stop_after_day_loss', block: 'limits', label: 'End the day at a realised loss of (₹)', kind: 'number', placeholder: '3000' },
    ],
  },
  {
    id: 'exits',
    title: 'Extra exits',
    summary: 'The clock, a stop that moves, and the index turning',
    fields: [
      { key: 'time_exit_minutes', block: 'exits', label: 'Exit N minutes after entry', kind: 'number', placeholder: '30' },
      { key: 'breakeven_after_points', block: 'exits', label: 'Stop to entry once up (points)', kind: 'number', placeholder: '10' },
      { key: 'breakeven_after_percent', block: 'exits', label: '…or once up (%)', kind: 'number', placeholder: '15' },
      { key: 'step_trail_step_percent', block: 'exits', label: 'Then move the stop up every (%)', kind: 'number', placeholder: '5', help: 'After the stop reaches entry, every further step of profit lifts it by the same step.' },
      { key: 'exit_on_ema_cross', block: 'exits', label: 'Exit when the index closes against EMA', kind: 'number', placeholder: '20' },
      { key: 'exit_on_vwap_cross', block: 'exits', label: 'Exit when it closes against VWAP', kind: 'toggle' },
      { key: 'exit_on_supertrend', block: 'exits', label: 'Exit when Supertrend turns (period, multiple)', kind: 'pair', pair: ['10', '3'] },
    ],
  },
  {
    id: 'contract',
    title: 'Which contract',
    summary: 'Only for strategies that emit a plain BUY or SELL',
    fields: [
      {
        key: 'strike_offset', block: 'contract', label: 'Strikes in the money', kind: 'number', placeholder: '0',
        help: 'Positive = in the money, negative = out of the money. 0 is the ATM strike.',
      },
      { key: 'flip', block: 'contract', label: 'Take the other side (flip)', kind: 'toggle', help: 'A BUY view buys the put instead — the test of whether a strategy is wrong rather than unprofitable.' },
    ],
  },
  {
    id: 'costs',
    title: 'Costs',
    summary: 'Slippage and the statutory charges',
    fields: [
      { key: 'slippage_percent', block: 'costs', label: 'Slippage a side (%)', kind: 'number', placeholder: '0.5', help: 'Half the spread plus impact. Setting this also charges brokerage, STT, exchange, SEBI, stamp duty and GST.' },
      { key: 'brokerage_per_order', block: 'costs', label: 'Brokerage per order (₹)', kind: 'number', placeholder: '20' },
      { key: 'stt_sell_pct', block: 'costs', label: 'STT on the sell side (%)', kind: 'number', placeholder: '0.15' },
    ],
  },
]

export const RULE_FIELDS: RuleField[] = RULE_GROUPS.flatMap((g) => g.fields)

export const WEEKDAYS = ['Mon', 'Tue', 'Wed', 'Thu', 'Fri'] as const

/** What the form holds while it is being typed: strings, exactly as entered. */
export type RulesDraft = Record<string, string>

const TIME = /^\d{1,2}:\d{2}$/

/**
 * The rows as they are being typed, half-filled ones included: a window whose
 * "from" is set but whose "to" is not must stay on screen until it is finished.
 * Only `parseWindows` — what the run is sent — insists on both.
 */
export function parseWindowRows(value: string): Array<[string, string]> {
  return (value || '')
    .split(',')
    .map((part) => part.trim())
    .filter(Boolean)
    .map((part) => {
      const [from = '', to = ''] = part.split('-')
      return [from.trim(), to.trim()] as [string, string]
    })
}

/** The complete windows only, as the engine reads them. */
export function parseWindows(value: string): Array<[string, string]> {
  return parseWindowRows(value).filter(([from, to]) => TIME.test(from) && TIME.test(to))
}

/** Rows back into the draft, keeping a row that has one side filled in so far. */
export function formatWindows(windows: Array<[string, string]>): string {
  return windows
    .filter(([a, b]) => a || b)
    .map(([a, b]) => `${a}-${b}`)
    .join(',')
}

function num(value: string): number | null {
  const text = (value ?? '').trim()
  if (text === '') return null
  const parsed = Number(text)
  return Number.isFinite(parsed) ? parsed : null
}

/**
 * The draft as the engine's parameter blocks. Empty fields are left out
 * entirely, so a run configures only what the operator actually set, and a
 * block with nothing in it never reaches the API.
 */
export function rulesToParams(draft: RulesDraft): Record<string, Record<string, unknown>> {
  const blocks: Record<string, Record<string, unknown>> = {}
  const put = (block: RuleBlock, key: string, value: unknown) => {
    blocks[block] = blocks[block] ?? {}
    blocks[block][key] = value
  }

  for (const field of RULE_FIELDS) {
    const raw = (draft[field.key] ?? '').trim()
    if (raw === '') continue
    switch (field.kind) {
      case 'toggle':
        if (raw === 'true') put(field.block, field.key, true)
        break
      case 'select':
      case 'time':
        put(field.block, field.key, raw)
        break
      case 'weekdays': {
        const days = raw.split(',').map((d) => d.trim()).filter(Boolean)
        if (days.length > 0 && days.length < WEEKDAYS.length) put(field.block, field.key, days)
        break
      }
      case 'windows': {
        const windows = parseWindows(raw)
        if (windows.length > 0) put(field.block, field.key, windows)
        break
      }
      case 'pair': {
        const [first, second] = raw.split(',').map((x) => num(x))
        if (first !== null && second !== null) put(field.block, field.key, [first, second])
        break
      }
      default: {
        const value = num(raw)
        if (value !== null) put(field.block, field.key, value)
      }
    }
  }
  return blocks
}

/** The draft back out of a run's parameters, for "run again". */
export function rulesFromParams(params: Record<string, unknown> | null | undefined): RulesDraft {
  const draft: RulesDraft = {}
  if (!params) return draft
  for (const field of RULE_FIELDS) {
    const block = params[field.block]
    if (!block || typeof block !== 'object') continue
    const value = (block as Record<string, unknown>)[field.key]
    if (value === undefined || value === null) continue
    if (field.kind === 'toggle') draft[field.key] = value ? 'true' : ''
    else if (field.kind === 'windows' && Array.isArray(value))
      draft[field.key] = formatWindows(value as Array<[string, string]>)
    else if (field.kind === 'weekdays' && Array.isArray(value)) draft[field.key] = (value as string[]).join(',')
    else if (field.kind === 'pair' && Array.isArray(value)) draft[field.key] = (value as unknown[]).join(',')
    else draft[field.key] = String(value)
  }
  return draft
}

/** How many rules a group has set, for the collapsed header. */
export function countSet(draft: RulesDraft, group: RuleGroup): number {
  return group.fields.filter((f) => (draft[f.key] ?? '').trim() !== '').length
}

/** One line naming everything the run will enforce, or null when it enforces nothing. */
export function describeRules(draft: RulesDraft): string | null {
  const parts = RULE_GROUPS.map((group) => {
    const set = countSet(draft, group)
    return set > 0 ? `${group.title.toLowerCase()} (${set})` : null
  }).filter(Boolean)
  return parts.length > 0 ? parts.join(' · ') : null
}

// --- saved sets ------------------------------------------------------------
//
// A set of rules is worth reusing: the same windows and limits are tried across
// strategies, and retyping them is how two runs quietly stop being comparable.
// They live in this browser only (localStorage), which is why every read and
// write is guarded — a private window or blocked storage must not break the form.

const PRESETS_KEY = 'backtest.rulePresets'

export type RulePresets = Record<string, RulesDraft>

export function loadPresets(): RulePresets {
  try {
    const raw = window.localStorage.getItem(PRESETS_KEY)
    const parsed = raw ? JSON.parse(raw) : null
    return parsed && typeof parsed === 'object' ? (parsed as RulePresets) : {}
  } catch {
    return {}
  }
}

export function savePreset(name: string, draft: RulesDraft): RulePresets {
  const cleaned = Object.fromEntries(Object.entries(draft).filter(([, value]) => (value ?? '').trim() !== ''))
  const next = { ...loadPresets(), [name.trim()]: cleaned }
  try {
    window.localStorage.setItem(PRESETS_KEY, JSON.stringify(next))
  } catch {
    // Storage can be full or blocked; the set stays in the form either way.
  }
  return next
}

export function deletePreset(name: string): RulePresets {
  const next = { ...loadPresets() }
  delete next[name]
  try {
    window.localStorage.setItem(PRESETS_KEY, JSON.stringify(next))
  } catch {
    // As above: nothing to do but keep going.
  }
  return next
}
