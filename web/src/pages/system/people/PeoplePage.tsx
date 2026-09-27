/**
 * System → People: who may use the console and what they may do with it.
 * Three tabs, each a URL: Users (/system/people), the accounts and their
 * grants; Packages (/system/people/packages), what a trader may run and the
 * ceilings with it; Invites (/system/people/invites), who may join.
 *
 * Packages was a page the menu never listed, reached only from a link inside
 * an account, and the invitations sat halfway down the users page; one
 * subject gets one page.
 */

import { PageTabs } from '../../../components/PageTabs'
import { Invites, Users } from './Users'
import { Packages } from './Packages'
import '../health/health.css'

const TABS = [
  { key: 'users', label: 'Users', to: '/system/people' },
  { key: 'packages', label: 'Packages', to: '/system/people/packages' },
  { key: 'invites', label: 'Invites', to: '/system/people/invites' },
] as const

export function PeoplePage({ view }: { view: (typeof TABS)[number]['key'] }) {
  return (
    <div className="page hp">
      <PageTabs label="People" tabs={TABS} current={view} />
      {view === 'users' && <Users />}
      {view === 'packages' && <Packages />}
      {view === 'invites' && <Invites />}
    </div>
  )
}
