/**
 * Module registry — the single source of truth for what this console is made
 * of. The sidebar, the overview grid, and (later) per-trader module grants all
 * read from here: when module access becomes a per-user setting stored on the
 * API, a user's grant list will be a set of these keys.
 */

import type { ComponentType, SVGProps } from 'react'
import {
  IconBell,
  IconBot,
  IconCandles,
  IconClock,
  IconDatabase,
  IconFlask,
  IconLayers,
  IconPlay,
  IconPlug,
  IconPlus,
  IconPulse,
  IconServer,
  IconShield,
  IconUsers,
} from '../components/icons'

export type ModuleStatus = 'ready' | 'legacy' | 'planned'

export interface ModuleDef {
  /** Stable key — future per-trader grants reference this. */
  key: string
  name: string
  description: string
  icon: ComponentType<SVGProps<SVGSVGElement>>
  route: string
  /**
   * ready  — rebuilt on the v2 design, fully supported.
   * legacy — functional page from v1, queued for its v2 rebuild.
   * planned — not built yet; rendered as disabled.
   */
  status: ModuleStatus
  adminOnly: boolean
}

export const MODULES: ModuleDef[] = [
  {
    key: 'data',
    name: 'Data',
    description:
      'Live tick feeds, historical candles, instruments and F&O chains — everything every strategy depends on.',
    icon: IconDatabase,
    route: '/admin/data',
    status: 'ready',
    adminOnly: true,
  },
  {
    key: 'strategies',
    name: 'Strategies',
    description: 'Deploy, monitor and control strategy runners.',
    icon: IconBot,
    route: '/admin/strategies',
    status: 'ready',
    adminOnly: true,
  },
  {
    key: 'backtesting',
    name: 'Backtesting',
    description: 'Replay any strategy over stored history: coverage-first, position-based results.',
    icon: IconFlask,
    route: '/admin/backtesting',
    status: 'ready',
    adminOnly: true,
  },
  {
    key: 'broker',
    name: 'Connectors',
    description: 'Data vendors and brokers: credentials, sessions, routing.',
    icon: IconPlug,
    route: '/admin/broker',
    status: 'ready',
    adminOnly: true,
  },
  {
    key: 'users',
    name: 'Users',
    description: 'Accounts, roles and, soon, per-trader module access.',
    icon: IconUsers,
    route: '/admin/users',
    status: 'legacy',
    adminOnly: true,
  },
  {
    key: 'system',
    name: 'System',
    description:
      'Everything operational in one place: service health, the kill switch and trading limits, and the alerter.',
    icon: IconServer,
    route: '/admin/system',
    status: 'legacy',
    adminOnly: true,
    /* Risk and Alerts live under this module as sections — see SYSTEM_SECTIONS. */
  },
]

/** Sub-navigation of the Data module — the first fully rebuilt module. */
export const DATA_SECTIONS = [
  {
    route: '/admin/data',
    label: 'Overview',
    icon: IconDatabase,
    end: true,
  },
  {
    route: '/admin/data/live',
    label: 'Live feeds',
    icon: IconPulse,
    end: false,
  },
  {
    route: '/admin/data/historical',
    label: 'Historical',
    icon: IconCandles,
    end: false,
  },
  {
    route: '/admin/data/instruments',
    label: 'Instruments & F&O',
    icon: IconServer,
    end: false,
  },
] as const

/** Sub-navigation of the Strategies module. */
export const STRATEGIES_SECTIONS = [
  {
    route: '/admin/strategies',
    label: 'Overview',
    icon: IconBot,
    end: true,
  },
  {
    route: '/admin/strategies/live',
    label: 'Live runner',
    icon: IconPlay,
    end: false,
  },
  {
    route: '/admin/strategies/history',
    label: 'Run history',
    icon: IconClock,
    end: false,
  },
  {
    route: '/admin/strategies/library',
    label: 'Library',
    icon: IconLayers,
    end: false,
  },
] as const

/** Sub-navigation of the Backtesting module. */
export const BACKTESTING_SECTIONS = [
  {
    route: '/admin/backtesting',
    label: 'Overview',
    icon: IconFlask,
    end: true,
  },
  {
    route: '/admin/backtesting/new',
    label: 'New backtest',
    icon: IconPlus,
    end: false,
  },
  {
    route: '/admin/backtesting/runs',
    label: 'Runs',
    icon: IconClock,
    end: false,
  },
] as const

/**
 * Sub-navigation of the System module. Risk and Alerts used to be separate
 * modules; an operator dealing with "is the platform behaving" wants health, the
 * kill switch and the alerter in one place, not three menu entries.
 */
export const SYSTEM_SECTIONS = [
  { route: '/admin/system', label: 'Overview', icon: IconServer, end: true },
  { route: '/admin/system/risk', label: 'Risk & kill switch', icon: IconShield },
  { route: '/admin/system/alerts', label: 'Alerts', icon: IconBell },
]
