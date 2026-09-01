/**
 * User administration: list, create, delete. New accounts are Traders; the
 * bootstrap admin is created by the API itself and cannot be deleted here
 * while it is the account you are signed in with.
 */

import { useState } from 'react'
import { useAuth } from '../../lib/auth'
import { useDeleteUser, useRegisterUser, useUsers } from '../../lib/queries'
import { formatDateTime, formatInrWhole } from '../../lib/format'
import { Badge, InlineError, Panel, QueryBoundary } from '../../components/ui'

export function UsersPage() {
  const { user: me } = useAuth()
  const users = useUsers()
  const register = useRegisterUser()
  const removeUser = useDeleteUser()

  const [form, setForm] = useState({ userName: '', email: '', password: '' })

  function handleCreate(event: React.FormEvent) {
    event.preventDefault()
    register.mutate(form, {
      onSuccess: () => setForm({ userName: '', email: '', password: '' }),
    })
  }

  return (
    <div className="page">
      <header className="page__header">
        <h1 className="page__title">Users</h1>
        <p className="page__subtitle">Accounts that can sign in to this platform.</p>
      </header>

      <Panel title="Accounts">
        {removeUser.isError && <InlineError error={removeUser.error} />}
        <QueryBoundary query={users} empty="No users.">
          {(data) => (
            <div className="tablewrap">
              <table className="table">
                <thead>
                  <tr>
                    <th>User</th>
                    <th>Email</th>
                    <th>Role</th>
                    <th className="r">Capital</th>
                    <th>Status</th>
                    <th className="r">Last login</th>
                    <th />
                  </tr>
                </thead>
                <tbody>
                  {data.map((u) => (
                    <tr key={u.id}>
                      <td className="mono">{u.userName}</td>
                      <td className="muted">{u.email}</td>
                      <td>
                        <Badge tone={u.role === 'Admin' ? 'accent' : 'neutral'}>{u.role}</Badge>
                      </td>
                      <td className="r mono">{formatInrWhole(u.totalCapital)}</td>
                      <td>
                        <Badge tone={u.isActive ? 'pos' : 'warn'}>
                          {u.isActive ? 'active' : 'disabled'}
                        </Badge>
                      </td>
                      <td className="r muted">{formatDateTime(u.lastLoginUtc)}</td>
                      <td className="r">
                        {u.userName !== me?.userName && (
                          <button
                            type="button"
                            className="btn btn--ghost btn--sm"
                            disabled={removeUser.isPending}
                            onClick={() => {
                              if (window.confirm(`Delete user "${u.userName}"?`)) {
                                removeUser.mutate(u.userName)
                              }
                            }}
                          >
                            Delete
                          </button>
                        )}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </QueryBoundary>
      </Panel>

      <Panel title="Create account">
        {register.isError && <InlineError error={register.error} />}
        <form className="form-row" onSubmit={handleCreate}>
          <div className="field">
            <label className="field__label" htmlFor="nu-name">Username</label>
            <input
              id="nu-name"
              className="field__input"
              required
              value={form.userName}
              onChange={(e) => setForm({ ...form, userName: e.target.value })}
            />
          </div>
          <div className="field">
            <label className="field__label" htmlFor="nu-email">Email</label>
            <input
              id="nu-email"
              type="email"
              className="field__input"
              required
              value={form.email}
              onChange={(e) => setForm({ ...form, email: e.target.value })}
            />
          </div>
          <div className="field">
            <label className="field__label" htmlFor="nu-pass">Password</label>
            <input
              id="nu-pass"
              type="password"
              className="field__input"
              required
              minLength={8}
              value={form.password}
              onChange={(e) => setForm({ ...form, password: e.target.value })}
            />
          </div>
          <button className="btn btn--primary" disabled={register.isPending}>
            {register.isPending ? 'Creating…' : 'Create trader'}
          </button>
        </form>
      </Panel>
    </div>
  )
}
