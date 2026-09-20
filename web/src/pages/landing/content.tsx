/**
 * What the public homepage says, shared by its two views (landing/Desktop.tsx
 * and landing/Mobile.tsx) so they can never disagree: the chapters' copy, the
 * labels that live at points in the world and the console's real screens. The
 * small components both views draw with are in landing/parts.tsx.
 *
 * Rules this content keeps: every figure is one the console itself shows (the
 * run's come from scene/evidence.ts); strategy code names never appear; the
 * synthetic objects in the world are captioned as synthetic.
 */

import type { ReactNode } from 'react'
import type { ChapterKey } from '../../scene/story'
import { RUN } from '../../scene/evidence'
import type { AnchorName } from '../../scene/machine'
import shotChain from '../../shots/option-chain.webp'
import shotPulse from '../../shots/market-pulse.webp'
import shotCoverage from '../../shots/history-coverage.webp'
import shotFactors from '../../shots/market-factors.webp'
import shotRun from '../../shots/run-ledger.webp'
import shotSetup from '../../shots/setup-editor.webp'

export const GITHUB_URL = 'https://github.com/helloupendra/algorithmic-trading-engine'
export const LICENSE_URL = `${GITHUB_URL}/blob/main/LICENSE`
export const LINKEDIN_URL = 'https://www.linkedin.com/in/upendrasingh12/'
export const AUTHOR = 'Upendra Singh Chauhan'

export const rupees = (n: number) => `₹${Math.abs(n).toLocaleString('en-IN')}`

/* ------------------------------------------------------------------ chapters */

export interface ChapterCopy {
  key: ChapterKey
  /** "01", and the layer's name as it is lettered on the page. */
  index?: string
  label: string
  title: ReactNode
  body: string
  /** The same point in two lines, for the phone's bottom sheet. */
  short: string
  specs?: [string, string][]
}

export const COPY: ChapterCopy[] = [
  {
    key: 'hero',
    label: 'Open-source algo trading desk · Indian F&O · self-hosted',
    title: <>Not a <em>black box.</em></>,
    body: 'OpenFNO is a trading engine you can see inside: every tick stored, every rule written down, every rupee accounted for — running on your own machine, with your own broker keys.',
    short: 'A trading engine you can see inside: every tick stored, every rule written down, every rupee accounted for — on your own machine, with your own broker keys.',
  },
  {
    key: 'data',
    index: '01',
    label: 'Data',
    title: <>Four feeds in. <em>Every tick kept.</em></>,
    body: 'Dhan, FYERS, Angel One and TrueData stream into one store as ticks, minute bars and option-chain snapshots — compressed after two hours, archived every night. Every picker in the console lists what is stored before you choose a range.',
    short: 'Dhan, FYERS, Angel One and TrueData stream into one store: ticks, minute bars, chain snapshots. Compressed after two hours, archived every night.',
    specs: [
      ['feeds', 'Dhan · FYERS · Angel One · TrueData'],
      ['history', '5 years of index candles, 1 minute to daily'],
      ['options', 'minute-level, ATM ±10 · NIFTY · BANKNIFTY · SENSEX'],
      ['store', 'TimescaleDB · NSE · BSE · MCX'],
    ],
  },
  {
    key: 'chain',
    index: '02',
    label: 'Option chain',
    title: <>Every strike. <em>Any minute.</em></>,
    body: 'Calls one side, puts the other, the strike ladder between them. Live, with Greeks, build-up, PCR, max pain and the walls — and the same chain rebuilt for any past minute from stored history, which is what makes an option strategy testable at all.',
    short: 'Live, with Greeks, build-up, PCR and max pain — and the same chain rebuilt for any past minute, which is what makes an option strategy testable.',
    specs: [
      ['live', 'Greeks · long build-up / short covering · PCR · max pain'],
      ['replay', 'the chain as it stood at any stored minute'],
      ['yours', 'open positions marked on the strikes you hold'],
    ],
  },
  {
    key: 'strategy',
    index: '03',
    label: 'Strategy',
    title: <>Rules you can <em>read.</em></>,
    body: 'A strategy is a list of conditions — price against VWAP or an EMA, an EMA pair, RSI, candle body, Supertrend, ADX, the opening range, the gap. Every bar passes through them; if they all hold, there is a signal. One on_bar contract runs live and in replay.',
    short: 'A strategy is a list of conditions. Every bar passes through them; if they all hold, there is a signal. One on_bar runs live and in replay.',
    specs: [
      ['catalogue', '25 strategies, described from their own code'],
      ['builder', 'write your own in the console, as conditions'],
      ['contract', 'one on_bar — the same live and in replay'],
    ],
  },
  {
    key: 'risk',
    index: '04',
    label: 'Risk',
    title: <>Three stops. <em>One switch.</em></>,
    body: 'A limit on the leg, on the group and on the day — editable while the run is going, measured per trading day — and a kill switch that survives a restart. Every stop says why it stopped: guard, limit, market close, you, or the end of the range.',
    short: 'A limit on the leg, the group and the day — editable while it runs — and a kill switch that survives a restart. Every stop says why.',
    specs: [
      ['levels', 'leg → group → day'],
      ['execution', 'paper, on real ticks · lots × lot size'],
      ['silence', 'none — a stalled feed or a dead runner says so'],
    ],
  },
  {
    key: 'ledger',
    index: '05',
    label: 'Ledger',
    title: <>The run <em>that lost.</em></>,
    body: `One tile for every session of a real run: a directional strategy on ${RUN.underlying} one-minute candles, January to September 2026. ${RUN.trades} closed trades, ${RUN.winRatePct}% winners, ${rupees(RUN.netPnl)} lost on ${rupees(RUN.initialCapital)}. It is here because a platform that only shows its wins is a pitch.`,
    short: 'One tile per session of a real run that lost money. It is here because a platform that only shows its wins is a pitch.',
    specs: [
      ['net', `−${rupees(RUN.netPnl)} on ${rupees(RUN.initialCapital)}`],
      ['per trade', `−₹${Math.abs(RUN.perTrade)} · ${RUN.winRatePct}% winners`],
      ['days', `${RUN.profitableSessions} of ${RUN.sessions} positive`],
      ['drawdown', rupees(RUN.maxDrawdown)],
    ],
  },
  {
    key: 'open',
    label: 'Open source',
    title: <>Read <em>every line.</em></>,
    body: 'A .NET 10 API, a Python engine, a React console, TimescaleDB and Redis — on hardware you control. No data leaves, no strategy is uploaded, and the whole engine is on GitHub.',
    short: 'A .NET 10 API, a Python engine, a React console, TimescaleDB and Redis — on hardware you control. The whole engine is on GitHub.',
  },
]

export interface WorldTag {
  anchor: AnchorName
  chapter: ChapterKey
  text: string
  tone?: 'caption' | 'alert'
  /** Shown on a phone too. The rest would crowd a 390 px stage, and the sheet's copy says them instead. */
  phone?: boolean
}

/** Labels that live at points in the world, and the chapter each belongs to. */
export const TAGS: WorldTag[] = [
  { anchor: 'feed0', chapter: 'data', text: 'Dhan' },
  { anchor: 'feed1', chapter: 'data', text: 'FYERS' },
  { anchor: 'feed2', chapter: 'data', text: 'Angel One' },
  { anchor: 'feed3', chapter: 'data', text: 'TrueData' },
  { anchor: 'tape', chapter: 'data', text: 'synthetic tape · not a quote', tone: 'caption', phone: true },
  { anchor: 'chain', chapter: 'chain', text: 'synthetic chain · not a quote', tone: 'caption', phone: true },
  { anchor: 'gate0', chapter: 'strategy', text: 'close > vwap' },
  { anchor: 'gate1', chapter: 'strategy', text: 'close > ema:9' },
  { anchor: 'gate2', chapter: 'strategy', text: 'body ≥ 0.5' },
  { anchor: 'gate3', chapter: 'strategy', text: 'rsi ↑ 60' },
  { anchor: 'ringLeg', chapter: 'risk', text: 'leg' },
  { anchor: 'ringGroup', chapter: 'risk', text: 'group' },
  { anchor: 'ringDay', chapter: 'risk', text: 'day' },
  { anchor: 'kill', chapter: 'risk', text: 'kill switch', tone: 'alert', phone: true },
  { anchor: 'threadEnd', chapter: 'ledger', text: `−${rupees(RUN.netPnl)}`, tone: 'alert', phone: true },
]

export const SCREENS = [
  { src: shotPulse, w: 2200, h: 924, name: 'Market pulse', text: 'The first screen: indices, the nearest MCX contracts and the large caps, each with its day range and how old its last price is. A stale price says so.', alt: 'The overview screen: market state, indices, MCX commodities and large caps, each with the day range and the age of its last price.' },
  { src: shotChain, w: 2200, h: 1291, name: 'Option chain', text: 'Calls left, puts right, strike in the middle; open interest and its change, IV, and what the pair of them means. Stamped with when it was captured and by which vendor.', alt: 'The option chain: calls left, puts right, strike in the middle, with open interest, its change, IV and the build-up on each row.' },
  { src: shotCoverage, w: 2100, h: 1112, name: 'History on hand', text: 'What is actually on disk — index, resolution, first and last session, bars, and whether they came from a backfill or the live feed — before you are asked to pick a range.', alt: 'The stored history table: index, resolution, the date range, sessions, bars and where each range came from.' },
  { src: shotSetup, w: 2000, h: 924, name: 'Setup editor', text: 'A long setup and a short setup written as conditions, with the rules the run will trade under listed beneath them.', alt: 'The setup editor: a long setup and a short setup written as conditions, with the run\'s rules under them.' },
  { src: shotFactors, w: 2200, h: 1222, name: 'Market factors', text: 'Walls, max pain, PCR, ATM IV, participant open interest and flows — each stamped with how fresh it is and with what our own testing found, including "not tested yet".', alt: 'The market factors screen: option walls, max pain, put-call ratio, ATM IV and India VIX, each labelled with how fresh it is and what testing it has had.' },
  { src: shotRun, w: 2100, h: 1351, name: 'A finished run', text: 'Net P&L, win rate, profit factor, drawdown — and the account panel, because a total says nothing about whether the account survived to collect it.', alt: 'A completed backtest: net P&L, trades, win rate, profit factor, drawdown, and an account panel with the lowest balance and the closing balance.' },
]
