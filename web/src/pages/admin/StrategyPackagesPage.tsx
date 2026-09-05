/**
 * Strategy packages — what a trader may run, and the ceilings that come with it.
 *
 * A package that only listed strategies would barely beat a row of checkboxes.
 * It carries limits because every trader here runs on the same broker connection
 * and the same capital: deciding what someone may run *is* deciding how much they
 * may risk. So the limits sit next to the membership, not on another page.
 */

import { useEffect, useMemo, useState } from 'react'
import { Link } from 'react-router-dom'
import {
  useCreateStrategyPackage,
  useDeleteStrategyPackage,
  useSetPackageStrategies,
  useStrategyCatalogNames,
  useStrategyPackages,
  useUpdateStrategyPackage,
} from '../../lib/queries'
import type { SaveStrategyPackageInput, StrategyPackage } from '../../lib/types'
import { formatAge } from '../../lib/format'
import { Badge, EmptyState, InlineError, Panel, QueryBoundary } from '../../components/ui'

const EMPTY_FORM: SaveStrategyPackageInput = {
  key: '',
  name: '',
  description: '',
  isEnabled: true,
  includesAllStrategies: false,
  maxLotsPerRun: null,
  maxConcurrentRuns: null,
  allowedUnderlyings: [],
  allowLiveMode: false,
}

function numberOrNull(raw: string): number | null {
  const trimmed = raw.trim()
  if (trimmed === '') return null
  const value = Number(trimmed)
  return Number.isFinite(value) && value > 0 ? value : null
}

/** The limit fields, shared by the create form and the editor. */
function LimitFields({
  value,
  onChange,
  idPrefix,
}: {
  value: SaveStrategyPackageInput
  onChange: (next: SaveStrategyPackageInput) => void
  idPrefix: string
}) {
  return (
    <>
      <div className="field">
        <label className="field__label" htmlFor={`${idPrefix}-lots`}>
          Max lots per run
        </label>
        <input
          id={`${idPrefix}-lots`}
          className="field__input"
          type="number"
          min={1}
          placeholder="no ceiling"
          value={value.maxLotsPerRun ?? ''}
          onChange={(e) => onChange({ ...value, maxLotsPerRun: numberOrNull(e.target.value) })}
        />
      </div>
      <div className="field">
        <label className="field__label" htmlFor={`${idPrefix}-runs`}>
          Max concurrent runs
        </label>
        <input
          id={`${idPrefix}-runs`}
          className="field__input"
          type="number"
          min={1}
          placeholder="no ceiling"
          value={value.maxConcurrentRuns ?? ''}
          onChange={(e) => onChange({ ...value, maxConcurrentRuns: numberOrNull(e.target.value) })}
        />
        <span className="small-note muted">Tightest of package, account and platform wins.</span>
      </div>
      <div className="field">
        <label className="field__label" htmlFor={`${idPrefix}-underlyings`}>
          Allowed underlyings
        </label>
        <input
          id={`${idPrefix}-underlyings`}
          className="field__input mono"
          placeholder="blank = whatever the strategy supports"
          value={value.allowedUnderlyings.join(',')}
          onChange={(e) =>
            onChange({
              ...value,
              allowedUnderlyings: e.target.value
                .split(',')
                .map((x) => x.trim().toUpperCase())
                .filter(Boolean),
            })
          }
        />
      </div>
      <div className="field">
        <label className="field__label">Mode</label>
        <label className="grant__head">
          <input
            type="checkbox"
            checked={value.allowLiveMode}
            onChange={(e) => onChange({ ...value, allowLiveMode: e.target.checked })}
          />
          Allow live mode
        </label>
        <span className="small-note muted">
          Off keeps holders on paper — the guard that matters most once live execution lands.
        </span>
      </div>
    </>
  )
}

function PackageEditor({ pkg }: { pkg: StrategyPackage }) {
  const update = useUpdateStrategyPackage()
  const setStrategies = useSetPackageStrategies()
  const remove = useDeleteStrategyPackage()
  const catalog = useStrategyCatalogNames()

  const [form, setForm] = useState<SaveStrategyPackageInput>({
    key: pkg.key,
    name: pkg.name,
    description: pkg.description,
    isEnabled: pkg.isEnabled,
    includesAllStrategies: pkg.includesAllStrategies,
    maxLotsPerRun: pkg.maxLotsPerRun,
    maxConcurrentRuns: pkg.maxConcurrentRuns,
    allowedUnderlyings: pkg.allowedUnderlyings,
    allowLiveMode: pkg.allowLiveMode,
  })

  // The list refetches every time membership changes, so re-sync from the server.
  useEffect(() => {
    setForm({
      key: pkg.key,
      name: pkg.name,
      description: pkg.description,
      isEnabled: pkg.isEnabled,
      includesAllStrategies: pkg.includesAllStrategies,
      maxLotsPerRun: pkg.maxLotsPerRun,
      maxConcurrentRuns: pkg.maxConcurrentRuns,
      allowedUnderlyings: pkg.allowedUnderlyings,
      allowLiveMode: pkg.allowLiveMode,
    })
  }, [pkg])

  const byCategory = useMemo(() => {
    const groups = new Map<string, { name: string; description: string }[]>()
    for (const entry of catalog.data ?? []) {
      const list = groups.get(entry.category) ?? []
      list.push({ name: entry.name, description: entry.description })
      groups.set(entry.category, list)
    }
    return [...groups.entries()].sort((a, b) => a[0].localeCompare(b[0]))
  }, [catalog.data])

  function toggleStrategy(name: string) {
    const held = pkg.strategies.includes(name)
    setStrategies.mutate({
      id: pkg.id,
      strategyNames: held
        ? pkg.strategies.filter((s) => s !== name)
        : [...pkg.strategies, name],
    })
  }

  return (
    <div className="panel" style={{ margin: '4px 0' }}>
      {update.isError && <InlineError error={update.error} />}
      {setStrategies.isError && <InlineError error={setStrategies.error} />}
      {remove.isError && <InlineError error={remove.error} />}
      {remove.isSuccess && (
        <div className="alert alert--success" role="status">
          {remove.data.message}
        </div>
      )}

      <h3 className="section-title">Details &amp; limits</h3>
      <form
        className="edit-grid"
        onSubmit={(e) => {
          e.preventDefault()
          update.mutate({ id: pkg.id, ...form })
        }}
      >
        <div className="field">
          <label className="field__label" htmlFor={`pk-name-${pkg.id}`}>
            Name
          </label>
          <input
            id={`pk-name-${pkg.id}`}
            className="field__input"
            required
            value={form.name}
            onChange={(e) => setForm({ ...form, name: e.target.value })}
          />
        </div>
        <div className="field">
          <label className="field__label" htmlFor={`pk-desc-${pkg.id}`}>
            Description
          </label>
          <input
            id={`pk-desc-${pkg.id}`}
            className="field__input"
            value={form.description}
            onChange={(e) => setForm({ ...form, description: e.target.value })}
          />
        </div>
        <LimitFields value={form} onChange={setForm} idPrefix={`pk-${pkg.id}`} />
        <div className="field">
          <label className="field__label">Status</label>
          <label className="grant__head">
            <input
              type="checkbox"
              checked={form.isEnabled}
              onChange={(e) => setForm({ ...form, isEnabled: e.target.checked })}
            />
            Enabled
          </label>
          <span className="small-note muted">A disabled package grants nothing.</span>
        </div>
        <div className="edit-grid__actions">
          <button className="btn btn--primary" disabled={update.isPending}>
            {update.isPending ? 'Saving…' : 'Save'}
          </button>
        </div>
      </form>

      <h3 className="section-title connector-section">Strategies</h3>
      <label className="grant" style={{ maxWidth: '62ch', marginBottom: 10 }}>
        <span className="grant__head">
          <input
            type="checkbox"
            checked={form.includesAllStrategies}
            onChange={(e) => {
              const next = { ...form, includesAllStrategies: e.target.checked }
              setForm(next)
              update.mutate({ id: pkg.id, ...next })
            }}
          />
          Every strategy in the catalog
        </span>
        <span className="grant__desc">
          Including ones written later. This is the one place a new strategy reaches a trader without
          anyone deciding it should — use it only for a fully trusted account.
        </span>
      </label>

      {form.includesAllStrategies ? (
        <p className="muted">
          Membership is not used while this is on — the package covers whatever the engine can run.
        </p>
      ) : (
        <QueryBoundary query={catalog}>
          {() => (
            <>
              <p className="small-note muted">
                {pkg.strategies.length} of {catalog.data?.length ?? 0} selected.
              </p>
              {byCategory.map(([category, entries]) => (
                <div key={category} style={{ marginBottom: 10 }}>
                  <div className="section-title" style={{ marginBottom: 6 }}>
                    {category}
                  </div>
                  <div className="grant-grid">
                    {entries.map((entry) => (
                      <label key={entry.name} className="grant">
                        <span className="grant__head">
                          <input
                            type="checkbox"
                            checked={pkg.strategies.includes(entry.name)}
                            disabled={setStrategies.isPending}
                            onChange={() => toggleStrategy(entry.name)}
                          />
                          {entry.name}
                        </span>
                        {entry.description && (
                          <span className="grant__desc grant__desc--clamp" title={entry.description}>
                            {entry.description}
                          </span>
                        )}
                      </label>
                    ))}
                  </div>
                </div>
              ))}
            </>
          )}
        </QueryBoundary>
      )}

      <h3 className="section-title connector-section">Remove</h3>
      <p className="muted" style={{ maxWidth: '78ch' }}>
        {pkg.holderCount === 0
          ? 'No account holds this package.'
          : `${pkg.holderCount} account(s) hold this package. Deleting it leaves them with none, which means they can run nothing until you assign another.`}
      </p>
      <button
        type="button"
        className="btn btn--danger"
        disabled={remove.isPending}
        onClick={() => remove.mutate(pkg.id)}
      >
        {remove.isPending ? 'Deleting…' : 'Delete package'}
      </button>
    </div>
  )
}

/** One package: a summary row, and the editor underneath when opened. */
function PackageRow({
  pkg,
  open,
  onToggle,
}: {
  pkg: StrategyPackage
  open: boolean
  onToggle: () => void
}) {
  return (
    <>
      <tr>
        <td>
          <button type="button" className="btn btn--ghost btn--sm" onClick={onToggle} aria-expanded={open}>
            {open ? '▾' : '▸'} {pkg.name}
          </button>
          <div className="small-note muted mono">{pkg.key}</div>
          {!pkg.isEnabled && (
            <div className="small-note">
              <Badge tone="warn">disabled</Badge>
            </div>
          )}
        </td>
        <td>
          {pkg.includesAllStrategies ? (
            <Badge tone="warn">every strategy</Badge>
          ) : pkg.strategies.length === 0 ? (
            <Badge tone="warn">none</Badge>
          ) : (
            <span className="mono">{pkg.strategies.length}</span>
          )}
        </td>
        <td className="r">{pkg.maxLotsPerRun ?? <span className="muted">—</span>}</td>
        <td className="r">{pkg.maxConcurrentRuns ?? <span className="muted">—</span>}</td>
        <td className="mono">
          {pkg.allowedUnderlyings.length > 0 ? (
            pkg.allowedUnderlyings.join(', ')
          ) : (
            <span className="muted">all</span>
          )}
        </td>
        <td>
          {pkg.allowLiveMode ? (
            <Badge tone="warn">live allowed</Badge>
          ) : (
            <Badge tone="pos">paper only</Badge>
          )}
        </td>
        <td className="r">{pkg.holderCount}</td>
      </tr>
      {open && (
        <tr>
          <td colSpan={7}>
            <PackageEditor pkg={pkg} />
          </td>
        </tr>
      )}
    </>
  )
}

function CreatePanel() {
  const create = useCreateStrategyPackage()
  const [open, setOpen] = useState(false)
  const [form, setForm] = useState<SaveStrategyPackageInput>(EMPTY_FORM)

  return (
    <Panel
      title="New package"
      actions={
        <button type="button" className="btn btn--primary btn--sm" onClick={() => setOpen((o) => !o)}>
          {open ? 'Cancel' : 'Create a package'}
        </button>
      }
    >
      <p className="muted" style={{ maxWidth: '78ch' }}>
        A package starts empty: create it, then tick the strategies it holds. Nothing reaches a trader
        until you both put a strategy in the package and put the trader on it.
      </p>
      {open && (
        <>
          {create.isError && <InlineError error={create.error} />}
          <form
            className="edit-grid"
            onSubmit={(e) => {
              e.preventDefault()
              create.mutate(form, {
                onSuccess: () => {
                  setForm(EMPTY_FORM)
                  setOpen(false)
                },
              })
            }}
          >
            <div className="field">
              <label className="field__label" htmlFor="np-name">
                Name
              </label>
              <input
                id="np-name"
                className="field__input"
                required
                placeholder="Starter"
                value={form.name}
                onChange={(e) => setForm({ ...form, name: e.target.value })}
              />
            </div>
            <div className="field">
              <label className="field__label" htmlFor="np-key">
                Key (permanent)
              </label>
              <input
                id="np-key"
                className="field__input mono"
                required
                pattern="[a-z0-9][a-z0-9-]{1,31}"
                title="2-32 characters: lowercase letters, digits or dashes"
                placeholder="starter"
                value={form.key}
                onChange={(e) => setForm({ ...form, key: e.target.value.toLowerCase() })}
              />
            </div>
            <div className="field">
              <label className="field__label" htmlFor="np-desc">
                Description
              </label>
              <input
                id="np-desc"
                className="field__input"
                value={form.description}
                onChange={(e) => setForm({ ...form, description: e.target.value })}
              />
            </div>
            <LimitFields value={form} onChange={setForm} idPrefix="np" />
            <div className="edit-grid__actions">
              <button className="btn btn--primary" disabled={create.isPending}>
                {create.isPending ? 'Creating…' : 'Create'}
              </button>
            </div>
          </form>
        </>
      )}
    </Panel>
  )
}

export function StrategyPackagesPage() {
  const packages = useStrategyPackages()
  const [openId, setOpenId] = useState<number | null>(null)

  return (
    <div className="page">
      <header className="page__header">
        <h1 className="page__title">Strategy packages</h1>
        <p className="page__subtitle">
          What a trader may run, and the ceilings that come with it. Enforced when a run is deployed —
          a trader outside their package gets a 403 with the reason, not a hidden button.
        </p>
      </header>

      <p className="small-note">
        <Link to="/admin/users">← Users</Link>
      </p>

      <Panel title="Packages">
        <QueryBoundary query={packages}>
          {(list) =>
            list.length === 0 ? (
              <EmptyState>No packages yet. Create one below.</EmptyState>
            ) : (
              <div className="tablewrap">
                <table className="table">
                  <thead>
                    <tr>
                      <th>Package</th>
                      <th>Strategies</th>
                      <th className="r">Max lots</th>
                      <th className="r">Max runs</th>
                      <th>Underlyings</th>
                      <th>Mode</th>
                      <th className="r">Holders</th>
                    </tr>
                  </thead>
                  <tbody>
                    {list.map((pkg) => (
                      <PackageRow
                        key={pkg.id}
                        pkg={pkg}
                        open={openId === pkg.id}
                        onToggle={() => setOpenId(openId === pkg.id ? null : pkg.id)}
                      />
                    ))}
                  </tbody>
                </table>
              </div>
            )
          }
        </QueryBoundary>
        <p className="small-note muted">
          Updated {packages.data?.[0] ? formatAge(packages.data[0].updatedUtc) : '—'}. Assign a package
          to a trader on the <Link to="/admin/users">Users</Link> page.
        </p>
      </Panel>

      <CreatePanel />
    </div>
  )
}
