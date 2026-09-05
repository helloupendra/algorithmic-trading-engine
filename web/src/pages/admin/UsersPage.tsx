/**
 * Users — accounts, what each may do, and how much rope they have.
 *
 * The important idea on this page: **grants are deny-by-default and enforced on
 * the server**. Unticking a module here is not cosmetic — the matching endpoints
 * answer 403 for that trader, whether they use the console or curl.
 */

import { useState } from 'react'
import { Link } from 'react-router-dom'
import {
  usePlatformModules,
  useRegisterUser,
  useResetUserPassword,
  useRevokeUserSessions,
  useCreateInvite,
  useInvites,
  useRevokeInvite,
  useSetUserGrants,
  useSetUserStrategyGrants,
  useStrategyCatalogNames,
  useStrategyPackages,
  useUpdateUser,
  useUserAccounts,
  useUserRoles,
} from '../../lib/queries'
import type { UserAdmin } from '../../lib/types'
import { formatAge, formatInr } from '../../lib/format'
import { Badge, EmptyState, InlineError, Panel, QueryBoundary } from '../../components/ui'

function RoleBadge({ role }: { role: string }) {
  if (role === 'Admin') return <Badge tone="accent">Admin</Badge>
  if (role === 'Service') return <Badge tone="neutral">Service</Badge>
  return <Badge tone="pos">Trader</Badge>
}

function AccountRow({
  user,
  modules,
  roles,
  onError,
}: {
  user: UserAdmin
  modules: { key: string; name: string; description: string }[]
  roles: string[]
  onError: (e: unknown) => void
}) {
  const update = useUpdateUser()
  const setGrants = useSetUserGrants()
  const resetPassword = useResetUserPassword()
  const revoke = useRevokeUserSessions()
  const [open, setOpen] = useState(false)
  const [newPassword, setNewPassword] = useState('')

  const isAdmin = user.role === 'Admin'
  const isService = user.role === 'Service'

  function patch(input: Parameters<typeof update.mutate>[0]) {
    update.mutate(input, { onError })
  }

  return (
    <>
      <tr>
        <td>
          <button
            type="button"
            className="btn btn--ghost btn--sm"
            onClick={() => setOpen((o) => !o)}
            aria-expanded={open}
          >
            {open ? '▾' : '▸'} {user.userName}
          </button>
          <div className="small-note muted">{user.email}</div>
        </td>
        <td>
          <RoleBadge role={user.role} />
        </td>
        <td>
          {user.isActive ? <Badge tone="pos">active</Badge> : <Badge tone="warn">disabled</Badge>}
        </td>
        <td className="r">{isService ? <span className="muted">—</span> : formatInr(user.totalCapital)}</td>
        <td>
          {isAdmin ? (
            <span className="muted">all modules</span>
          ) : isService ? (
            <span className="muted">machine account</span>
          ) : user.moduleGrants.length === 0 ? (
            <Badge tone="warn">no access</Badge>
          ) : (
            <span className="mono">{user.moduleGrants.join(', ')}</span>
          )}
        </td>
        <td>
          {user.lastLoginUtc ? formatAge(user.lastLoginUtc) : <span className="muted">never</span>}
          {user.activeSessions > 0 && (
            <div className="small-note muted">{user.activeSessions} session(s)</div>
          )}
        </td>
      </tr>

      {open && (
        <tr>
          <td colSpan={6}>
            <div className="panel" style={{ margin: '4px 0' }}>
              <h3 className="section-title">Access</h3>
              {isService ? (
                <p className="muted" style={{ maxWidth: '78ch' }}>
                  A machine account — the Python engine signs in as this. It holds no grants and gets
                  no capital, and it cannot reach anything behind the admin policy.
                </p>
              ) : isAdmin ? (
                <p className="muted" style={{ maxWidth: '78ch' }}>
                  Admins hold every module by definition, so there is nothing to grant here.
                </p>
              ) : (
                <>
                  <p className="muted" style={{ maxWidth: '78ch' }}>
                    Unticking a module makes its endpoints answer 403 for this trader — this is
                    enforced on the server, not just hidden in their menu.
                  </p>
                  <div className="grant-grid">
                    {modules.map((m) => {
                      const held = user.moduleGrants.includes(m.key)
                      return (
                        <label key={m.key} className="grant">
                          <span className="grant__head">
                            <input
                              type="checkbox"
                              checked={held}
                              disabled={setGrants.isPending}
                              onChange={() =>
                                setGrants.mutate(
                                  {
                                    id: user.id,
                                    moduleKeys: held
                                      ? user.moduleGrants.filter((k) => k !== m.key)
                                      : [...user.moduleGrants, m.key],
                                  },
                                  { onError },
                                )
                              }
                            />
                            {m.name}
                          </span>
                          <span className="grant__desc">{m.description}</span>
                        </label>
                      )
                    })}
                  </div>
                </>
              )}

              {!isService && !isAdmin && <StrategyAccessSection user={user} onError={onError} />}

              <h3 className="section-title connector-section">Account</h3>
              <div className="form-row">
                <div className="field">
                  <label className="field__label" htmlFor={`role-${user.id}`}>
                    Role
                  </label>
                  <select
                    id={`role-${user.id}`}
                    className="field__input"
                    value={user.role}
                    disabled={update.isPending}
                    onChange={(e) => patch({ id: user.id, role: e.target.value })}
                  >
                    {roles.map((r) => (
                      <option key={r} value={r}>
                        {r}
                      </option>
                    ))}
                  </select>
                </div>

                {!isService && (
                  <>
                    <div className="field">
                      <label className="field__label" htmlFor={`cap-${user.id}`}>
                        Capital (₹)
                      </label>
                      <input
                        id={`cap-${user.id}`}
                        className="field__input"
                        type="number"
                        min={0}
                        defaultValue={user.totalCapital}
                        onBlur={(e) => {
                          const v = Number(e.target.value)
                          if (v !== user.totalCapital) patch({ id: user.id, totalCapital: v })
                        }}
                      />
                    </div>
                    <div className="field">
                      <label className="field__label" htmlFor={`runs-${user.id}`}>
                        Max concurrent runs
                      </label>
                      <input
                        id={`runs-${user.id}`}
                        className="field__input"
                        type="number"
                        min={0}
                        placeholder="platform limit"
                        defaultValue={user.maxConcurrentRuns ?? ''}
                        onBlur={(e) => {
                          const raw = e.target.value.trim()
                          const v = raw === '' ? -1 : Number(raw)
                          if (v !== (user.maxConcurrentRuns ?? -1)) {
                            patch({ id: user.id, maxConcurrentRuns: v })
                          }
                        }}
                      />
                      <span className="small-note muted">Blank uses the platform limit.</span>
                    </div>
                  </>
                )}

                <button
                  type="button"
                  className={user.isActive ? 'btn btn--danger' : 'btn btn--primary'}
                  disabled={update.isPending}
                  onClick={() => patch({ id: user.id, isActive: !user.isActive })}
                >
                  {user.isActive ? 'Disable account' : 'Enable account'}
                </button>
              </div>
              {user.isActive && (
                <p className="small-note muted">
                  Disabling signs the account out everywhere immediately — it does not merely stop the
                  next sign-in.
                </p>
              )}

              <h3 className="section-title connector-section">Password &amp; sessions</h3>
              {resetPassword.isSuccess && (
                <div className="alert alert--success" role="status">
                  {resetPassword.data.message}
                </div>
              )}
              {revoke.isSuccess && (
                <div className="alert alert--success" role="status">
                  {revoke.data.message}
                </div>
              )}
              <form
                className="form-row"
                onSubmit={(e) => {
                  e.preventDefault()
                  resetPassword.mutate(
                    { id: user.id, newPassword },
                    { onSuccess: () => setNewPassword(''), onError },
                  )
                }}
              >
                <div className="field">
                  <label className="field__label" htmlFor={`pw-${user.id}`}>
                    Set a new password
                  </label>
                  <input
                    id={`pw-${user.id}`}
                    className="field__input"
                    type="password"
                    minLength={8}
                    required
                    placeholder="at least 8 characters"
                    value={newPassword}
                    onChange={(e) => setNewPassword(e.target.value)}
                    autoComplete="new-password"
                  />
                </div>
                <button className="btn btn--ghost" disabled={resetPassword.isPending}>
                  {resetPassword.isPending ? 'Resetting…' : 'Reset password'}
                </button>
                <button
                  type="button"
                  className="btn btn--ghost"
                  disabled={revoke.isPending || user.activeSessions === 0}
                  onClick={() => revoke.mutate(user.id, { onError })}
                >
                  Sign out {user.activeSessions} session(s)
                </button>
              </form>
              <p className="small-note muted">
                A reset also signs the account out everywhere, so an old password cannot keep a live
                session.
              </p>
            </div>
          </td>
        </tr>
      )}
    </>
  )
}

/**
 * What this trader may actually run: their package, plus any one-off extras.
 *
 * The module grant above decides whether they can reach the Strategies module at
 * all; this decides which strategies exist for them once they are in it.
 */
function StrategyAccessSection({
  user,
  onError,
}: {
  user: UserAdmin
  onError: (e: unknown) => void
}) {
  const packages = useStrategyPackages()
  const catalog = useStrategyCatalogNames()
  const update = useUpdateUser()
  const setStrategyGrants = useSetUserStrategyGrants()

  const pkg = (packages.data ?? []).find((p) => p.id === user.strategyPackageId)
  const inPackage = new Set(pkg?.strategies ?? [])

  return (
    <>
      <h3 className="section-title connector-section">Strategies</h3>
      <div className="form-row">
        <div className="field">
          <label className="field__label" htmlFor={`pkg-${user.id}`}>
            Package
          </label>
          <select
            id={`pkg-${user.id}`}
            className="field__input"
            value={user.strategyPackageId ?? ''}
            disabled={update.isPending}
            onChange={(e) =>
              update.mutate(
                {
                  id: user.id,
                  strategyPackageId: e.target.value === '' ? -1 : Number(e.target.value),
                },
                { onError },
              )
            }
          >
            <option value="">No package — can run nothing</option>
            {(packages.data ?? []).map((p) => (
              <option key={p.id} value={p.id}>
                {p.name}
                {p.isEnabled ? '' : ' (disabled)'}
              </option>
            ))}
          </select>
          <span className="small-note muted">
            <Link to="/admin/users/packages">Manage packages →</Link>
          </span>
        </div>
        {pkg && (
          <div className="field">
            <label className="field__label">What it allows</label>
            <span className="small-note muted">
              {pkg.includesAllStrategies
                ? 'Every strategy in the catalog'
                : `${pkg.strategies.length} strategies`}
              {pkg.maxLotsPerRun != null && ` · max ${pkg.maxLotsPerRun} lot(s) per run`}
              {pkg.maxConcurrentRuns != null && ` · max ${pkg.maxConcurrentRuns} run(s)`}
              {pkg.allowedUnderlyings.length > 0 && ` · ${pkg.allowedUnderlyings.join(', ')}`}
              {pkg.allowLiveMode ? ' · live allowed' : ' · paper only'}
            </span>
          </div>
        )}
      </div>

      {!pkg?.includesAllStrategies && (
        <>
          <p className="small-note muted">
            Extras on top of the package — for the one strategy you do not want to add to everyone
            else's plan.
          </p>
          <div className="grant-grid">
            {(catalog.data ?? [])
              .filter((entry) => !inPackage.has(entry.name))
              .map((entry) => {
                const held = user.strategyGrants.includes(entry.name)
                return (
                  <label key={entry.name} className="grant">
                    <span className="grant__head">
                      <input
                        type="checkbox"
                        checked={held}
                        disabled={setStrategyGrants.isPending}
                        onChange={() =>
                          setStrategyGrants.mutate(
                            {
                              id: user.id,
                              strategyNames: held
                                ? user.strategyGrants.filter((s) => s !== entry.name)
                                : [...user.strategyGrants, entry.name],
                            },
                            { onError },
                          )
                        }
                      />
                      {entry.name}
                    </span>
                    <span className="grant__desc">{entry.category}</span>
                  </label>
                )
              })}
          </div>
        </>
      )}
    </>
  )
}

function CreateAccountPanel({ onError }: { onError: (e: unknown) => void }) {
  const register = useRegisterUser()
  const [form, setForm] = useState({ userName: '', email: '', password: '' })

  return (
    <Panel title="Create an account">
      <p className="muted" style={{ maxWidth: '78ch' }}>
        A new account starts as a <b>Trader with no modules</b> — it can sign in and do nothing until
        you grant it something. That is deliberate: it is what makes adding people safe.
      </p>
      {register.isSuccess && (
        <div className="alert alert--success" role="status">
          Account created. Grant it the modules it needs below.
        </div>
      )}
      <form
        className="form-row"
        onSubmit={(e) => {
          e.preventDefault()
          register.mutate(form, {
            onSuccess: () => setForm({ userName: '', email: '', password: '' }),
            onError,
          })
        }}
      >
        <div className="field">
          <label className="field__label" htmlFor="nu-name">
            Username
          </label>
          <input
            id="nu-name"
            className="field__input"
            required
            value={form.userName}
            onChange={(e) => setForm({ ...form, userName: e.target.value })}
          />
        </div>
        <div className="field">
          <label className="field__label" htmlFor="nu-email">
            Email
          </label>
          <input
            id="nu-email"
            className="field__input"
            type="email"
            required
            value={form.email}
            onChange={(e) => setForm({ ...form, email: e.target.value })}
          />
        </div>
        <div className="field">
          <label className="field__label" htmlFor="nu-pw">
            Password
          </label>
          <input
            id="nu-pw"
            className="field__input"
            type="password"
            minLength={8}
            required
            value={form.password}
            onChange={(e) => setForm({ ...form, password: e.target.value })}
            autoComplete="new-password"
          />
        </div>
        <button className="btn btn--primary" disabled={register.isPending}>
          {register.isPending ? 'Creating…' : 'Create account'}
        </button>
      </form>
    </Panel>
  )
}

/**
 * Invitations — an admin decides who may join; the person sets their own
 * password, so it never passes through the admin or a chat message.
 */
function InvitesPanel({ onError }: { onError: (e: unknown) => void }) {
  const invites = useInvites()
  const modules = usePlatformModules()
  const packages = useStrategyPackages()
  const create = useCreateInvite()
  const revoke = useRevokeInvite()

  const [open, setOpen] = useState(false)
  const [form, setForm] = useState({
    email: '',
    suggestedUserName: '',
    moduleKeys: [] as string[],
    strategyPackageId: null as number | null,
    validDays: 7,
  })
  const [link, setLink] = useState<string | null>(null)

  return (
    <Panel
      title="Invitations"
      actions={
        <button type="button" className="btn btn--primary btn--sm" onClick={() => setOpen((o) => !o)}>
          {open ? 'Cancel' : 'Invite someone'}
        </button>
      }
    >
      <p className="muted" style={{ maxWidth: '78ch' }}>
        Signing up is not open. You choose who may join; they choose their own password. Anything you
        tick here is what their account starts with — leave it all off and they can sign in and do
        nothing until you decide otherwise.
      </p>

      {link && (
        <div className="alert alert--success" role="status">
          <div>
            Send this link to them. <b>It is shown only once</b> — the token is not recoverable.
          </div>
          <code style={{ wordBreak: 'break-all', display: 'block', marginTop: 6 }}>{link}</code>
          <button
            type="button"
            className="btn btn--ghost btn--sm"
            onClick={() => {
              navigator.clipboard?.writeText(link)
            }}
          >
            Copy
          </button>
          <button type="button" className="btn btn--ghost btn--sm" onClick={() => setLink(null)}>
            Dismiss
          </button>
        </div>
      )}

      {open && (
        <>
          {create.isError && <InlineError error={create.error} />}
          <form
            className="edit-grid"
            onSubmit={(e) => {
              e.preventDefault()
              create.mutate(form, {
                onSuccess: (created) => {
                  setLink(created.link)
                  setForm({
                    email: '',
                    suggestedUserName: '',
                    moduleKeys: [],
                    strategyPackageId: null,
                    validDays: 7,
                  })
                  setOpen(false)
                },
                onError,
              })
            }}
          >
            <div className="field">
              <label className="field__label" htmlFor="inv-email">
                Email
              </label>
              <input
                id="inv-email"
                className="field__input"
                type="email"
                required
                value={form.email}
                onChange={(e) => setForm({ ...form, email: e.target.value })}
              />
            </div>
            <div className="field">
              <label className="field__label" htmlFor="inv-username">
                Suggested username
              </label>
              <input
                id="inv-username"
                className="field__input"
                placeholder="from the email if blank"
                value={form.suggestedUserName}
                onChange={(e) => setForm({ ...form, suggestedUserName: e.target.value })}
              />
            </div>
            <div className="field">
              <label className="field__label" htmlFor="inv-days">
                Valid for (days)
              </label>
              <input
                id="inv-days"
                className="field__input"
                type="number"
                min={1}
                max={90}
                value={form.validDays}
                onChange={(e) => setForm({ ...form, validDays: Number(e.target.value) || 7 })}
              />
            </div>
            <div className="field">
              <label className="field__label" htmlFor="inv-package">
                Strategy package
              </label>
              <select
                id="inv-package"
                className="field__input"
                value={form.strategyPackageId ?? ''}
                onChange={(e) =>
                  setForm({
                    ...form,
                    strategyPackageId: e.target.value === '' ? null : Number(e.target.value),
                  })
                }
              >
                <option value="">None — can run nothing</option>
                {(packages.data ?? []).map((p) => (
                  <option key={p.id} value={p.id}>
                    {p.name}
                  </option>
                ))}
              </select>
            </div>
            <div className="edit-grid__actions">
              <div className="grant-grid" style={{ marginBottom: 10 }}>
                {(modules.data ?? []).map((m) => {
                  const held = form.moduleKeys.includes(m.key)
                  return (
                    <label key={m.key} className="grant">
                      <span className="grant__head">
                        <input
                          type="checkbox"
                          checked={held}
                          onChange={() =>
                            setForm({
                              ...form,
                              moduleKeys: held
                                ? form.moduleKeys.filter((k) => k !== m.key)
                                : [...form.moduleKeys, m.key],
                            })
                          }
                        />
                        {m.name}
                      </span>
                      <span className="grant__desc">{m.description}</span>
                    </label>
                  )
                })}
              </div>
              <button className="btn btn--primary" disabled={create.isPending}>
                {create.isPending ? 'Creating…' : 'Create invitation'}
              </button>
            </div>
          </form>
        </>
      )}

      {revoke.isError && <InlineError error={revoke.error} />}

      <QueryBoundary query={invites} empty="No invitations yet.">
        {(list) =>
          list.length === 0 ? (
            <EmptyState>No invitations yet.</EmptyState>
          ) : (
            <div className="tablewrap">
              <table className="table">
                <thead>
                  <tr>
                    <th>Email</th>
                    <th>Status</th>
                    <th>Starts with</th>
                    <th>Invited by</th>
                    <th>Expires</th>
                    <th />
                  </tr>
                </thead>
                <tbody>
                  {list.map((inv) => (
                    <tr key={inv.id}>
                      <td>
                        {inv.email}
                        <div className="small-note muted mono">{inv.suggestedUserName}</div>
                      </td>
                      <td>
                        {inv.status === 'pending' ? (
                          <Badge tone="accent">pending</Badge>
                        ) : inv.status === 'accepted' ? (
                          <Badge tone="pos">accepted</Badge>
                        ) : (
                          <Badge tone="neutral">{inv.status}</Badge>
                        )}
                      </td>
                      <td className="mono">
                        {inv.moduleKeys.length > 0 ? inv.moduleKeys.join(', ') : '—'}
                      </td>
                      <td>{inv.createdBy}</td>
                      <td>{formatAge(inv.expiresUtc)}</td>
                      <td>
                        {inv.status === 'pending' && (
                          <button
                            type="button"
                            className="btn btn--ghost btn--sm"
                            disabled={revoke.isPending}
                            onClick={() => revoke.mutate(inv.id, { onError })}
                          >
                            Revoke
                          </button>
                        )}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )
        }
      </QueryBoundary>
    </Panel>
  )
}

export function UsersPage() {
  const accounts = useUserAccounts()
  const modules = usePlatformModules()
  const roles = useUserRoles()
  const [error, setError] = useState<unknown>(null)

  return (
    <div className="page">
      <header className="page__header">
        <h1 className="page__title">Users</h1>
        <p className="page__subtitle">
          Accounts, the modules each may use, and how much they may put at risk. Grants are enforced
          on the server — a trader without a module gets 403, not a hidden menu entry.
        </p>
      </header>

      {error != null && <InlineError error={error} />}

      <Panel title="Accounts">
        <QueryBoundary query={accounts} empty="No accounts.">
          {(list) =>
            list.length === 0 ? (
              <EmptyState>No accounts yet.</EmptyState>
            ) : (
              <div className="tablewrap">
                <table className="table">
                  <thead>
                    <tr>
                      <th>Account</th>
                      <th>Role</th>
                      <th>Status</th>
                      <th className="r">Capital</th>
                      <th>Modules</th>
                      <th>Last sign-in</th>
                    </tr>
                  </thead>
                  <tbody>
                    {list.map((u) => (
                      <AccountRow
                        key={u.id}
                        user={u}
                        modules={modules.data ?? []}
                        roles={roles.data ?? ['Admin', 'Trader', 'Service']}
                        onError={setError}
                      />
                    ))}
                  </tbody>
                </table>
              </div>
            )
          }
        </QueryBoundary>
        <p className="small-note muted">
          Open an account to change its role, capital, run cap and modules. Accounts are disabled
          rather than deleted, so the runs and orders they made keep their owner.
        </p>
      </Panel>

      <InvitesPanel onError={setError} />

      <CreateAccountPanel onError={setError} />

      <Panel title="What each module allows">
        <QueryBoundary query={modules}>
          {(list) => (
            <div className="tablewrap">
              <table className="table">
                <thead>
                  <tr>
                    <th>Module</th>
                    <th>Key</th>
                    <th>What it allows</th>
                  </tr>
                </thead>
                <tbody>
                  {list.map((m) => (
                    <tr key={m.key}>
                      <td>{m.name}</td>
                      <td className="mono">{m.key}</td>
                      <td className="muted">{m.description}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </QueryBoundary>
        <p className="small-note muted">
          Signing up is not open yet — an admin creates accounts here. Invite links are the next step,
          and they are safe to add precisely because a new account holds nothing until it is granted.
        </p>
      </Panel>
    </div>
  )
}
