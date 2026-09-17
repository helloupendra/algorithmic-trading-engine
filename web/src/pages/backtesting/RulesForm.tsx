/**
 * The run's own trading rules, as a form: when it may trade, what the market
 * must look like, how often, when a position must come out, which contract a
 * bare BUY/SELL takes, and what a fill costs.
 *
 * Every group is collapsed until it is opened, and its header says how many of
 * its rules are set — so a run with three rules reads as three rules rather
 * than as a wall of empty inputs. The catalogue itself lives in
 * lib/backtestRules.ts; this only renders it.
 */

import { useState } from 'react'
import { IconChevronDown, IconChevronRight } from '../../components/icons'
import {
  RULE_GROUPS,
  WEEKDAYS,
  countSet,
  formatWindows,
  parseWindowRows,
  type RuleField,
  type RulesDraft,
} from '../../lib/backtestRules'

const MAX_WINDOWS = 3

export function RulesForm({
  draft,
  onChange,
  disabled,
}: {
  draft: RulesDraft
  onChange: (draft: RulesDraft) => void
  disabled?: boolean
}) {
  const [open, setOpen] = useState<string | null>(null)

  function set(key: string, value: string) {
    onChange({ ...draft, [key]: value })
  }

  return (
    <div className="rules">
      {RULE_GROUPS.map((group) => {
        const isOpen = open === group.id
        const set_ = countSet(draft, group)
        return (
          <section key={group.id} className={`rules__group ${isOpen ? 'is-open' : ''}`}>
            <button
              type="button"
              className="rules__head"
              aria-expanded={isOpen}
              onClick={() => setOpen(isOpen ? null : group.id)}
            >
              {isOpen ? <IconChevronDown /> : <IconChevronRight />}
              <span className="rules__title">{group.title}</span>
              <span className="rules__summary">{group.summary}</span>
              {set_ > 0 && <span className="rules__count">{set_} set</span>}
            </button>
            {isOpen && (
              <div className="rules__body">
                {group.fields.map((field) => (
                  <Field key={field.key} field={field} draft={draft} set={set} disabled={disabled} />
                ))}
              </div>
            )}
          </section>
        )
      })}
    </div>
  )
}

function Field({
  field,
  draft,
  set,
  disabled,
}: {
  field: RuleField
  draft: RulesDraft
  set: (key: string, value: string) => void
  disabled?: boolean
}) {
  const value = draft[field.key] ?? ''
  const id = `rule-${field.key}`

  if (field.kind === 'toggle') {
    return (
      <label className="rules__check" htmlFor={id}>
        <input
          id={id}
          type="checkbox"
          checked={value === 'true'}
          disabled={disabled}
          onChange={(e) => set(field.key, e.target.checked ? 'true' : '')}
        />
        <span>
          {field.label}
          {field.directional && <em className="rules__note"> · directional signals only</em>}
        </span>
        {field.help && <span className="field__help">{field.help}</span>}
      </label>
    )
  }

  if (field.kind === 'windows') {
    const windows = parseWindowRows(value)
    const rows: Array<[string, string]> = windows.length > 0 ? [...windows] : [['', '']]
    if (rows.length < MAX_WINDOWS) rows.push(['', ''])
    return (
      <div className="field rules__field rules__field--wide">
        <span className="field__label">{field.label}</span>
        <div className="rules__windows">
          {rows.map(([from, to], i) => (
            <div className="rules__window" key={i}>
              <input
                className="field__input field__input--sm"
                type="time"
                value={from}
                disabled={disabled}
                aria-label={`Window ${i + 1} from`}
                onChange={(e) => {
                  const next = [...rows]
                  next[i] = [e.target.value, to]
                  set(field.key, formatWindows(next))
                }}
              />
              <span className="faint">to</span>
              <input
                className="field__input field__input--sm"
                type="time"
                value={to}
                disabled={disabled}
                aria-label={`Window ${i + 1} to`}
                onChange={(e) => {
                  const next = [...rows]
                  next[i] = [from, e.target.value]
                  set(field.key, formatWindows(next))
                }}
              />
            </div>
          ))}
        </div>
        {field.help && <span className="field__help">{field.help}</span>}
      </div>
    )
  }

  if (field.kind === 'weekdays') {
    const chosen = new Set(value.split(',').map((d) => d.trim()).filter(Boolean))
    return (
      <div className="field rules__field rules__field--wide">
        <span className="field__label">{field.label}</span>
        <div className="seg seg--wrap" role="group" aria-label={field.label}>
          {WEEKDAYS.map((day) => {
            const active = chosen.size === 0 || chosen.has(day)
            return (
              <button
                key={day}
                type="button"
                className={`seg__btn ${active ? 'is-active' : ''}`}
                disabled={disabled}
                aria-pressed={active}
                onClick={() => {
                  const next = new Set(chosen.size === 0 ? WEEKDAYS : chosen)
                  if (next.has(day)) next.delete(day)
                  else next.add(day)
                  const all = next.size === 0 || next.size === WEEKDAYS.length
                  set(field.key, all ? '' : WEEKDAYS.filter((d) => next.has(d)).join(','))
                }}
              >
                {day}
              </button>
            )
          })}
        </div>
        <span className="field__help">All five are traded unless some are switched off.</span>
      </div>
    )
  }

  if (field.kind === 'select') {
    return (
      <div className="field rules__field">
        <label className="field__label" htmlFor={id}>{field.label}</label>
        <select
          id={id}
          className="field__input field__input--sm"
          value={value}
          disabled={disabled}
          onChange={(e) => set(field.key, e.target.value)}
        >
          {(field.options ?? []).map(([option, label]) => (
            <option key={option} value={option}>{label}</option>
          ))}
        </select>
        {field.help && <span className="field__help">{field.help}</span>}
      </div>
    )
  }

  if (field.kind === 'pair') {
    const [first = '', second = ''] = value.split(',')
    const [firstHint, secondHint] = field.pair ?? ['', '']
    return (
      <div className="field rules__field">
        <span className="field__label">{field.label}</span>
        <div className="rules__pair">
          <input
            className="field__input field__input--sm"
            inputMode="decimal"
            placeholder={firstHint}
            value={first}
            disabled={disabled}
            aria-label={`${field.label} — first`}
            onChange={(e) => set(field.key, `${e.target.value},${second}`.replace(/^,$/, ''))}
          />
          <input
            className="field__input field__input--sm"
            inputMode="decimal"
            placeholder={secondHint}
            value={second}
            disabled={disabled}
            aria-label={`${field.label} — second`}
            onChange={(e) => set(field.key, `${first},${e.target.value}`.replace(/^,$/, ''))}
          />
        </div>
        {field.help && <span className="field__help">{field.help}</span>}
      </div>
    )
  }

  return (
    <div className="field rules__field">
      <label className="field__label" htmlFor={id}>
        {field.label}
        {field.directional && <em className="rules__note"> · directional</em>}
      </label>
      <input
        id={id}
        className="field__input field__input--sm"
        type={field.kind === 'time' ? 'time' : 'text'}
        inputMode={field.kind === 'number' ? 'decimal' : undefined}
        placeholder={field.placeholder}
        value={value}
        disabled={disabled}
        onChange={(e) => set(field.key, e.target.value)}
      />
      {field.help && <span className="field__help">{field.help}</span>}
    </div>
  )
}
