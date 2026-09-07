import { useEffect, useRef, useState, type InputHTMLAttributes } from 'react'
import { dmyToIso, isoToDmy, maskDmy } from '../lib/dates'
import { IconCalendar } from './icons'

/**
 * A date field that always reads dd/mm/yyyy.
 *
 * A native <input type="date"> shows the browser's locale — mm/dd/yyyy on a
 * phone or a Chrome set to en-US — and nothing in HTML or CSS overrides that.
 * So the visible control is a text input with a dd/mm/yyyy mask, and the
 * native input is kept only for its picker, opened from the calendar button.
 *
 * Semantics mirror the native control so the pages around it did not change:
 * `value` and `onChange` speak yyyy-MM-dd; any complete real date is passed
 * up, in range or not (a page explains its own limits — the history page
 * says why a date is before what FYERS keeps), with `aria-invalid` set when
 * it falls outside min/max; text that never became a date is dropped on blur
 * and the last value shown again, as a native input does.
 */
export function DateField({
  value,
  onChange,
  min,
  max,
  className,
  id,
  disabled,
  title,
  ...rest
}: {
  value: string
  onChange: (iso: string) => void
  min?: string
  max?: string
  className?: string
  id?: string
  disabled?: boolean
  title?: string
} & Pick<InputHTMLAttributes<HTMLInputElement>, 'aria-label' | 'aria-describedby' | 'required' | 'autoFocus'>) {
  const [text, setText] = useState(() => isoToDmy(value))
  const native = useRef<HTMLInputElement>(null)

  // The parent moved the value (a clamp, a reset, the picker): show it, unless
  // the text already means that value — then the user is mid-edit and keeps
  // their caret.
  useEffect(() => {
    if (dmyToIso(text) !== (value || null) && !(text === '' && value === '')) setText(isoToDmy(value))
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [value])

  const iso = dmyToIso(text)
  const outOfRange = iso != null && ((min != null && min !== '' && iso < min) || (max != null && max !== '' && iso > max))
  const invalid = (text !== '' && iso == null) || outOfRange

  function onText(raw: string) {
    const masked = maskDmy(raw)
    setText(masked)
    if (masked === '') {
      if (value !== '') onChange('')
      return
    }
    const next = dmyToIso(masked)
    if (next != null && next !== value) onChange(next)
  }

  function onBlur() {
    if (text !== '' && dmyToIso(text) == null) setText(isoToDmy(value))
  }

  function openPicker() {
    const el = native.current
    if (!el) return
    try {
      if (typeof el.showPicker === 'function') el.showPicker()
      else el.focus()
    } catch {
      el.focus()
    }
  }

  return (
    <span className={`datefield${disabled ? ' datefield--disabled' : ''}`}>
      <input
        id={id}
        type="text"
        inputMode="numeric"
        autoComplete="off"
        placeholder="dd/mm/yyyy"
        className={className ?? 'field__input'}
        value={text}
        disabled={disabled}
        aria-invalid={invalid || undefined}
        title={outOfRange ? `Outside ${isoToDmy(min) || '…'} → ${isoToDmy(max) || '…'}` : title}
        onChange={(e) => onText(e.target.value)}
        onBlur={onBlur}
        {...rest}
      />
      <input
        ref={native}
        type="date"
        className="datefield__native"
        tabIndex={-1}
        aria-hidden="true"
        value={value}
        min={min}
        max={max}
        disabled={disabled}
        onChange={(e) => {
          onChange(e.target.value)
          setText(isoToDmy(e.target.value))
        }}
      />
      <button
        type="button"
        className="datefield__btn"
        aria-label="Pick a date"
        title="Pick a date"
        disabled={disabled}
        onClick={openPicker}
      >
        <IconCalendar />
      </button>
    </span>
  )
}
