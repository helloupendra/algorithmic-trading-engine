/**
 * System → Health: is the platform behaving? Two tabs, each a URL. Overview
 * (/system) is the machine now: the switches, the server, the database, the
 * processes and the archive, with Sentinel's latest verdict at the top.
 * Checkups (/system/checkups) is that verdict in full, the history, and a
 * button to ask for one.
 *
 * These were two System pages, the overview and the desk checkup, reached
 * from different menu entries; one question gets one page.
 */

import { PageTabs } from '../../../components/PageTabs'
import { HealthOverview } from './HealthOverview'
import { Checkups } from './Checkups'
import './health.css'

const TABS = [
  { key: 'overview', label: 'Overview', to: '/system' },
  { key: 'checkups', label: 'Checkups', to: '/system/checkups' },
] as const

export function HealthPage({ view }: { view: (typeof TABS)[number]['key'] }) {
  return (
    <div className="page hp">
      <PageTabs label="Health" tabs={TABS} current={view} />
      {view === 'overview' ? <HealthOverview /> : <Checkups />}
    </div>
  )
}
