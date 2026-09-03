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
  IconDatabase,
  IconFlask,
  IconLayers,
  IconPlay,
  IconPlug,
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
    description: 'Simulation runs, replays and performance reports.',
    icon: IconFlask,
    route: '/trader/strategies',
    status: 'legacy',
    adminOnly: false,
  },
  {
    key: 'risk',
    name: 'Risk',
    description: 'Kill switch and trading limits.',
    icon: IconShield,
    route: '/admin/risk',
    status: 'legacy',
    adminOnly: true,
  },
  {
    key: 'alerts',
    name: 'Alerts',
    description: 'Telegram alerter and market signals.',
    icon: IconBell,
    route: '/admin/live-alerts',
    status: 'legacy',
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
    key: 'broker',
    name: 'Broker',
    description: 'FYERS credentials and session.',
    icon: IconPlug,
    route: '/admin/broker',
    status: 'legacy',
    adminOnly: true,
  },
  {
    key: 'system',
    name: 'System',
    description: 'Service health and go-live checklist.',
    icon: IconServer,
    route: '/admin/system',
    status: 'legacy',
    adminOnly: true,
    /* old AdminOverviewPage — remounted at /admin/system in v2 */
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
    route: '/admin/strategies/library',
    label: 'Library',
    icon: IconLayers,
    end: false,
  },
] as const
